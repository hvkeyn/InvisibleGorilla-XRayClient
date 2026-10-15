# Orbot (Guardian Project) — технический разбор

**Версия кода, по которой сделан разбор:** ветка `master`, HEAD `0e06151de1a9fc3f5ae07244071b381c5fd14615` («Bump hev-socks5-tunnel to 2.19.0»), дата коммита 2026-10-15.
**Релиз, попавший в разбор артефактов:** `17.9.7-RC-1-tor-0.4.9.14` (APK `Orbot-17.9.7-RC-1-tor-0.4.9.14-1-g0ea112a4d-fullperm-arm64-v8a-release.apk`).
**Метод:** чтение исходников + разбор реального APK (`apksigner`, распаковка `lib/` и `assets/`) + официальные страницы Tor Project / Guardian Project / GitHub API.

Обозначения: **✅ проверено в коде/артефакте**, **⚠️ не подтверждено** (не нашёл подтверждения в источниках).

---

## 1. Архитектура

### 1.1 Два процесса и два сервиса

| Компонент | Файл | Роль |
|---|---|---|
| `OrbotService` | [OrbotService.java](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/OrbotService.java) | ✅ Наследник `android.net.VpnService` (строка 94). Fronted-сервис, `foregroundServiceType="systemExempted"`, `android:process=":tor"`, `android:stopWithTask="false"`, `android:permission="android.permission.BIND_VPN_SERVICE"`, `android:exported="true"` — [AndroidManifest.xml](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/AndroidManifest.xml) |
| `org.torproject.jni.TorService` | библиотека [tor-android](https://github.com/guardianproject/tor-android) | ✅ Демон Tor. В манифесте Orbot объявлен с `android:process=":tor"` и `tools:node="merge"`. `System.loadLibrary("tor")` — [TorService.java](https://raw.githubusercontent.com/guardianproject/tor-android/master/tor-android-binary/src/main/java/org/torproject/jni/TorService.java) |

Оба сервиса живут в отдельном процессе `:tor`, а не в основном процессе приложения. Это прямо задокументировано в README Orbot:

> «Recently `tor` was added to be its own Linux process on Android instead of having it run within the primary app process. That means that you will no longer see logs from `tor`, `OrbotService`, `OrbotVPNManager`…» — [README.md](https://github.com/guardianproject/orbot-android/blob/master/README.md), команды `adb logcat --pid=$(adb shell pidof -s "org.torproject.android.debug:tor")`.

### 1.2 Как именно запускается tor

✅ **tor — это не отдельный исполняемый файл, а библиотека `libtor.so`, загружаемая в процесс `:tor`.** Проверено двумя способами:

1. В `TorService.java` есть `static { System.loadLibrary("tor"); }` и набор `native`-методов: `createTorConfiguration()`, `mainConfigurationSetCommandLine(String[])`, `mainConfigurationSetupControlSocket()`, `runMain()`, `mainConfigurationFree()`, `prepareFileDescriptor(String)`. Аргументы передаются в `runMain()` программно, а не через `Runtime.exec`.
2. В реальном APK (arm64-v8a) присутствует `lib/arm64-v8a/libtor.so` размером **7 580 256 байт** — ✅ разобрано из APK. Отдельного `tor` в `assets/` нет.

Имя «`tor.so`» из формулировки задачи технически неточно: файл называется **`libtor.so`**. При этом сам Guardian Project пишет, что «The included `libtor.so` binaries can also be used directly as a tor daemon» — [README tor-android](https://raw.githubusercontent.com/guardianproject/tor-android/master/README.md).

**Порядок старта (✅ `TorService.torThread`):**

1. `createTorConfiguration()`.
2. `setDefaultProxyPorts()` — пишет `torrc-defaults` с `SOCKSPort 9050` / `HTTPTunnelPort 8118`, если порты свободны, иначе `auto`.
3. Запуск с `--verify-config` для проверки командной строки и torrc.
4. Стартует `controlPortThread`, который через `FileObserver` ждёт появления `ControlSocket` (таймаут 10 с), затем подключается.
5. Второй проход `mainConfigurationSetCommandLine()` + `mainConfigurationSetupControlSocket()` + `runMain()` — собственно запуск Tor.
6. `runMain()` выполняется в отдельном Java-`Thread` с именем `"tor"`, под статическим `ReentrantLock runLock`.

Командная строка Tor (✅): `--RunAsDaemon 0 -f <torrc> --defaults-torrc <torrc-defaults> --ignore-missing-torrc --SyslogIdentityTag TorService --CacheDirectory <cache>/TorService --DataDirectory <dir>/data --ControlSocket <dir>/data/ControlSocket --CookieAuthentication 0 --LogMessageDomains 1 --TruncateLogFile 1`.

Важно: `--CookieAuthentication 0`, а аутентификация на control-порту выполняется пустым паролем — `torControlConnection.authenticate(new byte[0])`. Control-порт — это **Unix-сокет**, не TCP; доступ ограничен правами файловой системы (`getAppTorServiceDataDir()` явно ставит `setReadable/setWritable/setExecutable(true, true)` — только владелец).

### 1.3 Управление через control port (TorControlConnection / jtorctl)

Orbot зависит от `info.guardianproject:jtorctl:0.4.5.7` и `info.guardianproject:tor-android:0.4.9.14` — [libs.versions.toml](https://github.com/guardianproject/orbot-android/blob/master/gradle/libs.versions.toml).

✅ `OrbotService.startTorService()` биндится к `TorService`, ждёт непустой `getTorControlConnection()` (опрос каждые 500 мс), спит ещё 1000 мс, ставит свой `OrbotRawEventListener` и подписывается на события:

```java
EVENT_STATUS_CLIENT, EVENT_OR_CONN_STATUS, EVENT_CIRCUIT_STATUS, EVENT_NOTICE_MSG,
EVENT_WARN_MSG, EVENT_ERR_MSG, EVENT_BANDWIDTH_USED, EVENT_NEW_DESC, EVENT_ADDRMAP
// + при Prefs.useDebugLogging():
EVENT_DEBUG_MSG, EVENT_STREAM_STATUS
```

Защита от «дублирования слушателей» (jtorctl `addRawEventListener` — это обычный `List.add`) реализована в `replaceRawEventListener(conn, staleListener, freshListener)` с TODO «would be nice to just merge jtorctl into tor-android».

По control-порту также читаются фактические порты (✅ `initControlConnection()`): `net/listeners/socks`, `net/listeners/httptunnel`, `net/listeners/dns`, `net/listeners/trans` → статические поля `mPortSOCKS`, `mPortHTTP`, `mPortDns`, `mPortTrans`.

**Команды/сигналы, отправляемые по control-порту (✅):**

| Метод | Что посылает |
|---|---|
| `stopTor()` | `conn.shutdownTor(SIGNAL_SHUTDOWN)` |
| `newIdentity()` | `conn.signal(SIGNAL_NEWNYM)` |
| `sendSignalActive()` | `conn.signal(SIGNAL_ACTIVE)` |
| `requestTorRereadConfig()` | `conn.signal(SIGNAL_RELOAD)` |
| `setExitNode()` | `setConf("ExitNodes"/"StrictNodes"/"GeoIPFile"/"GeoIPv6File")` + `DisableNetwork 1` → `0`; сброс — `resetConf(["ExitNodes","StrictNodes"])` |

### 1.4 Intent API

✅ `OrbotService.IncomingIntentRouter` обрабатывает: `ACTION_START`, `ACTION_STOP` (с extra `ACTION_STOP_FOREGROUND_TASK`), `ACTION_UPDATE_ONION_NAMES`, `ACTION_RESTART_VPN_IF_RUNNING`, `ACTION_STATUS`, `SIGNAL_RELOAD`, `SIGNAL_NEWNYM`, `LOCAL_ACTION_QUICK_SETTINGS_NEWNYM` (`"TILE_NEWNYM"`), `SIGNAL_ACTIVE`, `CMD_SET_EXIT` (`"setexit"`), `ACTION_LOCAL_LOCALE_SET`.

Константы действий — в [OrbotConstants.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/OrbotConstants.kt) и в `TorService` библиотеки tor-android:

- `ACTION_START = "org.torproject.android.intent.action.START"`
- `ACTION_STOP = "…action.STOP"`
- `ACTION_STATUS = "…action.STATUS"`, `ACTION_ERROR = "…action.ERROR"`
- `EXTRA_STATUS = "…extra.STATUS"`, `EXTRA_SERVICE_PACKAGE_NAME`
- Статусы: `STATUS_OFF`, `STATUS_ON`, `STATUS_STARTING`, `STATUS_STOPPING`

`STATUS_ON` библиотека выставляет **не по bootstrap-проценту, а по факту первого построенного канала**: `startedEventListener` слушает `EVENT_STATUS_CLIENT`, ждёт токен `CIRCUIT_ESTABLISHED` и только тогда рассылает `STATUS_ON` (✅ [TorService.java](https://raw.githubusercontent.com/guardianproject/tor-android/master/tor-android-binary/src/main/java/org/torproject/jni/TorService.java), константа `STATUS_CLIENT_CIRCUIT_ESTABLISHED`).

`replyWithStatus()` рассылает extras: `EXTRA_SOCKS_PROXY` (`socks://127.0.0.1:<port>`), `EXTRA_SOCKS_PROXY_HOST/PORT`, `EXTRA_HTTP_PROXY` (`http://127.0.0.1:<port>`), `EXTRA_HTTP_PROXY_HOST/PORT`, `EXTRA_DNS_PORT`.

✅ **Защита от внешнего запуска:** `EXTRA_NOT_SYSTEM = "org.torproject.android.intent.extra.NOT_SYSTEM"`. По комментарию в коде: «When present, indicates with certainty that the system itself did *not* send the Intent». Если extra отсутствует — Orbot считает, что VPN стартует система (always-on VPN), и в этом случае принудительно ставит `Prefs.putUseVpn(true)` и стартует, **игнорируя Power User Mode**. Это прямое следствие фикса Cure53-аудита `ORB-02-013 WP3: External Tor Activation Enabled by Default (Low)` — «Disable default behavior of allowing other apps to send Orbot a start intent» (видно в истории коммитов релиза 17.9.6-BETA-1).

### 1.5 VpnService + hev-socks5-tunnel

✅ `OrbotVpnManager` ([OrbotVpnManager.java](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/vpn/OrbotVpnManager.java)) строит туннель:

```java
builder.addAddress("198.18.0.1", 32)          // TProxyService.VIRTUAL_GATEWAY_IPV4
       .setMtu(8500)                          // TProxyService.TUNNEL_MTU
       .addRoute("0.0.0.0", 0)
       .addDnsServer("198.18.0.2")            // TProxyService.FAKE_DNS
       .setSession(Notifications.getVpnSessionName(mService))
       .addAddress("fc00::1", 128)            // VIRTUAL_GATEWAY_IPV6
       .addRoute("::", 0);
// API 29+: builder.setMetered(false); allowFamily(AF_INET); allowFamily(AF_INET6)
```

Константы — ✅ [TProxyService.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/TProxyService.kt); там же `System.loadLibrary("hev-socks5-tunnel")` и JNI-объявления `TProxyStartService(config_path, fd)`, `TProxyStopService()`, `TProxyIsRunning()`, `TProxyGetStats()`, `TASK_SIZE = 81920`.

Конфиг передаётся файлом `<cache>/tproxy.conf` (✅ `getHevSocksTunnelConfFile()`), генерируемый через YAML-строку:

```yaml
misc:   { log-level: warn, task-stack-size: 81920 }
tunnel: { ipv4: 198.18.0.1, ipv6: 'fc00::1', mtu: 8500 }
socks5: { port: <mTorSocks>, address: 127.0.0.1, udp: 'udp' }
mapdns: { address: 198.18.0.2, port: 53, network: 240.0.0.0, netmask: 240.0.0.0, cache-size: 10000 }
```

FD туннеля отдаётся в native через `TProxyService.TProxyStartService(conf, mInterface.getFd())` — с задержкой `DELAY_FD_LISTEN_MS = 5000` мс после `establish()`.

**Per-app routing** (✅ `doAppBasedRouting`):
- если выбраны конкретные приложения — `builder.addAllowedApplication(pkg)` для каждого torified-пакета (кроме самой Orbot);
- если ничего не выбрано и нет lockdown — `builder.addDisallowedApplication()` для Orbot и для `BYPASS_VPN_PACKAGES`;
- при `isLockdownEnabled()` (API 29+) — TODO: исключения Briar/OnionShare не реализованы, приложение не блокирует список (ссылка на issue #774).

`BYPASS_VPN_PACKAGES` (⚠️ не подтверждено — в коде это `mutableListOf`, но `const`-подобная семантика; фактически константный набор): `org.torproject.torbrowser_alpha`, `org.torproject.torbrowser`, `org.onionshare.android`, `org.onionshare.android.fdroid`, `org.briarproject.briar.android`, `im.cwtch.flwtch`. Комментарий: «to prevent tor over tor scenarios».

`VPN_SUGGESTED_APPS` — 14 пакетов (Signal `org.thoughtcrime.securesms`, DuckDuckGo, WhatsApp, Instagram, Element `im.vector.app`, Telegram, Twitter, Messenger, Messenger Lite, Brave, Firefox Focus, Vanadium, Molly, Accrescent).

**Отдельная деталь:** HTTP-прокси Orbot — только `CONNECT`, поэтому полноценный `setHttpProxy()` намеренно не используется (комментарий в коде со ссылкой на то, что прокси «only CONNECT and not a full proxy»).

### 1.6 Что делают `startTor()` / `startTorProcess` / `connectThroughTor`

⚠️ **Имена, которых в актуальном коде нет** — надо скорректировать ожидания:

- `startTorProcess` в `master` **отсутствует**. Есть `startTor()` (✅ OrbotService.java:388) и `startTorService()` (✅ OrbotService.java:460).
- `connectThroughTor` **отсутствует**. Логика «поднять VPN и пустить трафик через Tor» разнесена на `IncomingIntentRouter` → `mVpnManager.handleIntent()` → `setupTun2Socks()`.

`startTor()` (✅) под защитой `AtomicBoolean torStartRequested.compareAndSet(false, true)` (введено в PR #1809, чтобы `conn == null` не использовался как guard), сбрасывает прогресс (`setProgress(100, 0, false)`) и делегирует в `SmartConnect.handle(...)` с четырьмя лямбдами: `startTor`, `reconfigure`, `stopTor`, `completed`.

`startTorService()` (✅):
1. Пишет `torrc-defaults`: `DNSPort 0`, `TransPort 0`, `DisableNetwork 1` (то есть на этапе конфигурирования сеть Tor выключена).
2. `updateTorrcCustomFile()` → `TorConfig.build(...)` → `TorService.getTorrc(this)`.
3. `sendCallbackLogMessage(status_starting_up)`.
4. `bindService(Intent(this, TorService.class), …, BIND_AUTO_CREATE)`; на API 29+ — с `Executor`; результат пишется в `shouldUnbindTorService`; при `false` → `stopTorOnError("bindService failed")`.
5. В `onServiceConnected` — ожидание control-соединения, `initControlConnection()`, `replaceRawEventListener(...)`, `conn.setEvents(...)`.

### 1.7 Прочие компоненты

| Класс | Файл | Роль |
|---|---|---|
| `OrbotRawEventListener` | [OrbotRawEventListener.java](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/OrbotRawEventListener.java) | Разбор событий Tor, счётчик построенных нод, определение exit-ноды и её страны |
| `TorConfig` | [TorConfig.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/tor/TorConfig.kt) | Генерация `torrc` |
| `ShadowSocks` | [ShadowSocks.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/tor/ShadowSocks.kt) | Запуск `libsslocal.so` как subprocess |
| `Notifications` | [Notifications.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/Notifications.kt) | Канал `orbot_channel_1`, camo-уведомления |
| `Prefs` / `Settings` | [Prefs.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/util/Prefs.kt), [Settings.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/util/Settings.kt) | Хранилище настроек |
| `TProxyService` | [TProxyService.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/TProxyService.kt) | JNI-обёртка hev-socks5-tunnel |

---

## 2. Полный список типов мостов и транспортов

### 2.1 Enum `Transport` — канонический список

✅ [Transport.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/circumvention/Transport.kt):

| `Transport` | id (хранится в префах) | PT-модуль в IPtProxy | Встроенные мосты |
|---|---|---|---|
| `NONE` | `direct` | — | — |
| `MEEK` | `meek` | `IPtProxy.MeekLite` | `builtin.meek` (1 мост) |
| `OBFS4` | `obfs4` | `IPtProxy.Obfs4` (lyrebird) | `builtin.obfs4` |
| `SNOWFLAKE` | `snowflake` | `IPtProxy.Snowflake` | `builtin.snowflake` |
| `SNOWFLAKE_AMP` | `snowflake_amp` | `IPtProxy.Snowflake` | `builtin.snowflake`, перезаписанные на broker=`https://snowflake-broker.torproject.net/`, fronts=`www.google.com`, amp-cache=`https://cdn.ampproject.org/` |
| `SNOWFLAKE_SQS` | `snowflake_sqs` | `IPtProxy.Snowflake` | **отключён**: `throw RuntimeException("Snowflake SQS Not supported right now …/issues/1320")` |
| `WEBTUNNEL` | `webtunnel` | `IPtProxy.Webtunnel` | `builtin.webtunnel` (в поставке пусто) |
| `DNSTT` | `dnstt` | `IPtProxy.Dnstt` | `builtin.dnstt` + `dnsttBridges` + UDP-список по стране |
| `CUSTOM` | `custom` | по типу каждой строки | `Prefs.bridgesList` |

**Conjure: ❌ отсутствует.** Регистронезависимый поиск `conjure` по всему репозиторию — `No matches found` (✅ проверено `grep` по клону). В `MoatApi.SettingsRequest.transports` перечислены только `obfs4, snowflake, webtunnel, dnstt`. Conjure в Orbot не поддерживается.

**WebTunnel: есть в enum, но в UI недостижим.** ✅ В `ConfigConnectionBottomSheet.selectRadioButtonFromPreference()` и `askTor()` для `Transport.WEBTUNNEL` стоит `TODO()` с комментарием: «This should currently not happen, there's no default Webtunnel bridges advertised, yet.» Обратите внимание: `TODO()` в Kotlin бросает `NotImplementedError` — то есть при получении WebTunnel-транспорта от Moat приложение упадёт. Практически WebTunnel в Orbot сейчас **не используется**.

**`meek-azure` — не отдельный транспорт.** ✅ В `assets/builtin-bridges.json` ключ `"meek-azure"` содержит ровно ту же строку, что и `"meek"`, а `BuiltInBridges` знает только поля `meek, obfs4, snowflake, webtunnel, dnstt` — поле `meek-azure` при десериализации игнорируется (`Json { ignoreUnknownKeys = true }`). В UI вариант называется «meek-azure» (строка `bridge_meek_azure`), но транспорт один — `meek_lite`.

**Список в UI** (`ConfigConnectionBottomSheet`): Direct, Smart Connect, Snowflake, Snowflake (AMP), Snowflake (SQS, скрыт), Bridges via Telegram, Built-in obfs4, Bridges via Email, meek-azure, DNSTT (виден только для стран из списка), Custom bridges.

### 2.2 Микс транспортов в одном torrc

✅ `Transport.transportNames` для `CUSTOM` собирается так:

```kotlin
Prefs.bridgesList.mapNotNull { Bridge(it).transport }.filter { it.isNotBlank() }.toSet()
```

То есть список мостов **не обязан быть однородным** — если вставить строки `obfs4 …`, `webtunnel …` и `snowflake …`, Orbot поднимет все три PT-модуля и сгенерирует по одному `ClientTransportPlugin <name> socks5 127.0.0.1:<port>` на каждый. Для `NONE/MEEK/OBFS4/WEBTUNNEL/DNSTT` транспортов ровно один, для `SNOWFLAKE*` — `IPtProxy.Snowflake`.

### 2.3 Как хранятся пользовательские мосты

✅ `Prefs.bridgesList` ↔ ключ `pref_bridges_list`. Хранение — одна строка, строки разделены `\n`:

```kotlin
get() = cr?.getPrefString(PREF_BRIDGES_LIST)?.split("\n")?.filter { it.isNotBlank() }?.map { it.trim() } ?: emptyList()
set(value) = cr?.putPref(PREF_BRIDGES_LIST, value.filter { it.isNotBlank() }.joinToString("\n") { it.trim() })
```

Хранилище — `PreferenceProvider` (ContentProvider, `authorities="${applicationId}.provider.preferences"`, `android:exported="false"`). Прокси-креденшелы (`pref_proxy_type/host/port/username/password/pref_proxy_ss`) вынесены в **зашифрованный** `DataStore` (`AeadSerializer` + Tink, `AES128_GCM`, master key `android-keystore://_androidx_security_master_key_`), с fallback на незашифрованный файл, если encryption недоступна, и миграцией из SharedPreferences — ✅ [Settings.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/util/Settings.kt). Мосты в зашифрованное хранилище **не** перенесены.

**Валидация ввода** (✅ `CustomBridgeBottomSheet`):

```kotlin
^((meek_lite|obfs4|webtunnel|snowflake|dnstt)\s+)?
 ((\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}|\[[\da-fA-F:]+])(:\d{1,5})?)\s+
 ([\da-fA-F]{40})?[ \t\f\w\-/+:=.,\[\]]*$
```

Плюс нормализация Unicode-пробелов (`\p{Zs}` → обычный пробел) — фикс issue #1695 (письма Tor Project подставляют non-breaking space). Также есть импорт мостов по QR-коду (`quickie`), где JSON декодируется в `List<String>`; при дубликатах — тост `you_already_have_these_bridges`.

### 2.4 Встроенный список мостов и его обновление

✅ `assets/builtin-bridges.json` (2262 байта в коде, 2261 в APK). Фактическое содержимое:

- `meek`: 1 строка — `meek_lite 192.0.2.20:80 url=https://1603026938.rsc.cdn77.org front=www.phpmyadmin.net utls=HelloRandomizedALPN`
- `meek-azure`: та же строка (игнорируется)
- `obfs4`: **7 мостов** (212.83.43.74:443, 146.57.248.225:22, 212.83.43.95:443, 51.222.13.177:80, 45.145.95.6:27015, 37.218.245.14:38224, 209.148.46.65:443)
- `snowflake`: **2 моста** — `192.0.2.4:80` и `192.0.2.3:80`, оба с `url=https://1098762253.rsc.cdn77.org/`, `fronts=app.datapacket.com,www.datapacket.com`, `utls-imitate=hellorandomizedalpn` и списком из 8 STUN-серверов
- **`webtunnel`: отсутствует**
- **`dnstt`: отсутствует**

✅ **Механика обновления** ([BuiltInBridges.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/circumvention/BuiltInBridges.kt)):

1. Порядок чтения: сначала `cacheDir/updated-bridges.json`, если его нет — `assets/builtin-bridges.json`.
2. `isOutdated(context)` — файл обновлений считается устаревшим через **2 суток** (`2 * 24 * 60 * 60 * 1000` мс).
3. Если устарел — `AutoConf.do()` вызывает `api.builtin()` → `GET /moat/circumvention/builtin`, при успехе `bridges.store(context)` и `BuiltInBridges.invalidate()` для сброса кэша `instance`.
4. Дополнительно при каждом ответе Moat `AutoConf.extract()` **перезаписывает** `snowflake` (всегда, вне зависимости от `source`), а `obfs4`/`webtunnel`/`dnstt` — только если `bridge.source == "builtin"`.

⚠️ Это обновление вызывается **только при запуске Smart Connect или при нажатии кнопки «Ask Tor»**, а не по расписанию. Отдельного `WorkManager`-задания для обновления мостов в коде нет. При релизной сборке есть Gradle-задача `updateBuiltinBridges` (✅ `build.gradle.kts`), которая тянет список с torproject/Guardian Project перед сборкой APK.

### 2.5 Формат строки моста

✅ Парсер `Bridge.kt` поддерживает параметры: `transport`, `ip:port`, `fingerprint1` (40 hex), `fingerprint`, `url`, `front`, `fronts` (через запятую), `cert`, `iat-mode`, `ice`, `utls`, `utls-imitate`, `ver`, `udp`, `doh`, `dot`, `pubkey`, `domain`.

Отдельно решён известный краевой случай: **vanilla-мосты без транспорта**. Раньше различали по количеству кусков, что ломалось на `obfs4 192.0.2.5:443` (тоже 2 куска). Теперь первый токен проверяется на «похожесть на адрес» — содержит `.` или начинается с `[`.

### 2.6 DNSTT

✅ Дефолтные DNSTT-мосты (жёстко в коде, `BuiltInBridges.dnsttBridges`):

```
dnstt 192.0.2.4:1 A998F319ADB60EE344540EC4B21524CC484F96BE doh=https://dns.google/dns-query pubkey=241169008830694749fe96bb070c4855c5bb5b9c47b3833ed7d88521ba30a43f domain=t.ruhnama.net
dnstt 192.0.2.4:2 80EEFA4F4875ED2B7B5A86DF2D7588AD32E29F15 doh=https://dns.google/dns-query pubkey=a2fb71077eeaa54a02cda7a90be306af5d299ab21822a8b277d4eacbc9168631 domain=t2.bypasscensorship.org
```

Комментарий в коде объясняет нумерацию placeholder-адресов (`192.0.2.3` — Snowflake, `192.0.2.4` — dnstt) со ссылкой на [tpo/anti-censorship issue 40001](https://gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/trac/-/issues/40001#note_2811603). `MAX_DNSTT_BRIDGES_COUNT = 32 - 2 = 30` — потому что «Tor will max at 32 simultaneous SOCKS connections to PTs».

`getUdpDnstt(context, countryCode)` (✅) читает `assets/dns-<cc>.json` и размножает два базовых dnstt-моста по каждому DNS-серверу страны: подставляет `udp=<ip>:<port|53>`, убирает `doh`/`dot`, **убирает `fingerprint1`** (обоснование в коде: «otherwise only the first bridge lines with unique fingerprints will be used») и инкрементирует порт. Если результат больше 30 — случайная выборка 30.

Наличие asset-файлов (✅ проверено): `dns-ae, dns-af, dns-bd, dns-cn, dns-co, dns-global, dns-id, dns-ir, dns-kw, dns-pk, dns-qa, dns-ru, dns-sy, dns-tr, dns-ug, dns-uz`. Самый большой — `dns-ir.json` (**148 328 байт** в репозитории, 140 624 в APK), `dns-global.json` содержит публичные резолверы Google 8.8.8.8/8.8.4.4, Cloudflare 1.1.1.1/1.0.0.1 и т. д. Поле `lastUpdated` в файлах — `2026-01-20`/`2026-01-25`.

Страны с DNSTT-поддержкой (✅ `Regionalization.countriesWithDnsttSupport`): `AE, AF, BD, CN, CO, ID, IR, KW, PK, QA, RU, SY, TR, UG, UZ`.

### 2.7 Ссылки на официальную документацию Tor

- Что такое мост: [support.torproject.org/relays/getting-started/what-is-a-bridge](https://support.torproject.org/relays/getting-started/what-is-a-bridge/) — «Bridges are non-public Tor relays that aren't listed in the public directory».
- Установка obfs4-моста: [community.torproject.org/relay/setup/bridge](https://community.torproject.org/relay/setup/bridge/) — там же: «Note that the project was renamed to lyrebird» (про obfs4proxy).
- WebTunnel: [community.torproject.org/relay/setup/webtunnel](https://community.torproject.org/relay/setup/webtunnel/) — «a pluggable transport that attempts to imitate web browsing activities based on HTTPT», требования: статический IPv4, свой сайт+домен, валидный TLS-сертификат, ≥1 ГБ RAM.
- Snowflake: [community.torproject.org/relay/setup/snowflake](https://community.torproject.org/relay/setup/snowflake/).
- Как получить мосты (moat / сайт / Telegram / email): [support.torproject.org/tor-browser/circumvention/getting-bridges](https://support.torproject.org/tor-browser/circumvention/getting-bridges/) — Telegram-бот `@GetBridgesBot`, команды `/obfs4` и `/webtunnel`; email `bridges@torproject.org` только с Gmail/Riseup.
- Как пользоваться мостами: [support.torproject.org/tor-browser/circumvention/using-bridges](https://support.torproject.org/tor-browser/circumvention/using-bridges/) — там же про Bridge-moji: «**human-readable bridge identifiers** and do **not** represent the quality of connection».

---

## 3. «Переключение и проверка мостов на лету»

### 3.1 Переключение без перезапуска Tor — да, есть

✅ В `startTor()` Orbot передаёт в `SmartConnect` лямбду `reconfigure`:

```java
if (conn == null) return false;
conn.resetConf(Arrays.asList("UseBridges", "ClientTransportPlugin", "Bridge"));
conn.setConf(Prefs.getTransport().getTorConfig(this));
```

То есть набор мостов можно заменить **на живом Tor**: сбросить `UseBridges`, `ClientTransportPlugin`, `Bridge` и залить новый набор, при этом `DisableNetwork` не трогается, а `signal(SIGNAL_RELOAD)` используется в отдельном пути (`requestTorRereadConfig()`).

**Но есть два ограничения, которые в вопросе «без перезапуска?» надо учитывать:**

1. `reconfigure()` вызывается **только изнутри Smart Connect**. Если Smart Connect выключен, лямбда не исполняется вовсе — при обычном переключении транспорта UI вызывает `ConfigConnectionBottomSheet.closeAndConnect()` → `ConnectFragment.stopTorAndVpn()` + `Thread.sleep(3000)` → `attemptToStartTor()`, то есть **полный стоп/старт**.
2. Сам PT-процесс нужно поднять/погасить: `Transport.start(context)` и `Transport.stop()` управляют IPtProxy-контроллером; смена `OBFS4 ↔ SNOWFLAKE` требует смены транспорта в IPtProxy, что без остановки не сделать.

Итог: **переключение списка мостов одного и того же транспорта на лету реализовано технически (через `reconfigure`), но в пользовательском пути смена транспорта приводит к перезапуску Tor.**

### 3.2 Автоподбор мостов (Smart Connect)

✅ [SmartConnect.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/circumvention/SmartConnect.kt) — это и есть «автоматический перебор при неудаче». Механика:

1. Если `Prefs.smartConnect == false` — просто `startTor()`.
2. Иначе: `AutoConf.do(context)` → `Prefs.transport = conf.first ?: Transport.NONE`; если вернулись кастомные мосты — `Prefs.bridgesList = conf.second`.
3. `Prefs.transport.start(context)` + `startTor()`.
4. Заводится `Timer` с периодом **1000 мс**, а `connectionTimeout` вперёд сдвигается на `Prefs.smartConnectTimeout` секунд (**по умолчанию 30**, ключ `pref_smart_connect_timeout`).
5. `progress` обновляется из bootstrap-процентов (`SmartConnect.updateProgress`, вызывается из `sendCallbackLogMessage` и при `STATUS_ON` принудительно ставится 100).
6. Если `progress >= 100` — guard останавливается и **Smart Connect сам себя выключает** (`Prefs.smartConnect = false`).
7. Если таймаут вышел — перебор по строгой цепочке:

```
NONE → SNOWFLAKE → (bridgesList не пуст ? CUSTOM : OBFS4) → OBFS4 → [fail] stopTor(Exception("Smart Connect failed"))
```

То есть порядок ровно такой: **direct → snowflake → (custom | obfs4) → obfs4 → отказ**. Попытка на каждом шаге одна; цикл `do { … } while (!connected)` только до первого удачного `start()`.

Замечание по коду: у `Prefs.transport` для `SNOWFLAKE_SQS` в `Transport.start()` **всегда** бросается `RuntimeException` из-за issue #1320 — то есть Smart Connect, наткнувшись на `SNOWFLAKE_SQS`, поймает исключение и пойдёт дальше (`catch(_: Exception) {}`).

### 3.3 AutoConf и Moat

✅ [AutoConf.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/circumvention/AutoConf.kt) + [MoatApi.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/circumvention/MoatApi.kt) + [MoatTunnel.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/circumvention/MoatTunnel.kt).

**Два Moat-эндпоинта, оба доступны только через собственный PT:**

| | Tor Project | Guardian Project |
|---|---|---|
| Base URL | `https://bridges.torproject.org/moat/circumvention/` | `https://tns1.bypasscensorship.org/moat/circumvention/` |
| Транспорт | `IPtProxy.MeekLite` | `IPtProxy.Dnstt` |
| Конфиг | `url=https://1723079976.rsc.cdn77.org;front=www.phpmyadmin.net` | `doh=https://dns.google/dns-query;pubkey=c07ae9dd7b86ded6121c3db173de048bfd4f41de38dd430e3dfaf83ec8f36a06;domain=t1.bypasscensorship.org` |

Именно поэтому в FAQ Orbot сказано: «Hit "Ask Tor" – it will update the built-in Obfs4 bridge list, update the Snowflake configuration and also provide you with a bunch of custom bridges» ([orbot.app/en/faqs](https://orbot.app/en/faqs/)).

**REST-эндпоинты Moat** (✅ `MoatApi`): `POST settings`, `POST defaults`, `GET map`, `GET builtin`. Запрос `settings` по умолчанию перечисляет транспорты `[obfs4, snowflake, webtunnel, dnstt]` (conjure отсутствует). В `AutoConf.do()` обрабатываются ошибки `404` («Needs transport, but not the available ones») и `406` («no country from IP address») — при них принудительно выставляется `cannotConnectWithoutPt = true`.

Особый нюанс, зафиксированный в комментарии: **GP-Moat доступен только когда пользователь явно указал страну**, потому что за DNSTT-туннелем нет source IP.

Кастомные мосты от Moat **добавляются** к пользовательским (`customBridges = Prefs.bridgesList.toMutableSet() + conf.second`), а не заменяют их, и `Prefs.smartConnect` при «Ask Tor» сбрасывается в `false`.

Установка `Authenticator` для Moat использует пароль `"\u0000"` — это стандартный механизм «пароль не нужен» для Java `Authenticator` (см. комментарий в `AutoConf.do`).

**Кэш состояния мостов:** ✅ только `cacheDir/updated-bridges.json` (2 суток). Отдельного кэша «какие мосты работали/не работали» в коде нет.

### 3.4 ReachableAddresses

✅ `Prefs.reachableAddresses` (ключ `pref_reachable_addresses`, по умолчанию `false`) + `Prefs.reachableAddressesPorts` (ключ `pref_reachable_addresses_ports`, XML-defaultValue **`*:80,*:443`**). В `TorConfig.build()`:

```kotlin
if (Prefs.reachableAddresses) {
    val reachableAddressesPorts = Prefs.reachableAddressesPorts ?: "*:80,*:443"
    conf.add("ReachableAddresses $reachableAddressesPorts")
}
```

UI: подкатегория `category_reachable_addresses`, EditText становится активным только при включённом чекбоксе (`SettingsPreferenceFragment`).

### 3.5 «Power User Mode» и «Kindness Mode»

**Power User Mode** (✅):

- Преф `pref_power_user`, по умолчанию `false`, читается через `Prefs.isPowerUserMode`.
- Строка UI: «For users familiar with Tor. Allows you to start Orbot without the VPN setting and shows open SOCKS and HTTP ports» (`strings.xml`, `power_user_description`).
- В `ConnectFragment.attemptToStartTor()`: `Prefs.putUseVpn(!Prefs.isPowerUserMode)` — то есть в Power User Mode VPN **не** поднимается, Tor работает как обычный прокси.
- Ветка `attemptToStartTorPowerUserMode()` требует: (а) `AlarmManager.canScheduleExactAlarms()` на API 31+, иначе диалог `PowerUserForegroundPermDialog`; (б) диалог про battery optimizations (`PowerUserBatteryOptimizations`).
- Обоснование права `SCHEDULE_EXACT_ALARM` в строках: «This won't actually set any alarms on your device. This permission is needed to have the system keep OrbotService running when Orbot is not being used as a system-wide VPN.»
- В Power User Mode из меню подключения **убирается** пункт «Choose apps».

**Kindness Mode** (✅ снежный прокси-волонтёрство):

- `Prefs.beSnowflakeProxy` (ключ `pref_be_a_snowflake`, по умолчанию `false`).
- `Prefs.lastSnowflakeNatType` (ключ `pref_snowflake_last_nat`), значения — `IPtProxy.NATUnknown` / `NATRestricted` / `NATUnrestricted`.
- Лимиты: `pref_be_a_snowflake_limit_wifi` (**по умолчанию `true`** — «this *should* be true, we want snowflake to be Wi-Fi only», issue #1798) и `pref_be_a_snowflake_limit_charing` (**по умолчанию `false`**; обратите внимание на опечатку в ключе — `charing`).
- Счётчики: `pref_snowflakes_served`, `pref_snowflakes_served_weekly`, `pref_snowflakes_served_week` (неделя = `days/7`).
- Сервис `SnowflakeProxyService` (`foregroundServiceType="remoteMessaging"`, канал `snowflake`, notification id 103). Идентификатор прокси: **`SNOWFLAKE_PROXY_IDENTIFIER = "orbot-android"`** с комментарием «don't change the proxy identifier without getting permission from torproject».
- `SNOWFLAKE_PROXY_MAX_CLIENT_CAPACITY = 1`, `SNOWFLAKE_PROXY_BROKER_POLL_INTERVAL_SECONDS = 120`.
- **UPnP:** `mapPorts()` берёт случайный старт в диапазоне `49152..65534` и открывает **3 последовательных UDP-порта**. Если не удалось получить все три — все закрываются, и прокси работает в restricted-режиме (комментарий: «Snowflake Proxy needs Capacity * 2 + 1 = 3 consecutive ports mapped for unrestricted mode»). Порты сохраняются в `pref_snowflake_upnp_ports`, чтобы закрыть их после аварийного завершения процесса (issue #1795).
- **Региональное ограничение:** `Regionalization.isKindnessModeDisabledForCountry()` → `AFGHANISTAN, IRAN`. При смене страны, если это AF/IR, `Prefs.bridgeCountry`-сеттер принудительно выключает `beSnowflakeProxy` и выставляет `snowflakeNeedsQualityCheck = true`. В UI кнопка «Continue» блокируется и текст меняется на `kindness_mode_unsupported_country`.
- **Watchdog:** `KindnessWatchdogJob` на `JobScheduler` (был `WorkManager`), следит за флагом `SnowflakeProxyService.isRunning`, чтобы отличить «выключено пользователем» от «убито системой» (#1799, #1783).
- Проверка качества NAT (`snowflake-nat-probe = https://snowflake-broker.torproject.net:8443/probe`) раз в 24 часа (`Prefs.snowflakeNeedsQualityCheck`).
- Брокер/релей прокси берутся из `assets/fronts` (✅ содержимое): `snowflake-target-direct https://snowflake-broker.torproject.net/`, `snowflake-relay-url wss://snowflake.bamsoftware.com`, `snowflake-nat-probe https://snowflake-broker.torproject.net:8443/probe`.

**Camo Mode / Camouflage** (✅): `Prefs.isCamoEnabled`, список альтернативных иконок в манифесте — `NightWatch`, `Assistant`, `Paint`, `Tetras`, `ToDo`, `FitGrit`, `Birdie`, `OrbotAlt1..4`. При включённом camo уведомление подменяется (`Notifications.configureCamoNotification`), а имя VPN-сессии становится `Prefs.camoAppDisplayName` (по умолчанию строка `"Android"`).

---

## 4. Полный список пользовательских настроек

### 4.1 `res/xml/preferences.xml` (✅ файл существует, 13 530 байт)

| Категория | Ключ | Тип | Default | Назначение |
|---|---|---|---|---|
| General | `pref_default_locale` | ListPreference | — | Язык |
| General | `pref_start_boot` | CheckBox | **true** | Автозапуск при загрузке (см. `OnBootReceiver`) |
| General | `pref_open_proxy_on_all_interfaces` | CheckBox | **false** | SOCKS на `0.0.0.0`; на API 37+ требует `ACCESS_LOCAL_NETWORK` |
| General | `pref_power_user` | CheckBox | **false** | Power User Mode |
| General | `pref_open_battery_opt` | Preference | — | Диалог battery optimizations |
| General | `pref_detect_root` | CheckBox | **true** | Предупреждение о root (RootBeer) |
| Volunteer mode | `pref_show_snowflake_proxy_msg` | CheckBox | false | Тост при подключении клиента к вашему прокси |
| Node config | `pref_entrance_nodes` | EditText | — | `EntryNodes` (применяется **только при `Transport.NONE`**) |
| Node config | `pref_exit_nodes` | EditText | — | `ExitNodes` |
| Node config | `pref_exclude_nodes` | EditText | — | `ExcludeNodes` |
| Node config | `pref_strict_nodes` | CheckBox | false | `StrictNodes` |
| Reachable addresses | `pref_reachable_addresses` | CheckBox | false | Включает `ReachableAddresses` |
| Reachable addresses | `pref_reachable_addresses_ports` | EditText | `*:80,*:443` | Значение портов |
| Connectivity | `pref_isolate_dest` | CheckBox | false | `IsolateDestAddr` |
| Connectivity | `pref_isolate_port` | CheckBox | false | `IsolateDestPort` |
| Connectivity | `pref_isolate_protocol` | CheckBox | false | `IsolateClientProtocol` |
| Connectivity | `pref_isolate_keep_alive` | CheckBox | false | `KeepAliveIsolateSOCKSAuth` |
| Connectivity | `pref_prefer_ipv6` | CheckBox | **true** | `IPv6Traffic PreferIPv6` |
| Connectivity | `pref_disable_ipv4` | CheckBox | false | `IPv6Traffic NoIPv4Traffic` |
| Padding | `pref_connection_padding` | CheckBox | false | `ConnectionPadding 1` |
| Padding | `pref_reduced_connection_padding` | CheckBox | **true** | `ReducedConnectionPadding 1` |
| Padding | `pref_circuit_padding` | CheckBox | **true** | `CircuitPadding 1` |
| Padding | `pref_reduced_circuit_padding` | CheckBox | **true** | `ReducedCircuitPadding 1` |
| Proxy | `pref_proxy_type` | SecureListPreference | — | `https` / `socks4` / `socks5` / `ss` |
| Proxy | `pref_proxy_host` / `_port` / `_username` / `_password` | SecureEditText | — | Реквизиты outbound-прокси |
| Proxy | `pref_proxy_ss` | SecureEditText | — | Shadowsocks-URL (`ss://…`) |
| Debug | `pref_socks` | EditText | **9050** | `SOCKSPort` |
| Debug | `pref_http` | EditText | **8118** | `HTTPTunnelPort` |
| Debug | `pref_transport` | EditText | **auto** | `TransPort` |
| Debug | `pref_dnsport` | EditText | **auto** | `DNSPort` |
| Debug | `pref_custom_torrc` | EditText | — | Произвольные строки в конец `torrc` |
| Debug | `pref_enable_logging` | CheckBox | false | `Log debug syslog`, `SafeLogging 0`, + доп. события control-порта |

Примечание к Proxy: в `preferences.xml` есть строка «note_snowflake_doesn_t_support_proxies» — Snowflake не поддерживает upstream-прокси. В `Transport.start()` для `IPtProxy.Snowflake` и `IPtProxy.Dnstt` вызывается `controller.start(transport, null)` — прокси действительно **не передаётся**, тогда как для остальных транспортов передаётся `proxy?.toString()`.

### 4.2 `res/xml/safety_prefs.xml`

| Ключ | Тип | Default | Смысл |
|---|---|---|---|
| `pref_key_camo_dialog` | PreferenceScreen | — | Выбор иконки-камуфляжа (ведёт в `CamoFragment`) |
| `pref_flag_secure` | CheckBox | false | **Блокировка скриншотов** (`FLAG_SECURE`, `BaseActivity.resetSecureFlags()`) |
| `pref_require_password` | CheckBox | false | Требовать аутентификацию при открытии |
| `pref_auth_no_biometrics` | CheckBox | false, `isPreferenceVisible="false"` | Запрет биометрии (видно только на API 30+ и при включённом пароле) |

Блокировка скриншотов реализована флагом окна, а не через `setRecentsScreenshotEnabled`. В настройках также применяется `IME_FLAG_NO_PERSONALIZED_LEARNING` ко всем `EditTextPreference` (✅ `AbstractPreferenceFragment.setNoPersonalizedLearningOnEditTextPreferences`).

### 4.3 Настройки, отсутствующие в XML (программные)

| Что | Как включается | Файл |
|---|---|---|
| Тип подключения (`pref_connection_pathway`) | `ConfigConnectionBottomSheet` | `Transport.kt` |
| `Prefs.smartConnect` (`pref_use_smart_connect`) | RadioButton «Smart Connect» | `Prefs.kt` |
| `Prefs.smartConnectTimeout` (`pref_smart_connect_timeout`) | **не имеет UI**, только код, default `30` | `Prefs.kt` |
| `Prefs.bridgeCountry` (`pref_bridge_country`) | Поле автодополнения страны в «Ask Tor» | `Prefs.kt` |
| `Prefs.bridgesList` (`pref_bridges_list`) | `CustomBridgeBottomSheet` (ввод + QR) | `Prefs.kt` |
| Per-app VPN (`PrefTord`) | `AppManagerFragment` | `Prefs.torifiedApps` |
| Snowflake proxy (вкл/выкл, лимиты Wi-Fi/зарядки) | `KindnessFragment`, `KindnessConfigBottomSheet` | `Prefs.kt` |
| Onion services v3 + client authorization | `OnionServiceActivity`, `ClientAuthActivity` | `OnionServiceColumns.kt`, `V3ClientAuthColumns.kt` |
| `Prefs.currentVersionForUpdate`, `isGeoIpReinstallNeeded` | Служебные | `Prefs.kt` |

**Per-app VPN** (✅ `AppManagerFragment` + `TorifiedApp.getApps`): список приложений строится из `PackageManager.getInstalledApplications(0)`; в список попадают только приложения с разрешением `INTERNET` (или системные, или явно перечисленные); для каждого проверяется позиция в отсортированном списке `Prefs.torifiedApps` бинарным поиском. Есть поиск с debounce 250 мс и список «предлагаемых» — `OrbotConstants.VPN_SUGGESTED_APPS`.

### 4.4 Onion services

✅ `OnionServiceActivity` / `OnionServiceCreateDialogFragment` — хостинг v3 onion-сервисов; `ClientAuthActivity` — управление client authorization; `V3BackupUtils` + `ZipUtilities` — экспорт/импорт бэкапа. Каталоги: `ONION_SERVICES_DIR = "v3_onion_services"`, `V3_CLIENT_AUTH_DIR = "v3_client_auth"`. Строки добавляются в `torrc` через `OnionServiceColumns.addV3OnionServicesToTorrc(...)` и `V3ClientAuthColumns.addClientAuthToTorrc(...)`.

### 4.5 «No Network Auto-Sleep» и автозапуск

✅ **Явной настройки «No Network Auto-Sleep» в коде нет — есть автоматическое состояние `NoInternet`:**

```kotlin
// ConnectViewModel.updateState
status == STATUS_STARTING -> Starting(progress)
status == STATUS_ON       -> On
status == STATUS_STOPPING -> Stopping
!NetworkUtils.isNetworkAvailable(context, allowOtherVpnApps = true) -> NoInternet
else -> Off
```

UI: `switchConnect.visibility = View.GONE`, картинка `orbiesleeping`, текст `no_internet_title`/`no_internet_subtitle`. Плюс `DormantClientTimeout 10 minutes` и `DormantCanceledByStartup 1` в `torrc`.

`isNetworkAvailable(..., allowOtherVpnApps = true)` считает TRANSPORT_VPN за доступную сеть.

✅ **Автозапуск:** `OnBootReceiver` слушает `BOOT_COMPLETED` и `QUICKBOOT_POWERON`; игнорирует событие, если `SystemClock.uptimeMillis() > 10 минут` (защита от ложного срабатывания при деплое из Android Studio); ставит `EXTRA_NOT_SYSTEM`; отдельно поднимает snowflake-прокси, если `Prefs.beSnowflakeProxy && !isKindnessModeDisabledForCountry()`.

✅ **Quick Settings tiles:** `LaunchOrbotTileService` и `RefreshConnectionTileService` (второй посылает `TILE_NEWNYM` → `newIdentity(true)`).

### 4.6 Регионализация и exit-ноды

✅ [Regionalization.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/Regionalization.kt):

- `countriesForExitNodeUi` — 24 кода: `DE, AT, SE, CH, IS, CA, US, ES, FR, BG, PL, AU, BR, CZ, DK, FI, GB, HU, NL, JP, RO, RU, SG, UA` (SE продублирован в списке).
- `countriesWithDnsttSupport` — 15 кодов (см. §2.6).
- `isKindnessModeDisabledForCountry()` — `AF`, `IR`.
- `getFlagEmojiForCountryCode()` — флаг из regional indicator symbols; `getLocalizedNameForCountryCode()` — `Locale.Builder().setRegion(...).displayCountry`.
- При выборе exit-ноды (`ExitNodeBottomSheet` → `ConnectFragment.onExitNodeSelected`) значение оборачивается в фигурные скобки `{CC}` и отправляется интентом `CMD_SET_EXIT` с extra `"exit"`.

### 4.7 Что есть в `torrc`, но не в UI

Генерируется `TorConfig.build()` жёстко: `RunAsDaemon 1`, `AvoidDiskWrites 1`, `SafeSocks 0`, `TestSocks 0`, `VirtualAddrNetwork 10.192.0.0/10`, `AutomapHostsOnResolve 1`, `DormantClientTimeout 10 minutes`, `DormantCanceledByStartup 1`, `DisableNetwork 0`, `GeoIPFile`/`GeoIPv6File` (если файлы есть). При `openProxyOnAllInterfaces` добавляется `SocksPolicy accept *:*`.

---

## 5. Как Orbot измеряет и показывает прогресс/качество соединения

### 5.1 Bootstrap-проценты

✅ `OrbotService.sendCallbackLogMessage(String)` — единственное место извлечения прогресса:

```java
if (logMessage.contains(EVENT_STATUS_CLIENT)) {
    var bootstrapIndex = logMessage.indexOf(LOG_NOTICE_BOOTSTRAPPED);   // "BOOTSTRAP PROGRESS="
    if (bootstrapIndex != -1) {
        var subStr = logMessage.substring(bootstrapIndex + LOG_NOTICE_BOOTSTRAPPED.length());
        var arr = subStr.split(" ");
        var progress = Integer.parseInt(arr[0].trim());
        localIntent.putExtra(LOCAL_EXTRA_BOOTSTRAP_PERCENT, "" + progress);  // extra "percent"
        SmartConnect.updateProgress(progress);
        if (mNotifyBuilder != null) mNotifyBuilder.setProgress(100, progress, false);
    }
}
```

То есть: **источник — текст сообщения Tor, а не структурированное поле**. Константа `LOG_NOTICE_BOOTSTRAPPED = "BOOTSTRAP PROGRESS="` (✅ `OrbotConstants.kt`). Это штатный формат Tor: `STATUS_CLIENT NOTICE BOOTSTRAP PROGRESS=…`.

Дальше прогресс идёт двумя путями:
1. Broadcast `LOCAL_ACTION_LOG` (`"log"`) с extras `LOCAL_EXTRA_LOG` (`"log"`) и `LOCAL_EXTRA_BOOTSTRAP_PERCENT` (`"percent"`) → `OrbotActivity` → `ConnectViewModel.updateBootstrapPercent(Int)` → `ConnectUiState.Starting.bootstrapPercent` → `binding.progressBar.progress`.
2. Напрямую в `NotificationCompat.Builder.setProgress(100, progress, false)`.

### 5.2 Определение «соединение живо»

✅ Три независимых механизма:

1. **`STATUS_ON` от библиотеки** — по событию `CIRCUIT_ESTABLISHED` (см. §1.4). Самый надёжный сигнал.
2. **Smart Connect guard** — таймер раз в секунду, `progress` монотонно не убывает (`if (progress > this.progress)`); «живо» = прогресс вырос за последние `smartConnectTimeout` секунд.
3. **`SIGNAL_ACTIVE`** — `OrbotActivity.onResume()` посылает сервису `SIGNAL_ACTIVE`; `OrbotService.sendSignalActive()` вызывает `conn.signal(SIGNAL_ACTIVE)` только если `conn != null && mCurrentStatus == STATUS_ON`. Ответ приходит всегда через `replyWithStatus()`.

Прогресс-бар убирается при `STATUS_ON` (`mNotifyBuilder.setProgress(0, 0, false)`).

### 5.3 Расширенные уведомления и страна exit-ноды

✅ `OrbotRawEventListener` реализует интересную оптику:

- `EVENT_BANDWIDTH_USED` → `handleBandwidth(read, written)` → строка вида `"1 234 KiB/s ↓ / 567 KiB/s ↑"`; иконка меняется на `ic_stat_tor_xfer`, если хотя бы одно значение ≠ 0. Формат — `NumberFormat.getInstance()`, единицы — `< 1e6` → `kibibyte_per_second`, иначе `mebibyte_per_second`.
- `EVENT_CIRCUIT_STATUS` + `CIRC_EVENT_BUILT` → запоминается fingerprint последней ноды; внутренние каналы фильтруются по флагам `ONEHOP_TUNNEL` и `IS_INTERNAL`.
- `EVENT_STREAM_STATUS` + `STREAM_EVENT_SUCCEEDED` + протокол `SOCKS5` → запрос по control-порту: `conn.getInfo("ns/id/<fingerprint>")` (берётся 7-й токен = IP) и `conn.getInfo("ip-to-country/<ip>")`; результат (флаг + название страны) пишется в subtext уведомления. Для `.onion`-адресов данные не показываются («don't display to users exit node info for onion addresses!»).
- Ответ `??` (`TOR_CONTROLLER_COUNTRY_CODE_UNKNOWN`) трактуется как «страна неизвестна».

### 5.4 Диагностика

| Инструмент | Где | Что даёт |
|---|---|---|
| `pref_enable_logging` | Debug | `Log debug syslog` + `SafeLogging 0` в torrc, дополнительные события `EVENT_DEBUG_MSG`, `EVENT_STREAM_STATUS` |
| `LogBottomSheet` | UI (тап по логотипу в состоянии Starting/On) | Лог Orbot, кнопка копирования (`ic_copy_log`) |
| `Prefs.orbotServiceLogAppend` / `getOrbotServiceLog` | `pref_orbotservice_log` | Лог сервиса в префах; очищается в `onCreate()` сервиса; отдельно чинился баг дублирования строк (#1811) |
| `check.torproject.org` | `ConnectMenuActionAdapter.checkTorStatusUrl()` | Открывает проверку Tor **в приложении, которому разрешён Tor** — интент адресуется конкретному пакету (`package = pkg`) |
| Официальный FAQ | [orbot.app/en/faqs](https://orbot.app/en/faqs/) | «you will see a message that says 100% connected, and the button will turn green… you can also check your connection by going to check.torproject.org» |
| `./scripts/view_logs_tmux.sh` | репозиторий | Два `adb logcat` рядом: основной процесс и `:tor` |
| Подпись/`GeoIP`/`Private DNS` | `ConnectFragment.updatePrivateDnsLabel()` | Предупреждение, если включён Private DNS в режиме Strict (issue #1707) |

**Отдельного «connection test» в основном приложении нет.** «Connection test» существует только в Kindness Mode: `TestingDialogFragment` + `TestTorForSnowflakeProxyService` — это тест, может ли устройство работать как snowflake-прокси (NAT-тип), а не тест соединения Orbot.

---

## 6. Лицензии, размер, состав нативных библиотек

### 6.1 Лицензии

- **Сам Orbot — BSD-3-Clause.** Guardian Project: «License: BSD-3-Clause» — [guardianproject.info/apps/org.torproject.android](https://guardianproject.info/apps/org.torproject.android/). В `LICENSE` (репозиторий): «Orbot is distributed under this license (aka the 3-clause BSD license). Copyright (c) 2009-2026, Nathan Freitas, The Guardian Project».
- **Tor** — та же трёхпунктная BSD, «Copyright (c) 2007-2026, The Tor Project, Inc.».
- **OpenSSL** — упоминание «This product includes software developed by the OpenSSL Project».
- **IP-to-Country Database** — от WebHosting.Info (используется в `geoip`).
- **quickie QR** — MIT, «Copyright (C) 2022 Thomas Wirth».
- Стоит отметить: в `LICENSE` перечислены **не все** компоненты — там нет отдельных записей для IPtProxy, hev-socks5-tunnel, Snowflake, lyrebird, jtorctl, Tink, retrofit и т. д. ⚠️ Для полноты лицензионного аудита APK этого файла недостаточно.

### 6.2 Размер

✅ APK `Orbot-17.9.7-RC-1-tor-0.4.9.14-1-g0ea112a4d-fullperm-arm64-v8a-release.apk`:
- размер **35 860 127 байт** (≈ 34,2 МиБ), совпадает с `size` в GitHub API;
- SHA-256: `97d3a0a5181394e99fbabdda9fefb73027657cac4c7400c64ebf146a7778860b` — совпадает с `digest: "sha256:97d3a0…760b"` из GitHub API (✅ сверено локально `Get-FileHash`).

Другие сборки того же релиза (⚠️ в этом релизе на GitHub **только** arm64-вариант; universal-сборки есть у соседних релизов): `17.9.6-RC-1` universal — **82 550 661** байт, `17.9.6-BETA-1` universal — **79 394 667** байт.

### 6.3 Нативные библиотеки в APK (arm64-v8a, ✅ распаковано)

| `.so` | Размер, байт | Что это |
|---|---|---|
| `libtor.so` | 7 580 256 | Сам Tor (C) + статически слинкованные libevent / OpenSSL / zlib / zstd |
| `libgojni.so` | 24 973 816 | IPtProxy — Go-мост ко **всем** pluggable transports (Snowflake 2.15.1, lyrebird/obfs4 0.9.0, meek_lite, WebTunnel, DNSTT) |
| `libsslocal.so` | 5 857 520 | Shadowsocks-rust клиент (`sslocal`), запускается как **subprocess** из `nativeLibraryDir` |
| `libhev-socks5-tunnel.so` | 344 056 | hev-socks5-tunnel 2.19.0 — tun2socks |
| `libbarhopper_v3.so` | 4 946 720 | Google ML Kit barcode (для QR-сканирования мостов через `quickie`) |
| `libdatastore_shared_counter.so` | 10 376 | AndroidX DataStore |
| `libimage_processing_util_jni.so` | 32 528 | ML Kit |
| `libsurface_util_jni.so` | 4 896 | ML Kit |
| `libtoolChecker.so` | 5 512 | RootBeer |

✅ **Важное уточнение по `libsslocal.so`:** это не библиотека, а исполняемый файл. `ShadowSocks.start()` делает `ProcessBuilder(File(context.applicationInfo.nativeLibraryDir, "libsslocal.so"), "-b", address, "--server-url", serverUrl)`. Чтобы Android распаковал `.so` на диск (иначе его нельзя исполнить), в `build.gradle.kts` включено `packaging { jniLibs { useLegacyPackaging = true } }` с комментарием про ровно эту причину. Бинарники взяты из [shadowsocks-android](https://github.com/shadowsocks/shadowsocks-android), воспроизвести сборку нельзя из-за несовместимости плагина `org.mozilla.rust-android-gradle.rust-android` с новым Gradle. `isShadowSocksSupported()` возвращает `false`, если первый `SUPPORTED_ABIS` == `x86`; в этом случае схема `ss` убирается из списка proxy type.

### 6.4 Что залинковано в `libtor.so`

✅ По README tor-android: tor **0.4.9.14**, libevent **2.1.13**, OpenSSL **3.5.7**, zlib **1.3.2**, zstd **1.5.7**. Собирается через NDK toolchain **30.0.16248370**, воспроизводимо через Vagrant VM по `.gitlab-ci.yml`.

### 6.5 Версии сборки и компиляции

✅ `app/build.gradle.kts`: `orbotBaseVersionCode = 1797300100`, `namespace/applicationId = org.torproject.android`, `minSdk = 24`, `targetSdk = 37`, `compileSdk` = release(37) minorApiLevel 2, `multiDexEnabled = true`, `ndkVersion = "30.0.16248370"`, `java.toolchain` = JDK 24, Kotlin `KOTLIN_2_4`, `isMinifyEnabled = false`, `isShrinkResources = false` (то есть **R8/ProGuard не применяется** — это важно для разбора APK, код читаем).

ABI-сплиты: `x86, armeabi-v7a, x86_64, arm64-v8a` + `isUniversalApk = true`. `versionCode` инкрементируется по ABI: `armeabi-v7a +1, arm64-v8a +2, x86 +4, x86_64 +5`. `versionName` берётся из `git describe --tags --always`. Флейворы: `fullperm` и `nightly` (`applicationId = org.torproject.android.nightly`, `versionCode = сейчас/1000`).

### 6.6 Assets в APK

`geoip` — **9 775 942** байта, `geoip6` — **16 599 769** байт (это ~70 % всего APK), `ISRG Root X1.cer` (1391), `LICENSE`, `builtin-bridges.json`, 17 файлов `dns-*.json`, `fronts`, `mlkit_barcode_models/*.tflite` (3 файла). `ISRG Root X1.cer` подключается в `MoatApi` для API 24/25, у которых нет корневого сертификата Let's Encrypt в системном сторе.

### 6.7 Подпись APK (важно, см. §7)

✅ `apksigner verify --print-certs` для скачанного GitHub-релиза:

```
Verifies
Verified using v1 scheme (JAR signing): false
Verified using v2 scheme (APK Signature Scheme v2): true
Verified using v3 scheme: false
Number of signers: 1
Signer #1 certificate DN: CN=Tor Project, OU=TorProject.org, O=TorProject.org, L=New York, ST=NY, C=US
Signer #1 certificate SHA-256: a454b87a1847a89ed7f5e70fba6bba96f3ef29c26e0981204fe347bf231dfd5b
Signer #1 certificate SHA-1: cd142accde63fe57c1c52858e19d1b37c76422ce
Signer #1 key algorithm: RSA
Signer #1 key size: 1024 bits
```

---

## 7. Ложные срабатывания антивирусов и подделки Orbot

### 7.1 Ложные срабатывания — подтверждённые случаи

✅ **[Issue #952 «Security app detected Trojan»](https://github.com/guardianproject/orbot-android/issues/952)** (открыт 2023-08-02, закрыт 2023-08-23):

- Детект от **ESET Mobile Security**: имя **`Android/Spy.Agent.CTK`** (со скриншотов пользователя).
- Пользователь пишет: реагировало и на сборку из GitHub, и **на установку из Google Play**; ESET блокировала даже загрузку APK. Также упоминается установка через **FFUpdater** — и на неё та же реакция.
- Ответ мейнтейнера Nathan Freitas (`n8fr8`, MEMBER), 2023-08-03: «Can you test again with a clean install from google play? or you can test from our signed apk here: …» и отдельным комментарием: **«(That said, we've seen false positives like this before)»**.
- 2023-08-04, `n8fr8`: **«I also can't find any information online about Android/Spy.Agent.CTK»**.
- 2023-08-04, `n8fr8`: «Seems like this has happened with Tor Browser» — со ссылкой на форум ESET [topic/26520](https://forum.eset.com/topic/26520-eset-mobile-security-detects-tor-browser-as-malware/).
- Итог: пользователь оставил приложение в списке исключений.

✅ **[Issue #1107 «[BUG] android/spy. Agent. Dfm malware»](https://github.com/guardianproject/orbot-android/issues/1107)** (открыт 2024-03-13, закрыт 2024-05-24):

- Детект **ESET Mobile Security**, имя **`android/spy.Agent.Dfm`**.
- Установка — **из Google Play** (пользователь это специально уточняет в ответе мейнтейнеру).
- Ответ `n8fr8`, 2024-03-13: **«It is a false positive from the antivirus maker. However, if you want to be sure you have the authentic release signed by us, you can download the latest direct APK from here: …»** (ссылка на GitHub Releases). Мейнтейнер предлагает GitHub-релиз как способ убедиться в подлинности.
- Третий участник (`Hexag0nSun`): отправил обращение в ESET как false positive, техподдержка «insisted that part of Orbot code indeed can be used as a trojan», но он сам отмечает, что обращался, вероятно, к локальному реселлеру, и ждёт официального ответа ESET.

**Механика (почему это происходит) — что подтверждено кодом:** в APK есть (а) бинарник Tor, (б) исполняемый файл `libsslocal.so` в `nativeLibraryDir`, запускаемый как subprocess, (в) 24,9 МБ Go-бинаря `libgojni.so`, (г) сервис-наследник `VpnService` c `foregroundServiceType="systemExempted"` и `android:exported="true"`, (д) `usesCleartextTraffic="true"` (для SSDP/UPnP в Kindness Mode), (е) `Static { System.loadLibrary("tor") }` + JNI-вызовы. Всё это классические эвристики «hacktool / riskware / spy» — от запуска чужих бинарников до прозрачного перехвата трафика. ⚠️ Официального текста от Guardian Project или Tor Project, объясняющего срабатывания антивирусов, я не нашёл — есть только комментарии мейнтейнера в трекере.

⚠️ **Не подтверждено:** конкретные семейства/названия детектов у других вендоров (Avast, Kaspersky, Dr.Web и т. д.), официальные заявления Google Play Protect, отдельная статья в блоге Guardian Project об антивирусах.

### 7.2 Подделки и репаки

⚠️ **Не подтверждено.** Ни в issues репозитория `guardianproject/orbot-android`, ни на официальных страницах Guardian Project / orbot.app я не нашёл документированных случаев троянизированных или поддельных сборок Orbot. Единственное совпадение по поиску `fake|unofficial|repack in:title` — [issue #609](https://github.com/guardianproject/orbot-android/issues/609) «Fake text in Russian translation», что совсем про другое (устаревшее описание приложения в русской локализации).

**Важный контр-пример подлинности:** Orbot попал в кампанию вредоноса **`Android/Spy.Agent.CTK`** (ESET, 2023) **именно как легитимное приложение**, а не как подделка — то есть имя Orbot использовалось как ложная метка, а не как бренд для клона.

### 7.3 Как отличить официальную сборку — что реально проверяемо

**Официальные каналы распространения** (✅ [orbot.app/en/download](https://orbot.app/en/download/)):

1. Google Play — `org.torproject.android`
2. Guardian Project F-Droid repo — `https://guardianproject.info/fdroid`
3. GitHub Releases — [github.com/guardianproject/orbot/releases](https://github.com/guardianproject/orbot/releases) и [github.com/guardianproject/orbot-android/releases](https://github.com/guardianproject/orbot-android/releases)
4. Прямая ссылка `https://guardianproject.info/releases/orbot-latest.apk`
5. iOS: App Store / TestFlight

**Проверка SHA-256** (✅ работает): GitHub API отдаёт `digest` для каждого asset. Для текущего релиза:

```
GET https://api.github.com/repos/guardianproject/orbot-android/releases/latest
asset …fullperm-arm64-v8a-release.apk
  "size": 35860127,
  "digest": "sha256:97d3a0a5181394e99fbabdda9fefb73027657cac4c7400c64ebf146a7778860b"
```

Локальный `Get-FileHash -Algorithm SHA256` дал ровно то же значение. Рядом с APK лежит detached-подпись `.asc` (833 байта).

**Проверка PGP-подписи** (✅ механизм есть): `.asc` — detached OpenPGP-подпись, ключ с long ID `A801183E69B37AA9` = **Nathan Freitas `<nathan@guardianproject.info>`, отпечаток `BBE2 0FD6 DA48 A3DD 4CC7 DF41 A801 183E 69B3 7AA9`** — это опубликованный ключ со страницы [guardianproject.info/signing-keys](https://guardianproject.info/signing-keys/). ⚠️ Я **не смог криптографически проверить** подпись: в окружении отсутствует GnuPG. Комментарий в трекере подтверждает авторство ключа: «with APK and ASC signatures from my personal PGP key» (`n8fr8`, issue #952).

**Проверка сертификата APK — важный нюанс.** ⚠️ Сертификат APK из GitHub-релиза (**не** проверен gpg, но проверен `apksigner`) имеет отпечаток SHA-256 **`a454b87a1847a89ed7f5e70fba6bba96f3ef29c26e0981204fe347bf231dfd5b`** с DN `CN=Tor Project, OU=TorProject.org, O=TorProject.org, L=New York, ST=NY, C=US`, RSA **1024 бита**. Сравнение с опубликованными ключами Guardian Project:

| Источник | Subject | SHA-256 | SHA-1 |
|---|---|---|---|
| Опубликованный 1024-битный cert (✅ скачан и посчитан) | `CN=Guardian Project, OU=guardianproject.info, O=guardianproject.info, …` | `d71620711b04dfa13216116fbc8a9f3e3409846b16ff2bacf43e1c5ef5a3ed6b` | `9F1960C9584FEE5E166419354985A2B5FE413570` |
| Опубликованный 4096-битный cert (✅ скачан и посчитан) | `CN=guardianproject.info, O=Guardian Project, …` | `f006a20481c71a690de02e385ab0c9fa4ac1245240f68102682703ba0656867a` | `4CB3F539F63B32ACA13B4450638D605F531D4F4A` |
| **Сертификат APK из GitHub-релиза** | `CN=Tor Project, …` | **`a454b87a1847a89ed7f5e70fba6bba96f3ef29c26e0981204fe347bf231dfd5b`** | `cd142accde63fe57c1c52858e19d1b37c76422ce` |

Итог: **сертификат подписи APK из GitHub-релиза не совпадает ни с одним из двух cert'ов, опубликованных на странице signing-keys.** Однако он совпадает с `signer` в **официальном F-Droid-репозитории Guardian Project**. ✅ Я скачал `https://guardianproject.info/fdroid/repo/index-v1.json` и сопоставил каждый `signer` с именем APK, к которому он относится (сопоставление вручную, по позиции в JSON):

| signer (SHA-256) | APK, к которым он привязан |
|---|---|
| **`a454b87a1847a89ed7f5e70fba6bba96f3ef29c26e0981204fe347bf231dfd5b`** | **`orbot-latest.apk`, `Orbot-17.5.0-RC-1-tor-0.4.8.16-fullperm-arm64-v8a-release.apk`, `…-universal-release.apk`, `Orbot-17.2.1-RC-1-tor-0.4.8.7-fullperm-universal-release.apk`** |
| `d71620711b04dfa13216116fbc8a9f3e3409846b16ff2bacf43e1c5ef5a3ed6b` | PixelKnot, ObscuraCam |
| `f006a20481c71a690de02e385ab0c9fa4ac1245240f68102682703ba0656867a` | Orfox, Haven, ProofMode |
| `20061f04…`, `15f760b4…` | Tor Browser for Android (stable / alpha) |
| `fdbd9538…` | TorServices (alpha) |
| `29f34e5f…` | Signal Android |
| `6203a473…` | Firefox Klar |
| `927f7e38…`, `e08e63ca…`, `f3c7017a…` | Ripple, OpenArchive, Círculo |

То есть значение `a454b87a…` — это **именно cert, которым подписаны все официальные APK Orbot в F-Droid-репозитории Guardian Project**, включая `orbot-latest.apk` (та самая ссылка, что указана на [orbot.app/en/download](https://orbot.app/en/download/)). Значит, скачанный GitHub-релиз подписан тем же ключом, что и официально распространяемые сборки, — только этот ключ (`CN=Tor Project`, 1024-битный RSA) на странице signing-keys не опубликован.

⚠️ Отсюда практическое следствие: **отпечаток с публичной страницы signing-keys непригоден для проверки актуальных сборок Orbot.** У Guardian Project фактически есть третий, недокументированный там ключ подписи APK; единственный авторитетный на сегодня источник его отпечатка — `signer` из официального F-Droid-индекса.

Практический вывод: **отпечаток опубликованного cert'а на странице signing-keys не подходит для проверки текущих сборок Orbot** — это, по-видимому, устаревшая информация на странице. Единственный авторитетный на сегодня критерий сертификата — значение `signer` из официального F-Droid-индекса Guardian Project.

Дополнительное наблюдение ⚠️: **1024-битный RSA** для подписи приложения ниже современного минимума Google Play (2048 бит для новых загрузок) и, что существеннее, `v1 (JAR) signing: false` и `v3 signing: false` — это значит, что **ротация ключа (key rotation) для этой цепочки технически невозможна** (v3-подписи не используется). Сборки подписаны только схемой v2.

**F-Droid reproducible builds:** ⚠️ **не подтверждено.** Страница `https://f-droid.org/en/packages/org.torproject.android` отдаёт **404**, а `https://f-droid.org/api/v1/packages/org.torproject.android` — `{"error":"NOT_FOUND"}` (проверено 2026-10-15). Точка входа [orbot.app](https://orbot.app/en/) ведёт на **`https://guardianproject.info/fdroid`** (собственный репозиторий Guardian Project), а не на f-droid.org. ⚠️ Утверждение «F-Droid reproducible builds для Orbot» из задания **источниками не подтверждается**; в репозитории Orbot нет ни `metadata/org.torproject.android.yml`, ни `.fdroid.yml`.

**Индекс собственного F-Droid-репо Guardian Project** (✅ скачан `index-v1.json`, 867 296 байт): repo `name="Guardian Project"`, `address="https://guardianproject.info/fdroid/repo"`, `timestamp=1792047378578`, `version=30000`, **17 пакетов**. Для `org.torproject.android` в индексе присутствуют только: `orbot-latest.apk` (без версии в имени), `Orbot-17.5.0-RC-1-tor-0.4.8.16-fullperm-arm64-v8a-release.apk`, `Orbot-17.5.0-RC-1-tor-0.4.8.16-fullperm-universal-release.apk`, `Orbot-17.2.1-RC-1-tor-0.4.8.7-fullperm-universal-release.apk`. То есть F-Droid-репо отстаёт от GitHub-релизов (17.5.x против 17.9.7) — **обновиться до актуальной версии из F-Droid-репозитория Guardian Project нельзя**.

**Приложение Checkey** (✅ заявлено, ⚠️ инструкция не проверена): Guardian Project публикует `Checkey: info on local apps` как инструмент показа информации о локально установленных приложениях — [guardianproject.info/apps/info.guardianproject.checkey](https://guardianproject.info/apps/info.guardianproject.checkey/), пакет также присутствует в их F-Droid-индексе. Конкретных инструкций по проверке APK-подписи через Checkey я не нашёл.

**Что даёт совокупность проверок:**

```powershell
# 1) хеш — ожидаемо совпадает с "digest" из GitHub API
Get-FileHash .\Orbot-*.apk -Algorithm SHA256

# 2) OpenPGP-подпись (ключ BBE2 0FD6 DA48 A3DD 4CC7 DF41 A801 183E 69B3 7AA9)
gpg --verify .\Orbot-*.apk.asc .\Orbot-*.apk

# 3) сертификат — ОЖИДАЕМО: CN=Tor Project, RSA 1024,
#    SHA-256 a454b87a1847a89ed7f5e70fba6bba96f3ef29c26e0981204fe347bf231dfd5b
apksigner verify --print-certs -v .\Orbot-*.apk
```

Сверить полученный отпечаток нужно **не** со страницы signing-keys, а со значением `signer` для `orbot-latest.apk` в [guardianproject.info/fdroid/repo/index-v1.json](https://guardianproject.info/fdroid/repo/index-v1.json).

---

## 8. Что реально даёт лучшее качество работы и устойчивость в цензуре

### 8.1 Транспорт под конкретную блокировку, а не «один лучший»

Ключевое в Orbot — не «самый сильный транспорт», а **возможность сменить его без переустановки и без ручного torrc**. Код это подтверждает: `Transport.getTorConfig()` — единственное место, где формируется блок «`UseBridges` + `ClientTransportPlugin` + `Bridge`», а `Bridge.kt` разбирает и пересобирает строку моста. Механика устойчивости:

- **obfs4/lyrebird** (встроенные 7 мостов) обфусцирует трафик так, чтобы он не выглядел как Tor, но это не помогает от активного пробинга и от блокировки по IP/SNI-паттернам. В оброботке Tor Project obfs4 мосты — базовая рекомендация («This guide will help you set up an obfs4 bridge», [community.torproject.org/relay/setup/bridge](https://community.torproject.org/relay/setup/bridge/)), и там же зафиксировано переименование в lyrebird.
- **Snowflake** уходит в WebRTC-трафик к добровольческим прокси и, что важнее, **не требует знания конкретного IP моста**: адреса `192.0.2.4:80`/`192.0.2.3:80` — это placeholders, реальная точка входа определяется через брокер. Поэтому блокировка конкретных мостов не работает так, как с obfs4. Orbot дополнительно использует несколько непривязанных путей до брокера: обычный `https://snowflake-broker.torproject.net/`, AMP-путь (`amp-cache=https://cdn.ampproject.org/`, broker `https://snowflake-broker.torproject.net/`, fronts `www.google.com`) и отключённый по #1320 SQS.
- **meek_lite** работает через **domain fronting** (в поставке `url=https://1603026938.rsc.cdn77.org front=www.phpmyadmin.net`) — то есть клиент выглядит как запрос к легитимному фронт-домену CDN. Это самый устойчивый к IP-блокировкам вариант, но и самый медленный, и зависит от того, что CDN не отключит fronting.
- **WebTunnel** (в коде поддержан, но UI-путь через `TODO()` недостижим) имитирует обычный HTTPS-сайт: по документации Tor Project клиент обращается к секретному пути на настоящем сайте с валидным TLS-сертификатом, за NGINX-реверс-прокси — [community.torproject.org/relay/setup/webtunnel](https://community.torproject.org/relay/setup/webtunnel/). Это самый «неотличимый от обычного веба» вариант и требовательный к оператору (свой домен, свой сертификат, ≥1 ГБ RAM).
- **DNSTT** — единственный вариант, работающий через **DNS как транспорт**. Механика в Orbot: базовые dnstt-мосты уже используют `doh=https://dns.google/dns-query` (шифрованный DNS), а `getUdpDnstt()` для конкретной страны подменяет `doh` на сырой **UDP DNS-сервер** (`udp=<ip>:53`). Комментарий в коде объясняет смысл ровно так: UDP только для «heavily censored environment, where public DoH and DoT DNS servers are blocked». DNS почти никогда не блокируется полностью, поэтому это «последний рубеж».
- **Conjure** — единственный транспорт, который в Orbot **отсутствует**, поэтому тут он не даёт ничего (и в `MoatApi.SettingsRequest.transports` не заявлен).

**Практический вывод:** выигрыш даёт не выбор транспорта, а **наличие нескольких взаимозаменяемых** и умение переключаться. В Orbot наиболее «полезная тройка» по механике — **Snowflake (не знает IP моста) → DNSTT (идёт через DNS) → obfs4 (список от Moat)**, и именно такую логику частично автоматизирует Smart Connect.

### 8.2 Smart Connect — автоматический откат, а не «умный выбор»

Механика (`SmartConnect.kt`) реально полезна, но её надо понимать точно:

- перебор идёт по **фиксированной** цепочке `NONE → SNOWFLAKE → (CUSTOM|OBFS4) → OBFS4 → отказ`, а не по результатам измерения качества;
- критерий успеха — **рост bootstrap-процента** за `smartConnectTimeout` секунд (по умолчанию 30), а не реальная проверка связности;
- при успехе Smart Connect **сам себя выключает** (`Prefs.smartConnect = false`), чтобы не мешать дальше;
- замена мостов на живом Tor идёт через `conn.resetConf(["UseBridges","ClientTransportPlugin","Bridge"])` + `conn.setConf(...)` — без перезапуска процесса Tor.

Слабые места, видимые в коде: (а) цепочка не включает `MEEK` и `DNSTT` вообще; (б) при первом неудачном `start()` на шаге `SNOWFLAKE_SQS` кидается `RuntimeException` (#1320), которая молча проглатывается; (в) «успех» = выросший процент, поэтому медленный, но живой транспорт может не успеть за 30 с и будет заменён; (г) значение таймаута (`pref_smart_connect_timeout`) в UI отсутствует — только код.

### 8.3 Встроенные мосты + Moat: почему это устойчивее ручного ввода

Механика, дающая устойчивость в цензуре: **мост можно получить, не имея возможности достучаться до сайта Tor Project**. Orbot делает это двумя способами, и оба не требуют обычного HTTPS к `bridges.torproject.org`:

1. `MoatTunnel.TOR_PROJECT` — meek_lite c `front=www.phpmyadmin.net` через CDN.
2. `MoatTunnel.GUARDIAN_PROJECT` — **DNSTT** к `doh=https://dns.google/dns-query`, `domain=t1.bypasscensorship.org`.

То есть даже если у пользователя заблокированы и сайт Tor Project, и obfs4-мосты, у него остаётся путь получить свежие мосты через DNS. Это же объясняет требование в `AutoConf.do()`: GP-Moat работает только при явно указанной стране, потому что за DNSTT нет source IP.

Дополнительный слой: `BuiltInBridges.isOutdated()` (2 суток) + `updateBuiltinBridges` Gradle-задача на релизе → список obfs4 перезаписывается ответом Moat (`setting.bridge.source == "builtin"`). Смысл: если один-два из семи встроенных obfs4-мостов заблокированы, свежий список повышает шансы, что остальные живы, и пользователю не нужно ничего делать руками.

### 8.4 DNSTT по стране: почему подстановка UDP-DNS работает

Механика в `getUdpDnstt()`: берётся `assets/dns-<cc>.json` (для Ирана это **148 КБ** резолверов, для России 6,5 КБ), и каждый dnstt-мост размножается по каждому DNS-серверу с `udp=<ip>:53`. Дополнительно убирается `fingerprint1` — с комментарием, что иначе Tor использует только первую строку с уникальным fingerprint. Итог — до 30 попыток (лимит `MAX_DNSTT_BRIDGES_COUNT = 30`, потому что Tor держит максимум 32 SOCKS-соединения к PT) через разные DNS-серверы внутри страны.

Почему это работает: блокировка Tor в таких странах обычно делается через блокировку IP-адресов мостов и SNI-фильтрацию. Оба механизма **не применимы к UDP/53 внутри страны** — это DNS, который должен работать всегда, а DPI на DNS часто не имеет возможности выборочно дропать только «подозрительные» UDP-потоки без риска сломать весь интернет. Поэтому Orbot даёт именно локальные резолверы, а не только `8.8.8.8` из `dns-global.json`.

### 8.5 Что даёт изоляцию и защиту от утечек

- **Per-app VPN** (`addAllowedApplication`): в туннель попадают только выбранные приложения, остальные ходят напрямую. Это защищает от ситуации, когда какое-то приложение не терпит Tor и ломается, и уменьшает объём трафика через Tor. Побочный эффект: если пользователь ничего не выбрал и не включил lockdown, Orbot **явно исключает** себя и `BYPASS_VPN_PACKAGES` (Tor Browser, OnionShare, Briar, Cwtch) — это защита от «tor over tor».
- **`setMetered(false)` + `allowFamily(AF_INET/AF_INET6)`** — Android не будет считать VPN-соединение лимитным и не заблокирует один из стеков (issue 129).
- **`DisableNetwork 1` на этапе конфигурации** и `1→0` при смене exit-ноды — исключает утечки, пока конфиг ещё не применён.
- **`Prefs.isolateDest/Port/Protocol/KeepAlive`** → `IsolateDestAddr`/`IsolateDestPort`/`IsolateClientProtocol`/`KeepAliveIsolateSOCKSAuth`: разные сайты получают разные цепочки Tor, что мешает корреляционным атакам между сайтами.
- **Padding** (`ConnectionPadding`, `CircuitPadding`, `ReducedConnectionPadding`, `ReducedCircuitPadding`) — защита от анализа трафика по размеру/таймингу. Обратите внимание: в Orbot `reducedConnectionPadding`, `circuitPadding` и `reducedCircuitPadding` **включены по умолчанию** — это разумный компромисс между защитой и расходом трафика на мобильном.
- **`FLAG_SECURE`** (`pref_flag_secure`) — скриншоты и превью в списке недавних задач не покажут экран Orbot.
- **Camo Mode** — не защита от цензуры, но защита от «поиска Orbot глазами»: подмена иконки, названия уведомления и имени VPN-сессии (по умолчанию `"Android"`).
- **Зашифрованные креденшелы прокси** — Tink/AES128_GCM с ключом в Android Keystore; при недоступности шифрования fallback на обычный файл.

### 8.6 Kindness Mode: почему это помогает устойчивости сети в целом

Это не про скорость самого пользователя, а про **расширение пула точек входа**. Снежный прокси — это WebRTC-релей, через который пойдёт клиент из censored-страны. Механика, которая делает это качественным:

- **Определение NAT**: `natProbeUrl` + `natTypeUpdated` → `Prefs.lastSnowflakeNatType`. Если NAT `Restricted`, предлагается UPnP (`UPnPDialogFragment`), иначе прокси работает в restricted-режиме.
- **UPnP ровно на 3 UDP-порта** (Capacity × 2 + 1) — при неудаче порты закрываются, чтобы не оставлять открытые дырки.
- **Wi-Fi-only по умолчанию** (`limitSnowflakeProxyingWifi()` → `true`) — экономия мобильного трафика и защита от того, что прокси съест лимит.
- **Отключение для Ирана и Афганистана** (`isKindnessModeDisabledForCountry()` → `AF, IR`) — там прокси-трафик сам по себе может быть опасен для волонтёра.
- **Watchdog на JobScheduler** (#1799, #1783) — возвращает прокси после того, как его убила система; это принципиально для «волонтёрской» модели, где прокси должен работать долго.
- **Региональная блокировка при смене страны** — если пользователь сменил страну на AF/IR, преф `beSnowflakeProxy` принудительно выключается (`Prefs.bridgeCountry`-сеттер), а сервис останавливается через `OnSharedPreferenceChangeListener`.

Чем больше прокси-волонтёров, тем меньше шанс, что конкретный мост будет перегружен или заблокирован по IP — это и есть механизм «устойчивость к цензуре за счёт распределённости».

### 8.7 Что НЕ даёт улучшения (и об этом стоит знать)

- **WebTunnel в Orbot сейчас недоступен** — `TODO()` в `ConfigConnectionBottomSheet` для `Transport.WEBTUNNEL`, и в `builtin-bridges.json` нет webtunnel-мостов. Даже если Moat вернёт webtunnel-настройку, приложение упадёт с `NotImplementedError`.
- **Conjure не поддерживается вовсе.**
- **Snowflake SQS выключен** и гарантированно бросает исключение (#1320).
- **Smart Connect не перебирает meek и DNSTT** — самая медленная, но самая устойчивая к domain fronting связка остаётся ручной.
- **`pref_smart_connect_timeout` без UI** — значение по умолчанию 30 с может быть мало для DNSTT/meek, а изменить его пользователь через интерфейс не может.
- **При обновлении APK из GitHub (не из F-Droid/Play) приложение подписано 1024-битным ключом только по схеме v2** — ротация ключа технически невозможна, что в долгосрочной перспективе риск.
- **Кэша «какой мост работал» нет** — при каждой смене транспорта Smart Connect или пользователь начинает с чистого листа.

---

## Источники

**Исходный код (клон `master`, HEAD `0e06151`):**
- [guardianproject/orbot-android](https://github.com/guardianproject/orbot-android) — репозиторий
- [OrbotService.java](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/OrbotService.java), [OrbotRawEventListener.java](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/OrbotRawEventListener.java), [OrbotConstants.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/OrbotConstants.kt), [TProxyService.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/TProxyService.kt)
- [Transport.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/circumvention/Transport.kt), [Bridge.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/circumvention/Bridge.kt), [BuiltInBridges.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/circumvention/BuiltInBridges.kt), [AutoConf.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/circumvention/AutoConf.kt), [MoatApi.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/circumvention/MoatApi.kt), [MoatTunnel.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/circumvention/MoatTunnel.kt), [SmartConnect.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/circumvention/SmartConnect.kt)
- [OrbotVpnManager.java](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/vpn/OrbotVpnManager.java), [TorifiedApp.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/vpn/TorifiedApp.kt)
- [TorConfig.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/tor/TorConfig.kt), [ShadowSocks.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/service/tor/ShadowSocks.kt)
- [Prefs.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/util/Prefs.kt), [Settings.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/util/Settings.kt), [NetworkUtils.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/util/NetworkUtils.kt)
- [Regionalization.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/Regionalization.kt), [OrbotActivity.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/OrbotActivity.kt)
- [ConfigConnectionBottomSheet.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/ui/connect/ConfigConnectionBottomSheet.kt), [ConnectFragment.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/ui/connect/ConnectFragment.kt), [CustomBridgeBottomSheet.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/ui/connect/CustomBridgeBottomSheet.kt)
- [SnowflakeProxyService.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/ui/kindness/SnowflakeProxyService.kt), [SnowflakeProxyWrapper.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/ui/kindness/SnowflakeProxyWrapper.kt), [KindnessFragment.kt](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/java/org/torproject/android/ui/kindness/KindnessFragment.kt)
- [AndroidManifest.xml](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/AndroidManifest.xml), [app/build.gradle.kts](https://github.com/guardianproject/orbot-android/blob/master/app/build.gradle.kts), [libs.versions.toml](https://github.com/guardianproject/orbot-android/blob/master/gradle/libs.versions.toml), [LICENSE](https://github.com/guardianproject/orbot-android/blob/master/LICENSE), [README.md](https://github.com/guardianproject/orbot-android/blob/master/README.md)
- [res/xml/preferences.xml](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/res/xml/preferences.xml), [res/xml/safety_prefs.xml](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/res/xml/safety_prefs.xml)
- [assets/builtin-bridges.json](https://github.com/guardianproject/orbot-android/blob/master/app/src/main/assets/builtin-bridges.json), `assets/dns-*.json`, `assets/fronts`

**Библиотеки:**
- [guardianproject/tor-android](https://github.com/guardianproject/tor-android) и [TorService.java](https://raw.githubusercontent.com/guardianproject/tor-android/master/tor-android-binary/src/main/java/org/torproject/jni/TorService.java), [README](https://raw.githubusercontent.com/guardianproject/tor-android/master/README.md)
- ⚠️ [github.com/torproject/iptproxy](https://github.com/torproject/iptproxy) — **не подтверждено**: актуальный IPtProxy распространяется как `com.netzarchitekten:IPtProxy` (версия **5.7.0**) и его исходники на GitLab Tor Project (`tpo/anti-censorship/pluggable-transports/iptproxy`), но GitLab отдаёт **HTTP 403** на автоматические запросы, поэтому содержимое README проверить не удалось
- [heiher/hev-socks5-tunnel](https://github.com/heiher/hev-socks5-tunnel) — версия 2.19.0 (по HEAD-коммиту)

**Tor Project:**
- [community.torproject.org/relay/setup/bridge](https://community.torproject.org/relay/setup/bridge/) — obfs4, «the project was renamed to lyrebird»
- [community.torproject.org/relay/setup/webtunnel](https://community.torproject.org/relay/setup/webtunnel/) — WebTunnel = имитация веб-браузинга на базе HTTPT
- [community.torproject.org/relay/setup/snowflake](https://community.torproject.org/relay/setup/snowflake/)
- [support.torproject.org/relays/getting-started/what-is-a-bridge](https://support.torproject.org/relays/getting-started/what-is-a-bridge/)
- [support.torproject.org/circumvention](https://support.torproject.org/circumvention/)
- [support.torproject.org/tor-browser/circumvention/getting-bridges](https://support.torproject.org/tor-browser/circumvention/getting-bridges/)
- [support.torproject.org/tor-browser/circumvention/using-bridges](https://support.torproject.org/tor-browser/circumvention/using-bridges/)
- [gitlab.torproject.org tpo/anti-censorship issue 40001](https://gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/trac/-/issues/40001#note_2811603) — placeholder-адреса мостов

**Guardian Project:**
- [guardianproject.info/apps/org.torproject.android](https://guardianproject.info/apps/org.torproject.android/) — лицензия BSD-3-Clause, ссылки на F-Droid и Play
- [guardianproject.info/signing-keys](https://guardianproject.info/signing-keys/) — OpenPGP-ключи, 1024/4096-битные APK-сертификаты
- [guardianproject.info/releases/guardianproject-rsa1024-signing-key.cer](https://guardianproject.info/releases/guardianproject-rsa1024-signing-key.cer), [guardianproject-rsa4096-signing-certificate.pem](https://guardianproject.info/releases/guardianproject-rsa4096-signing-certificate.pem)
- [guardianproject.info/fdroid/repo/index-v1.json](https://guardianproject.info/fdroid/repo/index-v1.json) — официальный F-Droid-индекс (signer-хеши)
- [guardianproject.info/apps/info.guardianproject.checkey](https://guardianproject.info/apps/info.guardianproject.checkey/)
- [orbot.app](https://orbot.app/), [orbot.app/en/faqs](https://orbot.app/en/faqs/), [orbot.app/en/download](https://orbot.app/en/download/)
- [GitHub issue #952](https://github.com/guardianproject/orbot-android/issues/952) — «Android/Spy.Agent.CTK» (ESET), false positive
- [GitHub issue #1107](https://github.com/guardianproject/orbot-android/issues/1107) — «android/spy.Agent.Dfm» (ESET), false positive
- [GitHub issue #609](https://github.com/guardianproject/orbot-android/issues/609)
- [ESET forum: Tor Browser detected as malware](https://forum.eset.com/topic/26520-eset-mobile-security-detects-tor-browser-as-malware/)
- [GitHub API: releases](https://api.github.com/repos/guardianproject/orbot-android/releases/latest) — размеры, sha256-digest, .asc

**Проверено локально (артефакт):** APK `Orbot-17.9.7-RC-1-tor-0.4.9.14-1-g0ea112a4d-fullperm-arm64-v8a-release.apk` — SHA-256, `apksigner verify --print-certs`, содержимое `lib/` и `assets/`.

---

## Сводка пометок «не подтверждено»

| Утверждение | Статус |
|---|---|
| `startTorProcess` / `connectThroughTor` как имена в коде | ⚠️ таких методов в `master` нет |
| Conjure в Orbot | ❌ отсутствует (grep по репо — 0 совпадений) |
| WebTunnel-мосты в `builtin-bridges.json` | ❌ отсутствуют; UI-путь ведёт в `TODO()` |
| `meek-azure` как отдельный транспорт | ❌ поле игнорируется, транспорт один — `meek_lite` |
| Настройка «No Network Auto-Sleep» | ⚠️ явной настройки нет; есть автоматический `ConnectUiState.NoInternet` |
| F-Droid (f-droid.org) reproducible builds для Orbot | ⚠️ страница и API отдают 404; точка входа ведёт на собственный репо Guardian Project |
| Совпадение cert'а APK с опубликованными на signing-keys | ❌ не совпадает; ✅ совпадает с `signer` для `orbot-latest.apk` и релизов Orbot в официальном F-Droid-индексе GP |
| Документированные троянизированные репаки Orbot | ⚠️ не найдено |
| Официальное объяснение антивирусных срабатываний от GP/Tor Project | ⚠️ не найдено (только комментарии мейнтейнера в issues) |
| Детекты у вендоров кроме ESET | ⚠️ не подтверждено |
| Криптографическая проверка `.asc` | ⚠️ не выполнена (GnuPG отсутствует в окружении) |
| Содержимое IPtProxy (GitHub `torproject/iptproxy`) | ⚠️ GitLab отдаёт 403; версия известна из `libs.versions.toml` (5.7.0) |
