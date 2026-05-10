using System;

namespace BAZOS.Drivers
{
    public enum DriverLifecycleState
    {
        Loaded,
        Verified,
        Enabled,
        Started,
        Failed,
        Stopped
    }

    public enum DriverErrorReason
    {
        None,
        BadSignature,
        MissingDep,
        InitFailed,
        PolicyBlocked
    }

    public sealed class DriverStatus
    {
        public string DriverId { get; set; } = "";
        public DriverLifecycleState State { get; set; } = DriverLifecycleState.Loaded;
        public DriverErrorReason Reason { get; set; } = DriverErrorReason.None;
        public string Message { get; set; } = "";
        public string Phase { get; set; } = "";
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public bool Enabled { get; set; }
    }
}

