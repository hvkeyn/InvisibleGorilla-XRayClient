# Аудит алгоритмов и сетевой подсистемы (v3.6.58)

Дата: 2026-10-17. Исходная ревизия: `5633bc9` (v3.6.58). Те же дефекты в дереве Core (Android и Linux) закрыты вместе с Windows перед выпуском 3.6.59.
Подробные отчёты аудита: [.audit/tunnel.md](../.audit/tunnel.md), [.audit/switching.md](../.audit/switching.md), [.audit/config.md](../.audit/config.md), [.audit/tor.md](../.audit/tor.md).

## 1. Методика

Четыре параллельных аудита с чтением кода и ссылками на строки, плюс собственный разбор «горячих» мест, автотесты и сборка после каждого этапа.

| Область | Файлы | Находок |
|---|---|---|
| Туннель, системный прокси, пробы | `Handlers/Tunnels/*`, `Handlers/Proxies/*`, `Core/*`, `Services/{ConnectionProbe,ConnectionInfoService,Socks5Http}*`, `Windows/MainWindow/*` | 15 (1×P0, 6×P1, 8×P2) |
| Переключение и мониторинг | `Services/Goida/*`, `Services/OpenFlux/*` | 14 (2×P0, 4×P1, 8×P2) |
| Подписки, конфиги, настройки | `Handlers/Configs/*`, `Models/Templates/*`, `Handlers/SettingsHandler.cs`, `Utilities/*` | 16 (1×P0, 9×P1, 6×P2) |
| Tor и мосты | `Handlers/Tor/*` (Windows и Core) | 14 (1×P0, 4×P1, 9×P2) |

Автотесты: `tests/AuditHarness` (9 проверок чистой логики Tor-слоя).

## 2. Исправлено

### Безопасность и целостность данных

| # | Дефект | Файл | Severity |
|---|---|---|---|
| 1 | Инъекция команд в Tor control-port и директив в torrc через перевод строки в строке моста | `Handlers/Tor/TorrcBuilder.cs` (обе копии) | P1 |
| 2 | `profile-title: ..` → `Configs/..` → `Directory.Delete(..., true)` удалял каталог приложения | `Utilities/FileUtility.cs`, `Handlers/Configs/SubscriptionConfig.cs` | **P0** |
| 3 | `Settings.json`: неатомарная запись, падение на `null`/скаляре/неизвестном enum, молчаливый сброс настроек | `Handlers/SettingsHandler.cs` | P1 |
| 4 | Проба OpenFlux принимала любой TLS-сертификат (`static (_,_,_,_) => true`) | `Services/Socks5Http.cs` | P2 |
| 5 | Автообновление: имя ассета из API шло в путь без нормализации, размер не проверялся | `Services/WindowsAutoUpdateService.cs` | P2 |

### Логика соединения

| # | Дефект | Файл | Severity |
|---|---|---|---|
| 6 | «Залипший» системный прокси: `DisableProxy` гейтился живой настройкой, снятие галочки при активной сессии оставляло WinINET на мёртвом порту | `Core/InvisibleGorillaXRayCore.cs` | **P0** |
| 7 | Результат `EnableProxy` игнорировался; `WindowsProxy.Enable` всегда отдавал SUCCESS | `Core/InvisibleGorillaXRayCore.cs`, `Handlers/Proxies/WindowsProxy.cs` | P1 |
| 8 | Отмена во время старта считалась успехом (UI оставался в «Connected») | `Core/InvisibleGorillaXRayCore.cs` | P1 |
| 9 | Молчаливые провалы старта xray; `ActiveTunnelSession` не очищался на ранних выходах | `Core/InvisibleGorillaXRayCore.cs` | P1/P2 |
| 10 | Fallback остановки TUN недостижим (`INFO/CANCELED` вместо `ERROR`), порт захардкожен | `Handlers/Tunnels/WindowsTunnel.cs`, `Core/InvisibleGorillaXRayCore.cs` | P2 |

### Переключение, подписки, Tor

| # | Дефект | Файл | Severity |
|---|---|---|---|
| 11 | Failover не срабатывал на заблокированном узле (счётчик рос только после успешной пробы с утёкшим IP); HTTP-коды давали ложные переключения | `Windows/MainWindow/MainWindow.xaml.cs` | **P0** |
| 12 | Пауза живой сессии перед native-тестами была мертва (`Dispatcher.Invoke` + `CheckAccess`-guard) | `Managers/Initializers/HandlersInitializer.cs` | P1 |
| 13 | `async void` без `try/catch` в проверке моста: падение и вечно отключённая кнопка | `Windows/SettingsWindow/SettingsWindow.xaml.cs` | P1 |
| 14 | **tor не запускался на Windows**: `WorkingDirectory="./Tor"` + `-f "./Tor/Data/torrc"` → `<app>\Tor\Tor\Data\torrc` | `Handlers/Tor/TorManager.cs`, `TorrcBuilder.cs` | **P0** |
| 15 | Кавычки вокруг `exec`-пути PT при пробеле в пути → tor падал (`0xC0000409`); теперь абсолютный путь + 8.3 short path | `Handlers/Tor/TorrcBuilder.cs` | P1 |
| 16 | `Bridge obfs4 …` и «голые» `ip:port cert=…` оставляли тип NONE → torrc без `UseBridges` → тихое прямое подключение | `TorManager.cs` (Windows + Core) | P1 |
| 17 | `SwitchTransport`: при сбое `SETCONF` после `RESETCONF` tor оставался без мостов; теперь ставится `DisableNetwork=1` | `TorManager.cs` (Windows + Core) | P1 |
| 18 | Обновление подписки удаляло старый каталог до записи; теперь staging → `Move(old, .bak)` → `Move(new, old)` → cleanup, с откатом | `Handlers/Configs/SubscriptionConfig.cs` | P1 |
| 19 | `ss://`: отказ без `#`, нет plaintext-userinfo, пароль с `:` обрезался, неизвестный метод молча становился `none`; парсер переписан | `Models/Templates/Configs/Shadowsocks.cs` | P1 |
| 20 | JWT-подписка не выбиралась: base64url без паддинга бросал `FormatException` | `Models/Templates/Subscriptions/Jwt.cs` | P1 |
| 21 | Порты в настройках без диапазона: пустое поле → исключение (CONFIRM «не работал»), 70000 → 4464 | `Windows/SettingsWindow/SettingsWindow.xaml.cs` | P1 |
| 22 | Нет реакции на смену сети/обрыв; добавлены `NetworkChange.*` + дебаунс 3 с → `TryRerun()` | `Windows/MainWindow/MainWindow.xaml.cs` | P1 |
| 23 | Goida: сохранённые ID умирали при ротации ключа/порта; добавлен remap по endpoint + удаление осиротевших файлов узлов (~343k файлов / 469 МБ) | `Services/Goida/GoidaProfileManager.cs` | **P0** (смягчение) |

### Третий проход

| # | Дефект | Файл | Severity |
|---|---|---|---|
| 24 | WinINET: приложение при каждом запуске переводило прокси в DIRECT-only и удаляло `ProxyOverride` (ломало корпоративный прокси/PAC). Добавлен снапшот (реестр + блоб `DefaultConnectionSettings`) в `%LOCALAPPDATA%\InvisibleGorillaXRay\proxy-backup.json` и восстановление; `Disable` теперь трогает систему только при наличии своего снапшота | `Handlers/Proxies/WindowsProxy.cs` | P1 |
| 25 | VLESS: `Adapter` — вычисляемое свойство, поэтому дефолт `flow` для `reality`/`xtls` (`xtls-rprx-vision`/`origin`) записывался в выброшенный объект и терялся | `Models/Templates/Configs/Vless.cs` | P1 |
| 26 | VMess: base64url без паддинга не декодировался; `{}` проходил проверку и падал на `int.Parse` уже вне `try`; порт/version/aid без валидации | `Models/Templates/Configs/Vmess.cs` | P1 |
| 27 | `CheckBridge` писал в общий `BootstrapPercent`/`StatusSummary` живой сессии (проверка моста во время соединения ломала индикатор); теперь локальное состояние | `TorManager.cs` (Windows + Core) | P2 |

### Четвёртый проход

| # | Дефект | Файл | Severity |
|---|---|---|---|
| 28 | Гонка `Run`/`Stop`: fire-and-forget stop после перезапуска мог погасить уже новую сессию (убить новый `tor.exe`/`openflux.exe`, снять TUN/прокси). Добавлен `activeRunId`: `Stop`/`DisableMode`/`Cancel` завершают работу, только если сессия не сменилась | `Core/InvisibleGorillaXRayCore.cs` | P1 |
| 29 | CANCEL во время длинного старта (Moat/tor bootstrap) игнорировался, и поднималась «сессия-фантом». Добавлен `cancelRequested` с проверками после старта Tor и перед включением прокси/TUN | `Core/InvisibleGorillaXRayCore.cs` | P1 |
| 30 | Диалог импорта предлагал `.toml/.yaml`, но загрузчик читает только JSON — файл копировался и «исчезал». Оставлен только `*.json` | `Windows/ServerWindow/ServerWindowConfig.cs` | P1 |
| 31 | `CheckBridge` использовал общий каталог `TOR_DATA/check` (две параллельные проверки затирали друг друга). Теперь уникальный каталог на проверку + удаление после | `Handlers/Tor/TorManager.cs` | P2 |
| 32 | `XRAY_OVER_TOR` цеплял через Tor только `outbounds[0]`: остальные outbound (балансеры, резервные серверы) шли напрямую. Теперь через Tor идут все прокси-outbound (кроме `freedom`/`blackhole`/`dns`/`loopback` и самого Tor) | `Handlers/Tor/TorConfigBuilder.cs` | P1 |
| 33 | `MeekFrontSession.Dispose` освобождал `ManualResetEventSlim`, пока поток чтения ещё мог вызвать `ready.Set()` → `ObjectDisposedException` в фоне | `Handlers/Tor/MeekFrontSession.cs` | P2 |

## 3. Осталось (осознанно отложено)

| Приоритет | Находка | Почему отложено |
|---|---|---|
| P2 | `MoatClient` в Core-копии ходит через системный прокси с таймаутом 20 с (Windows-копия: direct/8 с); копии расходятся | Синхронизация копий — отдельная задача |
| P2 | Пробы геолокации: 7 эндпоинтов последовательно по 12 с (worst case ~84 с «Проверка…») | Параллельный опрос с общим дедлайном |

## 4. Проверка

| Проверка | Результат |
|---|---|
| `dotnet build InvisibleGorilla-XRay -c Release` | **0 ошибок** |
| `dotnet build InvisibleGorilla.Core -c Release` | **0 ошибок** |
| `dotnet run --project tests\AuditHarness -c Release` | **9/9 PASS** (до правок 7/9) |
| Ручные сценарии | не выполнялись (нужен реальный запуск и сеть) |

Запуск тестов:

```powershell
dotnet run --project tests\AuditHarness\AuditHarness.csproj -c Release
```

## 5. Ручные сценарии

1. **Прокси не залипает:** PROXY + системный прокси → подключиться → снять галочку → STOP → `ProxyEnable=0`, интернет работает.
2. **Прокси снимается при выходе:** PROXY → подключиться → закрыть приложение → `ProxyEnable=0`.
3. **Отмена старта:** RUN → CANCEL через 0.5–1 с (TUN) → UI не в «Connected», TUN и прокси сняты.
4. **Ошибка старта видна:** переименовать `Libraries/libXRayCore.dll` → RUN → сообщение об ошибке.
5. **TLS-проба:** подменить `api.ipify.org` самоподписанным → проба падает, чужой IP не показывается.
6. **Инъекция в torrc:** `obfs4 1.2.3.4:443` + перевод строки + `UseBridges 0` → в torrc ровно одна `UseBridges 1`.
7. **Имя подписки `..`:** импорт подписки с `profile-title: ..` → каталог приложения цел.
8. **Битый Settings.json:** записать `123` → приложение стартует, настройки из `.bak`.
9. **Битое обновление:** прервать скачивание → файл удалён, открыта страница релиза.
10. **Failover Goida:** заблокировать узел → проба падает с `timeout`/`SSL` → в логе `ReportTunnelFailure`, сервер меняется.
11. **Tor на Windows:** включить Tor с obfs4 → `Tor\Data\torrc` найден, bootstrap доходит до 100 % (раньше tor падал с «Unable to open configuration file»).
12. **Мост из torrc-формы:** вставить строку `Bridge obfs4 1.2.3.4:443 cert=…` → используется obfs4, а не прямое подключение.
13. **Смена сети:** подключиться, переключить Wi-Fi → через ~3 с сессия перезапускается.
14. **Goida-ротация:** сменить UUID в источнике и обновить список → активный узел и пул остаются рабочими (ID переназначены).

## 6. Артефакты

| Путь | Что |
|---|---|
| `.audit/tunnel.md` | 15 находок по туннелю/прокси/пробам |
| `.audit/switching.md` | 14 находок по Goida/OpenFlux |
| `.audit/config.md` | 16 находок по подпискам/конфигам/настройкам |
| `.audit/tor.md` | 14 находок по Tor/мостам |
| `tests/AuditHarness/` | 9 автотестов Tor-слоя |
