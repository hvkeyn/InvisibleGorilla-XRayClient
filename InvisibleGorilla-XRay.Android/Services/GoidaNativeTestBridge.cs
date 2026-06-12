using System;

namespace InvisibleGorillaXRay.Android.Services
{
    /// <summary>
    /// Lets Core Goida pause the live tunnel before a native VLESS probe during failover.
    /// MainView registers the callbacks at startup.
    /// </summary>
    internal static class GoidaNativeTestBridge
    {
        public static Func<bool>? PauseForNativeTest { get; set; }
        public static Action? ResumeAfterNativeTest { get; set; }
    }
}
