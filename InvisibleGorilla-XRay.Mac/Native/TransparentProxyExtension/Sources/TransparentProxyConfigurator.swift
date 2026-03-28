import Foundation
import NetworkExtension

struct TransparentProxyBridgeConfig: Decodable {
    let mode: String
    let socksPort: Int
    let tunnelAddress: String
    let dns: String
    let excludedBundleIdentifiers: [String]
    let generatedAtUtc: Date
}

enum TransparentProxyConfigurator {
    static func installOrUpdate(from configURL: URL) throws {
        let data = try Data(contentsOf: configURL)
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let config = try decoder.decode(TransparentProxyBridgeConfig.self, from: data)

        // TODO:
        // 1. Discover installed applications on macOS.
        // 2. Build NEAppRule include-list = all known bundle ids minus excludedBundleIdentifiers.
        // 3. Save/update a NETransparentProxyManager profile bound to the signed provider target.
        print("Prepared transparent proxy scaffold for \(config.excludedBundleIdentifiers.count) excluded apps")
    }
}
