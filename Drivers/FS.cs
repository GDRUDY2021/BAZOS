using Cosmos.Kernel.HAL;
using System;
using System.Text;

namespace BAZOS.FS
{
    public struct BazDirEntry
    {
        public string Name;       // ASCII, max 128 chars
        public uint FirstBlockLba;
        public uint Size;
        public byte Flags;        // 0 = файл, 1 = каталог
        public ushort OwnerGroupId;
        public byte PermOwner;    // bitmask rwxd
        public byte PermOther;    // bitmask rwxd
        public byte Inherit;      // 1 = children inherit perms from this dir
    }

    public struct BazSuperblock
    {
        public uint Magic;        // 'B''A''Z''0'
        public uint Version;      // 1
        public uint RootDirLba;   // LBA первого сектора корня
        public uint TotalBlocks;  // можно 0, заполним позже

        public const uint ExpectedMagic = 0x30415A42; // 'B','A','Z','0' LE
        public const uint CurrentVersion = 1;
    }

    public enum BazPathKind
    {
        NotFound,
        File,
        Directory
    }

    public struct BazPathResult
    {
        public BazPathKind Kind;
        public uint DirLba;       // каталог, в котором лежит объект
        public string Name;       // последний сегмент пути
        public BazDirEntry Entry; // валиден, если Kind != NotFound
    }

    // Объектный драйвер для работы с конкретным ATA диском (без хардкода 1 диска)
    public class AtaDrive
    {
        private readonly ushort _basePort;
        private readonly byte _drivePrefix;

        // --- ДОБАВЛЯЕМ ЭТИ ДВА СВОЙСТВА ДЛЯ ИСПРАВЛЕНИЯ ОШИБКИ CS1061 ---
        // Используем заглушку в 1 ГБ, чтобы не вешать шину ATA опросами пустых портов
        public ulong BlockSize => 512;
        public ulong BlockCount => 2097152;
        // ----------------------------------------------------------------

        // Вычисляем порты динамически на основе базового (Primary: 0x1F0, Secondary: 0x170)
        private ushort DataPort => _basePort;
        private ushort ErrorPort => (ushort)(_basePort + 1);
        private ushort SecCountPort => (ushort)(_basePort + 2);
        private ushort LbaLowPort => (ushort)(_basePort + 3);
        private ushort LbaMidPort => (ushort)(_basePort + 4);
        private ushort LbaHighPort => (ushort)(_basePort + 5);
        private ushort DrivePort => (ushort)(_basePort + 6);
        private ushort StatusCmdPort => (ushort)(_basePort + 7);

        const byte ATA_CMD_READ_SECTORS = 0x20;
        const byte ATA_CMD_WRITE_SECTORS = 0x30;

        const byte ATA_SR_BSY = 0x80;
        const byte ATA_SR_DRDY = 0x40;
        const byte ATA_SR_DF = 0x20;
        const byte ATA_SR_ERR = 0x01;
        const byte ATA_SR_DRQ = 0x08;

        public AtaDrive(ushort basePort, bool isMaster)
        {
            _basePort = basePort;
            // 0xE0 = Master LBA mode, 0xF0 = Slave LBA mode
            _drivePrefix = isMaster ? (byte)0xE0 : (byte)0xF0;
        }

        private bool WaitReady()
        {
            for (int i = 0; i < 2_000_000; i++)
            {
                byte status = PlatformHAL.PortIO.ReadByte(StatusCmdPort);
                if ((status & ATA_SR_BSY) == 0 && (status & ATA_SR_DRDY) != 0) return true;

                if (i % 2000 == 0 && !BazFs.IsIoLocked)
                {
                    BazFs.IsIoLocked = true;
                    BAZOS.Core.Scheduler.Tick();
                    BazFs.IsIoLocked = false;
                }
            }
            return false;
        }

        private bool WaitDrq()
        {
            for (int i = 0; i < 2_000_000; i++)
            {
                byte status = PlatformHAL.PortIO.ReadByte(StatusCmdPort);
                if ((status & ATA_SR_ERR) != 0 || (status & ATA_SR_DF) != 0) return false;
                if (((status & ATA_SR_BSY) == 0) && ((status & ATA_SR_DRQ) != 0)) return true;

                if (i % 2000 == 0 && !BazFs.IsIoLocked)
                {
                    BazFs.IsIoLocked = true;
                    BAZOS.Core.Scheduler.Tick();
                    BazFs.IsIoLocked = false;
                }
            }
            return false;
        }

        public bool WriteSector(uint lba, ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length < 512)
                throw new ArgumentException("Buffer must be at least 512 bytes", nameof(buffer));

            if (!WaitReady())
            {
                Console.WriteLine($"ATA WRITE timeout: disk at port 0x{_basePort:X} not ready.");
                return false;
            }

            byte driveHead = (byte)(_drivePrefix | ((lba >> 24) & 0x0F));
            PlatformHAL.PortIO.WriteByte(DrivePort, driveHead);

            PlatformHAL.PortIO.WriteByte(SecCountPort, 1);
            PlatformHAL.PortIO.WriteByte(LbaLowPort, (byte)(lba & 0xFF));
            PlatformHAL.PortIO.WriteByte(LbaMidPort, (byte)((lba >> 8) & 0xFF));
            PlatformHAL.PortIO.WriteByte(LbaHighPort, (byte)((lba >> 16) & 0xFF));

            PlatformHAL.PortIO.WriteByte(StatusCmdPort, ATA_CMD_WRITE_SECTORS);
            if (!WaitDrq())
            {
                Console.WriteLine($"ATA WRITE error: DRQ not set (Port: 0x{_basePort:X}).");
                return false;
            }

            for (int i = 0; i < 256; i++)
            {
                ushort w = (ushort)(buffer[i * 2 + 0] | (buffer[i * 2 + 1] << 8));
                PlatformHAL.PortIO.WriteWord(DataPort, w);
            }

            byte status = PlatformHAL.PortIO.ReadByte(StatusCmdPort);
            if ((status & ATA_SR_ERR) != 0)
            {
                byte err = PlatformHAL.PortIO.ReadByte(ErrorPort);
                Console.WriteLine($"ATA WRITE error. Status: 0x{status:X2}, Err: 0x{err:X2}");
                return false;
            }

            return true;
        }

        public bool ReadSector(uint lba, Span<byte> buffer)
        {
            if (buffer.Length < 512)
                throw new ArgumentException("Buffer must be at least 512 bytes", nameof(buffer));

            if (!WaitReady())
            {
                Console.WriteLine($"ATA READ timeout: disk at port 0x{_basePort:X} not ready.");
                return false;
            }

            byte driveHead = (byte)(_drivePrefix | ((lba >> 24) & 0x0F));
            PlatformHAL.PortIO.WriteByte(DrivePort, driveHead);

            PlatformHAL.PortIO.WriteByte(SecCountPort, 1);
            PlatformHAL.PortIO.WriteByte(LbaLowPort, (byte)(lba & 0xFF));
            PlatformHAL.PortIO.WriteByte(LbaMidPort, (byte)((lba >> 8) & 0xFF));
            PlatformHAL.PortIO.WriteByte(LbaHighPort, (byte)((lba >> 16) & 0xFF));

            PlatformHAL.PortIO.WriteByte(StatusCmdPort, ATA_CMD_READ_SECTORS);
            if (!WaitDrq())
            {
                Console.WriteLine($"ATA READ error: DRQ not set (Port: 0x{_basePort:X}).");
                return false;
            }

            for (int i = 0; i < 256; i++)
            {
                ushort w = PlatformHAL.PortIO.ReadWord(DataPort);
                buffer[i * 2 + 0] = (byte)(w & 0xFF);
                buffer[i * 2 + 1] = (byte)(w >> 8);
            }

            byte status = PlatformHAL.PortIO.ReadByte(StatusCmdPort);
            if ((status & ATA_SR_ERR) != 0)
            {
                byte err = PlatformHAL.PortIO.ReadByte(ErrorPort);
                Console.WriteLine($"ATA READ error. Status: 0x{status:X2}, Err: 0x{err:X2}");
                return false;
            }

            return true;
        }
    }

    // Менеджер контроллеров ATA (позволяет переключаться между разными дисками)
    public static class AtaManager
    {
        private static AtaDrive[] _drives;

        public static void Initialize()
        {
            if (_drives != null) return;

            _drives = new AtaDrive[4];
            // 0: Primary Master
            _drives[0] = new AtaDrive(0x1F0, true);
            // 1: Primary Slave
            _drives[1] = new AtaDrive(0x1F0, false);
            // 2: Secondary Master
            _drives[2] = new AtaDrive(0x170, true);
            // 3: Secondary Slave
            _drives[3] = new AtaDrive(0x170, false);
        }

        public static AtaDrive GetDrive(int slot)
        {
            Initialize();
            if (slot >= 0 && slot < _drives.Length)
                return _drives[slot];
            return null;
        }
    }

    public static class BazFs
    {
        // v2 entry layout: 128(name) +4(lba) +4(size) +1(flags) +2(ownerGid) +1(ownerPerm)+1(otherPerm)+1(inherit)+2(res) = 144
        private const int EntrySize = 144;
        private const int DirHeaderSize = 4; // NextDirLba
        private const int EntriesPerSector = (512 - DirHeaderSize) / EntrySize; // 3
        private const int FileChainHeaderSize = 4; // next LBA pointer in chained file block
        private const int FileChainPayloadSize = 512 - FileChainHeaderSize;

        private static AtaDrive _activeDrive;
        private static bool _mounted;
        private static BazSuperblock _superblock;
        private static uint _currentDirLba;

        private static readonly uint[] _dirStack = new uint[16];
        private static int _dirStackTop;

        private static uint _nextFreeLba;
        private static int _activeDiskSlot;

        private static readonly Queue<uint> _freeLbas = new Queue<uint>();
        public static bool IsIoLocked = false;

        public static bool IsMounted => _mounted;
        public static BazSuperblock Superblock => _superblock;
        public static uint CurrentDirLba => _currentDirLba;
        public static int ActiveDiskSlot => _activeDiskSlot;

        public const byte PermR = 1 << 0;
        public const byte PermW = 1 << 1;
        public const byte PermX = 1 << 2;
        public const byte PermD = 1 << 3;

        private static byte DefaultPermOwner => (byte)(PermR | PermW | PermX | PermD);
        private static byte DefaultPermOther => PermR;

        private static bool HasPerm(BazDirEntry entry, ushort currentGroupId, byte need)
        {
            byte perm = entry.OwnerGroupId == currentGroupId ? entry.PermOwner : entry.PermOther;
            return (perm & need) == need;
        }

        private static BazDirEntry MakeNewEntry(string name, uint firstLba, uint size, byte flags, ushort ownerGroupId, bool isDir)
        {
            return new BazDirEntry
            {
                Name = name,
                FirstBlockLba = firstLba,
                Size = size,
                Flags = flags,
                OwnerGroupId = ownerGroupId,
                PermOwner = DefaultPermOwner,
                PermOther = DefaultPermOther,
                Inherit = (byte)(isDir ? 1 : 0)
            };
        }

        // Теперь слот поддерживает номера от 0 до 3 (все доступные ATA контроллеры)
        public static bool SetActiveDiskSlot(int slot)
        {
            var drive = AtaManager.GetDrive(slot);
            if (drive == null)
            {
                Console.WriteLine($"BazFs: Disk slot {slot} is not available.");
                return false;
            }

            _activeDiskSlot = slot;
            _activeDrive = drive;
            return true;
        }

        private static void WriteUInt32(Span<byte> buffer, int offset, uint value)
        {
            buffer[offset + 0] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset)
        {
            uint b0 = buffer[offset + 0];
            uint b1 = buffer[offset + 1];
            uint b2 = buffer[offset + 2];
            uint b3 = buffer[offset + 3];
            return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
        }

        private static void WriteUInt16(Span<byte> buffer, int offset, ushort value)
        {
            buffer[offset + 0] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static ushort ReadUInt16(ReadOnlySpan<byte> buffer, int offset)
        {
            ushort b0 = buffer[offset + 0];
            ushort b1 = buffer[offset + 1];
            return (ushort)(b0 | (b1 << 8));
        }

        private static uint ReadNextDirLba(ReadOnlySpan<byte> buffer) => ReadUInt32(buffer, 0);
        private static void WriteNextDirLba(Span<byte> buffer, uint lba) => WriteUInt32(buffer, 0, lba);
        private static void WriteFileNextLba(Span<byte> buffer, uint lba) => WriteUInt32(buffer, 0, lba);
        private static uint ReadFileNextLba(ReadOnlySpan<byte> buffer) => ReadUInt32(buffer, 0);

        private static uint AllocateFreeSector()
        {
            if (_freeLbas.Count > 0)
                return _freeLbas.Dequeue();

            uint lba = _nextFreeLba;
            _nextFreeLba++;
            return lba;
        }

        private static uint ComputeNextFreeLba()
        {
            if (!_mounted || _activeDrive == null)
                return 2;

            uint max = 1;
            var visitedDirs = new HashSet<uint>();

            void ScanDir(uint startLba)
            {
                if (startLba == 0) return;
                if (!visitedDirs.Add(startLba)) return;

                VisitDirChain(startLba, (sectorLba, buffer) =>
                {
                    if (sectorLba > max) max = sectorLba;
                    uint next = ReadNextDirLba(buffer);
                    if (next > max) max = next;

                    int entryBase = DirHeaderSize;
                    for (int i = 0; i < EntriesPerSector; i++)
                    {
                        int offset = entryBase + i * EntrySize;
                        if (offset + EntrySize > 512) break;

                        var e = ReadDirEntry(buffer, offset);
                        if (e == null || string.IsNullOrEmpty(e.Value.Name)) continue;

                        uint lba = e.Value.FirstBlockLba;
                        if (lba > max) max = lba;

                        if (e.Value.Flags == 1) ScanDir(lba);
                    }
                    return true;
                });
            }

            ScanDir(_superblock.RootDirLba);

            if (max < 1) max = 1;
            uint nextFree = max + 1;
            if (nextFree < 2) nextFree = 2;
            return nextFree;
        }

        public static bool Format()
        {
            if (_activeDrive == null)
            {
                // По умолчанию инициализируем Primary Master, если слот еще не выбран
                SetActiveDiskSlot(0);
            }

            Span<byte> buffer = stackalloc byte[512];
            buffer.Clear();

            WriteUInt32(buffer, 0, BazSuperblock.ExpectedMagic);
            WriteUInt32(buffer, 4, BazSuperblock.CurrentVersion);
            WriteUInt32(buffer, 8, 1);   // RootDirLba = 1
            WriteUInt32(buffer, 12, 0);  // TotalBlocks = 0

            if (!_activeDrive.WriteSector(0, buffer))
            {
                Console.WriteLine("BazFs.Format: failed to write superblock");
                return false;
            }

            buffer.Clear();
            WriteNextDirLba(buffer, 0);
            if (!_activeDrive.WriteSector(1, buffer))
            {
                Console.WriteLine("BazFs.Format: failed to clear root directory sector");
                return false;
            }

            _nextFreeLba = 2;
            Console.WriteLine("BazFs.Format: OK");
            return true;
        }

        public readonly struct BazPermInfo
        {
            public ushort OwnerGroupId { get; }
            public byte PermOwner { get; }
            public byte PermOther { get; }
            public byte Inherit { get; }

            public BazPermInfo(ushort ownerGroupId, byte permOwner, byte permOther, byte inherit)
            {
                OwnerGroupId = ownerGroupId;
                PermOwner = permOwner;
                PermOther = permOther;
                Inherit = inherit;
            }
        }

        public static bool TryGetPerm(string path, out BazPermInfo perm)
        {
            perm = default;
            if (!_mounted || _activeDrive == null) return false;
            if (string.IsNullOrWhiteSpace(path)) path = ".";

            if (!ResolvePath(path, wantDirectory: false, out var res) || res.Kind == BazPathKind.NotFound)
                return false;

            perm = new BazPermInfo(res.Entry.OwnerGroupId, res.Entry.PermOwner, res.Entry.PermOther, res.Entry.Inherit);
            return true;
        }

        public static bool TrySetPerm(string path, byte permOwner, byte permOther, byte inherit)
        {
            if (!_mounted || _activeDrive == null) return false;
            if (string.IsNullOrWhiteSpace(path)) path = ".";

            if (!ResolvePath(path, wantDirectory: false, out var res) || res.Kind == BazPathKind.NotFound)
                return false;

            if (!HasPerm(res.Entry, BAZOS.Drivers.SecurityContext.CurrentGroupId, PermW))
                return false;

            bool updated = false;
            VisitDirChain(res.DirLba, (sectorLba, buf) =>
            {
                int entryBase = DirHeaderSize;
                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int off = entryBase + i * EntrySize;
                    if (off + EntrySize > 512) break;
                    var e = ReadDirEntry(buf, off);
                    if (e == null || string.IsNullOrEmpty(e.Value.Name)) continue;
                    if (!string.Equals(e.Value.Name, res.Name, StringComparison.OrdinalIgnoreCase)) continue;

                    var ee = e.Value;
                    ee.PermOwner = permOwner;
                    ee.PermOther = permOther;
                    ee.Inherit = inherit;
                    WriteDirEntry(buf, off, ee);
                    if (!_activeDrive.WriteSector(sectorLba, buf)) return false;
                    updated = true;
                    return false;
                }
                return true;
            });
            return updated;
        }

        public static bool Mount()
        {
            if (_activeDrive == null)
            {
                if (!SetActiveDiskSlot(0))
                {
                    _mounted = false;
                    return false;
                }
            }

            Span<byte> buffer = stackalloc byte[512];

            if (!_activeDrive.ReadSector(0, buffer))
            {
                Console.WriteLine("BazFs.Mount: failed to read sector 0");
                _mounted = false;
                return false;
            }

            var magic = ReadUInt32(buffer, 0);
            var version = ReadUInt32(buffer, 4);
            var rootLba = ReadUInt32(buffer, 8);
            var total = ReadUInt32(buffer, 12);

            if (magic != BazSuperblock.ExpectedMagic)
            {
                Console.WriteLine($"BazFs.Mount: invalid magic 0x{magic:X8}, expected 0x{BazSuperblock.ExpectedMagic:X8}");
                _mounted = false;
                return false;
            }

            if (version != BazSuperblock.CurrentVersion)
            {
                Console.WriteLine($"BazFs.Mount: unsupported version {version}");
                _mounted = false;
                return false;
            }

            _superblock = new BazSuperblock { Magic = magic, Version = version, RootDirLba = rootLba, TotalBlocks = total };
            _mounted = true;
            _dirStackTop = 0;
            _dirStack[0] = rootLba;
            _currentDirLba = rootLba;
            _nextFreeLba = ComputeNextFreeLba();

            Console.WriteLine("BazFs.Mount: OK");
            Console.WriteLine($"  RootDirLba = {rootLba}");
            Console.WriteLine($"  TotalBlocks = {total}");
            return true;
        }

        private delegate bool DirSectorVisitor(uint sectorLba, Span<byte> buffer);

        private static void VisitDirChain(uint startLba, DirSectorVisitor visitor)
        {
            if (_activeDrive == null) return;
            uint currentLba = startLba;
            var visited = new HashSet<uint>();
            int guard = 0;

            while (currentLba != 0)
            {
                guard++;
                if (guard > 100000)
                {
                    Console.WriteLine("BazFs.VisitDirChain: possible cycle.");
                    return;
                }

                if (currentLba < 1 || (_superblock.TotalBlocks != 0 && currentLba >= _superblock.TotalBlocks))
                {
                    Console.WriteLine($"BazFs.VisitDirChain: invalid LBA={currentLba}");
                    return;
                }

                if (!visited.Add(currentLba)) return;

                Span<byte> buf = stackalloc byte[512];
                if (!_activeDrive.ReadSector(currentLba, buf)) return;
                if (!visitor(currentLba, buf)) return;

                currentLba = ReadNextDirLba(buf);
            }
        }

        private static BazDirEntry? ReadDirEntry(ReadOnlySpan<byte> buffer, int offset)
        {
            if (offset < 0 || offset + EntrySize > buffer.Length) return null;

            ReadOnlySpan<byte> nameBytes = buffer.Slice(offset, 128);
            int len = 0;
            while (len < 128 && nameBytes[len] != 0) len++;

            if (len == 0) return null;

            var chars = new char[len];
            for (int i = 0; i < len; i++) chars[i] = (char)nameBytes[i];

            return new BazDirEntry
            {
                Name = new string(chars),
                FirstBlockLba = ReadUInt32(buffer, offset + 128),
                Size = ReadUInt32(buffer, offset + 132),
                Flags = buffer[offset + 136],
                OwnerGroupId = ReadUInt16(buffer, offset + 137),
                PermOwner = buffer[offset + 139],
                PermOther = buffer[offset + 140],
                Inherit = buffer[offset + 141]
            };
        }

        private static char SanitizeChar(char c)
        {
            if (c < 0x20 || c == 0x7F) return '_';
            return c;
        }

        private static void WriteDirEntry(Span<byte> buffer, int offset, BazDirEntry entry)
        {
            for (int i = 0; i < EntrySize; i++) buffer[offset + i] = 0;

            var name = entry.Name ?? string.Empty;
            if (name.Length > 128) name = name.Substring(0, 128);

            for (int i = 0; i < name.Length; i++)
                buffer[offset + i] = (byte)SanitizeChar(name[i]);

            WriteUInt32(buffer, offset + 128, entry.FirstBlockLba);
            WriteUInt32(buffer, offset + 132, entry.Size);
            buffer[offset + 136] = entry.Flags;
            WriteUInt16(buffer, offset + 137, entry.OwnerGroupId);
            buffer[offset + 139] = entry.PermOwner;
            buffer[offset + 140] = entry.PermOther;
            buffer[offset + 141] = entry.Inherit;
        }

        private static bool FindFreeEntryInDir(uint startLba, out uint sectorLba, out int entryOffset)
        {
            sectorLba = 0;
            entryOffset = -1;
            uint lastLba = 0, foundSectorLba = 0;
            int foundEntryOffset = -1;

            VisitDirChain(startLba, (lba, buffer) =>
            {
                lastLba = lba;
                int entryBase = DirHeaderSize;
                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512) break;

                    var e = ReadDirEntry(buffer, offset);
                    if (e == null || string.IsNullOrEmpty(e.Value.Name))
                    {
                        foundSectorLba = lba;
                        foundEntryOffset = offset;
                        return false;
                    }
                }
                return true;
            });

            if (foundSectorLba != 0 && foundEntryOffset >= 0)
            {
                sectorLba = foundSectorLba;
                entryOffset = foundEntryOffset;
                return true;
            }

            if (lastLba == 0) return false;

            uint newLba = AllocateFreeSector();
            Span<byte> newBuf = stackalloc byte[512];
            newBuf.Clear();
            WriteNextDirLba(newBuf, 0);

            if (!_activeDrive.WriteSector(newLba, newBuf)) return false;

            Span<byte> lastBuf = stackalloc byte[512];
            if (!_activeDrive.ReadSector(lastLba, lastBuf)) return false;

            WriteNextDirLba(lastBuf, newLba);
            if (!_activeDrive.WriteSector(lastLba, lastBuf)) return false;

            sectorLba = newLba;
            entryOffset = DirHeaderSize;
            return true;
        }

        private static bool ExistsInDir(uint dirLba, string name, out BazDirEntry entry)
        {
            entry = default;
            bool found = false;
            BazDirEntry foundEntry = default;

            VisitDirChain(dirLba, (sectorLba, buffer) =>
            {
                int entryBase = DirHeaderSize;
                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512) break;

                    var e = ReadDirEntry(buffer, offset);
                    if (e == null || string.IsNullOrEmpty(e.Value.Name)) continue;

                    if (string.Equals(e.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        foundEntry = e.Value;
                        found = true;
                        return false;
                    }
                }
                return true;
            });

            if (found) entry = foundEntry;
            return found;
        }

        private static bool IsDirectoryEmpty(uint startLba)
        {
            bool any = false;
            VisitDirChain(startLba, (sector, buf) =>
            {
                int entryBase = DirHeaderSize;
                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512) break;
                    var e = ReadDirEntry(buf, offset);
                    if (e == null || string.IsNullOrEmpty(e.Value.Name)) continue;
                    any = true;
                    return false;
                }
                return true;
            });
            return !any;
        }

        private static void ZeroDirectoryChain(uint startLba)
        {
            VisitDirChain(startLba, (sectorLba, buf) =>
            {
                Span<byte> zero = stackalloc byte[512];
                zero.Clear();
                _activeDrive.WriteSector(sectorLba, zero);
                return true;
            });
        }

        public static bool FsckLite()
        {
            if (!_mounted || _activeDrive == null) return false;
            int errors = 0;
            var visitedDirs = new HashSet<uint>();

            void ScanDir(uint startLba, int depth)
            {
                if (startLba == 0) { errors++; return; }
                if (!visitedDirs.Add(startLba) || depth > 64) { errors++; return; }

                var chainVisited = new HashSet<uint>();
                VisitDirChain(startLba, (sectorLba, buf) =>
                {
                    if (!chainVisited.Add(sectorLba)) { errors++; return false; }
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    int entryBase = DirHeaderSize;
                    for (int i = 0; i < EntriesPerSector; i++)
                    {
                        int offset = entryBase + i * EntrySize;
                        if (offset + EntrySize > 512) break;

                        var e = ReadDirEntry(buf, offset);
                        if (e == null || string.IsNullOrEmpty(e.Value.Name)) continue;

                        if (e.Value.Flags != 0 && e.Value.Flags != 1) errors++;
                        if (!names.Add(e.Value.Name)) errors++;
                        if (e.Value.FirstBlockLba == 0) errors++;
                        if (e.Value.Flags == 1) ScanDir(e.Value.FirstBlockLba, depth + 1);
                    }
                    return true;
                });
            }

            if (_superblock.Magic != BazSuperblock.ExpectedMagic) errors++;
            if (_superblock.Version != BazSuperblock.CurrentVersion) errors++;
            if (_superblock.RootDirLba == 0) errors++;
            else ScanDir(_superblock.RootDirLba, 0);

            if (errors == 0) { Console.WriteLine("fsck: OK"); return true; }
            Console.WriteLine($"fsck: errors={errors}"); return false;
        }

        public static bool ExistsInCurrentDir(string name, out BazDirEntry entry) => ExistsInDir(_currentDirLba, name, out entry);
        public static bool FileExistsInCurrentDir(string name) => ExistsInCurrentDir(name, out var e) && e.Flags == 0;
        public static bool DirectoryExistsInCurrentDir(string name) => ExistsInCurrentDir(name, out var e) && e.Flags == 1;

        public static bool ResolvePath(string path, bool wantDirectory, out BazPathResult result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(path)) return false;

            path = path.Replace('\\', '/');
            bool absolute = path.StartsWith("/");
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return false;

            uint cur = absolute ? _superblock.RootDirLba : _currentDirLba;
            var stack = new uint[32];
            int top = 0; stack[top] = cur;

            for (int i = 0; i < segments.Length - 1; i++)
            {
                string seg = segments[i];
                if (seg == ".") continue;
                if (seg == "..")
                {
                    if (top > 0) { top--; cur = stack[top]; }
                    else cur = stack[0];
                    continue;
                }

                if (!FindSubdirectory(cur, seg, out var childLba))
                {
                    result.Kind = BazPathKind.NotFound; return false;
                }

                cur = childLba;
                if (top + 1 < stack.Length) { top++; stack[top] = cur; }
            }

            string last = segments[segments.Length - 1];
            if (ExistsInDir(cur, last, out var entry))
            {
                result.DirLba = cur; result.Name = last; result.Entry = entry;
                result.Kind = entry.Flags == 1 ? BazPathKind.Directory : BazPathKind.File;
            }
            else
            {
                result.DirLba = cur; result.Name = last; result.Kind = BazPathKind.NotFound;
            }
            return true;
        }

        private static void CreateFileInDir(uint dirLba, string name, ReadOnlySpan<byte> data)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (ExistsInDir(dirLba, name, out _)) return;
            if (!FindFreeEntryInDir(dirLba, out uint sectorLba, out int entryOffset)) return;

            uint fileLba = 0;
            if (data.Length <= 512)
            {
                fileLba = AllocateFreeSector();
                Span<byte> fileBuf = stackalloc byte[512]; fileBuf.Clear();
                data.CopyTo(fileBuf);
                if (!_activeDrive.WriteSector(fileLba, fileBuf)) return;
            }
            else
            {
                int remaining = data.Length, cursor = 0;
                uint prevLba = 0; bool first = true;

                while (remaining > 0)
                {
                    uint curLba = AllocateFreeSector();
                    if (first) { fileLba = curLba; first = false; }

                    Span<byte> block = stackalloc byte[512]; block.Clear();
                    int chunk = remaining > FileChainPayloadSize ? FileChainPayloadSize : remaining;
                    WriteFileNextLba(block, 0);
                    data.Slice(cursor, chunk).CopyTo(block.Slice(FileChainHeaderSize, chunk));

                    if (!_activeDrive.WriteSector(curLba, block)) return;

                    if (prevLba != 0)
                    {
                        Span<byte> prevBuf = stackalloc byte[512];
                        if (!_activeDrive.ReadSector(prevLba, prevBuf)) return;
                        WriteFileNextLba(prevBuf, curLba);
                        if (!_activeDrive.WriteSector(prevLba, prevBuf)) return;
                    }
                    prevLba = curLba; cursor += chunk; remaining -= chunk;
                }
            }

            Span<byte> dirBuf = stackalloc byte[512];
            if (!_activeDrive.ReadSector(sectorLba, dirBuf)) return;

            WriteDirEntry(dirBuf, entryOffset, MakeNewEntry(name, fileLba, (uint)data.Length, 0, BAZOS.Drivers.SecurityContext.CurrentGroupId, false));
            _activeDrive.WriteSector(sectorLba, dirBuf);
        }

        private static void FreeFileChain(uint startLba, uint size)
        {
            if (startLba == 0) return;
            if (size <= 512) { 
                _freeLbas.Enqueue(startLba); return; 
            }

            uint curr = startLba;
            var visited = new HashSet<uint>();
            while (curr != 0 && visited.Add(curr))
            {
                _freeLbas.Enqueue(curr);
                Span<byte> block = stackalloc byte[512];
                if (_activeDrive.ReadSector(curr, block)) 
                    curr = ReadFileNextLba(block);
                else break;
            }
        }

        private static void DeleteFileInDir(uint dirLba, string name)
        {
            VisitDirChain(dirLba, (sectorLba, dirBuf) =>
            {
                int entryBase = DirHeaderSize;
                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512) break;

                    var entry = ReadDirEntry(dirBuf, offset);
                    if (entry == null || string.IsNullOrEmpty(entry.Value.Name)) continue;
                    if (!string.Equals(entry.Value.Name, name, StringComparison.OrdinalIgnoreCase)) continue;

                    if (!HasPerm(entry.Value, BAZOS.Drivers.SecurityContext.CurrentGroupId, PermD)) return false;
                    if (entry.Value.Flags == 1) return false;

                    FreeFileChain(entry.Value.FirstBlockLba, entry.Value.Size);

                    for (int j = 0; j < EntrySize; j++) dirBuf[offset + j] = 0;
                    _activeDrive.WriteSector(sectorLba, dirBuf);
                    return false;
                }
                return true;
            });
        }

        public static void CreateFileWithPath(string path, ReadOnlySpan<byte> data, bool overwrite)
        {
            if (!_mounted || _activeDrive == null) return;
            if (!ResolvePath(path, wantDirectory: false, out var res)) return;

            if (res.Kind == BazPathKind.File)
            {
                if (!overwrite) return;
                DeleteFileInDir(res.DirLba, res.Name);
            }
            else if (res.Kind == BazPathKind.Directory) return;

            if (TryGetDirEntryByLba(res.DirLba, out var parentEntry) &&
                !HasPerm(parentEntry, BAZOS.Drivers.SecurityContext.CurrentGroupId, PermW | PermX))
                return;

            CreateFileInDir(res.DirLba, res.Name, data);
        }

        private static bool TryGetDirEntryByLba(uint dirLba, out BazDirEntry entry)
        {
            entry = default;
            if (dirLba == _superblock.RootDirLba) return false;

            bool found = false; BazDirEntry foundEntry = default;
            VisitDirChain(_superblock.RootDirLba, (sector, buf) =>
            {
                int entryBase = DirHeaderSize;
                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int off = entryBase + i * EntrySize;
                    if (off + EntrySize > 512) break;
                    var e = ReadDirEntry(buf, off);
                    if (e == null || string.IsNullOrEmpty(e.Value.Name)) continue;
                    if (e.Value.Flags == 1 && e.Value.FirstBlockLba == dirLba)
                    {
                        foundEntry = e.Value; found = true; return false;
                    }
                }
                return true;
            });
            if (found) entry = foundEntry;
            return found;
        }

        public static void ListRoot()
        {
            if (!_mounted || _activeDrive == null) return;
            Console.WriteLine("BAZFS root directory:");
            bool any = false;

            VisitDirChain(_superblock.RootDirLba, (sectorLba, buffer) =>
            {
                int entryBase = DirHeaderSize;
                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512) break;

                    var entry = ReadDirEntry(buffer, offset);
                    if (entry == null || string.IsNullOrEmpty(entry.Value.Name)) continue;

                    any = true;
                    string kind = entry.Value.Flags == 1 ? "<DIR>" : "FILE";
                    Console.WriteLine($"{kind} {entry.Value.Size,8} {entry.Value.Name}");
                }
                return true;
            });
            if (!any) Console.WriteLine("  [empty]");
        }

        private static string PrintableName(string name)
        {
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] < 0x20 || chars[i] == 0x7F) chars[i] = '?';
            return new string(chars);
        }

        public static void ListDirectory()
        {
            if (!_mounted || _activeDrive == null) return;
            Console.WriteLine($"BAZFS dir (start LBA={_currentDirLba}):");
            bool any = false;

            VisitDirChain(_currentDirLba, (sectorLba, buffer) =>
            {
                int entryBase = DirHeaderSize;
                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512) break;

                    var entry = ReadDirEntry(buffer, offset);
                    if (entry == null || string.IsNullOrEmpty(entry.Value.Name)) continue;

                    any = true;
                    string kind = entry.Value.Flags == 1 ? "<DIR>" : "FILE";
                    Console.WriteLine($"{kind} {entry.Value.Size,8} {PrintableName(entry.Value.Name)}");
                }
                return true;
            });
            if (!any) Console.WriteLine("  [empty]");
        }

        public static void CreateFileInCurrentDir(string name, ReadOnlySpan<byte> data) => CreateFileInDir(_currentDirLba, name, data);

        public static void ReadFileFromCurrentDir(string name)
        {
            if (!_mounted || _activeDrive == null) return;
            if (!ResolvePath(name, wantDirectory: false, out var res) || res.Kind != BazPathKind.File) return;

            if (TryReadFileAllBytes(name, out var data))
            {
                var chars = new char[data.Length];
                for (int i = 0; i < data.Length; i++) chars[i] = (char)data[i];
                Console.WriteLine(new string(chars));
            }
        }

        public static bool TryReadFileBytes(string path, out byte[] data) => TryReadFileAllBytes(path, out data);

        public static bool TryReadFileAllBytes(string path, out byte[] data)
        {
            data = Array.Empty<byte>();
            if (!_mounted || _activeDrive == null) return false;
            if (!ResolvePath(path, wantDirectory: false, out var res) || res.Kind != BazPathKind.File) return false;
            if (!HasPerm(res.Entry, BAZOS.Drivers.SecurityContext.CurrentGroupId, PermR)) return false;

            var entry = res.Entry;
            int total = (int)entry.Size;
            if (total == 0) return true;

            data = new byte[total];
            if (entry.Size <= 512)
            {
                Span<byte> fileBuf = stackalloc byte[512];
                if (!_activeDrive.ReadSector(entry.FirstBlockLba, fileBuf)) return false;
                for (int i = 0; i < total; i++) data[i] = fileBuf[i];
                return true;
            }

            uint lba = entry.FirstBlockLba;
            int cursor = 0, guard = 0;
            var visited = new HashSet<uint>();

            while (lba != 0 && cursor < total && guard < 100000)
            {
                guard++;
                if (!visited.Add(lba)) return false;

                Span<byte> block = stackalloc byte[512];
                if (!_activeDrive.ReadSector(lba, block)) return false;

                uint next = ReadFileNextLba(block);
                int chunk = total - cursor;
                if (chunk > FileChainPayloadSize) chunk = FileChainPayloadSize;

                for (int i = 0; i < chunk; i++) data[cursor + i] = block[FileChainHeaderSize + i];
                cursor += chunk; lba = next;
            }
            return cursor == total;
        }

        public static void WriteFileAllBytes(string path, ReadOnlySpan<byte> data, bool overwrite = true) => CreateFileWithPath(path, data, overwrite);

        public static bool TryReadTextFile(string path, out string text)
        {
            text = string.Empty;
            if (!TryReadFileAllBytes(path, out var bytes)) return false;
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            text = new string(chars);
            return true;
        }

        public static void WriteTextFile(string path, string text, bool overwrite = true) => WriteFileAllBytes(path, System.Text.Encoding.ASCII.GetBytes(text ?? ""), overwrite);

        public static bool TryListDirectory(string path, out BazDirEntry[] entries)
        {
            entries = Array.Empty<BazDirEntry>();
            if (!_mounted || _activeDrive == null || string.IsNullOrWhiteSpace(path)) return false;

            uint dirLba;
            path = path.Replace('\\', '/');
            if (path == "/") dirLba = _superblock.RootDirLba;
            else
            {
                if (!ResolvePath(path, wantDirectory: true, out var res) || res.Kind != BazPathKind.Directory) return false;
                dirLba = res.Entry.FirstBlockLba;
            }

            var list = new System.Collections.Generic.List<BazDirEntry>();
            VisitDirChain(dirLba, (sector, buf) =>
            {
                int entryBase = DirHeaderSize;
                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512) break;
                    var e = ReadDirEntry(buf, offset);
                    if (e != null && !string.IsNullOrEmpty(e.Value.Name)) list.Add(e.Value);
                }
                return true;
            });
            entries = list.ToArray();
            return true;
        }

        public static void CreateDirectory(string path)
        {
            if (!_mounted || _activeDrive == null) return;
            path = (path ?? "").Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(path)) return;

            bool absolute = path.StartsWith("/");
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            uint curDir = absolute ? _superblock.RootDirLba : _currentDirLba;

            foreach (var rawSeg in segments)
            {
                string seg = rawSeg.Trim();
                if (seg.Length == 0 || seg == ".") continue;

                if (seg == "..")
                {
                    if (!absolute && _dirStackTop > 0) curDir = _dirStack[--_dirStackTop];
                    else curDir = _superblock.RootDirLba;
                    continue;
                }

                if (FindSubdirectory(curDir, seg, out var childLba)) { curDir = childLba; continue; }
                if (!FindFreeEntryInDir(curDir, out uint sectorLba, out int entryOffset)) return;

                uint newDirLba = AllocateFreeSector();
                Span<byte> newDirBuf = stackalloc byte[512]; newDirBuf.Clear();
                WriteNextDirLba(newDirBuf, 0);
                if (!_activeDrive.WriteSector(newDirLba, newDirBuf)) return;

                Span<byte> dirBuf = stackalloc byte[512];
                if (!_activeDrive.ReadSector(sectorLba, dirBuf)) return;

                WriteDirEntry(dirBuf, entryOffset, MakeNewEntry(seg, newDirLba, 0, 1, BAZOS.Drivers.SecurityContext.CurrentGroupId, true));
                _activeDrive.WriteSector(sectorLba, dirBuf);
                curDir = newDirLba;
            }
        }

        private static bool FindSubdirectory(uint dirLba, string name, out uint childLba)
        {
            childLba = 0;
            bool found = false; uint foundChildLba = 0;

            VisitDirChain(dirLba, (sector, buf) =>
            {
                int entryBase = DirHeaderSize;
                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512) break;
                    var entry = ReadDirEntry(buf, offset);
                    if (entry == null || string.IsNullOrEmpty(entry.Value.Name) || entry.Value.Flags != 1) continue;

                    if (string.Equals(entry.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        foundChildLba = entry.Value.FirstBlockLba; found = true; return false;
                    }
                }
                return true;
            });
            if (found) childLba = foundChildLba;
            return found;
        }

        public static void RemoveDirectory(string name, bool force = false)
        {
            if (!_mounted || _activeDrive == null || string.IsNullOrWhiteSpace(name)) return;

            VisitDirChain(_currentDirLba, (sectorLba, dirBuf) =>
            {
                int entryBase = DirHeaderSize;
                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512) break;
                    var e = ReadDirEntry(dirBuf, offset);
                    if (e == null || string.IsNullOrEmpty(e.Value.Name) || !string.Equals(e.Value.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) continue;

                    if (!HasPerm(e.Value, BAZOS.Drivers.SecurityContext.CurrentGroupId, PermD) || e.Value.Flags != 1) return false;
                    if (!force && !IsDirectoryEmpty(e.Value.FirstBlockLba)) return false;

                    for (int j = 0; j < EntrySize; j++) dirBuf[offset + j] = 0;
                    _activeDrive.WriteSector(sectorLba, dirBuf);
                    if (force) ZeroDirectoryChain(e.Value.FirstBlockLba);
                    return false;
                }
                return true;
            });
        }

        public static void DeleteFile(string name)
        {
            if (!_mounted || _activeDrive == null || !ResolvePath(name, wantDirectory: false, out var res) || res.Kind != BazPathKind.File) return;
            DeleteFileInDir(res.DirLba, res.Name);
        }

        public static void CopyFile(string sourceName, string destName, bool overwrite)
        {
            if (!_mounted || _activeDrive == null || string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(destName)) return;
            if (string.Equals(sourceName, destName, StringComparison.OrdinalIgnoreCase)) return;

            if (!ResolvePath(sourceName, wantDirectory: false, out var srcRes) || srcRes.Kind != BazPathKind.File) return;
            if (!ResolvePath(destName, wantDirectory: false, out var dstRes)) return;

            uint targetDirLba; string targetName;
            if (dstRes.Kind == BazPathKind.Directory)
            {
                targetDirLba = dstRes.Entry.FirstBlockLba; targetName = srcRes.Name;
            }
            else if (dstRes.Kind == BazPathKind.File)
            {
                if (!overwrite) return;
                targetDirLba = dstRes.DirLba; targetName = dstRes.Name;
                DeleteFileInDir(targetDirLba, targetName);
            }
            else { targetDirLba = dstRes.DirLba; targetName = dstRes.Name; }

            Span<byte> fileBuf = stackalloc byte[512];
            if (!_activeDrive.ReadSector(srcRes.Entry.FirstBlockLba, fileBuf)) return;
            CreateFileInDir(targetDirLba, targetName, fileBuf.Slice(0, (int)srcRes.Entry.Size));
        }

        public static void ChangeDirectory(string path)
        {
            if (!_mounted || _activeDrive == null || string.IsNullOrWhiteSpace(path)) return;
            path = path.Replace('\\', '/');

            if (path == "/") { _dirStackTop = 0; _dirStack[0] = _superblock.RootDirLba; _currentDirLba = _superblock.RootDirLba; return; }
            if (path == "." || path == "./") return;

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            uint cur = _currentDirLba; int top = _dirStackTop;

            foreach (var rawSeg in segments)
            {
                var seg = rawSeg.Trim();
                if (seg.Length == 0 || seg == ".") continue;
                if (seg == "..") { if (top > 0) cur = _dirStack[--top]; continue; }

                if (!FindSubdirectory(cur, seg, out var childLba)) return;
                if (top + 1 >= _dirStack.Length) return;

                _dirStack[++top] = childLba; cur = childLba;
            }
            _dirStackTop = top; _currentDirLba = cur;
        }
    }

    public static class Fat32
    {
        private static int _activeSlot = -1;
        private static uint _partitionStartLba = 0;

        // Переменные BPB (Bios Parameter Block)
        private static ushort _bytesPerSector;
        private static byte _sectorsPerCluster;
        private static ushort _reservedSectors;
        private static byte _fatCount;
        private static uint _sectorsPerFat;
        private static uint _rootCluster;

        private static uint _fatStartLba;
        private static uint _dataStartLba;

        public static bool IsMounted { get; private set; } = false;

        public static bool Mount(int slot)
        {
            var drive = AtaManager.GetDrive(slot);
            if (drive == null) return false;

            byte[] sector = new byte[512];

            // 1. Читаем MBR (сектор 0) чтобы найти раздел
            if (!drive.ReadSector(0, sector)) return false;

            // Проверяем сигнатуру MBR
            if (sector[510] != 0x55 || sector[511] != 0xAA) return false;

            // Читаем первую запись таблицы разделов (смещение 0x1BE)
            byte partitionType = sector[0x1BE + 4];
            if (partitionType != 0x0B && partitionType != 0x0C)
            {
                // Это не FAT32 раздел (0x0B или 0x0C)
                return false;
            }

            // Достаем LBA начала раздела
            _partitionStartLba = BitConverter.ToUInt32(sector, 0x1BE + 8);

            // 2. Читаем Boot Sector (BPB) самого FAT32
            if (!drive.ReadSector(_partitionStartLba, sector)) return false;

            _bytesPerSector = BitConverter.ToUInt16(sector, 11);
            _sectorsPerCluster = sector[13];
            _reservedSectors = BitConverter.ToUInt16(sector, 14);
            _fatCount = sector[16];
            _sectorsPerFat = BitConverter.ToUInt32(sector, 36);
            _rootCluster = BitConverter.ToUInt32(sector, 44);

            if (_bytesPerSector != 512 || _sectorsPerCluster == 0) return false;

            _fatStartLba = _partitionStartLba + _reservedSectors;
            _dataStartLba = _fatStartLba + (_fatCount * _sectorsPerFat);

            _activeSlot = slot;
            IsMounted = true;
            return true;
        }

        // Конвертация номера кластера в номер сектора (LBA) на диске
        private static uint ClusterToLba(uint cluster)
        {
            if (cluster >= 2)
            {
                return _dataStartLba + ((cluster - 2) * _sectorsPerCluster);
            }
            return 0;
        }

        // Чтение следующего кластера по таблице FAT
        private static uint ReadFatEntry(uint cluster)
        {
            var drive = AtaManager.GetDrive(_activeSlot);
            uint fatOffset = cluster * 4;
            uint fatSector = _fatStartLba + (fatOffset / 512);
            uint entryOffset = fatOffset % 512;

            byte[] sec = new byte[512];
            drive.ReadSector(fatSector, sec);
            return BitConverter.ToUInt32(sec, (int)entryOffset) & 0x0FFFFFFF;
        }

        public static List<string> ListRootDirectory()
        {
            var list = new List<string>();
            if (!IsMounted) return list;

            var drive = AtaManager.GetDrive(_activeSlot);
            uint currentCluster = _rootCluster;
            byte[] sector = new byte[512];

            while (currentCluster < 0x0FFFFFF8)
            {
                uint lba = ClusterToLba(currentCluster);
                for (uint i = 0; i < _sectorsPerCluster; i++)
                {
                    drive.ReadSector(lba + i, sector);

                    // Перебираем записи по 32 байта
                    for (int offset = 0; offset < 512; offset += 32)
                    {
                        if (sector[offset] == 0x00) return list; // Конец каталога
                        if (sector[offset] == 0xE5) continue; // Удаленный файл

                        byte attr = sector[offset + 11];
                        if (attr == 0x0F) continue; // LFN (Long File Name), пока пропускаем

                        // Читаем имя формата 8.3 (11 байт)
                        string name = Encoding.ASCII.GetString(sector, offset, 8).Trim();
                        string ext = Encoding.ASCII.GetString(sector, offset + 8, 3).Trim();

                        if (ext.Length > 0) name += "." + ext;

                        // Не добавляем метки тома
                        if ((attr & 0x08) == 0) list.Add(name);
                    }
                }
                currentCluster = ReadFatEntry(currentCluster);
            }
            return list;
        }
    }
}