# Tech Context

## Languages And Frameworks
- C# / .NET 8 for shared core and Android app code.
- Avalonia 11 for the Android UI layer.
- Go with cgo for the native `XRayCore` bridge and Android `tun2socks` glue.

## Important Android Components
- `InvisibleGorilla-XRay.Android/Services/AndroidVpnService.cs`
- `InvisibleGorilla-XRay.Android/Services/AndroidVpnServiceController.cs`
- `InvisibleGorilla-XRay.Android/Handlers/Tunnels/AndroidTunnel.cs`
- `InvisibleGorilla-XRay.Android/Views/MainView.axaml.cs`
- `XRay-Wrapper/xray/android_tun2socks.go`

## Tooling
- Local .NET SDK path used in this workspace: `C:\Users\hex\AppData\Local\Microsoft\dotnet\dotnet.exe`
- Android SDK root: `C:\Users\hex\AppData\Local\Android\Sdk`
- Android NDK version in use: `26.3.11579264`
- JDK path used for Android publish: `C:\Program Files\Eclipse Adoptium\jdk-17.0.18.8-hotspot`

## Build Notes
- Rebuild both Android native libraries after Go bridge changes.
- Publish emulator APK with RID `android-x64`.
- Publish device APK with RID `android-arm64`.
- The Android project currently disables trimming and AOT for build stability.

## Runtime Constraints
- Android uses app-private storage and packaged native libs from the APK.
- Complex `vless://` links can be awkward through `adb shell am start` because shell parsing may corrupt `&` and `#`.
- Mobile validation sometimes needs a mix of Mobile MCP and direct `adb shell input tap`.
