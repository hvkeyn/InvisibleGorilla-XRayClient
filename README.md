<p align="center">
  <img src="Images/image-1.png" width="300" alt="Invisible Gorilla XRay — Main Window"/>
</p>

<h1 align="center">Invisible Gorilla XRay</h1>

<p align="center">
  Free, open-source VPN client for Windows & macOS<br>
  powered by <a href="https://github.com/XTLS/Xray-core">Xray-core</a>
</p>

<p align="center">
  <a href="https://github.com/hvkeyn/InvisibleGorilla-XRayClient/releases/latest"><img src="https://img.shields.io/github/v/release/hvkeyn/InvisibleGorilla-XRayClient?style=flat-square" alt="Latest Release"/></a>
  <a href="./LICENSE.md"><img src="https://img.shields.io/badge/License-MIT-green.svg?style=flat-square" alt="MIT License"/></a>
</p>

---

## Download

| Platform | Download | Requirements |
|---|---|---|
| **Windows** | [Latest Release (.exe)](https://github.com/hvkeyn/InvisibleGorilla-XRayClient/releases/latest) | Windows 10+ |
| **macOS** | [Latest Release (.app)](https://github.com/hvkeyn/InvisibleGorilla-XRayClient/releases/latest) | macOS 13+ (Apple Silicon & Intel) |

## What Is This?

Invisible Gorilla XRay wraps the powerful [Xray-core](https://github.com/XTLS/Xray-core) proxy engine with a clean, intuitive interface. Import your server configuration, click **Run**, and your traffic is encrypted.

No command-line knowledge needed — just download, add your config, and connect.

## Features

- **One-click connect** — import config and press Run
- **VLESS, VMess, Trojan, Shadowsocks** — all major protocols supported
- **Proxy & TUN modes** — system-wide proxy or tunnel-based routing
- **Server management** — add, test, switch between multiple servers
- **Connection testing** — check latency with one click
- **Subscription support** — auto-update server lists from provider links
- **System tray** — runs quietly in background
- **Multi-language** — English, Russian, Persian
- **Crash-safe** — proxy settings are always cleaned up on exit

## Screenshots

<p align="center">
  <img src="Images/image-1.png" width="280" alt="Connected"/>
  &nbsp;&nbsp;
  <img src="Images/image-2.png" width="280" alt="Server Management"/>
</p>

## Quick Start

### 1. Download & Install

Grab the latest release for your platform from the [Releases page](https://github.com/hvkeyn/InvisibleGorilla-XRayClient/releases/latest).

### 2. Add a Server

Open the app → click **Manage server configuration** → tap **+** → paste your server config (JSON file or subscription link).

### 3. Connect

Select your server, go back to the main screen, and click **Run**. That's it — you're connected.

## Build from Source

<details>
<summary><b>Windows</b></summary>

```powershell
git clone https://github.com/hvkeyn/InvisibleGorilla-XRayClient.git
cd InvisibleGorilla-XRayClient
.\build.ps1
```

The script auto-installs Go, GCC, .NET 7 SDK if missing, then builds everything.

| Command | Description |
|---|---|
| `.\build.ps1` | Full build |
| `.\build.ps1 -Publish` | Build + single-file executable |
| `.\build.ps1 -Step GoWrapper` | Only build XRayCore.dll |
| `.\build.ps1 -Step DotNet` | Only build .NET app |

</details>

<details>
<summary><b>macOS</b></summary>

```bash
git clone https://github.com/hvkeyn/InvisibleGorilla-XRayClient.git
cd InvisibleGorilla-XRayClient
chmod +x build-macos.sh
./build-macos.sh
```

Tested on macOS Sequoia 15.7+ (Apple Silicon & Intel). Builds an `.app` bundle with Avalonia UI.

| Command | Description |
|---|---|
| `./build-macos.sh` | Full build |
| `./build-macos.sh --publish` | Build + distributable archive |
| `./build-macos.sh --step go` | Only build XRayCore.dylib |
| `./build-macos.sh --step bundle` | Only package .app bundle |

</details>

## Architecture

| Component | Technology | Platform |
|---|---|---|
| Windows GUI | WPF (.NET 7, C#) | Windows |
| macOS GUI | Avalonia UI 11 (.NET 8, C#) | macOS |
| Proxy engine | [Xray-core](https://github.com/XTLS/Xray-core) v25.1.30 | Cross-platform |
| Native bridge | Go 1.23 → cgo c-shared (`.dll` / `.dylib`) | Windows / macOS |
| Shared logic | InvisibleGorilla.Core (.NET class library) | Cross-platform |
| TUN service | [InvisibleMan-TUN](https://github.com/InvisibleManVPN/InvisibleMan-TUN) | Windows |
| Geo routing | [v2fly geoip](https://github.com/v2fly/geoip) + [domain-list](https://github.com/v2fly/domain-list-community) | Cross-platform |

## Troubleshooting

| Problem | Solution |
|---|---|
| VPN shows "Running" but IP doesn't change | Check `diagnostic.log` in the app folder. Try a different server or protocol. |
| Can't connect to server | Click **Check** on your config to test latency. If timeout — the server may be down. |
| App won't start on macOS | Right-click → Open (first launch). Grant Network permissions in System Settings. |
| Proxy stays on after crash | Restart the app — it automatically cleans up stale proxy settings. |

## Contributing

- **Report bugs** — open an [issue](https://github.com/hvkeyn/InvisibleGorilla-XRayClient/issues)
- **Add a language** — see [Language.md](./Language.md)
- **Submit code** — fork, branch, and send a pull request

## License

[MIT](./LICENSE.md)

## Credits

- [InvisibleMan-XRay](https://github.com/InvisibleManVPN/InvisibleMan-XRay) — original project
- [Xray-core](https://github.com/XTLS/Xray-core) — proxy engine
