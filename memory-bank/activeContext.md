# Active Context

## Current Focus
Stabilize Android device-wide VPN routing so the selected config affects real app/browser traffic and the connection lifecycle is safe on start/stop.

## Recent Changes
- Confirmed Android browser traffic routes through the selected config using `VpnService` plus the mobile `tun2socks` bridge.
- Fixed Android stop-path ANR by moving VPN stop work off the service main thread in `AndroidVpnService`.
- Fixed a native shutdown deadlock risk by releasing the Go tunnel mutex before closing the TUN FD and LWIP stack, and by closing the TUN file first.
- Hardened server shutdown in `XRay-Wrapper/xray/wrapper.go` so duplicate `StopServer()` calls do not leave stale stop signals or block later runs.
- Removed the eager Android UI-side `DisableMode()` stop race and added a stop guard in `MainView` plus `AndroidVpnServiceController`.
- Switched Android config checking from raw `TcpClient.ConnectAsync()` to the shared native `core.Test(...)` path, so the check actually validates the Xray config.
- Reworked Android config sharing into a two-option flow: copy source link when available or export the full config text, with source-link metadata stored for link imports.
- Restored Android top-notification details for running and stopped states by keeping the session metadata alive across app re-entry and by publishing a non-ongoing `Stopped` notification after VPN teardown.
- Split Android VPN start cleanup from the real stop path so startup no longer emits a false `Stopped` notification before the new foreground session is established.
- Prevented the UI thread from briefly forcing notification state back to `Running` while the Android VPN service is already processing `STOP_VPN`.
- Rebuilt native `libXRayCore.so` for `arm64-v8a` and `x86_64`.
- Published fresh APKs for `android-arm64` and `android-x64` under `publish-android/bugfix-arm64/` and `publish-android/bugfix-x64/`.

## Latest Validation Snapshot
- With VPN active, emulator Chrome resolved `https://api.ipify.org` to `158.160.104.107`.
- After `STOP`, the same emulator resolved a fresh request to `95.24.237.142`.
- No `ANR in io.invisiblegorilla.xray` or `Timeout executing service` remained in the final validation pass.
- After the latest Android UI fix, tapping the Wi-Fi/check icon on the emulator reported a real latency (`278 ms`) instead of always timing out.
- The new share flow was rebuilt successfully, but final tap-driven validation of the tiny share icon remains partially blocked by flaky emulator coordinate automation.
- After the notification fix, emulator `dumpsys notification --noredact` showed `Running - WhiteBlade.json - RX 0 B/s TX 0 B/s` while the VPN service was active.
- After tapping `STOP`, the same notification entry remained visible as `Stopped - WhiteBlade.json` instead of disappearing, and the stop transition no longer bounced back to `Running`.

## Next Likely Work
- Run the updated stop/start/share flow on a physical Samsung device.
- Clean up remaining Android warnings in notification and service code.
- Keep aligning Android UI and assets with the Windows/macOS presentation.
