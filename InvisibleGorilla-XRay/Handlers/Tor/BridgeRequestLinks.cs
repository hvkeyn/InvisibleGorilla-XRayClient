using System;

namespace InvisibleGorillaXRay.Handlers.Tor
{
    /// <summary>
    /// Assisted (non-automated) bridge-request channels, mirroring Orbot/Tor Browser.
    /// An app cannot read the user inbox or Telegram session, so these helpers only
    /// build the links to open; the user sends the request and pastes the reply back
    /// into the smart input box, which auto-detects the bridge lines.
    /// </summary>
    public static class BridgeRequestLinks
    {
        public const string EmailAddress = "bridges@torproject.org";
        public const string TelegramBot = "https://t.me/GetBridgesBot";
        public const string HttpsBridges = "https://bridges.torproject.org/options";

        /// <summary>
        /// Builds a mailto: link pre-filled to request bridges of the given transport.
        /// BridgeDB only honours requests sent from Gmail or Riseup addresses.
        /// </summary>
        public static string BuildEmailUrl(string transport = "obfs4")
        {
            string t = string.IsNullOrWhiteSpace(transport) ? "obfs4" : transport.Trim();
            string subject = Uri.EscapeDataString("get bridges");
            string body = Uri.EscapeDataString($"get transport {t}");
            return $"mailto:{EmailAddress}?subject={subject}&body={body}";
        }
    }
}