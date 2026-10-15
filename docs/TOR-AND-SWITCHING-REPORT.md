# Технический отчёт: Tor, мосты, «умное переключение» и архитектура смены серверов
**Репозиторий:** `E:\PPROJECTS\InvisibleGorilla-XRayClient`
**Ревизия:** `7c899d4` — «v3.6.54: load Tor bridges per method and keep connect and stop responsive» (HEAD)
**Версия приложения по csproj:** `3.6.54.0` (`InvisibleGorilla-XRay\InvisibleGorilla-XRay.csproj:23`)
**Дата анализа:** 2026-10-15

Все утверждения ниже проверены по коду; для каждого указан путь относительно корня репозитория и номера строк. Где функциональности нет — написано **«не реализовано»**.

---

## 0. Главное в двух абзацах

Tor встроен как **sidecar-процесс**: приложение генерирует `torrc`, запускает `Tor\tor.exe`, синхронно (блокирующе) ждёт bootstrap до 100 % через control-port с cookie-аутентификацией (по умолчанию 90 с), а затем превращает Tor в обычный outbound Xray: либо «Tor only» (весь трафик через локальный Tor-SOCKS), либо «Xray over Tor» (основной outbound Xray получает `dialerProxy` на Tor). Никакого «умного» управления Tor на лету нет: любой перезапуск — это `Stop()` + запуск процесса + полный bootstrap, а смена мостов вообще не триггерит перезапуск сессии (см. §9.4).

Настоящее «умное переключение» реализовано в двух других подсистемах: **Goida** — пул внешних VLESS-узлов с периодическим пробингом (TCP → реальный `TestConnection` через native Xray), автопереключением по порогу `AutoSwitchLatencyMs = 3000` и failover по факту провала проверки exit-IP; и **OpenFlux** — туннель через документ Яндекс.Диска, где hot-swap сделан **без** остановки UI: перезапускается только процесс `openflux.exe`, а прокси/TUN продолжают работать (цикл `WaitSession()`/`ConsumeRestartRequest()`).

---

## 1. Карта кода: Tor реализован ДВА раза

Ключевой архитектурный факт: проект `InvisibleGorilla-XRay` (Windows/WPF) **не ссылается** на `InvisibleGorilla.Core` — в csproj нет ни одного `ProjectReference` (`InvisibleGorilla-XRay\InvisibleGorilla-XRay.csproj:49-65` содержит только `PackageReference`). Ссылку на Core имеют Android, Linux и Mac:

| Проект | Ссылка на Core |
|---|---|
| `InvisibleGorilla-XRay` (Windows/WPF) | нет |
| `InvisibleGorilla-XRay.Android` | да — `InvisibleGorilla-XRay.Android\InvisibleGorilla-XRay.Android.csproj:55` |
| `InvisibleGorilla-XRay.Linux` | да — `InvisibleGorilla-XRay.Linux\InvisibleGorilla-XRay.Linux.csproj:26` |
| `InvisibleGorilla-XRay.Mac` | да — `InvisibleGorilla-XRay.Mac\InvisibleGorilla-XRay.Mac.csproj:24` |

Следствие: файлы Tor существуют в двух копиях с **расхождениями в обе стороны** (проверено `git diff --no-index`):

| Файл | Windows | Core (Android/Linux/Mac) |
|---|---|---|
| `Handlers\Tor\TorManager.cs` | 340 строк | 56 изменённых строк: читает хвост `tor.log` и отдаёт его в текст ошибки (`Exited` — `InvisibleGorilla.Core\Handlers\Tor\TorManager.cs:217`, `ReadLogTail` — `:238`), `WorkingDirectory = Directory.TOR_DATA` (`:145`, `:295`), создаёт каталог лога (`:75`) |
| `Handlers\Tor\MoatClient.cs` | **новее**: 8 с таймаут, прямой коннект через `SocketsHttpHandler{UseProxy=false}` (`InvisibleGorilla-XRay\Handlers\Tor\MoatClient.cs:68-79`), сопоставление транспорта `TransportMatches/NormalizeTransport` (`:313-334`), `ShortError` (`:84-93`) | старее: 20 с таймаут, `HttpClientHandler` (уважает системный прокси), без сопоставления транспорта |
| `Handlers\Tor\TorrcBuilder.cs` | без комментария | +2 строки комментария про кавычки exec-пути (`InvisibleGorilla.Core\Handlers\Tor\TorrcBuilder.cs:131-136`) |
| `Models\TorSettings.cs` | 103 строки | 74 изменённые строки: класс **`TorProfile`** (`InvisibleGorilla.Core\Models\TorSettings.cs:104-171`) — переключаемые Tor-профили в списке серверов |
| `Models\UserSettings.cs` | без Tor-профилей | +`List<TorProfile> TorProfiles` (`:88`), `GetTorProfiles()` (`:198`), `FindTorProfileByPath()` (`:200`) |
| `Models\GoidaProfileSettings.cs` | нет `MaxManualVerifyNodes`, `EnabledListIds` = 1..25, другой `ShouldShowInServerList` | +`MaxManualVerifyNodes = 500` (`InvisibleGorilla.Core\Models\GoidaProfileSettings.cs:18`), `EnabledListIds` = 1..26 (`:39`), `ShouldShowInServerList` учитывает `Enabled` (`:71-76`) |
| `Services\Goida\*` | `GoidaWhitelistStore.cs` есть только здесь; в `GoidaProfileManager.cs` 846 изменённых строк относительно Core | `GoidaOperationLog.cs` есть только здесь; Core-версия новее (в т.ч. логика live-failover) |
| `Handlers\OpenFlux\OpenFluxManager.cs` | 291 изменённая строка относительно Core (отстаёт); `ProbePeer` дёргается «выстрелил-и-забыл» в `ThreadPool` (`InvisibleGorilla-XRay\Handlers\OpenFlux\OpenFluxManager.cs:195`) | `WaitForPeer`, `LastExitIp`/`RememberExitIp`, `KillOrphanSidecars`, рециркуляция сессии через 3 ч |

**Практический вывод для Windows-сборки:** Windows-версия **не умеет** несколько Tor-профилей (`TorProfile` отсутствует), не имеет диагностики по `tor.log` (есть в Core) и имеет устаревший OpenFlux.

---

## 2. Как запускается Tor

### 2.1 Класс и публичная поверхность

`InvisibleGorilla-XRay\Handlers\Tor\TorManager.cs` (Windows) / `InvisibleGorilla.Core\Handlers\Tor\TorManager.cs` (Core):

| Элемент | Строка (Windows) | Значение |
|---|---|---|
| `class TorManager` (sealed) | 32 | один экземпляр на процесс: `InvisibleGorillaXRayCore` создаёт `private readonly TorManager torManager = new TorManager()` — `InvisibleGorilla-XRay\Core\InvisibleGorillaXRayCore.cs:41` |
| `Process torProcess` + `object sync` | 36-37 | примитивная блокировка вокруг процесса/статуса |
| `int BootstrapPercent` | 39 | публичное свойство, обновляется на каждом опросе |
| `string StatusSummary` | 40 | `"Off"` / `"Starting"` / `"Connected"` / `"{percent}% {summary}"` |
| `bool IsRunning` | 41-50 | `torProcess != null && !torProcess.HasExited` |
| `bool IsAvailable` | 52 | `File.Exists(Path.TOR_EXE)` |
| `Status Start(TorSettings settings, int maxWaitMs = 90000)` | 54 | **блокирующий** вызов на 90 с по умолчанию |
| `void Stop()` | 112-134 | `Kill()` + `WaitForExit(4000)`, `Dispose`, сброс статуса |
| `BridgeCheckResult CheckBridge(BridgeType, string bridgeLine, int timeoutMs = 60000)` | 218 | отдельный «мусорный» инстанс tor для проверки моста |
| `BridgeCheckResult` | 14-25 | `Success`, `LatencyMs`, `Message`; `Ok(ms)`, `Fail(msg)` |

### 2.2 Пути и порты

| Что | Значение | Источник |
|---|---|---|
| `Directory.TOR` | `./Tor` | `InvisibleGorilla-XRay\Values\Directory.cs:12` |
| `Directory.TOR_DATA` | `./Tor/Data` | `:14` |
| `Path.TOR_EXE` | `./Tor/tor.exe` | `InvisibleGorilla-XRay\Values\Path.cs:17` |
| DataDirectory сессии | `./Tor/Data/session` | `TorManager.cs:67` |
| Cookie-файл | `./Tor/Data/session/control_auth_cookie` | `:68` |
| torrc | `./Tor/Data/torrc` | `:69` |
| Лог tor | `./Tor/Data/tor.log` | `:70` |
| SocksPort по умолчанию | `9250` | `Models\TorSettings.cs:61` |
| ControlPort по умолчанию | `9251` | `:62` |
| Порт проверки моста | `19250` (+1 для control), поиск свободного в диапазоне 200 портов | `TorManager.cs:223-224`, `330-338` |
| SocksTimeout в torrc | `60` | `Handlers\Tor\TorrcBuilder.cs:34` |

Комментарий в модели объясняет выбор нестандартных портов: чтобы не конфликтовать с системным Tor/Tor Browser на 9050/9051 (`Models\TorSettings.cs:37-39`).

### 2.3 Последовательность запуска (`Start`)

1. `settings == null` → `ERROR/CANT_CONNECT "Tor settings are missing."` (55-57).
2. `!IsAvailable` → ошибка с путём к бинарю (59-61).
3. **`Stop()`** — глушение предыдущего процесса (63).
4. Вычисление портов/каталогов (65-70).
5. `Directory.CreateDirectory(dataDir)`; **удаление старого cookie-файла** (74-76).
6. `settings.GetBridgeLines()` → `TorrcBuilder.Build(...)` → запись `torrc` (78-82).
7. `DiagnosticLog.Write` с параметрами (84).
8. `StartProcess(torrcPath)` (86): `ProcessStartInfo{ FileName = ./Tor/tor.exe, Arguments = -f "<torrc>", WorkingDirectory = ./Tor, UseShellExecute=false, RedirectStandardOutput/Error=true, CreateNoWindow=true }` (136-147). stdout/stderr построчно уходят в диагностический лог (`TorManager.out` / `.err`, 150-151).
9. `StatusSummary="Starting"`, `BootstrapPercent=0` (89-90).
10. `WaitForBootstrap(controlPort, cookieFile, maxWaitMs)` (92). При неуспехе — `Stop()` и возврат ошибки (93-97).
11. Успех: `StatusSummary="Connected"`, `BootstrapPercent=100`, `Status(SUCCESS, SUCCESS, socksPort)` (99-102).
12. Любое исключение → `DiagnosticLog.WriteException` + `Stop()` + `ERROR/CANT_CONNECT "Tor error: ..."` (104-109).

### 2.4 Ожидание bootstrap (двухфазное)

`WaitForBootstrap` (`TorManager.cs:166-212`), интервал опроса — 1000 мс (`:169`):

* **Фаза 1** (172-182): ждём, пока `IsPortActive(controlPort)` **и** существует cookie-файл; если процесс умер — `"Tor process exited during startup. Check tor.log."` (175).
* **Фаза 2** (185-208): на каждой итерации создаётся `TorControlClient`, `Connect(controlPort, cookieFile)`, `GetBootstrapPercent(out summary)`; при `percent >= 100` — успех (194-201).
* Таймаут: `"Tor did not finish bootstrapping within {N}s (reached {BootstrapPercent}%). Try different bridges."` (210-211).
* `IsPortActive` — TCP-connect на `127.0.0.1:port` с ожиданием 500 мс (311-328).

**Важно:** проценты bootstrap и `StatusSummary` **нигде не читаются за пределами `TorManager`** (`BootstrapPercent`/`StatusSummary` не встречаются в UI-коде — проверено grep по всему репозиторию; единственные внешние обращения — `torManager.IsAvailable`: `InvisibleGorilla-XRay\Windows\SettingsWindow\SettingsWindow.xaml.cs:223,339`, `InvisibleGorilla-XRay.Android\Views\MainView.axaml.cs:812,3855`, `InvisibleGorilla-XRay.Mac\Views\SettingsWindow.axaml.cs:215,452`, и `CheckBridge`). То есть **прогресс bootstrap в UI не показывается — не реализовано**; пользователь видит «подключение» до 90 с без обратной связи.

### 2.5 Control-port клиент

`InvisibleGorilla-XRay\Handlers\Tor\TorControlClient.cs`:

| Метод | Строки | Что делает |
|---|---|---|
| `Connect(port, cookieFilePath, connectTimeoutMs = 5000)` | 19-42 | TCP + `StreamReader/Writer` (ASCII, `\r\n`), Read/WriteTimeout = 8000 мс (30-31) |
| `Authenticate` | 44-60 | `AUTHENTICATE <hex(cookie)>` при наличии файла, иначе `AUTHENTICATE` (безопасность: `CookieAuthentication 1`) |
| `GetBootstrapPercent(out summary)` | 65-93 | `GETINFO status/bootstrap-phase`, парсит `PROGRESS=` и `SUMMARY=` |
| `SignalShutdown()` | 95-105 | `SIGNAL HALT` — **нигде не вызывается** (проверено grep) |

Реализован ровно минимум. **Не реализовано:** `SETCONF`/`GETCONF`, `NEWNYM` (новая цепочка), `GETINFO circuit-status`/`stream-status`/`net/listeners`, `SIGNAL RELOAD`, `+POST`/асинхронные события (`SETEVENTS`), таймауты/статистика трафика.

### 2.6 Генерация torrc (полный список директив)

`InvisibleGorilla-XRay\Handlers\Tor\TorrcBuilder.cs`, метод `Build` (16-55):

```
SocksPort 127.0.0.1:{socksPort}          # :27
ControlPort 127.0.0.1:{controlPort}      # :28
CookieAuthentication 1                    # :29
CookieAuthFile "<cookie>"                 # :30
DataDirectory "<dataDir>"                 # :31
ClientOnly 1                              # :32
AvoidDiskWrites 1                         # :33
SocksTimeout 60                           # :34
Log notice file <path>                    # :44 (или "Log notice stdout", если в пути пробел/кавычка — :41-42)
GeoIPFile "<...>"                         # :48, только если файл существует
GeoIPv6File "<...>"                       # :50, только если файл существует
UseBridges 1                              # :78
ClientTransportPlugin <t> exec <path>     # :89/:92/:95/:98
Bridge <transport> <line>                 # :106 (по строке на мост)
```

Нормализация путей: обратные слэши → прямые (`:40`, `:133`, `:139`). Известная особенность, зафиксированная комментарием: tor парсит `Log ... file` сам и **сохраняет кавычки в имени файла**, поэтому путь с пробелом заменяется на `Log notice stdout` (`:38-44`). Аналогичный комментарий для exec-пути есть в Core (`InvisibleGorilla.Core\Handlers\Tor\TorrcBuilder.cs:131-136`).

### 2.7 Проверка отдельного моста (`CheckBridge`, 218-309)

* Отдельный каталог `./Tor/Data/check` (затирается рекурсивно, 233), torrc — `./Tor/Data/torrc.check` (227), лог — `./Tor/Data/tor-check.log` (228).
* Свободные порты от `19250` (223-224).
* В torrc попадает **ровно одна** строка моста, переданная аргументом (236-240).
* Замер: `Stopwatch` стартует до запуска процесса, останавливается при `percent >= 100` (261, 276-281). Возвращается `LatencyMs` = **время до полного bootstrap**, а не RTT.
* Таймаут по умолчанию 60 000 мс; из UI Windows Settings — по умолчанию (60 с), из ServerWindow — 45 000 мс (`InvisibleGorilla-XRay\Factories\WindowFactory.cs:349`).
* В `finally` процесс убивается (296-308).
* Ошибки: `"Tor binary not bundled."`, `"Failed to start tor for bridge check."`, `"Tor exited before reaching the bridge."`, `"Bridge did not connect within {N}s."`, либо текст исключения.
* **Не реализовано:** проверка всех строк мостов, параллельная проверка нескольких мостов, кэш результатов, автоматический выбор лучшего моста по результатам, повторная проверка по расписанию.

### 2.8 Обработка ошибок и перезапуск

* Ошибки Tor всплывают наверх как `Status(ERROR, CANT_CONNECT, ...)`, и в `InvisibleGorillaXRayCore.Run` превращаются в исключение, которое обрывает подключение: `throw new InvalidOperationException(torStatus.Content?.ToString() ?? "Tor failed to start.")` (`InvisibleGorilla-XRay\Core\InvisibleGorillaXRayCore.cs:183-188`).
* `Stop()` вызывается: перед стартом (63), при неуспехе bootstrap (95), в `catch` (107), при провале старта Xray-ядра (`:252,260,269`), при ошибке TUN (`:291`), по завершении сессии (`:324-328`), в `Stop()` ядра (`:570`) и `Cancel()` (`:582`).
* **Не реализовано:** автоматический повторный запуск Tor, backoff, эскалация «попробовать другой тип моста», перезапуск при падении процесса после успешного bootstrap (мониторинга живости нет — `IsRunning` проверяется только внутри цикла bootstrap), а также kill «осиротевших» `tor.exe` (нет аналога `KillOrphanSidecars`; ср. `InvisibleGorilla.Core\Handlers\OpenFlux\OpenFluxManager.cs:647`, где такая функция есть для OpenFlux). `Processor.StopSystemProcesses` используется только для TUN-процессов (`InvisibleGorilla-XRay\Handlers\Processes\TunnelProcess.cs:59`).
* Windows-версия **не читает `tor.log`** при падении процесса — текст ошибки просто содержит «Check tor.log.» (175, 188). В Core это исправлено: `Exited()` (`InvisibleGorilla.Core\Handlers\Tor\TorManager.cs:217-236`) добавляет последние 12 строк лога (`ReadLogTail`, `:238-272`) и обрезает сообщение до 500 символов.

---

## 3. Какие бинарники Tor используются

### 3.1 Windows, папка `InvisibleGorilla-XRay\Tor\` (фактическое содержимое на диске)

| Файл | Размер, байт | Комментарий |
|---|---|---|
| `tor.exe` | 10 658 816 | демон Tor |
| `lyrebird.exe` | 17 198 080 | pluggable transport (obfs4/meek_lite/webtunnel/snowflake) |
| `conjure-client.exe` | 12 529 664 | **поставляется, но нигде не используется** (см. §5.6) |
| `geoip` | 8 878 171 | для `GeoIPFile` |
| `geoip6` | 11 875 422 | для `GeoIPv6File` |
| `snowflake-client.exe` | **отсутствует** | хотя `Path.SNOWFLAKE_EXE` на него указывает |
| `obfs4proxy.exe` | **отсутствует** | legacy-бинарь |

**Версия бинарей:** PE-метаданные пусты (`FileVersion`/`ProductVersion` = пусто для `tor.exe`, `lyrebird.exe`, `conjure-client.exe` — проверено `Get-Item ...VersionInfo`). Версия определяется только сборочным скриптом: **Tor Expert Bundle 14.5.7** — `build.ps1:109` (`$TorBrowserVersion = "14.5.7"`) и `build-android.ps1:53` (то же значение), URL — `https://archive.torproject.org/tor-package-archive/torbrowser/<version>/tor-expert-bundle-...` (`build.ps1:110`, `:944-945`).

Скачивание/распаковка: `Get-TorBundle` (`build.ps1:930-997`) копирует `tor\tor.exe`, `tor\pluggable_transports\{lyrebird.exe, snowflake-client.exe, conjure-client.exe}` (`:962-972`), `data\{geoip, geoip6}` (`:974-979`) и все DLL рядом с tor.exe (`:981-983`). То есть сборка **пытается** положить `snowflake-client.exe`, но в текущем дереве/во всех publish-каталогах (`publish-windows\Tor`, `publish-windows-v3.6.54\Tor`, `InvisibleGorilla-XRay-Windows-win-x64\Tor`) его нет — состав везде: `conjure-client.exe, geoip, geoip6, lyrebird.exe, tor.exe`. Если bundle не содержит `snowflake-client.exe`, `Copy-Item` молча пропускается (`if (Test-Path $bin)`, `:969`), и приложение живёт в режиме «snowflake через lyrebird» (`TorrcBuilder.cs:80-99`).

### 3.2 Android (`Assets\Runtime\`)

`build-android.ps1:798-846`: из того же bundle берутся `tor/libTor.so` → `libTor.so` и `tor/pluggable_transports/lyrebird` → `liblyrebird.so` (переименование обязательно: Android разрешает исполнение только из `nativeLibraryDir`). Фактические файлы:

| Файл | arm64-v8a | x86_64 |
|---|---|---|
| `libTor.so` | 10 495 120 | 11 075 464 |
| `liblyrebird.so` | 16 672 352 | 18 663 072 |
| `libopenflux.so` | 18 397 648 | 19 836 816 |
| `libXRayCore.so` | 30 291 816 | 32 344 520 |

Подключены как `AndroidNativeLibrary` (`InvisibleGorilla-XRay.Android\InvisibleGorilla-XRay.Android.csproj:117-153` — Tor и lyrebird, условия `Exists(...)`). Пути подменяются в рантайме: `AndroidAppStorage.ConfigureTorBinaries` берёт `ApplicationInfo.NativeLibraryDir` и вызывает `Path.OverrideTorBinaries(torExe: libTor.so, pluggableTransportExe: liblyrebird.so, geoip: null, geoip6: null)` (`InvisibleGorilla-XRay.Android\Platforms\AndroidAppStorage.cs:35-61`); geoip на Android не поставляется, и `TorrcBuilder` просто не пишет `GeoIPFile` (`TorrcBuilder.cs:47-50`). Корень данных Android: `%LocalAppData%\InvisibleGorilla-XRay` (`AndroidAppStorage.cs:128-135`).

### 3.3 Платформенные пути в Core

`InvisibleGorilla.Core\Values\Path.cs`: `ExeName()` добавляет `.exe` только на Windows; `TOR_EXE`/`LYREBIRD_EXE`/`OBFS4_EXE`/`SNOWFLAKE_EXE`/`CONJURE_EXE` учтены; `PLUGGABLE_TRANSPORT_EXE = lyrebird ?? obfs4proxy`; `Directory.TOR` — read-only runtime-root, `Directory.TOR_DATA` — writable data-root (`InvisibleGorilla.Core\Values\Directory.cs`), отсюда и `WorkingDirectory = Directory.TOR_DATA` в Core-`TorManager`.

### 3.4 Библиотека Xray

`InvisibleGorilla-XRay\Libraries\`: `XRayCore.dll` (29 088 768) и `libXRayCore.so` (32 141 960). Windows-обёртка: `InvisibleGorilla-XRay\Core\XRayCoreWrapper.cs` — P/Invoke `GetConfigFormat`, `LoadConfig`, `StartServer`, `StopServer`, `TestConnection` из `Path.XRAY_CORE_DLL`.

---

## 4. Модели настроек Tor

### 4.1 `InvisibleGorilla-XRay\Models\TorSettings.cs` (Windows)

```csharp
public enum TorMode   { ONLY_TOR, XRAY_OVER_TOR }                       // :14
public enum BridgeType{ NONE, OBFS4, SNOWFLAKE, MEEK_AZURE, WEBTUNNEL } // :24
```

| Поле | Тип | Значение по умолчанию | Смысл | Строка |
|---|---|---|---|---|
| `Enabled` | bool | `false` | включён ли Tor для текущего подключения | 32, 59 |
| `Mode` | `TorMode` | `ONLY_TOR` | весь трафик через Tor **или** Xray-сервер поверх Tor | 35, 60 |
| `SocksPort` | int | `9250` | локальный SOCKS-порт tor | 40, 61 |
| `ControlPort` | int | `9251` | порт управления tor (в UI не редактируется) | 43, 62 |
| `BridgeType` | `BridgeType` | `NONE` | семейство транспорта | 46, 63 |
| `BridgeLines` | `List<string>` | `[]` | строки мостов (вручную/из moat) | 50, 64 |
| `LastLatencyMs` | int | `-1` | время последней успешной проверки моста, мс; `-1` = не проверялось | 55, 65 |

Маркеры-«псевдоконфиги» (виртуальные записи в списке серверов, файла на диске нет):

| Подсистема | Маркер | Источник |
|---|---|---|
| Tor | `./Configs/__TorBridges__.json` | `InvisibleGorilla-XRay\Services\Tor\TorProfilePaths.cs:8-9` |
| Goida | `./Configs/__GoidaProfile__.json` | `InvisibleGorilla-XRay\Services\Goida\GoidaProfilePaths.cs:8-9` |
| OpenFlux | `./Configs/__OpenFluxProfile__.json` | `InvisibleGorilla-XRay\Services\OpenFlux\OpenFluxProfilePaths.cs:8-9` |

Геттеры с санитизацией: `GetSocksPort()` → `SocksPort > 0 ? SocksPort : 9250` (72), `GetControlPort()` → `... : 9251` (74), `GetBridgeLines()` — фильтрация пустых и `Trim()` (78-87), `Clone()` (89-101). Атрибут `[DefaultValue(-1)]` на `LastLatencyMs` (54). Все поля помечены `[JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]` — при десериализации отсутствующие поля заполняются значениями из конструктора.

### 4.2 Core-версия и `TorProfile`

`InvisibleGorilla.Core\Models\TorSettings.cs` идентичен по полям и дефолтам, но дополнительно содержит **`TorProfile`** (104-171): `Name`, `ConfigPath`, `Mode`, `BridgeType` (по умолчанию `OBFS4`!), `BridgeLines`, `SocksPort = 9250`, `LastLatencyMs = -1`. Профиль представлен в списке серверов минимальным Tor-only Xray-конфигом.

### 4.3 Как это сохраняется в `Settings.json`

* Путь: `Path.USER_SETTINGS = $"{Directory.ROOT}/Settings.json"` → `.\Settings.json` (`InvisibleGorilla-XRay\Values\Path.cs:7`, `Values\Directory.cs:5`).
* `UserSettings.Tor` — вложенный объект (`InvisibleGorilla-XRay\Models\UserSettings.cs:83-84`), `Goida` (:86-87), `OpenFlux` (:89-90), `OpenFluxProfiles` (:92-93), `TorProfiles` — **только Core** (`InvisibleGorilla.Core\Models\UserSettings.cs:88`).
* Загрузка: `SettingsHandler` ctor → `LoadUserSettings()` (`InvisibleGorilla-XRay\Handlers\SettingsHandler.cs:20, 82-107`): если файла нет или JSON невалиден — берётся `new UserSettings()`; затем `Tor = (settings.Tor ?? new TorSettings()).Clone()` (101).
* Сохранение: `SaveUserSettings()` → `JsonConvert.SerializeObject(userSettings)` + `File.WriteAllText` (`:109-113`) — **не атомарно** (нет temp+rename), может быть частично записан при падении/выключении питания. Версионирования схемы и миграций нет.
* `UpdateUserSettings` копирует поля, включая `Tor`, `Goida`, `OpenFlux`, `OpenFluxProfiles` через `Clone()` (`:45-48`), после чего всегда пишет файл (51).
* Windows не сохраняет Tor-профили (`TorProfiles` в модели нет) — их просто негде хранить.

---

## 5. Мосты

### 5.1 Поддерживаемые типы и их torrc-обвязка

| `BridgeType` | `ClientTransportPlugin` | Транспорт в `Bridge` | Встроенные линии (`DefaultBridges.ForType`) | Ручной ввод | moat |
|---|---|---|---|---|---|
| `NONE` | — (`UseBridges` не пишется, `AppendBridges` выходит на `:59-60`) | — | — | — | — |
| `OBFS4` | `obfs4 exec lyrebird` (`TorrcBuilder.cs:89`) | `obfs4` | **7 линий** (`DefaultBridges.cs:51-60`) | да | да |
| `SNOWFLAKE` | `snowflake exec lyrebird` (fallback, `:97-99`, `:84`) | `snowflake` | 1 линия (`:16-22`), плюс `SnowflakeSecondary` (`:24-30`), `SnowflakeAmp` (`:34-41`) | да | да |
| `MEEK_AZURE` | `meek_lite exec lyrebird` (`:91-92`) | `meek_lite` | 1 линия (`:44-47`) | да | да |
| `WEBTUNNEL` | `webtunnel exec lyrebird` (`:94-95`) | `webtunnel` | **пусто** (`ForType` не обрабатывает webtunnel — `:62-75`) | да | да (через `/circumvention/settings`) |
| `CONJURE` | **не реализовано** (нет enum-значения; `conjure-client.exe` не используется) | — | — | — | — |

Нормализация строки моста (`NormalizeBridgeLine`, `TorrcBuilder.cs:110-129`): если строка уже начинается с имени транспорта или с `Bridge `, префикс не добавляется; иначе префиксуется `{transport} `. Мосты всегда пишутся как `Bridge ...` (`:106`).

Если пользователь выбрал транспорт, но список строк пуст и встроенных линий нет (единственный случай — `WEBTUNNEL`), `AppendBridges` возвращается **до** `UseBridges 1` (`:72-76`) — то есть **Tor молча уйдёт в прямое подключение без мостов**, без предупреждения в UI. Это заметный риск (§11).

### 5.2 Встроенный список (`Handlers\Tor\DefaultBridges.cs`)

* `Snowflake` (`:16-22`) — `snowflake 192.0.2.3:80 2B28...A72 fingerprint=... url=https://1098762253.rsc.cdn77.org/ fronts=www.cdn77.com,www.phpmyadmin.net ice=stun:stun.l.google.com:19302,stun:stun.antisip.com:3478 utls-imitate=hellorandomizedalpn`. Комментарий над константой: «Эта линия забутстрапилась в цензурируемой мобильной сети. Пара Orbot от марта 2026 (datapacket front, extra STUN) встала на 10 %, поэтому по умолчанию не она» (`:14-15`).
* `SnowflakeSecondary` (`:24-30`) — datapacket-пара, оставлена как альтернатива.
* `SnowflakeAmp` (`:34-41`) — rendezvous через AMP-cache Google (`ampcache=https://cdn.ampproject.org/ front=www.google.com`).
* `MeekAzure` (`:44-47`) — `meek_lite 192.0.2.20:80 9770...9CE url=https://1603026938.rsc.cdn77.org/ front=www.phpmyadmin.net utls=HelloRandomizedALPN`.
* `Obfs4` (`:51-60`) — 7 строк из `pt_config.json` Tor Browser (Dec 2025), с комментарием про `cert=` без пробелов внутри.
* Комментарий класса (`:8-11`) прямо признаёт: obfs4-линии «rotate periodically», и если встроенные перестанут работать — пользователь может получить свежие через moat.

Android дополнительно «ремонтирует» встроенные линии: `NeedsBuiltinTorBridges` (`InvisibleGorilla-XRay.Android\Views\MainView.axaml.cs:3704-3717`) считает набор «сломанным», если тип `NONE`, список пуст, есть подозрительная строка (`BridgeLineLooksBroken`, `:3719-3733` — эвристика по `cert=`), или если snowflake-строка содержит `datapacket.com`; тогда подставляется текущая пара `DefaultBridges.Snowflake`. В Windows такого «ремонта» **нет**.

### 5.3 Получение мостов из BridgeDB / moat / rdsys

`InvisibleGorilla-XRay\Handlers\Tor\MoatClient.cs`:

| Возможность | Реализация |
|---|---|
| Базовый URL | `https://bridges.torproject.org/moat` (`:40`) |
| Тип контента | `application/vnd.api+json` (`:41`) |
| Прокси | Конструктор `MoatClient(string socksProxy = null)` (`:45-48`); в UI объект создаётся **без** прокси (`SettingsWindow.xaml.cs:417, 507, 524, 568`) — то есть запрос идёт напрямую, минуя системный прокси (`SocketsHttpHandler{UseProxy=false, ConnectTimeout=6s}`, `MoatClient.cs:68-79`, общий `Timeout = 8s`, `:79`) |
| CAPTCHA-запрос | `RequestChallengeAsync("obfs4")` → `POST /fetch` с `type=client-transports` (`:98-158`) |
| Отправка решения | `SubmitSolutionAsync` → `POST /check`, поле `qrcode=false`, разбор `errors[0].detail` и `data[0].bridges` (`:163-233`) |
| «Ask Tor» (Smart Connect) | `GetCircumventionSettingsAsync(country, preferredTransport)` → `POST /circumvention/settings`, при отсутствии рекомендации — `/circumvention/builtin` (`:241-278`, `:340-366`) |
| Встроенные мосты от сервиса | `GetBuiltinBridgesAsync(transport)` (`:340-366`) |
| Гарантированный fallback | `ApplyLocalBridgeFallback` — встроенный список при неудаче сети (`SettingsWindow.xaml.cs:436-452`) |

Прочие каналы (`Handlers\Tor\BridgeRequestLinks.cs`): `EmailAddress = bridges@torproject.org`, `TelegramBot = https://t.me/GetBridgesBot`, `HttpsBridges = https://bridges.torproject.org/options`; `BuildEmailUrl("obfs4")` формирует `mailto:` с `subject=get bridges` и `body=get transport obfs4` (`:21-27`). Автоматического чтения почты/Telegram нет — только открытие ссылок (`SettingsWindow.xaml.cs:466-476`).

**rdsys напрямую не реализовано** — используется только moat-эндпоинт `bridges.torproject.org/moat` (это и есть прокси к rdsys).

**Автоматического обновления списка мостов нет:** и `/circumvention/*`, и CAPTCHA вызываются исключительно по кнопкам (`Ответ Tor` — `OnAskTorClick`, `Получить мосты` — `OnFetchMoatClick`, `Отправить капчу` — `OnSubmitCaptchaClick`; `SettingsWindow.xaml.cs:360-374, 498-558, 560-584`). Нет таймера, нет попытки обновить мосты после неудачного bootstrap, нет «возраста» встроенных линий.

Классификация вставленного текста (в т.ч. строк мостов из письма/бота) — `Handlers\SmartInput\PastedInputClassifier.cs` и `SmartImportService.MergeBridges` (`InvisibleGorilla-XRay\Handlers\SmartInput\SmartImportService.cs:129-154`): дедупликация, `Trim`, установка `BridgeType` и принудительное `Enabled = true`. На Windows вставка мостов сразу переключает `CurrentConfigPath` на Tor-маркер (`InvisibleGorilla-XRay\Factories\WindowFactory.cs:378-386`).

### 5.4 Проверка мостов

* Единственная проверка — `TorManager.CheckBridge` (§2.7), кнопка `Check` в настройках Windows (`SettingsWindow.xaml.cs:337-358`) и в Android (`MainView.axaml.cs:427, 591, 1008-1018`), а также тест в списке серверов (`ServerWindow` → `probeTorLatency`, `WindowFactory.cs:345-356`).
* Проверяется **только первая** строка списка: `lines.FirstOrDefault()` — Windows `SettingsWindow.xaml.cs:347`, Windows `WindowFactory.cs:348`, Android `MainView.axaml.cs:3866`.
* Кнопка в Windows-настройках **не сохраняет** результат: `OnCheckBridgeClick` только пишет текст статуса (`:354-356`). Сохраняют: `ProbeTorLatency` (`WindowFactory.cs:353-354` → `TorSettings.LastLatencyMs` + `UpdateUserSettings`) и Android `ApplyTorCheckResult` (`MainView.axaml.cs:3879-3897` → `TorProfile.LastLatencyMs` или `TorSettings.LastLatencyMs` + `SetConfigAvailability`).
* **Не реализовано:** тестирование всех строк, тест «набора» мостов как группы, кэш «последний рабочий набор», проверка мостов в фоне.

### 5.5 Автоматический перебор и переключение мостов

* **Автоматический перебор мостов при неудачном подключении — не реализовано.** `TorManager.Start` делает один запуск с одним torrc; при таймауте возвращает ошибку с советом «Try different bridges» (`TorManager.cs:210-211`), и подключение обрывается (`InvisibleGorillaXRayCore.cs:183-188`).
* Если в torrc попало **несколько** `Bridge`-строк (например, все 7 obfs4-линий из встроенного набора — они пишутся все, `TorrcBuilder.cs:102-107`), то перебор выполняет **сам tor** (несколько bridge-кандидатов в качестве entry guards) — это поведение демона, а не логика приложения; приложение не управляет этим, не знает, какой мост выбран, и не может исключить «плохой» мост.
* **Переключение мостов «на лету» (без полного перезапуска) — не реализовано.** Доказательства:
  * `TorControlClient` умеет только `AUTHENTICATE`, `GETINFO status/bootstrap-phase`, `SIGNAL HALT` — нет `SETCONF Bridge ...`, нет `NEWNYM`, нет `SIGNAL RELOAD` (§2.5).
  * `TorManager.Start` всегда начинается с `Stop()` (`:63`) и заново проходит полный bootstrap.
  * Смена настроек Tor в окне настроек **не перезапускает активную сессию**: `WindowFactory.UpdateUserSettings` перезапускает мод/протокол/порты/DNS/system-proxy/UDP, но **не** Tor (`InvisibleGorilla-XRay\Factories\WindowFactory.cs:206-224`), а `ShowTorEditorOnly`/`OpenTorSettings` после закрытия диалога лишь перезагружает список серверов (`WindowFactory.cs:336-343`). Android в этом смысле тоже требует ручного перезапуска, но там есть профили мостов и переключение между ними как между серверами (§9.5).
  * Косвенное «переключение» возможно только как полный rerun: `MainWindow.TryRerun` (`:613-625`) = `onDisableMode()` + `onStopServer()` и затем новый `Run`.

### 5.6 Прочее по бинарям PT

* `Path.SNOWFLAKE_EXE = ./Tor/snowflake-client.exe` (`InvisibleGorilla-XRay\Values\Path.cs:20`) — файла нет, поэтому `TorrcBuilder` использует `PLUGGABLE_TRANSPORT_EXE` (= lyrebird, `Path.cs:28-30`) для snowflake (`TorrcBuilder.cs:80-84`, `:97-99`).
* `Path.CONJURE_EXE` объявлен (`Path.cs:21`, Core `Path.cs:77`), но **не используется нигде** (grep по `CONJURE` даёт только объявление) — 12.5 МБ мёртвого веса, тип моста отсутствует.

---

## 6. Latency и качество

### 6.1 Что означает `LastLatencyMs` для Tor

`LastLatencyMs` — это **время до 100 % bootstrap** «мусорного» tor-инстанса, замеренное `Stopwatch` от `check.Start()` (`TorManager.cs:261-280`), а **не** RTT и не ping. Значения-маркеры общие для проекта: `Availability.NOT_CHECKED = -3`, `ERROR = -2`, `TIMEOUT = -1` (`InvisibleGorilla-XRay\Values\Availability.cs:5-7`); для Tor по умолчанию `-1` (`TorSettings.cs:65`).

### 6.2 Где измеряется и где показывается

| Место | Что делает | Строки |
|---|---|---|
| Windows, окно настроек | кнопка `Check`: `CheckBridge(type, firstLine)` (60 с), результат только в тексте статуса | `SettingsWindow.xaml.cs:337-358` |
| Windows, список серверов | `probeTorLatency`: `CheckBridge(type, firstLine, 45000)`, при успехе пишет `TorSettings.LastLatencyMs` и сохраняет | `WindowFactory.cs:345-356` |
| Android, редактор Tor | `CheckTorProfileAsync`, кнопка `CheckBridgeButton`, результат → `TorProfile.LastLatencyMs` / `TorSettings.LastLatencyMs` + `SetConfigAvailability` | `MainView.axaml.cs:427, 591, 3853-3897` |
| Список серверов (Windows) | карточка Tor: подпись = `hint · count мостов`, availability = `LastLatencyMs` | `Managers\Initializers\HandlersInitializer.cs:90-112` |
| Список серверов (Android) | `hint`: `snowflake`/`snowflake amp`/`obfs4`/`meek`/`webtunnel`; availability = `LastLatencyMs` | `AndroidHandlersInitializer.cs:80-106` |
| Android, подпись профиля | `Tor bridge (obfs4) · 1234 ms` | `MainView.axaml.cs:3808-3814` |

**Периодического мониторинга Tor нет** (нет таймера, нет фонового перепроба). Значение живёт до следующего ручного теста, поэтому в списке может отображаться устаревшая «доступность».

### 6.3 Периодический мониторинг, который всё-таки есть (не Tor-специфичный)

* В Windows `MainWindow` работает `connectionInfoTimer` (интервал 20 с; 8 с для Goida-профиля; 45 с для OpenFlux — `MainWindow.xaml.cs:897-903`), дергающий `RefreshConnectionInfo` (`:1054-1167`), который каждые N секунд делает запрос к `ipinfo.io/json`, `ifconfig.co/json`, `api.ipify.org`, `ipwho.is`, `ipapi.co/json` (`Services\ConnectionInfoService.cs:62-69`) **через локальный прокси** (`ConnectionProbe.BuildExitProxy`, `Services\ConnectionProbe.cs:29-40`; для TUN+credentials — `ActiveTunnelSession.BuildProbeProxy`, `Services\ActiveTunnelSession.cs:46-67`).
* В Tor-only режиме xray слушает локальный порт и выходит через Tor, поэтому проверка показывает IP выхода Tor; в TUN-режиме проба идёт «напрямую» (перехват на сетевом уровне).
* Ошибка пробы не считается обрывом: до 4 ретраев с задержкой 4 с (`MainWindow.xaml.cs:48, 1122-1137`), и только затем — `RegisterTunnelFailure` (`:1197-1248`): порог 2 подряд неудачи (1 для Goida), grace-период 15 с после запуска и 20/25 с после переключения Goida.
* `ConnectionProbe.DescribeMode` формирует человекочитаемый путь, включая Tor: `"Tor (bridge obfs4) · Proxy/SOCKS"`, `"VLESS over Tor (bridge snowflake) · TUN/SOCKS"` (`Services\ConnectionProbe.cs:47-79, 122-132`). В UI Tor-подпись включается флагом `torActive` (`MainWindow.xaml.cs:1345-1346`, локализация `Lang.ConnectionInfo.ProtectedTor`).
* Android: то же самое, интервалы/ретраи — `MainView.axaml.cs` (в т.ч. `api.ipify.org` через SOCKS для OpenFlux, `:4269-4283`) и `MainView.Goida.cs:771-791`.

**Автопереключение по latency для Tor не реализовано** (нет альтернатив, между которыми переключаться, кроме мостов внутри одного torrc). Автопереключение есть только для Goida (§7) и ручное — для OpenFlux-профилей/URL.

---

## 7. Goida: «умное переключение» по задержке

Исходники (Windows — форк; Core — используемая Android/Linux/Mac версия): `InvisibleGorilla.Core\Services\Goida\*`, `InvisibleGorilla-XRay\Services\Goida\*`, модели `InvisibleGorilla.Core\Models\Goida*.cs`, обработчик `InvisibleGorilla.Core\Handlers\GoidaProfileHandler.cs` (`InvisibleGorilla-XRay\Handlers\GoidaProfileHandler.cs`).

### 7.1 Модель настроек (`InvisibleGorilla.Core\Models\GoidaProfileSettings.cs`)

| Константа | Значение | Строка |
|---|---|---|
| `DefaultRefreshMinutes` | `9` | 10 |
| `DefaultProbeSeconds` | `60` | 11 |
| `DefaultAutoSwitchLatencyMs` | **`3000`** | 12 |
| `DefaultAutoPoolSize` | `100` | 13 |
| `MaxVerifiedNodes` | `100` | 14 |
| `MaxManualVerifyNodes` | `500` (только Core) | 18 |

| Поле | Тип | По умолчанию | Смысл | Строка |
|---|---|---|---|---|
| `Enabled` | bool | `false` | включена ли подсистема | 21 |
| `AutoSwitchOnFly` | bool | **`true`** | разрешено ли переключать узел на ходу | 24 |
| `RefreshIntervalMinutes` | int | `9` | период обновления списков узлов | 27 |
| `ProbeIntervalSeconds` | int | `60` (в цикле — не менее 15) | период пробинга | 30 |
| `AutoSwitchLatencyMs` | int | **`3000`** | порог «плохой задержки» и одновременно отсечка для VLESS-проверки | 33 |
| `SelectionMode` | `GoidaSelectionMode` | `AutoBest` | режим выбора | 36 |
| `EnabledListIds` | `List<int>` | `1..26` (Windows-копия: `1..25`) | какие списки источников использовать | 39 |
| `ActiveNodeId` | string | `""` | текущий узел | 42 |
| `PinnedNodeId` | string | `""` | закреплённый узел для `ManualFixed` | 45 |
| `ManualPoolNodeIds` | `List<string>` | `[]` | ручной пул для `ManualPool` | 48 |
| `LastRefreshUtc` | DateTime | `default` | когда последний раз обновляли списки | 51 |

`GoidaSelectionMode` — `AutoBest`, `ManualFixed`, `ManualPool` (`InvisibleGorilla.Core\Models\GoidaSelectionMode.cs`).

`GoidaNode` (`InvisibleGorilla.Core\Models\GoidaNode.cs`): `Id`, `ListId`, `DisplayName`, `Endpoint`, `ConfigPath`, `Country`, `Protocol`, `LatencyMs` (по умолчанию `Availability.NOT_CHECKED = -3`, `:30`), `Status` (`Unknown`), `LastCheckedUtc`, `VlessVerified`; `GoidaNodeStatus` = `Unknown/Ok/Timeout/Error`.

### 7.2 Как реально измеряется задержка

Двухфазная схема (`InvisibleGorilla.Core\Services\Goida\GoidaHealthMonitor.cs`):

1. **TCP-фаза** — `GoidaEndpointProbe.ProbeTcp(endpoint, timeoutMs)` (`Services\Goida\GoidaEndpointProbe.cs:15-40`): обычный `TcpClient.BeginConnect` на host:port из `GoidaNode.Endpoint`, таймаут по умолчанию 1500 мс, в мониторе — `FastProbeTimeoutMs = 2500` (`GoidaHealthMonitor.cs:14`); результат — миллисекунды, либо `Availability.TIMEOUT`/`ERROR`. Параллелизм — `FastProbeConcurrency = 16` (`:13`).
2. **VLESS-фаза (реальный туннель)** — `RunNativeProbeWithDeadline` → `monitor.TestNodeNative(node)` (`GoidaProfileManager.cs:342`), а он — через `GoidaConnectionTest.CreateFromConfigPath(core.LoadConfig, core.Test)` (`Managers\Initializers\HandlersInitializer.cs:363`): загрузка конфига узла и `XRayCoreWrapper.TestConnection(configJson, testPort)` (`Services\Goida\GoidaConnectionTest.cs:12-44`), т.е. **настоящая проверка соединения native-ядром Xray** (не ping и не TCP). Таймаут — `NativeProbeTimeoutMs = 7000` (`GoidaHealthMonitor.cs:15`), выполнение сериализовано глобальным `SemaphoreSlim NativeTestGate = new(1,1)` (`:18`), потому что native-ядро одно.
3. Отбор в VLESS-фазу: только узлы с TCP-latency ≤ `MaxTcpLatencyForVlessMs` (= `AutoSwitchLatencyMs`, `GoidaProfileManager.cs:645`) и ещё не `VlessVerified` (`GoidaHealthMonitor.cs:132-151`).

### 7.3 Алгоритм выбора и пороги

`InvisibleGorilla.Core\Services\Goida\GoidaActiveSelector.cs`:

* `SelectBestNode` (`:10-33`): кандидаты фильтруются по `EnabledListIds` и режиму (`FilterCandidates`, `:80-105`), затем берутся только `VlessVerified && Status == Ok && LatencyMs >= 0`, сортировка по `LatencyMs` (возрастание). `ManualFixed` → узел с `Id == PinnedNodeId`; `ManualPool` → `SelectFromPool` (текущий активный, иначе первый здоровый из `ManualPoolNodeIds`); иначе — `healthy[0]`.
* `ShouldAutoSwitch` (`:50-78`) — условия «да»: `Enabled && AutoSwitchOnFly`; режим `AutoBest` или `ManualPool`; есть `bestNode`; `currentNode == null` **или** `currentNode.Status ∈ {Timeout, Error}` **или** (`currentNode.LatencyMs < 0 || > AutoSwitchLatencyMs`) и `bestNode.LatencyMs < currentNode.LatencyMs`. Это гистерезис «только если лучший строго лучше».
* `SelectNextFailoverNode` (`:35-48`): для `ManualPool` — `SelectNextInOrderedPool` (строгий порядок пула, пропуск исключённых и нездоровых, `:176-218`); иначе `SelectNextInAutoRoundRobin` (`:137-174`) — сортировка по latency/`ListId`/имени и круговая ротация от текущего индекса, с исключением уже провалившихся.

### 7.4 Интервалы и жизненный цикл

Фоновый цикл `InvisibleGorilla.Core\Services\Goida\GoidaProfileManager.cs:780-841` (запускается `GoidaProfileHandler.StartBackground()`, вызывается из `HandlersInitializer.cs:55`):

```
тик каждые 5 с (Task.Delay(5s))                                     # :799, :823
refreshInterval = max(1, RefreshIntervalMinutes) минут              # :803-804
probeInterval   = max(15, ProbeIntervalSeconds) секунд              # :805-806
при старте: если LastRefreshUtc == default → RefreshListsAsync()    # :781-784
если !Enabled → просто ждём 5 с                                     # :797-801
если прошло ≥ refreshInterval → RefreshListsAsync()                 # :808-812
если !probingSuspended && canProbe() && есть узлы && прошло ≥ probeInterval
        → ProbeAsync()                                              # :814-821
любое исключение → пауза 30 с                                       # :829-839
```

`ProbeAsync` (`:602-697`): защита `Interlocked.CompareExchange(ref probeInProgress, 1, 0)` — одновременно только один прогон (`:610-611`); набор целей — `FilterProbeTargets` (`:624-628`); **при активной VPN-сессии нативная VLESS-проверка отключается** (`MaxVlessTests = 0`, `EarlyStopOkCount = 0` — `:635-644`), чтобы не мешать живому туннелю; иначе для ручного прогона лимит `MaxManualVerifyNodes = 500`, для фонового — 8 проверок с ранней остановкой после 3 успехов (`:637-644`). На время native-проверки туннель **приостанавливается** через `pauseNativeForTest`/`resumeNativeAfterTest` (`:649`, `:684-687`), что на Windows реализовано как полный `TryPauseForNativeTest`/`TryResumeAfterNativeTest` (`MainWindow.xaml.cs:645-679`).

Автопереключение вызывается дважды: по первому успешному VLESS-тесту (`OnFirstVlessOk → EvaluateAutoSwitchAsync`, `:646`) и после прогона (`:665-666`). `EvaluateAutoSwitchAsync` (`:854-878`) всегда перечитывает свежие настройки, при `ShouldAutoSwitch` пишет `ActiveNodeId`, логирует `Auto-switch: <name>` и вызывает `onActiveNodeChanged`.

### 7.5 Failover по факту обрыва («tunnel broken»)

* `ReportTunnelFailure()` (`GoidaProfileManager.cs:262-319`): отказ, если подсистема выключена, `AutoSwitchOnFly = false` или режим `ManualFixed` (`:266-269`); дебаунс `FailoverDebounce = 1 с` (`:61`, `:271-272`); узел помечается «недавно провалившимся» в `recentTunnelFailures` (`:280-281`), cooldown — `TunnelFailureCooldown = 90 с` (`:60`, `:419`); затем `SelectNextFailoverNode`; если пул исчерпан — `TryLiveVlessFailover` с реальными VLESS-тестами до `MaxLiveFailoverTests = 20` узлов (`:321-373`); при успехе — `SetActiveNode(next.Id)` и `true` (`:317-318`).
* Кто вызывает: Windows `WindowFactory.OnTunnelBroken` (`Factories\WindowFactory.cs:91-97`) — и **только** если текущий путь = `GoidaProfilePaths.MarkerPath`; сам вызов инициирует `MainWindow.RegisterTunnelFailure` после провала exit-IP проверки (`MainWindow.xaml.cs:1197-1248`); Android — `MainView.Goida.cs:781, 903`.
* После переключения: grace 25 с (`MainWindow.xaml.cs:1233`), затем `TryRerun()` → **полный перезапуск** Xray-сессии (см. §9.3). «Бесшовного» переноса сокетов нет.

### 7.6 Откуда берутся узлы

* `GoidaSourceCatalog` (`Services\Goida\GoidaSourceCatalog.cs`): `MinListId = 1`, `MaxListId = 26` (`:8-9`); 5 зеркал одного и того же репозитория `AvenCores/goida-vpn-configs` (raw.githubusercontent → github.com/raw → jsdelivr → fastly.jsdelivr → gitmirror, `:13-20`); таблица названий списков (`:22-51`).
* `GoidaFetcher` (`Services\Goida\GoidaFetcher.cs`): общий `HttpClient` c `Timeout = 90 с`, UA `InvisibleGorilla-XRay/3.6.12`, `Accept: text/plain` (`:15-33`); параллелизм — `SemaphoreSlim(3)` (`:41`); перебор зеркал, отбрасывание пустых/HTML-ответов (`LooksLikeHtmlError`, `:98`); ошибка на список → пустая строка, без падения (`:67-74`).
* `GoidaNodeParser` / `GoidaSubscriptionNormalizer` — разбор base64/подписок в узлы; `GoidaNodeStore` — хранилище узлов с bulk-обновлением (`BeginBulkUpdate`/`EndBulkUpdate`, `GoidaProfileManager.cs:652-663`) и `GoidaHealthMonitor.NodesUpdated` → троттлинг обновления UI 400 мс (`HandlersInitializer.cs:416-427`).

---

## 8. OpenFlux: туннель через документ Яндекс.Диска

### 8.1 Что это

`InvisibleGorilla-XRay\Handlers\OpenFlux\OpenFluxManager.cs` + `OpenFluxHttpBridge.cs` (Windows) и `InvisibleGorilla.Core\Handlers\OpenFlux\*` (Core, +291 строка новее). Sidecar `openflux.exe` реализует клиент туннеля поверх документа Яндекс.Диска; приложение запускает его в режиме SOCKS5-инбаунда.

* Бинарь: `OpenFlux\openflux.exe` (25 604 608 байт), ключ `OpenFlux\enc.key` (64 байта), `NOTICE.txt`; в csproj — `Content Include` с условиями и таргет `CopyOpenFluxBinary`, который копирует бинарь из `E:\PPROJECTS\whiteBlade\artifacts\openflux\openflux.exe` (`InvisibleGorilla-XRay.csproj:97-130`). Версия в PE-метаданных отсутствует.
* Android: `libopenflux.so` (см. §3.2), путь подменяется `Path.OverrideOpenFluxBinary` (`AndroidAppStorage.cs:63-80`).
* Проверка наличия: `IsAvailable => File.Exists(AppPath.OPENFLUX_EXE)` (`OpenFluxManager.cs:51`/Core `:54`).

### 8.2 Модель профиля (`Models\OpenFluxSettings.cs`, Windows)

| Поле/константа | Значение | Строка |
|---|---|---|
| `enum OpenFluxTransportMode` | `Auto`, `Vyandex`, `Yandex` | 10-15 |
| `DefaultSocksPort` | `11080` | 23 |
| `DefaultCodec` | `"batched"` | 24 |
| `Name` | `"OpenFlux"` | 52 |
| `DocUrl` | `""` | 53 |
| `Transport` | `Auto` | 54 |
| `Codec` | `batched` (любой другой приводится к `batched` — `GetCodec()` и принудительно в `Start`, Core `OpenFluxManager.cs:176-177`) | 55, 64 |
| `EncryptionKey` | `""` | 56 |
| `SocksPort` | `11080`, санитизация `GetSocksPort()` | 57, 62 |
| `LastLatencyMs` | `-1` | 58 |
| `ConfigPath` | `""` → маркер `./Configs/__OpenFluxProfile__.json` (`Services\OpenFlux\OpenFluxProfilePaths.cs:8-9`; проставляется в `UserSettings.GetOpenFluxProfile`, `Models\UserSettings.cs:178-184`) | 59 |

`OpenFluxUrl` (`:82-177`): `Trim/Canonicalize` (переводит `docs.yandex.ru/.../edit/d/...` и `volga.yandex.ru` на `disk.yandex.ru`, `:98-121`), `DeriveKey` = `SHA256("OpenFlux document key v1\0" + canonicalUrl)` в hex (`:86-95`), `TryValidate` (только http/https и хосты `disk.yandex.ru`/`docs.yandex.ru`/`volga.yandex.ru`, `:123-151`), `ResolveTransport` (`vyandex` по умолчанию, `yandex` для `docs.yandex.ru`, `:153-176`).

### 8.3 Запуск процесса (точные аргументы и окружение)

`OpenFluxManager.StartProcess` (Core `:316-375`, Windows `:280-338`):

```
openflux.exe --role=client --inbound=socks5 --transport=<vyandex|yandex>
             --codec=batched --url=<canonicalUrl>
             --socks5=127.0.0.1:<port> [--encryption-key-file=<OpenFlux\enc.key>]
```
(`:331-338`). WorkingDirectory = `Directory.OPENFLUX`; stdin не подключён; stdout/stderr читаются построчно (`:359-360`). Окружение: `NO_PROXY=*`, `HTTP(S)_PROXY`/`ALL_PROXY` пустые (`:340-346`), при наличии `doc.html`/`doc.cookies` — `OPENFLUX_HTML_FILE`/`OPENFLUX_COOKIE_FILE` (`:347-352`), батчинг: `OPENFLUX_BATCH_BYTES=32768`, `OPENFLUX_BATCH_COUNT=96`, `OPENFLUX_BATCH_LINGER_MS=4` (`:354-356`).

`Start(profile, logDirectory, listenWaitMs = 15000)` (Core `:155-225`): проверки профиля/URL/ключа (`MinKeyLength = 16`, `:36`, `:167-170`), `KillOrphanSidecars()` (только Core, `:172`), выбор порта `ResolveListenPort` (сканирование от `11080` до +40, пропуск 10801/10802/10803 — `:100-110`), остановка старого процесса, ожидание освобождения порта, `WaitForListen(port, 15000)`, перевод статуса в `Connected`. Статусы и разбор лога (`HandleProcessLine`, `:377-414`): `Running as CLIENT` → `WaitingPeer`; `yandex captcha` → ошибка `captcha`; `looks like a login page`/`config not found` → `document`; `balancer_url missing` → `transport`.

### 8.4 Задержка/качество для OpenFlux

* В списке серверов Windows задержка = время HTTP-запроса к самому документу Яндекс.Диска **напрямую** (без прокси): `OpenFluxUrlCheck.Inspect` — `HttpClient{UseProxy=false}`, `Timeout = 8 с`, замер `Stopwatch` вокруг `GetAsync(canonical, ResponseHeadersRead)`, отдельная обработка редиректа на `passport.yandex` («login»), HTTP ≥ 400 → ошибка, иначе `LooksLikeDocument = true` (`InvisibleGorilla-XRay\Services\OpenFlux\OpenFluxUrlCheck.cs:25-90`); вызывается из `ServerWindow.ProbeOpenFluxLatency` (`ServerWindow.xaml.cs:565-585`) и на главном экране (`MainWindow.xaml.cs:1459, 1613`).
* Реальная задержка туннеля появляется в виджете соединения: exit-IP берётся через SOCKS: `Socks5Http.GetHttpsBody("127.0.0.1", port, "api.ipify.org", "/", 8000)` (`MainWindow.xaml.cs:968`; Core `OpenFluxManager.cs:589` в `ProbePeer`), результат пишется в `lastOpenFluxPingMs` (`:1156`) и в `OpenFluxManager.LastExitIp` (Core `:70-83`, `RememberExitIp`).
* OpenFlux-специфичные интервалы: виджет соединения — 45 с (`MainWindow.xaml.cs:899-901`), таймаут запроса 30 с (`:1086`), `ProbePeer` — попытки по 8 с с шагом 400 мс (`Core OpenFluxManager.cs:580-614`), `WaitForPeer` (только Core, `:229-241`).
* **Автопереключения между профилями `OpenFluxProfiles` нет**: список профилей существует в настройках (`UserSettings.OpenFluxProfiles`), клонируется/сливается (`Handlers\SettingsHandler.cs:48, 104, 158-167`; Core — ещё `MergeOpenFluxProfiles`), но нигде не используется для выбора/переключения (grep по `GetOpenFluxProfiles` даёт только сохранение в `SettingsWindow.xaml.cs:801` и определения в моделях). Активен всегда один профиль `UserSettings.OpenFlux`.

### 8.5 Hot-swap без остановки UI (это и есть «переключение на лету»)

Реализация — цикл в `InvisibleGorillaXRayCore.RunOpenFlux` (Windows `Core\InvisibleGorillaXRayCore.cs:389-493`):

```
while (true) {
    profile = getOpenFluxProfile().Clone();           # :414 — свежий профиль каждый круг
    profile.EncryptionKey = OpenFluxUrl.DeriveKey(...)# :416
    openFluxManager.Start(profile, logPath)           # :417 — новый процесс на новом порту
    OpenFluxExitRegistry.RegisterInBackground(...)    # :423
    openFluxHttpBridge.Start(socksPort)               # :428
    ActiveTunnelSession.SetOpenFlux(...)              # :429
    ... прокси/TUN переключаются только при смене порта  # :431-466
    if (first) onReady()                              # :468-472
    openFluxManager.WaitSession()                     # :474 — блокирует до рестарта/остановки
    if (!openFluxManager.ConsumeRestartRequest()) break# :475-476
}
```

`RequestRestart()` (Core `OpenFluxManager.cs:285-290`) ставит `restartFlag` и глушит **только** процесс sidecar; `WaitSession()` (`:249-273`) возвращает управление, а также сам инициирует рециркуляцию: **«логин Яндекс.Диска в sidecar умирает примерно через сутки, процесс при этом выглядит живым»** → принудительный рестарт каждые 3 часа (`recycleAt = UtcNow.AddHours(3)`, `:253-265`). Внешний запуск: `ApplyOpenFluxDocUrl` → `OpenFluxExitRegistry.Register` и `RequestRestart()`, если процесс уже работает (`Core\InvisibleGorillaXRayCore.cs:495-519`). Итог: смена URL/рестарт sidecar **не рвут системный прокси/TUN**, Xray при OpenFlux вообще не участвует (`Run` уходит в `RunOpenFlux` при маркере — `:164-168`); на время рестарта трафик через мёртвый SOCKS-порт, разумеется, не идёт.

---

## 9. Общая архитектура смены сервера/конфига

### 9.1 Что происходит при «Connect»

`MainWindow` → `onRunServer: config => core.Run(config, mainWindow.NotifyReady)` (`Factories\WindowFactory.cs:69`) → `InvisibleGorillaXRayCore.Run` (`Core\InvisibleGorillaXRayCore.cs:160-339`):

1. `DiagnosticLog.Clear()`; если активен OpenFlux-маркер → отдельная ветка `RunOpenFlux` (`:162-168`).
2. Tor: если выбран Tor-маркер **или** `TorSettings.Enabled` → `torManager.Start(torSettings)` **синхронно** (`:177-191`, до 90 с), затем `config = BuildTorRuntimeConfig(torSettings)` (`:190`).
3. `BuildTorRuntimeConfig` (`:346-379`): `ONLY_TOR` → минимальный JSON с одним SOCKS-outbound на Tor (`TorConfigBuilder.BuildTorOnlyConfig`); `XRAY_OVER_TOR` → чтение **файла** текущего сервера и `TorConfigBuilder.WrapConfigOverTor` (добавляет outbound `tag=tor-out` и `streamSettings.sockopt.dialerProxy=tor-out` первому outbound — `Handlers\Tor\TorConfigBuilder.cs:50-105`). Если файла нет (например, это Tor-маркер `./Configs/__TorBridges__.json`) — тихий откат к Tor-only (`:359-366`). Итоговый JSON пишется в `./Tor/Data/runtime.json` и прогоняется через native `LoadConfig` (`:369-378`).
4. Подготовка портов/прокси/локальных креденшелов, `ActiveTunnelSession.Set` (`:193-201`).
5. Xray стартует **в отдельном потоке** (`XRayCoreWrapper.StartServer`, блокирующий вызов — `:214-236`), затем ожидание активности локального порта `WaitForPortActive(proxyPort, 15000)` (`:245`, комментарий объясняет переход с 5 с на 15 с из-за AV-сканирования).
6. Проверки: исключение в потоке/поток мёртв/порт не поднялся → `StopServer()`, `torManager.Stop()` и выход или исключение (`:249-273`).
7. `PROXY` → `EnableProxy()`; иначе `EnableTunnel(...)`; ошибка TUN → глушение всего и исключение (`:275-296`).
8. `onReady()` → UI помечает `isConnected = true` (см. §6.3).
9. `serverThread.Join()` — блокировка до конца сессии; затем отключение прокси/TUN, `torManager.Stop()`, `ActiveTunnelSession.Clear()` (`:307-330`).

Системный прокси: `WindowsProxy` пишет `ProxyServer`/`ProxyOverride` и в per-connection `INTERNET_PER_CONN_*`, и в реестр `Software\Microsoft\Windows\CurrentVersion\Internet Settings`, затем `InternetSetOption(SETTINGS_CHANGED/REFRESH)` и проверяет состояние (`Handlers\Proxies\WindowsProxy.cs:16-32, 74-100, 146-266`). Байпас по умолчанию: `<local>;localhost;127.*;10.*;192.168.*` (`:32`), плюс `ExtraBypass` (для OpenFlux — список хостов Яндекса, `Core\InvisibleGorillaXRayCore.cs:535-536, 404`). TUN: `WindowsTunnel` с таймаутами `ServiceStartTimeoutMs = 10000`, `ServicePortTimeoutMs = 10000`, `InterfaceReadyTimeoutMs = 20000`, имя интерфейса `InvisibleGorilla-XRay`, через `InvisibleGorilla-TUN.exe` (`Handlers\Tunnels\WindowsTunnel.cs:20-33`; `Values\Path.cs:9-14`).

### 9.2 Смена сервера в UI — всегда полный перезапуск

Выбор сервера в списке: `ServerWindow` → `onSelect: onUpdateConfig.Invoke(config.Path)` (`ServerWindow.xaml.cs:275-278`) → `WindowFactory.UpdateConfig` (`:366-376`):

```csharp
settingsHandler.UpdateCurrentConfigPath(path);   // :371 — сохранить в Settings.json
mainWindow.UpdateUI();                           // :372
if (mainWindow.IsServerRunning)                  // :374
    mainWindow.TryRerun();                       // :375 — ПОЛНЫЙ рестарт
```

`TryRerun` (`MainWindow.xaml.cs:613-625`) не пытается ничего сохранить: он выставляет `isRerunRequest` и асинхронно вызывает `onDisableMode()` + `onStopServer()`; поток `Run` выходит из `Join`, снимает прокси/TUN, глушит Tor, и воркер запускает `Run` заново. **Бесшовного переключения нет** — соединение рвётся, Tor (если был) проходит bootstrap заново. `TryDisableModeAndRerun` (`:627-639`) — то же, но без ожидания. `IsServerRunning => runWorker.IsBusy` (`:641`).

### 9.3 Проверки доступности серверов

* `ServerWindow` для каждой карточки дергает `testConnection` (`ServerWindow.xaml.cs:311-326`): OpenFlux → `ProbeOpenFluxLatency` (HTTP к документу, §8.4); Tor → `probeTorLatency` (`CheckBridge` первой строки, 45 с); Goida → `probeGoida` (ручной `ProbeAsync(manual: true)`, `WindowFactory.cs:388-407`); обычный сервер → `loadConfig` + `core.Test` (native `TestConnection`).
* Результат складывается в `Config.SetAvailability(int)`; маркеры: `NOT_CHECKED = -3`, `ERROR = -2`, `TIMEOUT = -1` (`Values\Availability.cs`). Goida мапит статусы (`HandlersInitializer.cs:157-182`).
* Tor-доступность = последняя ручная проверка (`HandlersInitializer.cs:109-110`); периодического обновления нет.

### 9.4 Изменение настроек и перезапуск

`WindowFactory.UpdateUserSettings` (из окна настроек, `:206-224`) сравнивает **только**: `Mode`, `Protocol`, `ProxyPort`, `TunPort`, `TunIp`, `Dns`, `IsSystemProxyUse`, `IsUdpEnable`. Если что-то из них изменилось — `TryDisableModeAndRerun()` (`:222-223`). **Изменения Tor (`Enabled`, `Mode`, `BridgeType`, `BridgeLines`), Goida и OpenFlux в этот список не входят** → сохранение настроек Tor при активном подключении **не приводит к перезапуску**, новые мосты вступят в силу только после ручного Stop/Connect. Отдельно: `AppRulesWindow` перезапускает всегда (`:431-438`), `GoidaProfileWindow` и `HandlersInitializer.OnGoidaActiveNodeChanged` — при смене активного узла (`:482-491`; `Managers\Initializers\HandlersInitializer.cs:397-414`).

### 9.5 Android: отличия в Tor-обвязке

* Используется Core-`TorManager` (тот, что читает `tor.log`) и Core-модели с `TorProfile`.
* Есть редактор Tor прямо на карточке профиля: контролы `TorEnabledToggle`, `TorModeSelector`, `BridgeTypeSelector`, `TorSocksPortInput`, `BridgesInput`, `TorStatusText`, `CheckBridgeButton` (`MainView.axaml.cs:434-451, 794-815, 871-884`).
* Выбор конфига настраивает Tor автоматически: `ApplyTorStateForSelectedConfig` (`:3666-3702`) — Tor-маркер включает `ONLY_TOR` и при необходимости подставляет snowflake; Tor-профиль включает сохранённые `Mode/BridgeType/BridgeLines/SocksPort`; любой другой сервер **выключает** Tor.
* Можно создавать переключаемые Tor-профили из вставленных/полученных мостов: `CreateTorBridgeProfile` (`:3739-3770`) создаёт Tor-only Xray-конфиг и добавляет `TorProfile` в `UserSettings.TorProfiles`; удаление — `RemoveTorProfileForPath` (`:3816-3825`).
* Кнопки быстрого выбора методов (Snowflake / Snowflake AMP / meek-azure / «Ask Tor») — Windows-аналоги в `SettingsWindow.xaml` (`:377-396`) и обработчики `OnSnowflakeClick`/`OnSnowflakeAmpClick`/`OnMeekAzureClick` (`SettingsWindow.xaml.cs:376-388`).

---

## 10. Полный список пользовательских настроек (`UserSettings`)

Windows: `InvisibleGorilla-XRay\Models\UserSettings.cs`; Core (Android/Linux/Mac): `InvisibleGorilla.Core\Models\UserSettings.cs` — набор полей совпадает, кроме `TorProfiles` (только Core).

| Поле | Тип | По умолчанию | Смысл | Строка (Windows) |
|---|---|---|---|---|
| `ClientId` | string | `""` | анонимный идентификатор (генерируется при первом запуске) | 18 |
| `Language` | string | `"en-US"` (JSON-имя `language`) | язык UI | 21 |
| `CurrentConfigPath` | string | `"./Configs"` | выбранный конфиг/маркер профиля | 24 |
| `Mode` | `Mode` | `PROXY` | режим: PROXY или TUN | 27 |
| `Protocol` | `Protocol` | `HTTP` | локальный протокол (HTTP/SOCKS) | 30 |
| `IsSystemProxyUse` | bool | `true` | переключать системный прокси | 33 |
| `IsUdpEnable` | bool | `true` | UDP в Xray | 36 |
| `IsRunningAtStartup` | bool | `false` | автозапуск (реестр Windows) | 39 |
| `IsStartHidden` | bool | `false` | стартовать свёрнутым | 42 |
| `IsAutoConnect` | bool | `false` | автоподключение при старте | 45 |
| `IsSendingAnalytics` | bool | `true` | отправка аналитики | 48 |
| `ProxyPort` | int | `10801` | локальный порт Xray | 51 |
| `TunPort` | int | `10802` | порт сервиса TUN | 54 |
| `TestPort` | int | `10803` | порт проверки соединения | 57 |
| `TunIp` | string | `"10.0.236.10"` | адрес TUN-интерфейса | 60 |
| `Dns` | string | `"8.8.8.8"` | DNS для TUN | 63 |
| `LogLevel` | `LogLevel` | `NONE` | NONE/DEBUG/INFO/WARNING/ERROR | 66 |
| `LogPath` | string | `"./Logs"` | каталог логов | 69 |
| `AppRulesMode` | `AppRulesMode` | `ALL_APPS` | режим правил приложений | 72 |
| `AppRules` | `List<AppRule>` | `[]` | правила по приложениям | 75 |
| `AppRuleTemplates` | `List<AppRuleTemplate>` | `[]` | шаблоны правил | 78 |
| `AppRuleTemplateBindings` | `List<AppRuleTemplateBinding>` | `[]` | привязка шаблон→конфиг | 81 |
| `Tor` | `TorSettings` | см. §4.1 | Tor и мосты | 84 |
| `Goida` | `GoidaProfileSettings` | см. §7.1 | Goida | 87 |
| `OpenFlux` | `OpenFluxProfile` | см. §8.2 | активный OpenFlux-профиль | 90 |
| `OpenFluxProfiles` | `List<OpenFluxProfile>` | `[]` (не используется для переключения) | профили OpenFlux | 93 |
| `TorProfiles` | `List<TorProfile>` | `[]` | **только Core** — переключаемые Tor-профили в списке серверов | Core `:88` |

UI-переключатели, которых нет в модели как отдельные поля: подключение по ссылке/подписке (`Configs\SubscriptionConfig.cs`), привязка шаблонов правил к конфигу, «умная вставка» (`Handlers\SmartInput\*`), окно политики (генерация `ClientId`), проверка обновлений (`UpdateHandler`), broadcast-сообщения (`BroadcastHandler`).

---

## 11. Слабые места и что улучшить в части устойчивости к блокировкам

### 11.1 Мосты

1. **Нет автоматического перебора/эскалации транспортов.** При провале bootstrap приложение сдаётся и предлагает «Try different bridges» (`TorManager.cs:210-211`; `InvisibleGorillaXRayCore.cs:183-188`). Нет цепочки «obfs4 → snowflake → webtunnel → meek → Snowflake AMP», хотя все они уже поддержаны. Предложение: реализовать в `TorManager` (или над ним) стратегию `foreach (candidate in BridgeAttemptPlan) { Start(...); if (success) break; }` с эскалацией и запоминанием рабочего варианта в `TorSettings.BridgeType/BridgeLines`.
2. **Тихий выход в прямое подключение без мостов.** Если `BridgeType = WEBTUNNEL` (или любой тип с пустыми строками, для которого нет встроенных линий), `AppendBridges` выходит до `UseBridges 1` (`TorrcBuilder.cs:72-76`) — Tor соединяется напрямую, что в цензурируемой сети означает мгновенную блокировку, а в логах UI это никак не отражено. Предложение: при `BridgeType != NONE` без линий — ошибка конфигурации (fail-fast) либо автоподстановка snowflake.
3. **Проверяется только первый мост** (`SettingsWindow.xaml.cs:347`, `WindowFactory.cs:348`, Android `MainView.axaml.cs:3866`). Список из 7 obfs4-линий фактически не валидируется. Предложение: параллельная проверка всех строк (по образцу `GoidaHealthMonitor`: `SemaphoreSlim` + таймаут), ранжирование и запись «рабочего» подмножества.
4. **Нет планового обновления мостов и учёта их «свежести».** Moat вызывается только кнопками, встроенные obfs4-линии датированы Dec 2025 (комментарий `DefaultBridges.cs:8-11, 49`). Предложение: фоновый refresh по TTL (например, 24–72 ч) при включённом Tor, плюс «подтянуть свежие builtin» после N неудачных bootstrap.
5. **CAPTCHA/moat только для obfs4.** При `MEEK_AZURE`/`WEBTUNNEL` CAPTCHA-флоу не запускается (`SettingsWindow.xaml.cs:521-524` — только `OBFS4 || NONE`), а при `MoatClient` с 8-секундным таймаутом и прямым соединением (`MoatClient.cs:71-79`) в цензурируемой сети он часто и не дойдёт. Предложение: несколько зеркал/эндпоинтов, экспоненциальный backoff, опция «через уже поднятый Tor/Snowflake» (в `MoatClient` поле `socksProxy` **уже есть**, но в UI не используется — `SettingsWindow.xaml.cs:417,507,524,568`).
6. **`snowflake-client.exe` отсутствует при заявленном пути и в build-скрипте.** Работает только за счёт fallback на lyrebird (`TorrcBuilder.cs:80-84`). Предложение: проверять при сборке фактическое наличие всех PT-бинарей (`snowflake-client.exe`, `lyrebird.exe`, `conjure-client.exe`) и явно сообщать в UI, какой транспорт доступен, вместо тихой подмены.
7. **`conjure-client.exe` не используется** (нет `BridgeType.CONJURE`, `Path.CONJURE_EXE` мёртв) — 12.5 МБ в поставке без функции. Либо реализовать conjure как ещё один транспорт, либо убрать из bundle.
8. **`BridgeLineLooksBroken` — эвристика только на Android** (`MainView.axaml.cs:3719-3733`), в Windows её нет; вставка «сломанной» строки (например, `cert=` с пробелом) приведёт к тому, что tor отвергнет весь файл. Предложение: валидация строк при вставке/сохранении на всех платформах (общий код в Core).

### 11.2 Управление Tor и устойчивость

9. **Нет мониторинга живости `tor.exe` после bootstrap.** `IsRunning` опрашивается только внутри `WaitForBootstrap` (`TorManager.cs:174, 187`). Если tor умрёт в середине сессии, приложение узнает об этом лишь косвенно — через провал проверки exit-IP (до 20 с), и **только для Goida-профиля** есть реакция (`WindowFactory.cs:91-97`). Для Tor-профиля не делается ничего. Предложение: лёгкий watchdog (поток/таймер каждые 2–5 с: `HasExited` + TCP-проба `SocksPort`), при падении — авто-рестарт с эскалацией мостов и перезапуском Xray.
10. **Нет авто-рестарта и backoff.** Единственный запуск, единственная попытка. Предложение: до N попыток с паузами, сохраняя состояние UI («Tor перезапускается, попытка 2/5, транспорт snowflake»).
11. **Нет «горячей» смены мостов.** `TorControlClient` не умеет `SETCONF`/`NEWNYM`/`SIGNAL RELOAD` (§2.5), `Start` всегда делает `Stop()` + полный bootstrap (`:63`). Реализация «на лету»: `SETCONF Bridge=...`/`UseBridges=1` (для части настроек tor требует `SIGNAL RELOAD`, что можно сделать без потери соединения), плюс кэш bootstrap-состояния tor (штатный `DataDirectory` уже используется — но сбрасывается удалением cookie и перезапуском). Это самый крупный выигрыш по UX: сейчас смена моста = разрыв VPN.
12. **Смена настроек Tor не перезапускает активную сессию** (`WindowFactory.cs:206-224`). Пользователь думает, что применил новые мосты, а туннель работает на старых. Предложение: добавить в предикат `reconnect` сравнение `Tor.Enabled/Mode/BridgeType/BridgeLines/SocksPort` (и аналогично для OpenFlux/Goida).
13. **Нет прогресса bootstrap в UI.** `BootstrapPercent`/`StatusSummary` не читаются вне `TorManager` (проверено grep). Для цензурируемых сетей это критично: пользователь не понимает, «висит на 10 %» или «работает». Предложение: событие `ProgressChanged` из `WaitForBootstrap` с пробросом в UI (у Android/Windows уже есть инфраструктура статусов).
14. **Windows-версия теряет диагностику.** В Core ошибка падения tor содержит последние 12 строк `tor.log` (`InvisibleGorilla.Core\Handlers\Tor\TorManager.cs:217-272`), в Windows — только «Check tor.log.» (`:175, 188`). Портировать.
15. **`tor.log` нигде не ротируется и растёт** (`TorrcBuilder.cs:36-45`, ротация есть только у `DiagnosticLog`, `InvisibleGorilla.Core\Core\DiagnosticLog.cs:13`). Реализовать ротацию/ограничение размера для `Tor/Data/tor.log` и `tor-check.log`.
16. **`ControlPort` не настраивается и не защищён паролем** — используется только cookie-аутентификация (`TorrcBuilder.cs:29-30`), порт всегда `9251` и берётся из модели без UI (`SettingsWindow.xaml.cs:242`). Это приемлемо (слушает только localhost), но стоит добавить проверку занятости порта и понятную ошибку, если 9251 занят другим процессом.
17. **Осиротевшие процессы.** Нет аналога `KillOrphanSidecars` для tor (для OpenFlux он есть — `InvisibleGorilla.Core\Handlers\OpenFlux\OpenFluxManager.cs:647`), а `RequestGracefulShutdown` через 15 с делает `Process.Kill()` всего приложения (`MainWindow.xaml.cs:558-566`), не убивая дочерний `tor.exe`. После такого «жёсткого» выхода остаётся tor, держащий 9250/9251; следующая попытка подключения может упасть на привязке портов. Предложение: job object / kill-by-name при старте (как у OpenFlux) и корректный `Stop()` до watchdog.
18. **Тест моста конфликтует с активной сессией.** `CheckBridge` поднимает второй tor на 19250+ из того же `Tor/Data/check` (`TorManager.cs:223-241`), не проверяя, активна ли сессия; в списке серверов кнопка теста доступна всегда (`ServerWindow.xaml.cs:315-316`). Для snowflake/meek это лишняя нагрузка и потенциальные ложные выводы. Предложение: запрет теста при активном Tor-профиле либо предупреждение, и отдельная очередь тестов.
19. **`LastLatencyMs` Tor легко «протухает»** и семантически это время bootstrap, а не ping (`TorManager.cs:261-280`), что несопоставимо с latency обычных серверов в том же списке. Предложение: подписывать метрику как «bootstrap, мс» и/или хранить `LastCheckedUtc` (в `TorSettings` его нет, хотя в `TorProfile`/`GoidaNode` есть).
20. **Дублирование кода Windows vs Core уже привело к регрессиям** (§1): Windows не имеет `TorProfile`, диагностики по логу и нового OpenFlux; Core отстал по `MoatClient`. Предложение: вынести Tor/OpenFlux/Goida в Core (или общий проект) с едиными тестами — иначе каждое исправление нужно делать дважды.

### 11.3 Переключение и диагностика (общее)

21. **Любая смена сервера = полный разрыв** (`WindowFactory.cs:366-376`, `MainWindow.cs:613-625`). Единственная «бесшовная» механика — OpenFlux (`RunOpenFlux` + `RequestRestart`, §8.5); её паттерн (перезапуск сайдкара без снятия прокси/TUN) можно распространить на Tor при смене мостов.
22. **Нет активного health-check до переключения.** Goida переключается по факту провала exit-IP (`MainWindow.xaml.cs:1197-1248`) или по latency-порогу; для обычных серверов переключение только ручное, а «доступность» — это результат давнего ручного теста.
23. **Порог 3000 мс и интервалы фиксированы** (`GoidaProfileSettings.cs:10-13`) — нет адаптации к реальной деградации, нет гистерезиса по времени (переключение возможно на каждом прогоне при колебаниях latency), нет ограничения на число переключений в час (есть только `FailoverDebounce = 1 с` и cooldown 90 с на узел).
24. **`AutoSwitchLatencyMs` используется и как порог качества, и как отсечка VLESS-проверки** (`GoidaProfileManager.cs:645`) — при большом пороге native-проверок становится слишком много (до 500 в ручном режиме), при малом — узлы отбрасываются до проверки.
25. **TUN + Tor Only не имеет отдельной проверки.** `ResolveTunnelServerAddress` ищет `address` в outbounds и в Tor-only вернёт `127.0.0.1` (`Core\InvisibleGorillaXRayCore.cs:745-776`), что уходит в `tunnel.Enable(..., server: ...)`. Это не проверено в рантайме (нет автотеста и нет ветки «Tor-only + TUN»), риск — некорректный bypass-маршрут/шумная ошибка TUN. Предложение: явная ветка «Tor-only + TUN» (bypass не нужен, Tor — тоже loopback) + смоук-тест.
26. **Диагностика для пользователя минимальна:** нет экспорта `tor.log`/`diagnostic.log` одной кнопкой, нет отображения выбранного моста/цепочки, нет счётчиков трафика Tor (`GETINFO` не используется). Для разбора блокировок полезно: `GETINFO status/circuit-established`, `entry-guards`, `traffic/read|written`, `net/listeners/socks`, а также сохранение последнего рабочего пресета мостов отдельно от пользовательских строк.

---

## 12. Сводка констант и значений по умолчанию

| Параметр | Значение | Источник |
|---|---|---|
| SocksPort Tor | `9250` | `InvisibleGorilla-XRay\Models\TorSettings.cs:61` |
| ControlPort Tor | `9251` | `:62` |
| Таймаут bootstrap Tor | `90 000` мс | `Handlers\Tor\TorManager.cs:54` |
| Таймаут проверки моста | `60 000` мс (45 000 из списка серверов) | `:218`, `Factories\WindowFactory.cs:349` |
| Порт/каталог проверки моста | `19250+`, `./Tor/Data/check` | `:223-225` |
| Tor Expert Bundle | `14.5.7` | `build.ps1:109`, `build-android.ps1:53` |
| `Availability` | `NOT_CHECKED = -3`, `ERROR = -2`, `TIMEOUT = -1` | `Values\Availability.cs:5-7` |
| Goida refresh / probe / порог | `9 мин` / `60 с` (мин. 15 с) / `3000 мс` | `Core\Models\GoidaProfileSettings.cs:10-12`; `Core\Services\Goida\GoidaProfileManager.cs:803-806` |
| Goida TCP-проба | таймаут `2500` мс, параллелизм `16` | `Core\Services\Goida\GoidaHealthMonitor.cs:13-14` |
| Goida native-проба | таймаут `7000` мс, гейт `SemaphoreSlim(1,1)`; фоново 8 тестов, ранний стоп при 3 успехах; вручную до `500` | `:15-18`; `Core\Services\Goida\GoidaProfileManager.cs:635-647` |
| Goida failover | debounce `1 с`, cooldown узла `90 с`, до `20` живых проверок | `Core\Services\Goida\GoidaProfileManager.cs:60-61, 321` |
| OpenFlux SOCKS-порт | `11080` (скан +40, пропуск 10801-10803) | `Models\OpenFluxSettings.cs:23`; `Core\Handlers\OpenFlux\OpenFluxManager.cs:100-110` |
| OpenFlux ожидание listen | `15 000` мс | `Core\Handlers\OpenFlux\OpenFluxManager.cs:155` |
| OpenFlux recycle сессии | `3 ч` | `:253` |
| OpenFlux probe peer | попытка `8000` мс, шаг `400` мс | `:582-609` |
| OpenFlux минимум ключа | `16` символов | `:36` |
| Виджет соединения | 20 с (8 с Goida, 45 с OpenFlux), до 4 ретраев | `Windows\MainWindow\MainWindow.xaml.cs:48, 897-903, 1122-1137` |
| Порог «tunnel broken» | 2 подряд (1 для Goida), grace 15 с | `:894-896, 1211-1221` |
| Xray: ожидание порта | `15 000` мс | `Core\InvisibleGorillaXRayCore.cs:245` |
| TUN-таймауты | `10 000` / `10 000` / `20 000` мс | `Handlers\Tunnels\WindowsTunnel.cs:20-22` |
