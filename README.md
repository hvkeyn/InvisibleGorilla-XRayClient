# Invisible Gorilla - XRay Client

> A modern, open-source GUI client for [Xray-core](https://github.com/XTLS/Xray-core) on Windows

Invisible Gorilla XRay is a free, open-source desktop application that wraps the powerful Xray-core proxy engine with an intuitive WPF interface. Easily configure, manage, and switch between multiple proxy servers with support for VLESS, VMess, Trojan, and Shadowsocks protocols.

## Features

- **Multi-protocol support** — VLESS, VMess, Trojan, Shadowsocks
- **Proxy & TUN modes** — system-wide proxy or TUN-based tunneling
- **Subscription management** — import and auto-update server lists from subscription links
- **Connection testing** — one-click latency check for each server
- **Deep link support** — import configs via `invisiblegorilla://` URI scheme
- **Multi-language UI** — English, Russian, Persian (easily extensible)
- **System tray integration** — runs quietly in background with quick-access menu
- **QR code sharing** — generate QR codes for sharing server configs
- **Auto-update** — check and install new versions from within the app
- **Single-instance** — prevents duplicate app launches with IPC pipe forwarding

## Architecture

```
InvisibleGorilla-XRayClient/
├── InvisibleGorilla-XRay/       # C# WPF application (.NET 7)
│   ├── Core/                    # XRay core wrapper & P/Invoke bridge
│   ├── Handlers/                # Business logic (proxy, tunnel, config, settings)
│   ├── Factories/               # Window creation via factory pattern
│   ├── Managers/                # App lifecycle, IPC pipes, services
│   ├── Models/                  # Data models & protocol templates
│   ├── Services/                # Localization, analytics
│   ├── Windows/                 # WPF windows (Main, Server, Settings, About, etc.)
│   └── Assets/                  # Icons, localization XAML resources
├── XRay-Wrapper/                # Go wrapper — compiles Xray-core into XRayCore.dll
│   ├── xray/                    # Server start/stop, config parsing, connection test
│   ├── main.go                  # Entry point
│   └── go.mod                   # Go 1.23, xray-core v25.1.30
└── build.ps1                    # One-command build script (auto-installs deps)
```

## Quick Start

### Option 1: Download release

Download the latest build from [Releases](https://github.com/InvisibleGorilla/InvisibleGorilla-XRayClient/releases/latest).

### Option 2: Build from source

```powershell
git clone "https://github.com/InvisibleGorilla/InvisibleGorilla-XRayClient.git"
cd InvisibleGorilla-XRayClient
.\build.ps1
```

The build script automatically:
1. Checks and installs **Go** (via MSI from go.dev) if missing
2. Checks and installs **.NET 7 SDK** (via official Microsoft script) if missing
3. Builds **XRayCore.dll** from the Go wrapper
4. Downloads **geoip.dat** and **geosite.dat** routing databases
5. Downloads **InvisibleMan-TUN** service for TUN mode
6. Builds the .NET application

#### Build script options

| Command | Description |
|---|---|
| `.\build.ps1` | Full build (all steps) |
| `.\build.ps1 -Publish` | Build + publish as single-file executable |
| `.\build.ps1 -Step GoWrapper` | Only build XRayCore.dll |
| `.\build.ps1 -Step DotNet` | Only build .NET app |
| `.\build.ps1 -Step GeoFiles` | Only download geo databases |
| `.\build.ps1 -Configuration Debug` | Build in Debug mode |
| `.\build.ps1 -SkipTUN` | Skip TUN service download |

### Manual build

<details>
<summary>Click to expand manual build steps</summary>

**Prerequisites:** [Go 1.23+](https://go.dev/dl/) and [.NET 7 SDK](https://dotnet.microsoft.com/download)

1. Build the Go wrapper:
   ```
   cd XRay-Wrapper
   go build --buildmode=c-shared -o XRayCore.dll -trimpath -ldflags "-s -w -buildid=" .
   mkdir ..\InvisibleGorilla-XRay\Libraries
   copy XRayCore.dll ..\InvisibleGorilla-XRay\Libraries
   ```

2. Download [InvisibleMan-TUN](https://github.com/InvisibleManVPN/InvisibleMan-TUN/releases/latest), extract to `InvisibleGorilla-XRay/TUN/`

3. Download geo databases:
   ```
   cd ..\InvisibleGorilla-XRay
   curl -L -o geoip.dat https://github.com/v2fly/geoip/releases/latest/download/geoip.dat
   curl -L -o geosite.dat https://github.com/v2fly/domain-list-community/releases/latest/download/dlc.dat
   ```

4. Run:
   ```
   dotnet run
   ```
</details>

## Tech Stack

| Component | Technology |
|---|---|
| GUI | WPF (.NET 7, C#) |
| Proxy engine | [Xray-core](https://github.com/XTLS/Xray-core) v25.1.30 |
| Native bridge | Go 1.23 → C-shared DLL (cgo) |
| TUN service | [InvisibleMan-TUN](https://github.com/InvisibleManVPN/InvisibleMan-TUN) |
| Geo routing | [v2fly geoip](https://github.com/v2fly/geoip) + [domain-list](https://github.com/v2fly/domain-list-community) |

## Contributing

We welcome contributions! Here's how you can help:

- **Report bugs** — open an [issue](https://github.com/InvisibleGorilla/InvisibleGorilla-XRayClient/issues)
- **Add a language** — see [Language.md](./Language.md) for instructions
- **Submit code** — fork, branch, and send a pull request

## License

[MIT](./LICENSE.md)
