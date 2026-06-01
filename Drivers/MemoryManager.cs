using System;
using System.Collections.Generic;

namespace BAZOS.Drivers
{
    public readonly struct MemoryStats
    {
        public int CapacityBytes { get; }
        public int UsedBytes { get; }
        public int FreeBytes { get; }
        public int PeakUsedBytes { get; }
        public int AllocCount { get; }

        public MemoryStats(int capacityBytes, int usedBytes, int freeBytes, int peakUsedBytes, int allocCount)
        {
            CapacityBytes = capacityBytes;
            UsedBytes = usedBytes;
            FreeBytes = freeBytes;
            PeakUsedBytes = peakUsedBytes;
            AllocCount = allocCount;
        }
    }

    public static class MemoryManager
    {
        private sealed class Block
        {
            public int Handle;
            public byte[] Data = Array.Empty<byte>();
        }

        private static readonly Dictionary<int, Block> _blocks = new();
        private static readonly object _sync = new();
        private static int _nextHandle = 1;
        private static int _capacityBytes = 4 * 1024 * 1024;
        private static int _usedBytes;
        private static int _peakUsedBytes;

        public static int CapacityBytes => _capacityBytes;

        public static void Configure(int capacityBytes)
        {
            if (capacityBytes < 256 * 1024)
                capacityBytes = 256 * 1024;
            lock (_sync)
            {
                _capacityBytes = capacityBytes;
                if (_usedBytes > _capacityBytes)
                    Console.WriteLine("mem: warning: used exceeds configured capacity.");
            }
        }

        public static bool TryAlloc(int size, out int handle)
        {
            handle = 0;
            if (size < 0)
                return false;

            lock (_sync)
            {
                if (_usedBytes + size > _capacityBytes)
                    return false;

                var block = new Block
                {
                    Handle = _nextHandle++,
                    Data = new byte[size]
                };
                _blocks[block.Handle] = block;
                _usedBytes += size;
                if (_usedBytes > _peakUsedBytes)
                    _peakUsedBytes = _usedBytes;
                handle = block.Handle;
                return true;
            }
        }

        public static bool TryAllocCopy(byte[] source, out int handle)
        {
            handle = 0;
            if (source == null)
                source = Array.Empty<byte>();

            if (!TryAlloc(source.Length, out handle))
                return false;

            return TryWrite(handle, source);
        }

        public static bool TryWrite(int handle, byte[] data)
        {
            if (data == null)
                data = Array.Empty<byte>();

            lock (_sync)
            {
                if (!_blocks.TryGetValue(handle, out var block))
                    return false;
                if (block.Data.Length != data.Length)
                    return false;

                Array.Copy(data, block.Data, data.Length);
                return true;
            }
        }

        public static bool TryReadCopy(int handle, out byte[] data)
        {
            data = Array.Empty<byte>();
            lock (_sync)
            {
                if (!_blocks.TryGetValue(handle, out var block))
                    return false;
                data = new byte[block.Data.Length];
                Array.Copy(block.Data, data, block.Data.Length);
                return true;
            }
        }

        public static bool Free(int handle)
        {
            lock (_sync)
            {
                if (!_blocks.TryGetValue(handle, out var block))
                    return false;
                _usedBytes -= block.Data.Length;
                if (_usedBytes < 0)
                    _usedBytes = 0;
                _blocks.Remove(handle);
                return true;
            }
        }

        public static void Reset()
        {
            lock (_sync)
            {
                _blocks.Clear();
                _usedBytes = 0;
                _peakUsedBytes = 0;
                _nextHandle = 1;
            }
        }

        public static MemoryStats GetStats()
        {
            lock (_sync)
            {
                int free = _capacityBytes - _usedBytes;
                if (free < 0)
                    free = 0;
                return new MemoryStats(_capacityBytes, _usedBytes, free, _peakUsedBytes, _blocks.Count);
            }
        }
    }
}
