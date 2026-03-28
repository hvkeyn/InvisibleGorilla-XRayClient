# Progress

## Working
- Shared Xray config loading and runtime startup in `InvisibleGorilla.Core`.
- Android config import from links/files and subscription management UI.
- Android config check now uses the shared native connection test path instead of a raw endpoint socket probe.
- Android config share now offers a choice between copying the source link and exporting the full config text.
- Android native library packaging through `AndroidNativeLibrary`.
- Android `VpnService` startup with the local mobile bridge to the SOCKS listener.
- Android connection notification now keeps detailed state for both active and recently stopped sessions.
- Browser IP switching on the emulator when VPN is started and restored after stop.
- APK publishing for both `android-x64` and `android-arm64`.

## Recently Fixed
- Native `DllNotFoundException` path for Android packaged libraries.
- x86_64 Android runtime packaging and publish flow.
- Device-wide Android routing through `VpnService`.
- Stop-path ANR during `AndroidVpnService` teardown.
- Duplicate/stale `StopServer()` signal handling in the Go wrapper.
- Android config check always reporting timeout.
- Android config share no longer forcing the full JSON into the clipboard by default.
- Android notification no longer loses connection details when the app is reopened or when the VPN transitions from running to stopped.
- Android startup no longer flashes a false `Stopped` notification because stale-tunnel cleanup is separated from the real stop flow.

## Remaining Risks
- Physical-device validation is still needed after the latest stop/share fixes.
- Android analyzer/nullability warnings remain in notification and service code.
- Deep-link testing through raw `adb shell am start` can still be misleading because of shell escaping issues.
- Emulator UI automation is still flaky for the small icon-only buttons, so share-sheet validation was only partial on the latest pass.

## Artifacts
- Emulator APK output: `publish-android/bugfix-x64/io.invisiblegorilla.xray-Signed.apk`
- Device APK output: `publish-android/bugfix-arm64/io.invisiblegorilla.xray-Signed.apk`
