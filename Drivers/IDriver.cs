using System;

namespace BAZOS.Drivers
{
    public readonly struct DriverContext
    {
        public bool IsDevMode { get; }

        public DriverContext(bool isDevMode)
        {
            IsDevMode = isDevMode;
        }
    }

    public interface IDriver
    {
        string Id { get; }
        string Version { get; }

        void Init(DriverContext ctx);
        void Shutdown();
    }
}

