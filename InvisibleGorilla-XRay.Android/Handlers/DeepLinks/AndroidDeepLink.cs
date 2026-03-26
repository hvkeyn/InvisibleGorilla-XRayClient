using System;

namespace InvisibleGorillaXRay.Android.Handlers.DeepLinks
{
    using InvisibleGorillaXRay.Handlers.DeepLinks;

    public sealed class AndroidDeepLink : IDeepLink
    {
        public void Register()
        {
        }
    }

    public static class AndroidDeepLinkDispatcher
    {
        public static Action<string> OnReceiveArg = _ => { };
    }
}
