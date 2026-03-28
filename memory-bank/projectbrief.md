# Project Brief

## Project
`InvisibleGorilla-XRayClient` is a cross-platform Xray client with Windows, macOS, and Android heads built around shared runtime/configuration logic in `InvisibleGorilla.Core`.

## Primary Goals
- Provide a desktop-grade Xray client UX with shared config import, subscription handling, testing, and connect/disconnect flows.
- Keep Windows and macOS stable while bringing Android to functional parity where practical.
- Route Android device traffic through the selected Xray config by using `Android.Net.VpnService` plus a native mobile tunnel bridge.
- Ship Android builds for both emulator and device targets, especially `x86_64` and `arm64-v8a`.

## Current Android Direction
- Android UI is implemented in `InvisibleGorilla-XRay.Android` with Avalonia.
- Android runtime uses packaged native `libXRayCore.so` libraries from `Assets/Runtime/<abi>/`.
- Device-wide routing is handled by Android `VpnService`, not by porting the Windows TUN service model directly.

## Success Criteria
- Import config or subscription from file/link.
- Select a config and start routing successfully.
- Confirm browser IP changes while VPN is running and reverts after stop.
- Build signed or unsigned APKs for the supported Android ABIs without manual asset copying hacks.
