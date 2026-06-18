# Simply Linux 11.1 — установка, TUN и права root

Simply Linux 11.1 основан на ALT Linux и использует **apt-get**, **GNOME** и **polkit (pkexec)** — это совместимо с `./build.sh` (семейство `alt`).

## 1. Зависимости

```bash
sudo apt-get update
sudo apt-get install -y \
  gcc make pkg-config curl unzip \
  libgtk+3 notify-send polkit iproute2 systemd \
  fontconfig libICE libSM libX11 libXi libXrandr libxcb-cursor \
  dotnet-sdk-8.0 golang git
```

Если `dotnet-sdk-8.0` недоступен в репозитории, установите [.NET SDK 8](https://dotnet.microsoft.com/download) вручную или запустите `./build.sh` — скрипт попытается поставить пакеты сам.

Для TUN также нужен `tun2socks` — его скачивает `./build.sh`.

## 2. Сборка

```bash
git clone https://github.com/hvkeyn/InvisibleGorilla-XRayClient.git
cd InvisibleGorilla-XRayClient
chmod +x build.sh
./build.sh
```

Архив появится в `dist-linux/`, распакованная папка — в `dist-linux/InvisibleGorilla-XRay-Linux-*/`.

## 3. Запуск без установки

```bash
cd dist-linux/InvisibleGorilla-XRay-Linux-linux-x64-v3.6.1.0
chmod +x run-igxray
./run-igxray
```

## 4. Установка в систему (рекомендуется)

```bash
cd dist-linux/InvisibleGorilla-XRay-Linux-linux-x64-v3.6.1.0
chmod +x install.sh
./install.sh
```

После этого приложение доступно из меню или командой:

```bash
invisible-gorilla-xray
# или
igxray
```

## 5. Один раз настроить права для TUN (без пароля на каждый чих)

Начиная с **v3.6.1**, команды `ip` / `resolvectl` выполняются **пакетом** (1–2 запроса pkexec на подключение вместо ~10). Чтобы **вообще не спрашивать пароль** при каждом подключении/отключении:

```bash
cd InvisibleGorilla-XRayClient   # корень репозитория
chmod +x scripts/linux/install-tun-policy.sh
./scripts/linux/install-tun-policy.sh
```

Скрипт кладёт правило polkit в `/etc/polkit-1/rules.d/50-invisible-gorilla-xray-tun.rules`. Оно разрешает только временные скрипты приложения с именем `igxray-priv-*` (настройка TUN), не произвольный root.

**После установки правила** выйдите из сессии и войдите снова (или перезагрузите ПК).

### Альтернатива: sudo без пароля (если pkexec не используется)

Если в системе нет `pkexec`, приложение пробует `sudo -n`. Тогда добавьте (замените `USERNAME`):

```bash
sudo visudo -f /etc/sudoers.d/invisible-gorilla-xray
```

```
USERNAME ALL=(root) NOPASSWD: /usr/bin/ip, /usr/bin/resolvectl
```

## 6. Первый запуск и конфиг

1. Импортируйте VLESS/VMess ссылку или JSON-конфиг.
2. Режим **Proxy** — системный прокси GNOME (`gsettings`), root не нужен.
3. Режим **TUN** — глобальный туннель; нужны права из раздела 5.
4. **Правила приложений** на Linux пока сохраняются в JSON (без kernel enforcement); смена шаблона не перезапускает VPN.

## 7. Проверка TUN

```bash
# до подключения
curl -4 ifconfig.me

# подключите TUN в приложении, затем снова
curl -4 ifconfig.me

# отключите — IP должен вернуться
```

## 8. Устранение неполадок

| Симптом | Решение |
|--------|---------|
| Много запросов пароля | Обновитесь до v3.6.1+, установите polkit rule (раздел 5) |
| `Neither pkexec nor sudo` | `sudo apt-get install polkit` |
| `tun2socks binary not found` | Пересоберите `./build.sh --step tun2socks` |
| Зависание в «Правила приложений» | Обновитесь до v3.6.1+ (фоновая загрузка списка приложений) |
| Падение / `too many open files` при TUN | Обновитесь до сборки с фиксом connection-info; запускайте через `./run-igxray` (поднимает `ulimit -n`) |
| Нет .NET 8 | Установите SDK 8 или соберите на машине с `./build.sh` |

Логи: каталог данных приложения рядом с бинарником (см. `Settings.json` / diagnostic log в UI).
