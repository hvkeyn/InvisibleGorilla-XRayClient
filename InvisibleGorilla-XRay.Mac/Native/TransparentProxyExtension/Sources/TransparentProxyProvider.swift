import Foundation
import NetworkExtension

// Scaffold for the future signed Network Extension target.
// The .NET client writes TUN/macos-transparent-proxy-config.json before starting
// TUN mode so the native helper can map the excluded bundle identifiers to the
// effective include-list used by NETransparentProxyManager.
final class InvisibleGorillaTransparentProxyProvider: NETransparentProxyProvider {
    override func startProxy(options: [String : Any]? = nil, completionHandler: @escaping (Error?) -> Void) {
        completionHandler(nil)
    }

    override func stopProxy(with reason: NEProviderStopReason, completionHandler: @escaping () -> Void) {
        completionHandler()
    }

    override func handleNewFlow(_ flow: NEAppProxyFlow) -> Bool {
        // TODO:
        // 1. Resolve the flow source app bundle identifier.
        // 2. Forward included flows to the local XRay SOCKS listener described
        //    by macos-transparent-proxy-config.json.
        // 3. Keep UDP/TCP parity with the existing tun2socks path.
        return true
    }
}
