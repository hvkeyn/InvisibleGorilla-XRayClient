# Invisible Gorilla - XRay Client

> A modern, open-source GUI client for [Xray-core](https://github.com/XTLS/Xray-core) on Windows & macOS

[![GitHub release](https://img.shields.io/github/v/release/hvkeyn/InvisibleGorilla-XRayClient?style=flat-square)](https://github.com/hvkeyn/InvisibleGorilla-XRayClient/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg?style=flat-square)](./LICENSE.md)

Invisible Gorilla XRay is a free, open-source desktop application that wraps the powerful Xray-core proxy engine with an intuitive WPF interface. Easily configure, manage, and switch between multiple proxy servers with support for VLESS, VMess, Trojan, and Shadowsocks protocols.

**Fork of [InvisibleMan-XRay](https://github.com/InvisibleManVPN/InvisibleMan-XRay)** with critical proxy fixes, improved reliability, and gorilla branding.

## What's Fixed in This Fork

| Issue | Root Cause | Fix |
|---|---|---|
| **VPN shows "Running" but IP doesn't change** | Windows `DefaultConnectionSettings` blob not updated — browsers ignore registry-only proxy changes | Use `INTERNET_OPTION_PER_CONNECTION_OPTION` API to update both registry and binary blob |
| **Browser needs restart after proxy toggle** | Missing `WM_SETTINGCHANGE` broadcast to running applications | Non-blocking `SendNotifyMessage(HWND_BROADCAST)` notifies all browsers instantly |
| **App hangs on disconnect** | `SendMessageTimeout` blocks UI thread waiting for all windows | Replaced with async `SendNotifyMessage` + background thread for proxy cleanup |
| **Proxy stays enabled after crash** | No cleanup on unhandled exceptions or forced exit | Added `UnhandledException`, `ProcessExit`, `OnExit` handlers |
| **Race condition on startup** | System proxy enabled before xray-core starts listening | Start xray in background thread, poll port, enable proxy only when ready |

## Features

- **Multi-protocol support** — VLESS, VMess, Trojan, Shadowsocks
- **Proxy & TUN modes** — system-wide proxy or TUN-based tunneling
- **Instant browser integration** — proxy changes apply to Chrome, Edge, Yandex Browser without restart
- **Crash-safe proxy** — system proxy is always cleaned up on exit, crash, or session end
- **Subscription management** — import and auto-update server lists from subscription links
- **Connection testing** — one-click latency check for each server
- **Deep link support** — import configs via `invisiblegorilla://` URI scheme
- **Multi-language UI** — English, Russian, Persian (easily extensible)
- **System tray integration** — runs quietly in background with quick-access menu
- **Diagnostic logging** — detailed `diagnostic.log` for troubleshooting proxy issues
- **Auto-update** — check and install new versions from within the app

## Screenshots

*Coming soon — the app features a clean dark interface with gorilla branding*

## Architecture

```
InvisibleGorilla-XRayClient/
├── InvisibleGorilla-XRay/       # C# WPF application (.NET 7)
│   ├── Core/                    # XRay core wrapper, P/Invoke bridge, diagnostic logging
│   ├── Handlers/                # Business logic (proxy, tunnel, config, settings)
│   │   └── Proxies/            # Windows proxy management (per-connection API)
│   ├── Factories/               # Window creation via factory pattern
│   ├── Managers/                # App lifecycle, IPC pipes, services
│   ├── Models/                  # Data models & protocol templates
│   ├── Services/                # Localization, analytics
│   ├── Windows/                 # WPF windows (Main, Server, Settings, About, etc.)
│   └── Assets/                  # Icons, localization XAML resources
├── XRay-Wrapper/                # Go wrapper — compiles Xray-core into native library
│   ├── xray/                    # Server start/stop, config parsing, connection test
│   ├── cmd/gorilla-xray/        # Standalone CLI client for macOS/Linux
│   ├── main.go                  # Entry point (c-shared library)
│   └── go.mod                   # Go 1.23, xray-core v25.1.30
├── build.ps1                    # Windows build script (auto-installs deps)
└── build-macos.sh               # macOS build script (Sequoia 15.7+, auto-installs deps)
```

## Quick Start

### Option 1: Download release

Download the latest build from [Releases](https://github.com/hvkeyn/InvisibleGorilla-XRayClient/releases/latest).

### Option 2: Build from source (Windows)

```powershell
git clone "https://github.com/hvkeyn/InvisibleGorilla-XRayClient.git"
cd InvisibleGorilla-XRayClient
.\build.ps1
```

The build script automatically:
1. Checks and installs **Go** (via MSI from go.dev) if missing
2. Checks and installs **GCC** (w64devkit, compatible with cgo) if missing
3. Checks and installs **.NET 7 SDK** (via official Microsoft script) if missing
4. Builds **XRayCore.dll** from the Go wrapper
5. Downloads **geoip.dat** and **geosite.dat** routing databases
6. Downloads **InvisibleMan-TUN** service for TUN mode
7. Builds the .NET WPF application

#### Windows build options

| Command | Description |
|---|---|
| `.\build.ps1` | Full build (all steps) |
| `.\build.ps1 -Publish` | Build + publish as single-file executable |
| `.\build.ps1 -Step GoWrapper` | Only build XRayCore.dll |
| `.\build.ps1 -Step DotNet` | Only build .NET app |
| `.\build.ps1 -Step GeoFiles` | Only download geo databases |
| `.\build.ps1 -Configuration Debug` | Build in Debug mode |
| `.\build.ps1 -SkipTUN` | Skip TUN service download |

### Option 3: Build from source (macOS)

```bash
git clone "https://github.com/hvkeyn/InvisibleGorilla-XRayClient.git"
cd InvisibleGorilla-XRayClient
chmod +x build-macos.sh
./build-macos.sh
```

Tested on **macOS Sequoia 15.7.x** (Apple Silicon & Intel). The script automatically:
1. Checks and installs **Xcode Command Line Tools** (C compiler for cgo)
2. Checks and installs **Go** (via Homebrew or direct .pkg from go.dev)
3. Builds **XRayCore.dylib** from the Go wrapper (cgo c-shared)
4. Downloads **geoip.dat** and **geosite.dat** routing databases
5. Packages a distribution bundle (`dist/`) with CLI binary + data files

The output is a standalone **`gorilla-xray`** CLI binary that can be run directly:

```bash
# Start proxy
./gorilla-xray -config your-config.json

# Test connection
./gorilla-xray -config config.json -test

# SOCKS5 on custom port
./gorilla-xray -config config.json -port 1080 -socks

# Enable macOS system proxy
networksetup -setwebproxy "Wi-Fi" 127.0.0.1 10801
networksetup -setsecurewebproxy "Wi-Fi" 127.0.0.1 10801
```

> **Note:** The WPF GUI is Windows-only. On macOS, the script builds a standalone CLI client + XRayCore shared library. For a macOS GUI, the project needs porting to [Avalonia UI](https://avaloniaui.net) or [.NET MAUI](https://dot.net/maui).

#### macOS build options

| Command | Description |
|---|---|
| `./build-macos.sh` | Full build (all steps) |
| `./build-macos.sh --step go` | Only build gorilla-xray + XRayCore.dylib |
| `./build-macos.sh --step geo` | Only download geo databases |
| `./build-macos.sh --step bundle` | Only package distribution |
| `./build-macos.sh --publish` | Build + package as distributable archive |
| `./build-macos.sh --config Debug` | Build in Debug mode |
| `./build-macos.sh --skip-dotnet` | Skip .NET build (default for WPF projects) |

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

| Component | Technology | Platform |
|---|---|---|
| GUI | WPF (.NET 7, C#) | Windows |
| Proxy engine | [Xray-core](https://github.com/XTLS/Xray-core) v25.1.30 | Cross-platform |
| Native bridge | Go 1.23 → C-shared (cgo): `.dll` / `.dylib` | Windows / macOS |
| TUN service | [InvisibleMan-TUN](https://github.com/InvisibleManVPN/InvisibleMan-TUN) | Windows |
| Geo routing | [v2fly geoip](https://github.com/v2fly/geoip) + [domain-list](https://github.com/v2fly/domain-list-community) | Cross-platform |
| Build system | `build.ps1` (PowerShell) / `build-macos.sh` (Bash) | Windows / macOS |

## Troubleshooting

If the VPN shows "Running" but your IP doesn't change:

1. Check `diagnostic.log` in the app directory for detailed proxy status
2. Verify in Windows Settings > Network > Proxy that manual proxy is enabled
3. Try opening a new incognito window in your browser
4. Make sure your VPN config file is valid (test connection via "Manage server configuration")

## Contributing

We welcome contributions! Here's how you can help:

- **Report bugs** — open an [issue](https://github.com/hvkeyn/InvisibleGorilla-XRayClient/issues)
- **Add a language** — see [Language.md](./Language.md) for instructions
- **Submit code** — fork, branch, and send a pull request

## License

[MIT](./LICENSE.md)

## Acknowledgments

- [InvisibleMan-XRay](https://github.com/InvisibleManVPN/InvisibleMan-XRay) — original project this fork is based on
- [Xray-core](https://github.com/XTLS/Xray-core) — the proxy engine powering this app
