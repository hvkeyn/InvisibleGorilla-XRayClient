using System;
using InvisibleGorillaXRay.Models;

namespace InvisibleGorillaXRay.Android.Services
{
    internal static class GoidaActiveNodeBridge
    {
        public static Action<GoidaNode>? OnActiveNodeChanged { get; set; }
    }
}
