using System;
using Cosmos.Kernel.HAL;

namespace BAZOS.FS
{
    public struct BazDirEntry
    {
        public string Name;       // ASCII, max 128 chars
        public uint FirstBlockLba;
        public uint Size;
        public byte Flags;        // 0 = файл, 1 = каталог
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

    public static class AtaDisk
    {
        // ATA primary channel ports
        const ushort ATA_DATA = 0x1F0;
        const ushort ATA_ERROR = 0x1F1;
        const ushort ATA_SECCOUNT = 0x1F2;
        const ushort ATA_LBA_LOW = 0x1F3;
        const ushort ATA_LBA_MID = 0x1F4;
        const ushort ATA_LBA_HIGH = 0x1F5;
        const ushort ATA_DRIVE = 0x1F6;
        const ushort ATA_COMMAND = 0x1F7;
        const ushort ATA_STATUS = 0x1F7;
        const ushort ATA_ALTSTATUS = 0x3F6;

        const byte ATA_CMD_READ_SECTORS = 0x20;
        const byte ATA_CMD_WRITE_SECTORS = 0x30;

        const byte ATA_SR_BSY = 0x80;
        const byte ATA_SR_DRDY = 0x40;
        const byte ATA_SR_DF = 0x20;
        const byte ATA_SR_DSC = 0x10;
        const byte ATA_SR_DRQ = 0x08;
        const byte ATA_SR_ERR = 0x01;

        static void WaitReady()
        {
            while ((PlatformHAL.PortIO.ReadByte(ATA_STATUS) & ATA_SR_BSY) != 0) { }
        }

        static void WaitDrq()
        {
            byte status;
            do
            {
                status = PlatformHAL.PortIO.ReadByte(ATA_STATUS);
            }
            while (((status & ATA_SR_BSY) != 0) || ((status & ATA_SR_DRQ) == 0));
        }

        public static bool WriteSector(uint lba, ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length < 512)
                throw new ArgumentException("Buffer must be at least 512 bytes", nameof(buffer));

            WaitReady();

            byte driveHead = (byte)(0xE0 | ((lba >> 24) & 0x0F));
            PlatformHAL.PortIO.WriteByte(ATA_DRIVE, driveHead);

            PlatformHAL.PortIO.WriteByte(ATA_SECCOUNT, 1);
            PlatformHAL.PortIO.WriteByte(ATA_LBA_LOW, (byte)(lba & 0xFF));
            PlatformHAL.PortIO.WriteByte(ATA_LBA_MID, (byte)((lba >> 8) & 0xFF));
            PlatformHAL.PortIO.WriteByte(ATA_LBA_HIGH, (byte)((lba >> 16) & 0xFF));

            PlatformHAL.PortIO.WriteByte(ATA_COMMAND, ATA_CMD_WRITE_SECTORS);
            WaitDrq();

            for (int i = 0; i < 256; i++)
            {
                ushort w = (ushort)(buffer[i * 2 + 0] | (buffer[i * 2 + 1] << 8));
                PlatformHAL.PortIO.WriteWord(ATA_DATA, w);
            }

            byte status = PlatformHAL.PortIO.ReadByte(ATA_STATUS);
            if ((status & ATA_SR_ERR) != 0)
            {
                byte err = PlatformHAL.PortIO.ReadByte(ATA_ERROR);
                Console.WriteLine($"ATA WRITE status: 0x{status:X2}, error: 0x{err:X2}");
                return false;
            }

            return true;
        }

        public static bool ReadSector(uint lba, Span<byte> buffer)
        {
            if (buffer.Length < 512)
                throw new ArgumentException("Buffer must be at least 512 bytes", nameof(buffer));

            WaitReady();

            byte driveHead = (byte)(0xE0 | ((lba >> 24) & 0x0F));
            PlatformHAL.PortIO.WriteByte(ATA_DRIVE, driveHead);

            PlatformHAL.PortIO.WriteByte(ATA_SECCOUNT, 1);
            PlatformHAL.PortIO.WriteByte(ATA_LBA_LOW, (byte)(lba & 0xFF));
            PlatformHAL.PortIO.WriteByte(ATA_LBA_MID, (byte)((lba >> 8) & 0xFF));
            PlatformHAL.PortIO.WriteByte(ATA_LBA_HIGH, (byte)((lba >> 16) & 0xFF));

            PlatformHAL.PortIO.WriteByte(ATA_COMMAND, ATA_CMD_READ_SECTORS);
            WaitDrq();

            for (int i = 0; i < 256; i++)
            {
                ushort w = PlatformHAL.PortIO.ReadWord(ATA_DATA);
                buffer[i * 2 + 0] = (byte)(w & 0xFF);
                buffer[i * 2 + 1] = (byte)(w >> 8);
            }

            byte status = PlatformHAL.PortIO.ReadByte(ATA_STATUS);
            if ((status & ATA_SR_ERR) != 0)
            {
                byte err = PlatformHAL.PortIO.ReadByte(ATA_ERROR);
                Console.WriteLine($"ATA status: 0x{status:X2}, error: 0x{err:X2}");
                return false;
            }

            return true;
        }
    }

    public static class BazFs
    {
        private const int EntrySize = 140;
        private const int DirHeaderSize = 4; // NextDirLba
        private const int EntriesPerSector = (512 - DirHeaderSize) / EntrySize; // 3

        private static bool _mounted;
        private static BazSuperblock _superblock;
        private static uint _currentDirLba;

        private static readonly uint[] _dirStack = new uint[16];
        private static int _dirStackTop;

        private static uint _nextFreeLba;

        public static bool IsMounted => _mounted;
        public static BazSuperblock Superblock => _superblock;
        public static uint CurrentDirLba => _currentDirLba;

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

        private static uint ReadNextDirLba(ReadOnlySpan<byte> buffer)
        {
            return ReadUInt32(buffer, 0);
        }

        private static void WriteNextDirLba(Span<byte> buffer, uint lba)
        {
            WriteUInt32(buffer, 0, lba);
        }

        private static uint AllocateFreeSector()
        {
            uint lba = _nextFreeLba;
            _nextFreeLba++;
            return lba;
        }

        private static uint ComputeNextFreeLba()
        {
            if (!_mounted)
                return 2;

            uint max = 1;
            var visitedDirs = new HashSet<uint>();

            void ScanDir(uint startLba)
            {
                if (startLba == 0)
                    return;

                if (!visitedDirs.Add(startLba))
                    return;

                VisitDirChain(startLba, (sectorLba, buffer) =>
                {
                    if (sectorLba > max)
                        max = sectorLba;

                    uint next = ReadNextDirLba(buffer);
                    if (next > max)
                        max = next;

                    int entryBase = DirHeaderSize;
                    for (int i = 0; i < EntriesPerSector; i++)
                    {
                        int offset = entryBase + i * EntrySize;
                        if (offset + EntrySize > 512)
                            break;

                        var e = ReadDirEntry(buffer, offset);
                        if (e == null || string.IsNullOrEmpty(e.Value.Name))
                            continue;

                        uint lba = e.Value.FirstBlockLba;
                        if (lba > max)
                            max = lba;

                        if (e.Value.Flags == 1)
                            ScanDir(lba);
                    }

                    return true;
                });
            }

            ScanDir(_superblock.RootDirLba);

            if (max < 1)
                max = 1;

            uint nextFree = max + 1;
            if (nextFree < 2)
                nextFree = 2;
            return nextFree;
        }

        public static bool Format()
        {
            Span<byte> buffer = stackalloc byte[512];
            buffer.Clear();

            WriteUInt32(buffer, 0, BazSuperblock.ExpectedMagic);
            WriteUInt32(buffer, 4, BazSuperblock.CurrentVersion);
            WriteUInt32(buffer, 8, 1);   // RootDirLba = 1
            WriteUInt32(buffer, 12, 0);  // TotalBlocks = 0

            if (!AtaDisk.WriteSector(0, buffer))
            {
                Console.WriteLine("BazFs.Format: failed to write superblock");
                return false;
            }

            buffer.Clear();
            WriteNextDirLba(buffer, 0); // один сектор каталога
            if (!AtaDisk.WriteSector(1, buffer))
            {
                Console.WriteLine("BazFs.Format: failed to clear root directory sector");
                return false;
            }

            _nextFreeLba = 2;

            Console.WriteLine("BazFs.Format: OK");
            return true;
        }

        public static bool Mount()
        {
            Span<byte> buffer = stackalloc byte[512];

            if (!AtaDisk.ReadSector(0, buffer))
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
                Console.WriteLine($"BazFs.Mount: unsupported version {version}, expected {BazSuperblock.CurrentVersion}");
                _mounted = false;
                return false;
            }

            _superblock = new BazSuperblock
            {
                Magic = magic,
                Version = version,
                RootDirLba = rootLba,
                TotalBlocks = total
            };

            _mounted = true;
            _dirStackTop = 0;
            _dirStack[0] = rootLba;
            _currentDirLba = rootLba;
            _nextFreeLba = 2;
            _nextFreeLba = ComputeNextFreeLba();

            Console.WriteLine("BazFs.Mount: OK");
            Console.WriteLine($"  RootDirLba = {rootLba}");
            Console.WriteLine($"  TotalBlocks = {total}");

            return true;
        }

        private delegate bool DirSectorVisitor(uint sectorLba, Span<byte> buffer);

        private static void VisitDirChain(uint startLba, DirSectorVisitor visitor)
        {
            uint currentLba = startLba;

            while (currentLba != 0)
            {
                Span<byte> buf = stackalloc byte[512];

                if (!AtaDisk.ReadSector(currentLba, buf))
                {
                    Console.WriteLine($"BazFs.VisitDirChain: failed to read dir sector LBA={currentLba}");
                    return;
                }

                if (!visitor(currentLba, buf))
                    return;

                uint next = ReadNextDirLba(buf);
                currentLba = next;
            }
        }

        private static BazDirEntry? ReadDirEntry(ReadOnlySpan<byte> buffer, int offset)
        {
            if (offset < 0 || offset + 137 > buffer.Length)
                return null;

            ReadOnlySpan<byte> nameBytes = buffer.Slice(offset, 128);
            int len = 0;
            while (len < 128 && nameBytes[len] != 0)
                len++;

            if (len == 0)
                return null; // пустая запись

            var chars = new char[len];
            for (int i = 0; i < len; i++)
                chars[i] = (char)nameBytes[i];

            string name = new string(chars);

            uint lba = ReadUInt32(buffer, offset + 128);
            uint size = ReadUInt32(buffer, offset + 132);
            byte flags = buffer[offset + 136];

            return new BazDirEntry
            {
                Name = name,
                FirstBlockLba = lba,
                Size = size,
                Flags = flags
            };
        }

        private static char SanitizeChar(char c)
        {
            if (c < 0x20 || c == 0x7F)
                return '_';
            return c;
        }

        private static void WriteDirEntry(Span<byte> buffer, int offset, BazDirEntry entry)
        {
            for (int i = 0; i < EntrySize; i++)
                buffer[offset + i] = 0;

            var name = entry.Name ?? string.Empty;
            if (name.Length > 128)
                name = name.Substring(0, 128);

            for (int i = 0; i < name.Length; i++)
                buffer[offset + i] = (byte)SanitizeChar(name[i]);

            if (name.Length < 128)
                buffer[offset + name.Length] = 0;

            WriteUInt32(buffer, offset + 128, entry.FirstBlockLba);
            WriteUInt32(buffer, offset + 132, entry.Size);
            buffer[offset + 136] = entry.Flags;
        }

        private static bool FindFreeEntryInDir(uint startLba, out uint sectorLba, out int entryOffset)
        {
            sectorLba = 0;
            entryOffset = -1;

            uint lastLba = 0;
            uint foundSectorLba = 0;
            int foundEntryOffset = -1;

            VisitDirChain(startLba, (lba, buffer) =>
            {
                lastLba = lba;

                int entryBase = DirHeaderSize;
                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512)
                        break;

                    var e = ReadDirEntry(buffer, offset);

                    // пустой слот
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

            if (lastLba == 0)
                return false;

            uint newLba = AllocateFreeSector();
            Span<byte> newBuf = stackalloc byte[512];
            newBuf.Clear();
            WriteNextDirLba(newBuf, 0);

            if (!AtaDisk.WriteSector(newLba, newBuf))
            {
                Console.WriteLine("BazFs.FindFreeEntryInDir: failed to write new dir sector.");
                return false;
            }

            Span<byte> lastBuf = stackalloc byte[512];
            if (!AtaDisk.ReadSector(lastLba, lastBuf))
            {
                Console.WriteLine("BazFs.FindFreeEntryInDir: failed to read last dir sector.");
                return false;
            }

            WriteNextDirLba(lastBuf, newLba);
            if (!AtaDisk.WriteSector(lastLba, lastBuf))
            {
                Console.WriteLine("BazFs.FindFreeEntryInDir: failed to update last dir sector.");
                return false;
            }

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
                    if (offset + EntrySize > 512)
                        break;

                    var e = ReadDirEntry(buffer, offset);
                    if (e == null || string.IsNullOrEmpty(e.Value.Name))
                        continue;

                    if (string.Equals(e.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        foundEntry = e.Value;
                        found = true;
                        return false;
                    }
                }
                return true;
            });

            if (found)
                entry = foundEntry;

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
                    if (offset + EntrySize > 512)
                        break;

                    var e = ReadDirEntry(buf, offset);
                    if (e == null || string.IsNullOrEmpty(e.Value.Name))
                        continue;

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
                AtaDisk.WriteSector(sectorLba, zero);
                return true;
            });
        }

        public static bool FsckLite()
        {
            if (!_mounted)
            {
                Console.WriteLine("fsck: FS is not mounted.");
                return false;
            }

            int errors = 0;
            var visitedDirs = new HashSet<uint>();

            void ScanDir(uint startLba, int depth)
            {
                if (startLba == 0)
                {
                    errors++;
                    Console.WriteLine("fsck: directory start LBA is 0");
                    return;
                }

                if (!visitedDirs.Add(startLba))
                    return;

                // protect from insane recursion in corrupted FS
                if (depth > 64)
                {
                    errors++;
                    Console.WriteLine("fsck: directory recursion limit hit");
                    return;
                }

                var chainVisited = new HashSet<uint>();
                VisitDirChain(startLba, (sectorLba, buf) =>
                {
                    if (!chainVisited.Add(sectorLba))
                    {
                        errors++;
                        Console.WriteLine($"fsck: dir chain cycle at LBA={sectorLba}");
                        return false;
                    }

                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int entryBase = DirHeaderSize;
                    for (int i = 0; i < EntriesPerSector; i++)
                    {
                        int offset = entryBase + i * EntrySize;
                        if (offset + EntrySize > 512)
                            break;

                        var e = ReadDirEntry(buf, offset);
                        if (e == null || string.IsNullOrEmpty(e.Value.Name))
                            continue;

                        if (e.Value.Flags != 0 && e.Value.Flags != 1)
                        {
                            errors++;
                            Console.WriteLine($"fsck: invalid flags={e.Value.Flags} for \"{e.Value.Name}\"");
                        }

                        if (!names.Add(e.Value.Name))
                        {
                            errors++;
                            Console.WriteLine($"fsck: duplicate name \"{e.Value.Name}\" in directory LBA={startLba}");
                        }

                        if (e.Value.FirstBlockLba == 0)
                        {
                            errors++;
                            Console.WriteLine($"fsck: entry \"{e.Value.Name}\" has FirstBlockLba=0");
                        }

                        if (e.Value.Flags == 1)
                            ScanDir(e.Value.FirstBlockLba, depth + 1);
                    }

                    return true;
                });
            }

            if (_superblock.Magic != BazSuperblock.ExpectedMagic)
            {
                errors++;
                Console.WriteLine($"fsck: bad magic 0x{_superblock.Magic:X8}");
            }

            if (_superblock.Version != BazSuperblock.CurrentVersion)
            {
                errors++;
                Console.WriteLine($"fsck: unsupported version {_superblock.Version}");
            }

            if (_superblock.RootDirLba == 0)
            {
                errors++;
                Console.WriteLine("fsck: RootDirLba is 0");
            }
            else
            {
                ScanDir(_superblock.RootDirLba, 0);
            }

            if (errors == 0)
            {
                Console.WriteLine("fsck: OK");
                return true;
            }

            Console.WriteLine($"fsck: errors={errors}");
            return false;
        }

        public static bool ExistsInCurrentDir(string name, out BazDirEntry entry)
        {
            return ExistsInDir(_currentDirLba, name, out entry);
        }

        public static bool FileExistsInCurrentDir(string name)
        {
            if (ExistsInCurrentDir(name, out var e))
                return e.Flags == 0;
            return false;
        }

        public static bool DirectoryExistsInCurrentDir(string name)
        {
            if (ExistsInCurrentDir(name, out var e))
                return e.Flags == 1;
            return false;
        }

        // Разбор пути до (каталог, имя, entry / not found)
        public static bool ResolvePath(string path, bool wantDirectory, out BazPathResult result)
        {
            result = default;

            if (string.IsNullOrWhiteSpace(path))
                return false;

            path = path.Replace('\\', '/');

            bool absolute = path.StartsWith("/");
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                return false;

            uint cur = absolute ? _superblock.RootDirLba : _currentDirLba;
            var stack = new uint[32];
            int top = 0;
            stack[top] = cur;

            // все кроме последнего – каталоги
            for (int i = 0; i < segments.Length - 1; i++)
            {
                string seg = segments[i];
                if (seg == ".")
                    continue;
                if (seg == "..")
                {
                    if (top > 0)
                    {
                        top--;
                        cur = stack[top];
                    }
                    else
                    {
                        // already at root/current base
                        cur = stack[0];
                    }
                    continue;
                }

                if (!FindSubdirectory(cur, seg, out var childLba))
                {
                    result.Kind = BazPathKind.NotFound;
                    return false;
                }

                cur = childLba;
                if (top + 1 < stack.Length)
                {
                    top++;
                    stack[top] = cur;
                }
            }

            string last = segments[segments.Length - 1];

            if (ExistsInDir(cur, last, out var entry))
            {
                result.DirLba = cur;
                result.Name = last;
                result.Entry = entry;
                result.Kind = entry.Flags == 1 ? BazPathKind.Directory : BazPathKind.File;
            }
            else
            {
                result.DirLba = cur;
                result.Name = last;
                result.Kind = BazPathKind.NotFound;
            }

            return true;
        }

        // Создание файла в конкретном каталоге
        private static void CreateFileInDir(uint dirLba, string name, ReadOnlySpan<byte> data)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.WriteLine("BazFs.CreateFileInDir: invalid name.");
                return;
            }

            if (data.Length > 512)
            {
                Console.WriteLine("BazFs.CreateFileInDir: data too large (max 512 bytes).");
                return;
            }

            if (ExistsInDir(dirLba, name, out _))
            {
                Console.WriteLine($"BazFs.CreateFileInDir: \"{name}\" already exists.");
                return;
            }

            if (!FindFreeEntryInDir(dirLba, out uint sectorLba, out int entryOffset))
            {
                Console.WriteLine("BazFs.CreateFileInDir: no space in directory.");
                return;
            }

            uint fileLba = AllocateFreeSector();

            Span<byte> fileBuf = stackalloc byte[512];
            fileBuf.Clear();
            data.CopyTo(fileBuf);

            if (!AtaDisk.WriteSector(fileLba, fileBuf))
            {
                Console.WriteLine("BazFs.CreateFileInDir: failed to write file data.");
                return;
            }

            Span<byte> dirBuf = stackalloc byte[512];
            if (!AtaDisk.ReadSector(sectorLba, dirBuf))
            {
                Console.WriteLine("BazFs.CreateFileInDir: failed to read dir sector.");
                return;
            }

            WriteDirEntry(dirBuf, entryOffset, new BazDirEntry
            {
                Name = name,
                FirstBlockLba = fileLba,
                Size = (uint)data.Length,
                Flags = 0
            });

            if (!AtaDisk.WriteSector(sectorLba, dirBuf))
            {
                Console.WriteLine("BazFs.CreateFileInDir: failed to update dir sector.");
                return;
            }

            Console.WriteLine($"BazFs.CreateFileInDir: created file \"{name}\" at LBA {fileLba}, size {data.Length}.");
        }

        // Удаление файла в конкретном каталоге
        private static void DeleteFileInDir(uint dirLba, string name)
        {
            bool deleted = false;

            VisitDirChain(dirLba, (sectorLba, dirBuf) =>
            {
                int entryBase = DirHeaderSize;

                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512)
                        break;

                    var entry = ReadDirEntry(dirBuf, offset);
                    if (entry == null || string.IsNullOrEmpty(entry.Value.Name))
                        continue;

                    if (!string.Equals(entry.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (entry.Value.Flags == 1)
                    {
                        Console.WriteLine("BazFs.DeleteFileInDir: is a directory.");
                        deleted = false;
                        return false;
                    }

                    for (int j = 0; j < EntrySize; j++)
                        dirBuf[offset + j] = 0;

                    if (!AtaDisk.WriteSector(sectorLba, dirBuf))
                    {
                        Console.WriteLine("BazFs.DeleteFileInDir: failed to update dir sector.");
                        deleted = false;
                        return false;
                    }

                    Console.WriteLine($"BazFs.DeleteFileInDir: \"{name}\" deleted.");
                    deleted = true;
                    return false;
                }

                return true;
            });

            if (!deleted)
                Console.WriteLine($"BazFs.DeleteFileInDir: file \"{name}\" not found.");
        }

        // Путь + overwrite
        public static void CreateFileWithPath(string path, ReadOnlySpan<byte> data, bool overwrite)
        {
            if (!_mounted)
            {
                Console.WriteLine("BazFs.CreateFileWithPath: FS is not mounted.");
                return;
            }

            if (!ResolvePath(path, wantDirectory: false, out var res))
            {
                Console.WriteLine("BazFs.CreateFileWithPath: invalid path.");
                return;
            }

            if (res.Kind == BazPathKind.File)
            {
                if (!overwrite)
                {
                    Console.WriteLine($"BazFs.CreateFileWithPath: \"{path}\" already exists.");
                    return;
                }

                DeleteFileInDir(res.DirLba, res.Name);
            }
            else if (res.Kind == BazPathKind.Directory)
            {
                Console.WriteLine("BazFs.CreateFileWithPath: path points to directory.");
                return;
            }

            CreateFileInDir(res.DirLba, res.Name, data);
        }

        // Остальное – твой исходный интерфейс на текущий каталог

        public static void ListRoot()
        {
            if (!_mounted)
            {
                Console.WriteLine("BazFs.ListRoot: FS is not mounted.");
                return;
            }

            Console.WriteLine("BAZFS root directory:");
            bool any = false;

            VisitDirChain(_superblock.RootDirLba, (sectorLba, buffer) =>
            {
                int entryBase = DirHeaderSize;

                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512)
                        break;

                    var entry = ReadDirEntry(buffer, offset);
                    if (entry == null || string.IsNullOrEmpty(entry.Value.Name))
                        continue;

                    any = true;
                    string kind = entry.Value.Flags == 1 ? "<DIR>" : "FILE";
                    Console.WriteLine($"{kind} {entry.Value.Size,8} {entry.Value.Name}");
                }

                return true;
            });

            if (!any)
                Console.WriteLine("  [empty]");
        }

        private static string PrintableName(string name)
        {
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] < 0x20 || chars[i] == 0x7F)
                    chars[i] = '?';
            }
            return new string(chars);
        }

        public static void ListDirectory()
        {
            if (!_mounted)
            {
                Console.WriteLine("BazFs.ListDirectory: FS is not mounted.");
                return;
            }

            Console.WriteLine($"BAZFS dir (start LBA={_currentDirLba}):");
            bool any = false;

            VisitDirChain(_currentDirLba, (sectorLba, buffer) =>
            {
                int entryBase = DirHeaderSize;

                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512)
                        break;

                    var entry = ReadDirEntry(buffer, offset);
                    if (entry == null || string.IsNullOrEmpty(entry.Value.Name))
                        continue;

                    any = true;
                    string kind = entry.Value.Flags == 1 ? "<DIR>" : "FILE";
                    string displayName = PrintableName(entry.Value.Name);
                    Console.WriteLine($"{kind} {entry.Value.Size,8} {displayName}");
                }

                return true;
            });

            if (!any)
                Console.WriteLine("  [empty]");
        }

        public static void CreateFileInCurrentDir(string name, ReadOnlySpan<byte> data)
        {
            CreateFileInDir(_currentDirLba, name, data);
        }

        public static void ReadFileFromCurrentDir(string name)
        {
            if (!_mounted)
            {
                Console.WriteLine("BazFs.ReadFileFromCurrentDir: FS is not mounted.");
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                Console.WriteLine("BazFs.ReadFileFromCurrentDir: invalid name.");
                return;
            }

            if (!ResolvePath(name, wantDirectory: false, out var res))
            {
                Console.WriteLine("BazFs.ReadFileFromCurrentDir: invalid path.");
                return;
            }

            if (res.Kind == BazPathKind.NotFound)
            {
                Console.WriteLine($"BazFs.ReadFileFromCurrentDir: file \"{name}\" not found.");
                return;
            }

            if (res.Kind == BazPathKind.Directory)
            {
                Console.WriteLine("BazFs.ReadFileFromCurrentDir: is a directory.");
                return;
            }

            var entry = res.Entry;

            Span<byte> fileBuf = stackalloc byte[512];
            if (!AtaDisk.ReadSector(entry.FirstBlockLba, fileBuf))
            {
                Console.WriteLine($"BazFs.ReadFileFromCurrentDir: failed to read data sector {entry.FirstBlockLba}.");
                return;
            }

            int len = (int)Math.Min((uint)fileBuf.Length, entry.Size);
            var content = new char[len];
            for (int i = 0; i < len; i++)
                content[i] = (char)fileBuf[i];

            Console.WriteLine(new string(content));
        }

        public static bool TryReadFileBytes(string path, out byte[] data)
        {
            data = Array.Empty<byte>();

            if (!_mounted)
            {
                Console.WriteLine("BazFs.TryReadFileBytes: FS is not mounted.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                Console.WriteLine("BazFs.TryReadFileBytes: invalid path.");
                return false;
            }

            if (!ResolvePath(path, wantDirectory: false, out var res))
            {
                Console.WriteLine("BazFs.TryReadFileBytes: invalid path.");
                return false;
            }

            if (res.Kind != BazPathKind.File)
                return false;

            var entry = res.Entry;

            Span<byte> fileBuf = stackalloc byte[512];
            if (!AtaDisk.ReadSector(entry.FirstBlockLba, fileBuf))
            {
                Console.WriteLine("BazFs.TryReadFileBytes: failed to read file sector.");
                return false;
            }

            int len = (int)Math.Min((uint)512, entry.Size);
            data = new byte[len];
            for (int i = 0; i < len; i++)
                data[i] = fileBuf[i];

            return true;
        }

        public static bool TryListDirectory(string path, out BazDirEntry[] entries)
        {
            entries = Array.Empty<BazDirEntry>();

            if (!_mounted)
                return false;

            if (string.IsNullOrWhiteSpace(path))
                return false;

            uint dirLba;

            path = path.Replace('\\', '/');
            if (path == "/")
            {
                dirLba = _superblock.RootDirLba;
            }
            else
            {
                if (!ResolvePath(path, wantDirectory: true, out var res))
                    return false;

                if (res.Kind != BazPathKind.Directory)
                    return false;

                dirLba = res.Entry.FirstBlockLba;
            }

            var list = new System.Collections.Generic.List<BazDirEntry>();
            VisitDirChain(dirLba, (sector, buf) =>
            {
                int entryBase = DirHeaderSize;
                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512)
                        break;

                    var e = ReadDirEntry(buf, offset);
                    if (e == null || string.IsNullOrEmpty(e.Value.Name))
                        continue;

                    list.Add(e.Value);
                }
                return true;
            });

            entries = list.ToArray();
            return true;
        }

        public static void CreateDirectory(string path)
        {
            if (!_mounted)
            {
                Console.WriteLine("BazFs.CreateDirectory: FS is not mounted.");
                return;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                Console.WriteLine("BazFs.CreateDirectory: invalid path.");
                return;
            }

            path = path.Replace('\\', '/');

            bool absolute = path.StartsWith("/");
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                Console.WriteLine("BazFs.CreateDirectory: invalid path.");
                return;
            }

            uint curDir = absolute ? _superblock.RootDirLba : _currentDirLba;
            foreach (var rawSeg in segments)
            {
                string seg = rawSeg.Trim();
                if (seg.Length == 0)
                    continue;

                if (seg == ".")
                    continue;

                if (seg == "..")
                {
                    // best-effort: allow going up only when starting from current dir stack
                    if (!absolute && _dirStackTop > 0)
                    {
                        curDir = _dirStack[0];
                        int top = _dirStackTop;
                        if (top > 0)
                        {
                            top--;
                            curDir = _dirStack[top];
                        }
                    }
                    else
                    {
                        curDir = _superblock.RootDirLba;
                    }
                    continue;
                }

                // есть ли уже такой подкаталог
                if (FindSubdirectory(curDir, seg, out var childLba))
                {
                    curDir = childLba;
                    continue;
                }

                // нет — создаём новый каталог seg внутри curDir
                if (!FindFreeEntryInDir(curDir, out uint sectorLba, out int entryOffset))
                {
                    Console.WriteLine("BazFs.CreateDirectory: no space in directory.");
                    return;
                }

                uint newDirLba = AllocateFreeSector();

                Span<byte> newDirBuf = stackalloc byte[512];
                newDirBuf.Clear();
                WriteNextDirLba(newDirBuf, 0);

                if (!AtaDisk.WriteSector(newDirLba, newDirBuf))
                {
                    Console.WriteLine("BazFs.CreateDirectory: failed to write new dir sector.");
                    return;
                }

                Span<byte> dirBuf = stackalloc byte[512];
                if (!AtaDisk.ReadSector(sectorLba, dirBuf))
                {
                    Console.WriteLine("BazFs.CreateDirectory: failed to read parent dir sector.");
                    return;
                }

                WriteDirEntry(dirBuf, entryOffset, new BazDirEntry
                {
                    Name = seg,
                    FirstBlockLba = newDirLba,
                    Size = 0,
                    Flags = 1
                });

                if (!AtaDisk.WriteSector(sectorLba, dirBuf))
                {
                    Console.WriteLine("BazFs.CreateDirectory: failed to update parent dir sector.");
                    return;
                }

                curDir = newDirLba;
            }

            Console.WriteLine($"BazFs.CreateDirectory: path \"{path}\" created/ensured.");
        }

        private static bool FindSubdirectory(uint dirLba, string name, out uint childLba)
        {
            childLba = 0;

            bool found = false;
            uint foundChildLba = 0;

            VisitDirChain(dirLba, (sector, buf) =>
            {
                int entryBase = DirHeaderSize;

                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512)
                        break;

                    var entry = ReadDirEntry(buf, offset);
                    if (entry == null || string.IsNullOrEmpty(entry.Value.Name))
                        continue;

                    if (entry.Value.Flags != 1)
                        continue;

                    if (string.Equals(entry.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        foundChildLba = entry.Value.FirstBlockLba;
                        found = true;
                        return false;
                    }
                }

                return true;
            });

            if (found)
                childLba = foundChildLba;

            return found;
        }

        public static void RemoveDirectory(string name, bool force = false)
        {
            if (!_mounted)
            {
                Console.WriteLine("BazFs.RemoveDirectory: FS is not mounted.");
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                Console.WriteLine("BazFs.RemoveDirectory: invalid name.");
                return;
            }
            name = name.Trim();

            bool found = false;
            bool removed = false;

            VisitDirChain(_currentDirLba, (sectorLba, dirBuf) =>
            {
                int entryBase = DirHeaderSize;

                for (int i = 0; i < EntriesPerSector; i++)
                {
                    int offset = entryBase + i * EntrySize;
                    if (offset + EntrySize > 512)
                        break;

                    var e = ReadDirEntry(dirBuf, offset);
                    if (e == null || string.IsNullOrEmpty(e.Value.Name))
                        continue;

                    if (!string.Equals(e.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    found = true;

                    if (e.Value.Flags != 1)
                    {
                        Console.WriteLine("BazFs.RemoveDirectory: not a directory.");
                        removed = false;
                        return false;
                    }

                    if (!force && !IsDirectoryEmpty(e.Value.FirstBlockLba))
                    {
                        Console.WriteLine("BazFs.RemoveDirectory: directory not empty (use rmdir /f to discard).");
                        removed = false;
                        return false;
                    }

                    for (int j = 0; j < EntrySize; j++)
                        dirBuf[offset + j] = 0;

                    if (!AtaDisk.WriteSector(sectorLba, dirBuf))
                    {
                        Console.WriteLine("BazFs.RemoveDirectory: failed to update dir sector.");
                        removed = false;
                        return false;
                    }

                    if (force)
                        ZeroDirectoryChain(e.Value.FirstBlockLba);

                    Console.WriteLine(force
                        ? $"BazFs.RemoveDirectory: \"{name}\" removed (contents discarded)."
                        : $"BazFs.RemoveDirectory: \"{name}\" removed.");
                    removed = true;
                    return false;
                }

                return true;
            });

            if (!removed)
            {
                if (!found)
                    Console.WriteLine($"BazFs.RemoveDirectory: directory \"{name}\" not found.");
            }
        }

        public static void DeleteFile(string name)
        {
            if (!_mounted)
            {
                Console.WriteLine("BazFs.DeleteFile: FS is not mounted.");
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                Console.WriteLine("BazFs.DeleteFile: invalid name.");
                return;
            }

            if (!ResolvePath(name, wantDirectory: false, out var res))
            {
                Console.WriteLine("BazFs.DeleteFile: invalid path.");
                return;
            }

            if (res.Kind == BazPathKind.NotFound)
            {
                Console.WriteLine($"BazFs.DeleteFile: file \"{name}\" not found.");
                return;
            }

            if (res.Kind == BazPathKind.Directory)
            {
                Console.WriteLine("BazFs.DeleteFile: is a directory (use rmdir).");
                return;
            }

            DeleteFileInDir(res.DirLba, res.Name);
        }

        public static void CopyFile(string sourceName, string destName, bool overwrite)
        {
            if (!_mounted)
            {
                Console.WriteLine("BazFs.CopyFile: FS is not mounted.");
                return;
            }

            if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(destName))
            {
                Console.WriteLine("BazFs.CopyFile: usage CopyFile(source, dest, overwrite).");
                return;
            }

            if (string.Equals(sourceName, destName, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("BazFs.CopyFile: source and destination are the same.");
                return;
            }

            // источник
            if (!ResolvePath(sourceName, wantDirectory: false, out var srcRes))
            {
                Console.WriteLine("BazFs.CopyFile: invalid source path.");
                return;
            }

            if (srcRes.Kind != BazPathKind.File)
            {
                Console.WriteLine($"BazFs.CopyFile: source \"{sourceName}\" is not a file.");
                return;
            }

            // разбираем dest
            if (!ResolvePath(destName, wantDirectory: false, out var dstRes))
            {
                Console.WriteLine("BazFs.CopyFile: invalid destination path.");
                return;
            }

            uint targetDirLba;
            string targetName;

            if (dstRes.Kind == BazPathKind.Directory)
            {
                // copy a folder/  -> folder/a
                targetDirLba = dstRes.Entry.FirstBlockLba;
                string srcBase = srcRes.Name;
                targetName = srcBase;
            }
            else if (dstRes.Kind == BazPathKind.File)
            {
                if (!overwrite)
                {
                    Console.WriteLine($"BazFs.CopyFile: destination \"{destName}\" already exists.");
                    return;
                }

                targetDirLba = dstRes.DirLba;
                targetName = dstRes.Name;

                DeleteFileInDir(targetDirLba, targetName);
            }
            else // NotFound
            {
                targetDirLba = dstRes.DirLba;
                targetName = dstRes.Name;
            }

            var src = srcRes.Entry;

            Span<byte> fileBuf = stackalloc byte[512];
            if (!AtaDisk.ReadSector(src.FirstBlockLba, fileBuf))
            {
                Console.WriteLine("BazFs.CopyFile: failed to read source data sector.");
                return;
            }

            var data = fileBuf.Slice(0, (int)src.Size);
            CreateFileInDir(targetDirLba, targetName, data);
        }

        public static void ChangeDirectory(string path)
        {
            if (!_mounted)
            {
                Console.WriteLine("BazFs.ChangeDirectory: FS is not mounted.");
                return;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                Console.WriteLine("BazFs.ChangeDirectory: invalid path.");
                return;
            }

            path = path.Replace('\\', '/');

            if (path == "/")
            {
                _dirStackTop = 0;
                _dirStack[0] = _superblock.RootDirLba;
                _currentDirLba = _superblock.RootDirLba;
                return;
            }

            if (path == "." || path == "./")
                return;

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                return;

            uint cur = _currentDirLba;
            int top = _dirStackTop;

            foreach (var rawSeg in segments)
            {
                var seg = rawSeg.Trim();
                if (seg.Length == 0)
                    continue;

                if (seg == ".")
                    continue;

                if (seg == "..")
                {
                    if (top > 0)
                    {
                        top--;
                        cur = _dirStack[top];
                    }
                    continue;
                }

                if (!FindSubdirectory(cur, seg, out var childLba))
                {
                    Console.WriteLine($"BazFs.ChangeDirectory: directory \"{seg}\" not found.");
                    return;
                }

                if (top + 1 >= _dirStack.Length)
                {
                    Console.WriteLine("BazFs.ChangeDirectory: directory stack overflow.");
                    return;
                }

                top++;
                _dirStack[top] = childLba;
                cur = childLba;
            }

            _dirStackTop = top;
            _currentDirLba = cur;
        }
    }
}