using System;
using InvisibleGorillaXRay.Models;

namespace InvisibleGorillaXRay.Services.Goida
{
    public sealed class GoidaProbeProgress
    {
        public int Current { get; init; }
        public int Total { get; init; }
        public GoidaNode? Node { get; init; }
        public int LatencyMs { get; init; }
        public GoidaNodeStatus Status { get; init; }
        public bool IsVlessPhase { get; init; }
    }

    public sealed class GoidaTcpVlessProbeOptions
    {
        public int MaxVlessTests { get; init; } = 100;
        public int EarlyStopOkCount { get; init; } = 100;
        public int MaxTcpLatencyForVlessMs { get; init; } = 3000;
        public int NativeTimeoutMs { get; init; } = 7000;
        public int TcpTimeoutMs { get; init; } = 2500;
        public Action? OnFirstVlessOk { get; init; }
    }

    public sealed class GoidaProbeResult
    {
        public int Total { get; init; }
        public int Completed { get; init; }
        public int Ok { get; init; }
        public int Timeout { get; init; }
        public int Error { get; init; }
        public bool Cancelled { get; init; }
        public GoidaNode? BestNode { get; init; }
    }
}
