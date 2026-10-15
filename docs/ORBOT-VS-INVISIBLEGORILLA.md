# Orbot (Guardian Project) vs InvisibleGorilla XRay: разбор алгоритма и сравнение

Дата: 2026-10-15. Проверялось: **Orbot 17.9.7-RC-1 (tor 0.4.9.14)**, APK для arm64-v8a, сборка `fullperm` — официальный релиз с GitHub.
Наша сторона: репозиторий `InvisibleGorilla-XRayClient`, ревизия `7c899d4` (v3.6.54).
Все артефакты, скрипты и сырые данные — в каталоге `.orbot-analysis/` (в `.gitignore`).

Дополнительные документы:

- [VERIFICATION-orbot-17.9.7.md](.orbot-analysis/VERIFICATION-orbot-17.9.7.md) — проверка подлинности и «антивирусный» анализ APK (хеш, PGP, сертификат, статика, репутация).
- [orbot-technical-analysis.md](docs/orbot-technical-analysis.md) — детальный разбор Orbot по исходникам (863 строки).
- [orbot-17.9.7-circumvention-report.md](.orbot-analysis/reports/orbot-17.9.7-circumvention-report.md) — разбор подсистемы мостов Orbot **по декомпилированному APK** (873 строки).
- [TOR-AND-SWITCHING-REPORT.md](docs/TOR-AND-SWITCHING-REPORT.md) — разбор нашей реализации Tor/мостов/переключения (662 строки).

---

## 0. Главное

**Про «вирусы».** В Orbot 17.9.7-RC-1 вредоносного кода не найдено. Сборка подлинная: SHA-256 совпадает с релизом GitHub, detached PGP-подпись валидна ключом релиз-менеджера Guardian Project (`BBE2 0FD6 … 69B3 7AA9`, Nathan Freitas), APK подписан тем же сертификатом `CN=Tor Project` (`a454b87a…`), что и `orbot-latest.apk` в официальном F-Droid-индексе Guardian Project, и тем же, что зафиксирован как «Tor Project APK signing key … used to sign: Orbot» в исходниках Guardian Project. Разговоры про «вирусную активность» — это **подтверждённые ложные срабатывания ESET** ([issue #952](https://github.com/guardianproject/orbot-android/issues/952): `Android/Spy.Agent.CTK`; [issue #1107](https://github.com/guardianproject/orbot-android/issues/1107): `android/spy.Agent.Dfm`, установка из Google Play) плюс технические особенности подписи: RSA **1024 бита**, SHA-1, X.509 **без единого расширения**, только v2-схема. Из-за этого εxodus пишет «x509 certificate does not comply the RFC», а часть сканеров считает APK «неподписанным». Детали и доказательства — в [VERIFICATION-orbot-17.9.7.md](.orbot-analysis/VERIFICATION-orbot-17.9.7.md).

**Про «Orbot работает лучше».** Частично это правда, и причины механические, а не мистические:

1. В одном нативном модуле (`libgojni.so`, это IPtProxy) лежат **8 транспортов** — obfs4/lyrebird, meek_lite, snowflake (клиент + прокси), webtunnel, DNSTT, — и они включаются строкой в torrc, без внешних бинарников.
2. Есть **автоэскалация при неудачном подключении** (Smart Connect): прямое → snowflake → obfs4/custom, с окном 30 с на шаг.
3. **Смена транспорта идёт «на лету»** — через контроль-порт tor (`resetConf(["UseBridges","ClientTransportPlugin","Bridge"])` + `setConf(...)`), без перезапуска tor.
4. Мосты **подтягиваются автоматически** из Moat (в т.ч. свежий `builtin`-список с TTL 48 ч) через два независимых туннеля (meek_lite и DNSTT).
5. Есть **DNSTT с подстановкой локальных DNS-резолверов по стране** (для Ирана в базе 1924 резолвера) — то, чего у нас нет вообще.

При этом Orbot **не** «умнее» нас в другом: у него **нет** мониторинга живости после подключения, **нет** измерения задержки и **нет** автопереключения при деградации — ровно то, что у нас реализовано для Xray-узлов (Goida) и OpenFlux. И у Orbot нет Tor-профилей, которые есть в нашей Core-версии.

---

## 1. Что именно делалось

| Шаг | Инструмент | Результат |
|---|---|---|
| Скачивание релиза | `curl` из GitHub Releases (тег `17.9.7-RC-1-tor-0.4.9.14`) | APK 35 860 127 байт + `.asc` |
| Целостность | SHA-256 vs `digest` GitHub API | совпадает |
| PGP-подпись | `scripts/verify_pgp.py` (pgpy) | `SIGNATURE VALID: True`, SHA-512, 2026-10-09 19:11 UTC |
| Подпись APK | `apksigner` + `scripts/CertDump.java` (apksig) | v2-only, `CN=Tor Project`, RSA 1024, SHA1withRSA, без расширений |
| Декомпиляция | `jadx 1.5.6` + `apktool 3.0.3` | 7 829 классов, манифест, ресурсы, smali |
| Инвентаризация | `scripts/apk_inventory.py` | 1 369 файлов внутри, 9 `.so`, 3 dex, sha256 каждого |
| Строки нативных библиотек | `scripts/native_strings.py` | подтверждены tor 0.4.9.14, IPtProxy (obfs4/snowflake/meek/webtunnel/dnstt), shadowsocks-rust |
| Эвристики по коду | `scripts/code_heuristics.py` | нет динамической загрузки классов, нет телеметрии, нет доступа к SMS/контактам/микрофону/геолокации |
| Сетевые адреса | `scripts/network_endpoints.py` | 20 доменов всего, все объяснимы |
| Внешняя репутация | AlienVault OTX, εxodus | 0 pulses, 0 трекеров |
| Наша сторона | 3 субагента + чтение кода | [TOR-AND-SWITCHING-REPORT.md](docs/TOR-AND-SWITCHING-REPORT.md) |

Чего **не** делалось: мультидвижковый скан VirusTotal (нужен API-ключ, ключа нет — по решению пользователя пропущено) и локальный скан антивирусом (на машине служба `WinDefend` остановлена).

---

## 2. Алгоритм работы Orbot

### 2.1 Архитектура

- **tor запускается внутри процесса приложения**: `libtor.so` (7 580 256 байт, tor **0.4.9.14**) грузится через `System.loadLibrary("tor")`, `main()` выполняется в Java-потоке с именем `tor` посредством JNI-обёртки `TorService`. Отдельного `tor`-процесса, как у нас, нет.
- Управление — **Unix-сокет** `DataDirectory/ControlSocket`, причём с `--CookieAuthentication 0` и `authenticate(new byte[0])` (аутентификация фактически отключена, доступ ограничен правами ФС на каталог приложения).
- Перехват трафика — `VpnService` + `hev-socks5-tunnel` (свой `.so`, MTU 8500, фейковый DNS `198.18.0.2`, `mapdns` 240.0.0.0/4) + per-app маршрутизация (`TorifiedApp`, `BYPASS_VPN_PACKAGES`: Tor Browser, OnionShare, Briar, Cwtch исключаются из VPN, чтобы не зацикливаться).
- Прогресс bootstrap парсится из текстового лога tor (`BOOTSTRAP PROGRESS=`), а «подключено» ставится по событию контроль-порта `STATUS_CLIENT` + `CIRCUIT_ESTABLISHED` (не по проценту).
- Всё, что делает пользователь, приходит в `OrbotService.IncomingIntentRouter` (ACTION_START/STOP и т.д.).

### 2.2 Транспорты и мосты

- Enum `Transport` (по декомпилированному коду, `Transport.java:35-44`) — 9 значений: `NONE(direct)`, `MEEK(meek_lite)`, `OBFS4`, `SNOWFLAKE`, `SNOWFLAKE_AMP`, `SNOWFLAKE_SQS`, `WEBTUNNEL`, `DNSTT`, `CUSTOM`.
  - `SNOWFLAKE_SQS` в рабочем коде бросает `RuntimeException` (не поддержан).
  - `WEBTUNNEL` объявлен, но UI-путь ведёт в `TODO()`; встроенных webtunnel-мостов нет (могут прийти только из Moat).
  - `conjure` в Orbot **отсутствует полностью** (проверено по коду и по строкам в `.so`).
  - Shadowsocks — **не транспорт**, а внешний процесс `libsslocal.so -b localhost:<порт> --server-url <url>`, используемый как upstream-прокси (нужен для DNSTT-схемы).
- Порты плагинов **динамические**: `.so` биндится на `127.0.0.1:0`, номер читается через `IPtProxy.Controller.port(name)` и подставляется в torrc как `ClientTransportPlugin <name> socks5 127.0.0.1:<port>`.
- Строка моста (`Bridge.build()`): `[transport] [ip:port] [fingerprint]` плюс параметры `url= front= fronts= cert= iat-mode= ice= utls= udp= doh= dot= pubkey= domain=`.
- Встроенные мосты (`assets/builtin-bridges.json`): **10 строк** — 7 obfs4 + 1 meek (+1 `meek-azure`, поле игнорируется) + 2 snowflake; webtunnel/dnstt только из Moat. Разбивки по странам нет, страновой разрез есть только у DNSTT.
- **Moat** (`MoatApi`, `MoatTunnel`): эндпоинты `builtin`, `settings`, `defaults` (а `map` не используется). Ходит двумя туннелями:
  - Tor Project: `https://bridges.torproject.org/moat/circumvention/` через `meek_lite` (`url=https://1723079976.rsc.cdn77.org`, `front=www.phpmyadmin.net`);
  - Guardian Project: `https://tns1.bypasscensorship.org/moat/circumvention/` через **DNSTT** (`doh=https://dns.google/dns-query`, `domain=t1.bypasscensorship.org`, встроенный pubkey).
  TLS-валидация — по единственному корню `ISRG Root X1` (пиннинг на Let's Encrypt). Явных таймаутов нет (дефолты OkHttp 10 с), CAPTCHA нет. Кэшируется только `builtin` → `<cacheDir>/updated-bridges.json`, считается устаревшим через **48 часов**.
- **DNSTT**: 16 файлов `assets/dns-<cc>.json` (всего ~2600 резолверов; для Ирана — 1924), поддержаны 15 стран (AE, AF, BD, CN, CO, ID, IR, KW, PK, QA, RU, SY, TR, UG, UZ) + `global`; из резолверов страны синтезируются до **30** dnstt-мостов вида `dnstt 192.0.2.4:<n> <fingerprint> pubkey=… udp=<resolver-ip>:53 domain=…`, плюс два зашитых моста с `domain=t.ruhnama.net` / `t2.bypasscensorship.org`.

### 2.3 Smart Connect — автоматический перебор транспортов (то самое «на лету»)

Если `pref_use_smart_connect = true`:

1. Сначала запрашиваются мосты: `AutoConf.do()` → кэш `builtin` (если старше 48 ч — обновить) → `POST settings` к Tor Project через meek_lite-туннель → если задана страна, `POST settings` к Guardian Project через DNSTT-туннель → если ничего нет, `POST defaults`.
2. Устанавливается транспорт и запускается tor.
3. Дальше работает таймер с тиком **1 с** и дедлайном `now + pref_smart_connect_timeout` (**по умолчанию 30 с**, ключа в UI нет):
   - если bootstrap-процент вырос — дедлайн продлевается;
   - если достигнут 100 % — Smart Connect **сам себя выключает** (`Prefs.setSmartConnect(false)`), подключение признано успешным;
   - если дедлайн истёк — **смена транспорта без перезапуска tor**:

     ```
     conn.resetConf(["UseBridges", "ClientTransportPlugin", "Bridge"])
     conn.setConf(newTransport.getTorConfig(context))
     ```

   - цепочка переходов: `NONE → SNOWFLAKE → (CUSTOM, если есть свои мосты, иначе OBFS4)`; из `CUSTOM → OBFS4`; из `MEEK|OBFS4|WEBTUNNEL|DNSTT` → стоп с ошибкой «Smart Connect failed».
4. Итого автоматика перебирает максимум 3-4 конфигурации: **прямое подключение → snowflake → obfs4 (или пользовательские мосты → obfs4)**.

Что тут важно понять точно:

- «Переключение на лету» в Orbot — это **не** выбор другого моста внутри списка и **не** переключение после подключения. Это подмена всего транспортного блока torrc через контроль-порт во время попыток подключения.
- `meek` и `dnstt` в цепочку Smart Connect **не входят** — они используются как транспорт для запросов к Moat (meek_lite TOR_PROJECT, dnstt GUARDIAN_PROJECT) и как ручной выбор.
- Проверка задержки мостов отсутствует: критерий — «растёт ли bootstrap-процент».

### 2.4 Что происходит после подключения

**Ничего автоматического.** Нет периодической проверки живости, нет измерения RTT/скорости, нет автопереключения при деградации, нет перезапроса мостов после успеха. Единственный watchdog в приложении — `KindnessWatchdogJob` (15 мин) для режима «быть snowflake-прокси», и он к клиентскому трафику отношения не имеет.

### 2.5 Настройки

`res/xml/preferences.xml` (13 530 байт) + `safety_prefs.xml` + программные ключи — всего около **35 ключей**. Основные группы: транспорт обхода (`pref_connection_pathway` — внимание, это не `pref_transport`, который означает Tor `TransPort` 9040), список мостов `pref_bridges_list`, страна мостов `pref_bridge_country`, Smart Connect и его таймаут, SOCKS/HTTP/DNS/Trans-порты (9050/8118/9053/9040), изоляция потоков (`pref_isolate_*`), padding-настройки, IPv6/reachable addresses, exit/entrance/exclude nodes, VPN и per-app список (`PrefTord`), Kindness Mode (snowflake-прокси, лимит по Wi-Fi, UPnP-порты), Camo Mode (маскировка иконки), блокировка скриншотов, пароль/биометрия, автозапуск, логи, кастомный torrc, power user mode.

---

## 3. Что у нас (InvisibleGorilla XRay v3.6.54)

Кратко по фактам из [TOR-AND-SWITCHING-REPORT.md](docs/TOR-AND-SWITCHING-REPORT.md):

**Запуск Tor.** Sidecar `tor.exe -f torrc` (`InvisibleGorilla-XRay/Handlers/Tor/TorManager.cs:140-147`), блокирующий `Start(..., maxWaitMs = 90000)`, двухфазное ожидание bootstrap (TCP-проба контроль-порта + `GETINFO status/bootstrap-phase` раз в 1000 мс), порты по умолчанию 9250/9251. Control-клиент умеет **только** `AUTHENTICATE`, `GETINFO status/bootstrap-phase`, `SIGNAL HALT` — ни `SETCONF`, ни `NEWNYM`, ни `RELOAD`.

**Мосты.** Встроенные списки есть: obfs4 — 7 строк, snowflake (+Secondary/AMP), meek-azure; **для WebTunnel встроенных линий нет**. Две проблемы:

- если выбран `WEBTUNNEL` и список пуст, `TorrcBuilder` вообще не пишет `UseBridges` → **tor молча уходит в прямое подключение** (TorrcBuilder.cs:72-76);
- в поставке **нет** `snowflake-client.exe` и `obfs4proxy.exe`, хотя пути на них указывают; `TorrcBuilder` молча подменяет snowflake на `lyrebird.exe` (TorrcBuilder.cs:80-99).

**Проверка моста.** `TorManager.CheckBridge` тестирует **только первую строку списка** и меряет время до bootstrap, а не RTT; параллельно поднимается второй экземпляр tor на портах 19250+.

**Автоматика.** Moat/BridgeDB реализован полностью (включая CAPTCHA-флоу `/fetch`, `/check`, `/circumvention/settings`, `/circumvention/builtin`), но вызывается **только по нажатию кнопки**. Планового обновления мостов нет, TTL устаревания нет. Автоперебора транспортов и автопереключения мостов нет; смена Tor-настроек **не перезапускает** активную сессию.

**Задержка.** `LastLatencyMs` — время до 100 % bootstrap (не RTT), показывается в UI. Периодического мониторинга Tor нет.

**«Умное переключение» у нас есть — но для Xray-узлов и OpenFlux:**

- **Goida**: пробы TCP (таймаут 2500 мс, параллелизм 16) → реальный native `TestConnection` (7000 мс, глобальный семафор 1), порог `AutoSwitchLatencyMs = 3000`, probe 60 с, refresh 9 мин, гистерезис «строго лучше», failover по провалу проверки exit-IP (таймер 20 с, 2 подряд = обрыв, debounce 1 с, cooldown 90 с);
- **OpenFlux**: единственный настоящий hot-swap — цикл перезапускает только sidecar `openflux.exe`, прокси/TUN не снимаются (принудительная рециркуляция раз в 3 ч);
- переключение сервера/конфига в UI — всегда полный разрыв (`TryRerun`).

**Архитектурный долг.** Tor реализован **дважды**: Windows-проект не ссылается на `InvisibleGorilla.Core`, копии разошлись в обе стороны (в Core есть чтение хвоста `tor.log` при падении и класс `TorProfile` с переключаемыми Tor-профилями; в Windows — более новый `MoatClient`). Нет watchdog `tor.exe`, нет авто-рестарта, нет очистки осиротевших процессов, нет ротации `tor.log`, прогресс bootstrap не выводится в UI.

---

## 4. Сравнение по пунктам

| Возможность | Orbot 17.9.7 | InvisibleGorilla XRay v3.6.54 | Кто лучше |
|---|---|---|---|
| Транспорты PT | obfs4/lyrebird, meek_lite, snowflake (клиент+прокси), webtunnel (мёртв в UI), DNSTT, shadowsocks как upstream — всё в одном `libgojni.so` | obfs4/lyrebird, snowflake (фактически через lyrebird), meek-azure, webtunnel (объявлен, но пустой) | **Orbot** |
| Встроенные мосты | 10 (7 obfs4 + 1 meek + 2 snowflake), обновление `builtin` из Moat с TTL 48 ч | obfs4 7 + snowflake + meek-azure; обновление только вручную | **Orbot** |
| Получение мостов | Moat автоматически (`builtin`/`settings`/`defaults`) через 2 туннеля (meek_lite + DNSTT) | Moat/BridgeDB полностью, но только по кнопкам; есть CAPTCHA-флоу | **Orbot** (автоматизация), **мы** (CAPTCHA/BridgeDB `/fetch`) |
| Автоэскалация при неудаче | **Да**: direct → snowflake → custom/obfs4, окно 30 с на шаг | **Нет** | **Orbot** |
| Смена транспорта без перезапуска tor | **Да**: `resetConf` + `setConf` через контроль-порт | **Нет** (только полный Stop/Start) | **Orbot** |
| DNSTT | **Да**, 15 стран, до 30 мостов, до 1924 резолверов на страну | **Нет** | **Orbot** |
| Проверка/выбор лучшего моста | Нет (критерий — рост bootstrap) | Проверка есть, но только первой строки, без RTT | ничья (оба слабы) |
| Автопереключение после подключения | **Нет** | **Есть** для Xray-узлов (Goida) и OpenFlux (hot-swap sidecar) | **мы** |
| Измерение задержки | **Нет** | Есть (`LastLatencyMs` = время bootstrap) | **мы** (частично) |
| Мониторинг живости туннеля | Нет | Есть: проверка exit-IP каждые 20 с (8 с Goida, 45 с OpenFlux), failover по 2 провалам | **мы** |
| Watchdog/авто-рестарт сервиса Tor | Tor в процессе приложения; отдельного watchdog тоже нет | Нет | ничья |
| Прогресс bootstrap в UI | Да (парсинг лога, уведомление) | Нет (значение не читается вне TorManager) | **Orbot** |
| Tor-профили | Нет | Есть в Core (`TorProfile`), нет в Windows | **мы** |
| Per-app маршрутизация | Да (`TorifiedApp`, исключения BYPASS) | Да (AppRules/шаблоны правил) | ничья |
| Занимаемое место | 34 МБ (из них 26 МБ geoip/geoip6) | ~103 МБ exe + Tor-каталог (~40 МБ) на Windows | **Orbot** (мобильная оптимизация) |
| Подпись/ротация ключа | RSA 1024, SHA-1, без расширений, **только v2** → ротация невозможна | — | Orbot как проект имеет здесь риск |
| Телеметрия | 0 трекеров (εxodus), Firebase только как обвязка ML Kit | `IsSendingAnalytics` в настройках (по умолчанию true) | Orbot (по факту анализа) |

---

## 5. Так «работает лучше» или нет

**Лучше — в устойчивости к блокировкам и в автоматике подключения.** Механика:

1. **Мультитранспортность в одном модуле.** У Orbot любой из 5 боевых транспортов включается парой строк torrc: не нужен внешний `.exe`, нет проблем с путями и версиями — у нас именно на этом месте тихие баги (snowflake → lyrebird, WEBTUNNEL → direct).
2. **Smart Connect = автоматический откат, а не «умный выбор».** Он не ищет лучший мост, он просто не даёт пользователю остаться без связи: если прямое подключение не поднялось за 30 с — сам включает snowflake, потом obfs4. При жёсткой цензуре это ровно тот сценарий, где «у нас не подключается, а у него подключается».
3. **Подмена транспорта через контроль-порт** экономит ~10-20 с на каждой попытке (нет повторного запуска tor, нет пересоздания TUN).
4. **Moat «builtin» с TTL 48 ч** означает, что встроенные мосты не устаревают: свежие obfs4/snowflake приходят автоматически, а DNSTT-резолверы для страны берутся из локальной базы (актуальна на январь 2026) — это то, что в Иране/Турции/России даёт разницу между «работает» и «не работает».
5. **DNSTT-схема с UDP-резолверами** работает там, где DoH/DoT уже заблокированы, потому что обращается к локальным DNS-серверам провайдера по UDP/53.

**Не лучше — там, где у нас как раз есть механика:**

1. После успешного подключения Orbot «слепой»: ни задержки, ни контроля живости, ни автопереключения. Наш мониторинг exit-IP (20 с) + Goida с порогом 3000 мс и гистерезисом — существенно более взрослая логика.
2. Hot-swap у нас есть там, где он реально нужен для серверов (OpenFlux), а не только на этапе подключения.
3. У Orbot нет Tor-профилей, нет диагностики по хвосту лога при падении — в Core-версии это есть.
4. Orbot — однозадачное приложение (Tor), у нас мультипротокольный клиент; сравнивать «качество работы» напрямую некорректно: у нас Tor — одна из функций.

**Вывод.** Утверждение «у него Orbot лучше работает и можно на лету менять мосты и проверять» верно в части: **автоперебор транспортов + смена транспорта без перезапуска + автообновление мостов + DNSTT**. Это ровно 4 переносимых механизма, и три из них у нас отсутствуют полностью.

---

## 6. Что стоит перенять (по приоритету)

### P0 — исправить то, что уже сломано

1. **Убрать тихий выход в direct.** Если выбран транспорт без мостов (сейчас — WebTunnel), надо либо сходить в Moat и получить мосты, либо явно отказать пользователю. Сейчас `UseBridges` не пишется вообще (`InvisibleGorilla-XRay/Handlers/Tor/TorrcBuilder.cs:72-76`) — это утечка трафика мимо Tor при внешне включённом Tor.
2. **Починить маппинг snowflake.** В поставке нет `snowflake-client.exe`, тор-конфиг молча уходит на `lyrebird.exe` (`TorrcBuilder.cs:80-99`) — пользователь думает, что у него snowflake, а у него obfs4. Либо доложить бинарник, либо честно сообщить в UI.
3. **Сменить ключ подписи и включить v3-схему** — иначе ротация подписи невозможна (это уже про наш релизный процесс, не про Tor).

### P1 — перенести механику Orbot (даёт наибольший эффект при цензуре)

4. **Автоэскалация транспортов при подключении** (аналог Smart Connect): `direct → obfs4 → snowflake → webtunnel`, окно на шаг (по умолчанию 30 с, настраиваемое), критерий успеха — рост bootstrap-процента; при неудаче шага — смена конфигурации **без перезапуска tor**.
5. **Расширить контроль-клиент tor** до `SETCONF`/`RESETCONF`/`RELOAD`/`GETINFO` (`TorControlClient.cs:44-105` сейчас умеет только `AUTHENTICATE`/`GETINFO bootstrap-phase`/`SIGNAL HALT`). Это техническая основа для п. 4, для смены exit-ноды на ходу и для `NEWNYM`.
6. **Плановое обновление мостов из Moat**: `GET /circumvention/builtin` с TTL (48 ч как у Orbot), кэш в настройках, фоновое обновление; `MoatClient` уже реализует нужные вызовы — не хватает только автоматики и хранения TTL.
7. **Проверка мостов по-человечески**: тестировать N строк (а не первую), измерять **RTT через SOCKS5**, параллельно, с кэшем результатов и учётом «когда проверялся».

### P2 — расширить возможности

8. **DNSTT как транспорт** для стран с DNS-only связностью: переносится почти один-в-один — `dnstt 192.0.2.x:port pubkey=… udp=<resolver>:53 domain=…`, база резолверов по странам (`assets/dns-*.json` у Orbot), лимит ~30 мостов.
9. **Мониторинг Tor после подключения**: переиспользовать уже существующий таймер проверки exit-IP (20 с) — при 2 провалах подряд автоматически эскалировать транспорт/мост и перезапускать Tor.
10. **Watchdog `tor.exe` + авто-рестарт с backoff + очистка осиротевших процессов + ротация `tor.log`** — сейчас при падении tor пользователь остаётся без диагностики.
11. **Прогресс bootstrap в UI** — данные уже собираются, просто не читаются вне `TorManager`.
12. **Слияние двух реализаций Tor** (Windows и Core) — расхождения уже дают разный функционал на разных платформах.
13. **Страновой выбор мостов** (как `pref_bridge_country` у Orbot) + «Ask Tor» уже есть, добавить автоподстановку страны по геолокации exit-узла/языку.

### Чего делать не надо

- Копировать отключённую cookie-аутентификацию control-порта Orbot (`--CookieAuthentication 0`) — у нас TCP-порт, это было бы хуже.
- Тащить `conjure` (у Orbot его нет, а `conjure-client.exe` у нас лежит неиспользуемым — либо задействовать, либо убрать из поставки).
- Ожидать от Orbot того, чего у него нет: он не переключает мосты после подключения и не измеряет задержку.

---

## 7. Оговорки

1. Мультидвижковый антивирусный скан (VirusTotal) не выполнялся — нет API-ключа. Локальный скан невозможен: `WinDefend` остановлен.
2. Ложные срабатывания ESET подтверждены документально (issues #952, #1107), но это не доказывает отсутствие *любых* проблем; проверялась конкретная сборка 17.9.7-RC-1 (arm64, fullperm) и две соседние.
3. Разбор Orbot делался по **декомпилированному APK** + официальным исходникам; в отчёте субагента помечены места, где декомпиляция неоднозначна (например, третий аргумент `AutoConf.do` в UI-пути).
4. Наша сторона описана по коду; заявления вида «TUN + Tor Only требует рантайм-проверки» помечены как непроверенные.
5. `geoip`/`geoip6` (26 МБ) в Orbot сравнимы с нашими данными, но у нас они лежат вне exe — сравнение размеров условное.

---

## 8. Где лежат артефакты

| Путь | Что это |
|---|---|
| `.orbot-analysis/downloads/` | скачанный APK + `.asc`, а также 2 соседних релиза для сверки сертификата |
| `.orbot-analysis/jadx-out/` | декомпилированный код (7 829 классов) |
| `.orbot-analysis/apktool-out/` | манифест, ресурсы, smali |
| `.orbot-analysis/native/` | извлечённые нативные библиотеки |
| `.orbot-analysis/reports/` | инвентаризация APK, строки `.so`, эвристики, сетевые адреса, отчёт по circumvention |
| `.orbot-analysis/scripts/` | все скрипты проверки (воспроизводимо) |
| `.orbot-analysis/tools/` | jadx + apktool (158 МБ) |
| `.orbot-analysis/VERIFICATION-orbot-17.9.7.md` | доказательная база по подлинности/вирусам |
| `docs/orbot-technical-analysis.md` | разбор Orbot по исходникам |
| `docs/TOR-AND-SWITCHING-REPORT.md` | разбор нашей реализации |

Общий объём — около **688 МБ**; каталог `.orbot-analysis/` добавлен в `.gitignore`.
