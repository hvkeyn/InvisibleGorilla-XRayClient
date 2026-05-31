# Защита от детекции (RKN / RKNHardering)

Документ описывает, как клиент Invisible Gorilla XRay противостоит инструментам
детекции обхода блокировок класса RKNHardering, что уже сделано на стороне
приложения и какие решения остаются за оператором серверов.

Базис — реальный прогон RKNHardering v2.8.1 на клиенте v3.3.3
(`test-tools/rkn-report-v3.3.3.json`). Итоговый вердикт: **`NEEDS_REVIEW`**.

## 1. Как RKNHardering выносит вердикт

Сначала проверяются 4 безусловных правила `DETECTED`:

1. `SPLIT_TUNNEL_BYPASS` в bypass-evidence;
2. найден `XRAY_API` (Xray gRPC API на localhost);
3. `VPN_GATEWAY_LEAK` (утечка через underlying-сеть);
4. локация подтверждает РФ **И** GeoIP даёт иностранный сигнал одновременно.

Если ни одно не сработало, считается матрица трёх флагов:

- **Geo** = иностранный GeoIP-сигнал;
- **Direct** = evidence `DIRECT_NETWORK_CAPABILITIES` / `SYSTEM_PROXY`;
- **Indirect** = evidence `INDIRECT_NETWORK_CAPABILITIES` / `NETWORK_INTERFACE` / `ROUTING` / `DNS` / `PROXY_TECHNICAL_SIGNAL`.

| Geo | Direct | Indirect | Вердикт |
| --- | --- | --- | --- |
| нет | нет | нет | `NOT_DETECTED` |
| нет | да | нет | `NOT_DETECTED` |
| нет | нет | да | `NOT_DETECTED` |
| да | нет | нет | `NEEDS_REVIEW` |
| нет | да | да | `NEEDS_REVIEW` |
| остальные | `DETECTED` |

Сигналы `INSTALLED_APP` и `VPN_SERVICE_DECLARATION` (имя приложения, версия,
имя VPN-сервиса, тип ядра) **в матрицу не входят** — они чисто диагностические.

## 2. Потолок для Android VpnService

Для любого работающего Android-VpnService:

- **Direct всегда `да`** — `TRANSPORT_VPN` / `VpnTransportInfo` система выставляет
  на активную сеть, скрыть нельзя без root и перехвата системных API.
- **Indirect всегда `да`** — интерфейс `tun0` и маршрут по умолчанию через него
  обязательны для туннеля.

Следовательно строка матрицы всегда минимум `нет / да / да` → **`NEEDS_REVIEW`**.
`NOT_DETECTED` недостижим без отказа от схемы VpnService. **`NEEDS_REVIEW` — это
лучший возможный результат, и клиент его достигает.**

Единственный способ скатиться в `DETECTED` — поднять **Geo = да** (иностранный
выходной IP). Поэтому выбор сервера важнее любых правок клиента.

## 3. Что закрыто на стороне клиента

| Вектор | Статус | Где |
| --- | --- | --- |
| Xray gRPC API на localhost | Удаляется из конфигурации | `XRay-Wrapper/xray/config.go` (`stripManagementApps`), `JsonUtility.SanitizeRuntimeManagedSections` |
| `api`/`stats`/`policy`/`inbounds` в импортируемых и подписочных конфигах | Санитизируются | `JsonUtility`, `SubscriptionConfig` |
| Локальный SOCKS-прокси | Требует логин/пароль (`AUTH_REQUIRED`), обход не подтверждается | `local_socks_auth.go` |
| VPN gateway leak | Underlying-сеть не привязывается напрямую | поведение VpnService |
| MTU | 1500, без аномалий | `AndroidVpnService.DefaultMtu` |
| DNS | Туннелируется через SOCKS UDP на `8.8.8.8` | `AndroidVpnService`, `android_socks_handlers.go` |

> **Замечание про DNS.** Пробовали рекламировать на TUN приватный sentinel
> (`10.0.236.1`) с NAT-ом в нативном мосту, чтобы скрыть «публичный DNS». На
> практике это сломало разрешение имён (внешние чекеры не отвечали, GeoIP пустой),
> и вердикт RKNHardering ухудшился до `DETECTED` (`LOCAL_PROXY_OR_API_ONLY`).
> При этом по матрице `VerdictEngine` DNS-сигнал ничего не даёт — `Indirect` и так
> `да` из-за интерфейса `tun0`. Поэтому изменение **откачено**: DNS остаётся на
> рабочем `8.8.8.8`, что удерживает `NEEDS_REVIEW`.

## 4. Чего НЕ стоит делать

- **Переименовывать `libXRayCore.so` / VPN-сервис ради «coreType».** Это
  диагностический сигнал `VPN_SERVICE_DECLARATION`, не входящий в матрицу
  вердикта. Высокий риск поломки сборки/обновлений при нулевом эффекте.
- **Прятать `versionName`.** Нужен для механизма обновлений.

## 5. Рекомендации по серверам (зона ответственности оператора)

Это **самый действенный** рычаг. Цель — чтобы `Geo = нет`.

1. **Выходной IP не должен геолоцироваться как иностранный относительно
   ожидаемой локации пользователя.** В прогоне выход был Yandex.Cloud (RU) —
   именно это удержало `Geo = нет` и вердикт `NEEDS_REVIEW`, а не `DETECTED`.
2. **Избегать «грязных» датацентровых диапазонов.** GeoIP-блок помечает
   `hosting provider: yes` (5/5 источников: ipapi.is, iplocate.io, ipquery.io,
   iplookup.it, ipbot.com). Сам по себе hosting у RU-IP не поднимает `Geo=да`,
   но повышает подозрительность; резидентские/мобильные ASN предпочтительнее.
3. **Проверять IP по proxy/VPN-базам.** В прогоне `0/5` — это хорошо. Сжигать
   IP, попавшие в публичные blocklist-базы.
4. **Не использовать IP из публичных списков прокси/VPN.** Ротация и собственные
   диапазоны лучше арендованных «общих» прокси.
5. **Согласованность IP.** Все чекеры (RU и не-RU) должны видеть один и тот же
   выходной IP (в прогоне — да). Дивергенция (WARP-подобное поведение, разные IP
   по каналам) повышает риск.

### Чек-лист перед раздачей сервера

- [ ] `whatismyipaddress` / `ipapi.is` показывают страну, ожидаемую пользователем;
- [ ] IP не помечен как `hosting` в большинстве GeoIP-источников (или хотя бы
      ASN не очевидно датацентровый);
- [ ] IP отсутствует в публичных proxy/VPN blocklist;
- [ ] один и тот же IP отдаётся всем внешним чекерам.

## 6. Воспроизведение проверки

1. Установить клиент и RKNHardering (API 26+), подключить VPN.
2. В RKNHardering запустить полную проверку.
3. Экспортировать отчёт (кнопка «скачать») в `Download/` — JSON с секциями
   `verdict`, `results.bypass`, `results.geoIp`, `results.directSigns`,
   `results.indirectSigns`.
4. Контрольные точки: `bypass.status = [OK]`, `Xray gRPC API: not found`,
   `proxy authRequired: true`, отсутствие `VPN_GATEWAY_LEAK`, `geoFacts.outsideRu = false`.
