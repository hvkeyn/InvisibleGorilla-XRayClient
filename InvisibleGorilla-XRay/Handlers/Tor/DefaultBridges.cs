using System.Collections.Generic;

namespace InvisibleGorillaXRay.Handlers.Tor
{
    using Models;

    /// <summary>
    /// Built-in bridge lines shipped with Tor Browser / Orbot. Used as a fallback when the
    /// user has not supplied (or fetched) their own bridges. obfs4 lines rotate periodically;
    /// if the built-ins stop working the user can fetch fresh ones via the moat client.
    /// </summary>
    public static class DefaultBridges
    {
        // Snowflake uses a single well-known transport line (broker + STUN + front are baked in).
        public const string Snowflake =
            "snowflake 192.0.2.3:80 2B280B23E1107BB62ABFC40DDCC8824814F80A72 " +
            "fingerprint=2B280B23E1107BB62ABFC40DDCC8824814F80A72 " +
            "url=https://1098762253.rsc.cdn77.org/ " +
            "fronts=www.cdn77.com,www.phpmyadmin.net " +
            "ice=stun:stun.l.google.com:19302,stun:stun.antisip.com:3478 " +
            "utls-imitate=hellorandomizedalpn";

        // meek-azure domain-fronted bridge (built into Tor Browser).
        public const string MeekAzure =
            "meek_lite 192.0.2.20:80 97700DFE9F483596DDA6264C4D7DF7641E1E39CE " +
            "url=https://1786721410.rsc.cdn77.org/ front=www.cdn77.com " +
            "utls=HelloRandomizedALPN";

        // A representative subset of the obfs4 bridges bundled with Tor Browser.
        // The moat client (Fetch bridges) can replace these with fresh, less-blocked ones.
        public static readonly List<string> Obfs4 = new List<string>
        {
            "obfs4 37.218.245.14:38224 D9A82D2F9C2F65A18407B1D2B764F130847F8B5D cert=bjRaungkGdjm4KAxyrYHzU+P6lZQ7+QtVQR4cBwiQ60FXrt9hJNoUO9eQz4O0VyJXrgYa6Q+w iat-mode=0",
            "obfs4 85.31.186.98:443 011F2599C0E9B27EE74B353155E244813763C3E5 cert=ayq0XzCwhpdysn5o0EyDUbmSOx3X/oTEbzDMvczHOdBJKlvIdHHLJGkZ ARtT4dcBFArPPg iat-mode=0",
            "obfs4 85.31.186.26:443 91A6354697E6B02A386312F68D82CF86824D3606 cert=PBwr+S8JTVZo6MPdHnkTwXJPILWADLqfMGoVvhZClMq/Urndyd42Bwf9YFJHZnHb14kAYg iat-mode=0",
            "obfs4 193.11.166.194:27015 2D82C2E354D531A68469ADF7F878FA6060C6BACA cert=4TLQPJrTSaDffMK7Nbao6LC7G9OW/NHkUwIdjLSS3KYf0Nv4/nQiiI8dY2TcsQx01NniO g iat-mode=0",
            "obfs4 193.11.166.194:27020 86AC7B8D430DAC4117E9F42C9EAED18133863AAF cert=0LDeJH4JzMDtkJJrFphJCiPqKx7loozKN7VNfuukMGfHO0Z8OGdzHVkhVAOfo1mUdv9cMg iat-mode=0",
            "obfs4 193.11.166.194:27025 1AE2C08904527FEA90C4C4F8C1083EA59FBC6FAF cert=ItvYZzW5tn6v3G4UnQa6Qz04Npro6e81AP70YujmK/KXwDFPTs3aHXcHp4n8Vt6w/bv8cA iat-mode=0",
        };

        public static List<string> ForType(BridgeType type)
        {
            switch (type)
            {
                case BridgeType.SNOWFLAKE:
                    return new List<string> { Snowflake };
                case BridgeType.MEEK_AZURE:
                    return new List<string> { MeekAzure };
                case BridgeType.OBFS4:
                    return new List<string>(Obfs4);
                default:
                    return new List<string>();
            }
        }
    }
}
