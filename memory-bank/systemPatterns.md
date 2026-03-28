# System Patterns

## Architecture
- `InvisibleGorilla.Core` owns shared config loading, runtime settings, Xray startup/shutdown, and proxy/tunnel abstraction.
- Platform heads provide UI, storage, and OS-specific routing integrations.
- Android-specific VPN control lives in `InvisibleGorilla-XRay.Android/Services`.

## Android Routing Flow
1. `MainView` saves settings and requests VPN permission through `MainActivity`.
2. `InvisibleGorillaXRayCore.Run()` starts `XRayCoreWrapper.StartServer()` and waits for the local SOCKS listener to become active.
3. In TUN mode, `AndroidTunnel.Enable()` delegates to `AndroidVpnServiceController.Start(...)`.
4. `AndroidVpnService` establishes the TUN interface and calls `XRayCoreWrapper.StartAndroidTunnel(...)`.
5. Native Go code in `XRay-Wrapper/xray/android_tun2socks.go` bridges TUN packets to the local SOCKS listener.

## Android Stop Pattern
- Stop must not block the service main thread.
- `AndroidVpnService` handles stop work asynchronously.
- Native tunnel shutdown must not hold the global tunnel mutex while closing the TUN FD or LWIP stack, otherwise Android service ANRs can occur.
- Closing the TUN file before LWIP teardown helps unblock the packet reader cleanly.
- Android notification state should only move to `Stopped` from the real VPN teardown path; startup cleanup of stale tunnel state must not reuse the same stop-notification routine, or the foreground notification will briefly regress to `Stopped` during `START`.

## Packaging Pattern
- Android native runtime is packaged as `AndroidNativeLibrary`, not copied from assets at runtime.
- ABI outputs live under `InvisibleGorilla-XRay.Android/Assets/Runtime/arm64-v8a/` and `.../x86_64/`.

## Verification Pattern
- Do not trust UI state alone for Android VPN work.
- Validate with emulator/device browser IP before and after `STOP`.
- Use `diagnostic.log`, `logcat`, and screenshots together when debugging lifecycle issues.
