<p align="center">
  <img src="Images/image-1.png" width="300" alt="Invisible Gorilla XRay - Main Window"/>
</p>

<h1 align="center">Invisible Gorilla XRay</h1>

<p align="center">
  Free, open-source Xray client for Windows, macOS, and Android (experimental)<br>
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
| **Android** | Build from source with `.\build-android.ps1` | .NET Android workload, Android SDK, JDK 11+, Android NDK, arm64 device |

## What Is This?

Invisible Gorilla XRay wraps [Xray-core](https://github.com/XTLS/Xray-core) with desktop and mobile clients, shared config management, and platform-specific routing logic.

On Windows and macOS, the project already provides desktop-ready flows. Android support is now present as an experimental Avalonia head with APK packaging groundwork, local proxy workflow, shared config management, and storage/runtime preparation for mobile packaging.

## Current Android Status

Android support is **experimental**.

- The repo now includes an `InvisibleGorilla-XRay.Android` project that can be packaged as an APK.
- Shared config handling, local Xray listener startup, config import, and Android app-private storage setup are implemented.
- The Android mobile tunnel bridge is **not bundled yet**, so full `VpnService`-backed TUN routing still needs a follow-up native runtime step.
- `proxy mode` on Android currently means a local listener on `127.0.0.1:<port>` rather than desktop-style global system proxy switching.

## Features

- **One-click connect** on desktop - import config and press Run
- **VLESS, VMess, Trojan, Shadowsocks** - all major protocols supported
- **Proxy and TUN modes** - Windows and macOS desktop routing, Android groundwork for local proxy and future mobile VPN
- **Server management** - add, test, switch between multiple servers
- **Connection testing** - check latency with one click
- **Subscription support** - auto-update server lists from provider links
- **Shared core logic** - common config, templates, analytics, and Xray wrapper integration across platforms
- **Diagnostic logging** - startup/runtime troubleshooting for both desktop and mobile app roots

## Screenshots

<p align="center">
  <img src="Images/image-1.png" width="280" alt="Connected"/>
  &nbsp;&nbsp;
  <img src="Images/image-2.png" width="280" alt="Server Management"/>
</p>

## Quick Start

### 1. Download or build

- Windows and macOS: use the latest release from the [Releases page](https://github.com/hvkeyn/InvisibleGorilla-XRayClient/releases/latest).
- Android: build the APK from source with `.\build-android.ps1`.

### 2. Add a server

Open the app, import a raw JSON config, config link, or subscription, then select the config you want to run.

### 3. Connect

- Desktop: choose your mode and click **Run**.
- Android: use the experimental Android head to manage configs and start the local proxy workflow while the mobile tunnel bridge is being finalized.

## Build from Source

<details>
<summary><b>Windows</b></summary>

```powershell
git clone https://github.com/hvkeyn/InvisibleGorilla-XRayClient.git
cd InvisibleGorilla-XRayClient
.\build.ps1
```

The script auto-installs Go, GCC, and .NET 7 SDK if missing, then builds everything for Windows.

| Command | Description |
|---|---|
| `.\build.ps1` | Full Windows build |
| `.\build.ps1 -Publish` | Build + single-file executable |
| `.\build.ps1 -Step GoWrapper` | Only build `XRayCore.dll` |
| `.\build.ps1 -Step DotNet` | Only build the .NET desktop app |

</details>

<details>
<summary><b>macOS</b></summary>

```bash
git clone https://github.com/hvkeyn/InvisibleGorilla-XRayClient.git
cd InvisibleGorilla-XRayClient
chmod +x build-macos.sh
./build-macos.sh
```

Tested on macOS Sequoia 15.7+ (Apple Silicon and Intel). Builds an `.app` bundle with Avalonia UI.

| Command | Description |
|---|---|
| `./build-macos.sh` | Full macOS build |
| `./build-macos.sh --publish` | Build + distributable archive |
| `./build-macos.sh --step go` | Only build `XRayCore.dylib` |
| `./build-macos.sh --step bundle` | Only package the `.app` bundle |

</details>

<details>
<summary><b>Android</b></summary>

### Prerequisites

On Windows, `.\build-android.ps1` now installs missing prerequisites automatically on first run. That includes:

- .NET 8 SDK
- .NET Android workload
- JDK 17
- Android command-line tools
- Android SDK platform/build-tools
- Android NDK
- Go 1.23

You still need a working internet connection, and some installers may request elevation depending on local system policy.

### Build

```powershell
git clone https://github.com/hvkeyn/InvisibleGorilla-XRayClient.git
cd InvisibleGorilla-XRayClient
.\build-android.ps1
```

The Android build script:

- checks for missing build dependencies and installs them automatically when possible
- downloads `geoip.dat` and `geosite.dat` into `InvisibleGorilla-XRay.Android/Assets/Runtime`
- builds the Android native bridge from `XRay-Wrapper` using the Android NDK and packages it into runtime assets
- publishes `InvisibleGorilla-XRay.Android` as an APK

| Command | Description |
|---|---|
| `.\build-android.ps1` | Full Android build + APK publish |
| `.\build-android.ps1 -SkipNativeBridge` | Reuse the existing Android native bridge runtime asset in `Assets/Runtime` |
| `.\build-android.ps1 -SkipGeoFiles` | Reuse existing geo files in `Assets/Runtime` |
| `.\build-android.ps1 -NoPublish` | Prepare runtime assets without publishing the APK |
| `.\build-android.ps1 -KeystorePath <path> -KeyAlias <alias>` | Publish a signed APK using `ANDROID_SIGNING_PASSWORD` |

</details>

## Architecture

| Component | Technology | Platform |
|---|---|---|
| Windows GUI | WPF (.NET 7, C#) | Windows |
| macOS GUI | Avalonia UI 11 (.NET 7, C#) | macOS |
| Android GUI | Avalonia UI 11 (.NET 8 Android, C#) | Android |
| Shared logic | `InvisibleGorilla.Core` (.NET class library) | Cross-platform |
| Proxy engine | [Xray-core](https://github.com/XTLS/Xray-core) v25.1.30 | Cross-platform |
| Native bridge | Go 1.23 -> cgo shared library (`.dll` / `.dylib` / `.so`) | Windows / macOS / Android |
| Windows tunnel service | [InvisibleGorilla-TUN](https://github.com/hvkeyn/InvisibleGorilla-TUN) | Windows only |
| Android mobile VPN layer | `VpnService` groundwork in Android head | Android |
| Geo routing | [v2fly geoip](https://github.com/v2fly/geoip) + [domain-list](https://github.com/v2fly/domain-list-community) | Cross-platform |

## Troubleshooting

| Problem | Solution |
|---|---|
| App shows "Running" but traffic does not change on desktop | Check `diagnostic.log` in the app folder. Try a different server or protocol. |
| Android proxy mode starts but apps do not route automatically | Android currently starts a local listener; point apps to `127.0.0.1:<proxy-port>` or wait for the follow-up mobile tunnel bridge. |
| Android TUN mode reports an error | This repository now contains the Android groundwork, but the native mobile tunnel bridge is still a follow-up task. |
| Android native bridge asset is missing during packaging | Run `.\build-android.ps1` with a valid `ANDROID_NDK_ROOT` so the wrapper can build and package the Android shared library runtime asset. |
| `dotnet publish` cannot find Android SDK | Set `ANDROID_SDK_ROOT` or `ANDROID_HOME`, or pass `-AndroidSdkDirectory` to `.\build-android.ps1`. |
| Desktop proxy stays on after a crash | Restart the app - it cleans up stale proxy settings on startup/shutdown. |

## Contributing

- **Report bugs** - open an [issue](https://github.com/hvkeyn/InvisibleGorilla-XRayClient/issues)
- **Add a language** - see [Language.md](./Language.md)
- **Submit code** - fork, branch, and send a pull request

## License

[MIT](./LICENSE.md)

## Credits

- [InvisibleMan-XRay](https://github.com/InvisibleManVPN/InvisibleMan-XRay) - original project
- [Xray-core](https://github.com/XTLS/Xray-core) - proxy engine
- [InvisibleGorilla-TUN](https://github.com/hvkeyn/InvisibleGorilla-TUN) - Windows tunnel companion service
