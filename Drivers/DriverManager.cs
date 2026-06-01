using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BAZOS.FS;
using BAZOS.Runtime;
using BAZOS.Core;

namespace BAZOS.Drivers
{
    public sealed class DriverManager
    {
        public const string DriversRoot = DriverPackageFormat.DriversRoot;
        public const string DriversConfigPath = "/system/config/drivers.cfg";
        public const string DriverListPath = "/system/drivers/list.txt";

        private readonly Dictionary<string, DriverPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _enabled = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DriverStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _fileCacheHandles = new(StringComparer.OrdinalIgnoreCase);
        private int _cacheHits;
        private int _cacheMisses;

        public SecurityPolicy Policy { get; private set; } = new SecurityPolicy();
        public Keyring Keyring { get; private set; } = new Keyring();

        public IEnumerable<DriverPackage> Packages => _packages.Values;
        public IEnumerable<DriverStatus> Statuses => _statuses.Values;
        public int CacheEntries => _fileCacheHandles.Count;
        public int CacheHits => _cacheHits;
        public int CacheMisses => _cacheMisses;

        public void Reload()
        {
            Policy = SecurityPolicy.Load();
            Keyring = Keyring.Load();

            _packages.Clear();
            _enabled.Clear();
            _statuses.Clear();

            LoadEnabledList();
            LoadPackagesFromFs();
        }

        public void ApplyEnabled()
        {
            // Сбрасываем клавиатуру, пока внешний драйвер сам ее не включит
            InputBus.SetKeyboardEnabled(true);

            foreach (var pkg in _packages.Values.OrderBy(p => p.DriverId, StringComparer.OrdinalIgnoreCase))
            {
                if (!_enabled.Contains(pkg.DriverId))
                {
                    SetStatus(pkg.DriverId, DriverLifecycleState.Stopped, DriverErrorReason.None, "disabled", "enabled-check");
                    continue;
                }

                SetStatus(pkg.DriverId, DriverLifecycleState.Enabled, DriverErrorReason.None, "enabled", "enabled-check", enabled: true);

                if (!string.Equals(pkg.Runtime, "vm1", StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus(pkg.DriverId, DriverLifecycleState.Failed, DriverErrorReason.InitFailed, $"unsupported runtime \"{pkg.Runtime}\"", "runtime-check", enabled: true);
                    MaybeCrashForRequired(pkg, null);
                    continue;
                }

                if (!string.Equals(pkg.PayloadFormat, "bvx-v1", StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus(pkg.DriverId, DriverLifecycleState.Failed, DriverErrorReason.InitFailed, $"unsupported payload_format \"{pkg.PayloadFormat}\"", "format-check", enabled: true);
                    MaybeCrashForRequired(pkg, null);
                    continue;
                }

                if (!CheckDependencies(pkg, out var depMsg))
                {
                    SetStatus(pkg.DriverId, DriverLifecycleState.Failed, DriverErrorReason.MissingDep, depMsg, "deps-check", enabled: true);
                    MaybeCrashForRequired(pkg, null);
                    continue;
                }

                SetStatus(pkg.DriverId, DriverLifecycleState.Verified, DriverErrorReason.None, "dependencies OK", "deps-check", enabled: true);

                if (!VerifyPackageWithReason(pkg, out var verifyReason, out var verifyMsg))
                {
                    SetStatus(pkg.DriverId, DriverLifecycleState.Failed, verifyReason, verifyMsg, "verify", enabled: true);
                    MaybeCrashForRequired(pkg, null);
                    continue;
                }

                SetStatus(pkg.DriverId, DriverLifecycleState.Verified, DriverErrorReason.None, verifyMsg, "verify", enabled: true);

                if (!VmModule.TryLoad(pkg.PayloadBytes, out var vm, out var loadErr))
                {
                    SetStatus(pkg.DriverId, DriverLifecycleState.Failed, DriverErrorReason.InitFailed, loadErr, "vm-load", enabled: true);
                    MaybeCrashForRequired(pkg, null);
                    continue;
                }

                Scheduler.StartProcess(pkg.DriverId, vm);

                SetStatus(pkg.DriverId, DriverLifecycleState.Started, DriverErrorReason.None, "scheduled as VM process", "vm-init", enabled: true);
                Console.WriteLine($"driver {pkg.DriverId}: started (VM Process)");
            }
        }

        public bool IsEnabled(string driverId) => _enabled.Contains(driverId);

        public void Enable(string driverId)
        {
            _enabled.Add(driverId);
            SetStatus(driverId, DriverLifecycleState.Enabled, DriverErrorReason.None, "enabled", "manual-enable", enabled: true);
            SaveEnabledList();
        }

        public void Disable(string driverId)
        {
            _enabled.Remove(driverId);
            SetStatus(driverId, DriverLifecycleState.Stopped, DriverErrorReason.None, "disabled", "manual-disable");
            SaveEnabledList();
        }

        public bool TryGetPackage(string driverId, out DriverPackage pkg)
            => _packages.TryGetValue(driverId, out pkg);

        public bool VerifyPackage(string driverId, out string message)
        {
            message = "";
            if (!_packages.TryGetValue(driverId, out var pkg))
            {
                message = "not found";
                return false;
            }
            return VerifyPackage(pkg, out message);
        }

        public bool VerifyPackage(DriverPackage pkg, out string message)
        {
            return VerifyPackageWithReason(pkg, out _, out message);
        }

        private bool VerifyPackageWithReason(DriverPackage pkg, out DriverErrorReason reason, out string message)
        {
            reason = DriverErrorReason.None;
            message = "";

            // --- ОБХОД ДЛЯ РАЗРАБОТЧИКА ---
            if (pkg.PubKeyId == "dev")
            {
                message = "verified (dev mode bypass)";
                return true;
            }

            bool hasSignature = pkg.SignatureBytes != null && pkg.SignatureBytes.Length > 0;

            if (!hasSignature)
            {
                message = "unsigned (blocked)";
                reason = DriverErrorReason.PolicyBlocked;
                return false;
            }

            message = "strict verification not implemented yet";
            reason = DriverErrorReason.BadSignature;
            return false;
        }

        public bool TryGetStatus(string driverId, out DriverStatus status)
            => _statuses.TryGetValue(driverId, out status);

        public void ClearCache()
        {
            foreach (var kv in _fileCacheHandles)
                MemoryManager.Free(kv.Value);
            _fileCacheHandles.Clear();
            _cacheHits = 0;
            _cacheMisses = 0;
        }

        private void LoadEnabledList()
        {
            if (!BazFs.IsMounted || !TryReadFileBytesCached(DriversConfigPath, out var bytes)) return;

            var text = Encoding.ASCII.GetString(bytes);
            var lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i]?.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                if (line.StartsWith("enable ", StringComparison.OrdinalIgnoreCase))
                {
                    var id = line.Substring("enable ".Length).Trim();
                    if (id.Length > 0) _enabled.Add(id);
                }
            }
        }

        private void SaveEnabledList()
        {
            if (!BazFs.IsMounted) return;

            BazFs.CreateDirectory("/system/config");
            var sb = new StringBuilder();
            sb.AppendLine("# enabled drivers");
            foreach (var id in _enabled.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.Append("enable ").AppendLine(id);

            BazFs.CreateFileWithPath(DriversConfigPath, Encoding.ASCII.GetBytes(sb.ToString()), overwrite: true);
        }

        private void LoadPackagesFromFs()
        {
            if (!BazFs.IsMounted) return;

            // Читаем наш list.txt
            if (!TryReadFileBytesCached(DriverListPath, out var listBytes))
            {
                Console.WriteLine("[DriverManager] list.txt not found. No drivers loaded.");
                return;
            }

            var text = Encoding.ASCII.GetString(listBytes);
            var lines = text.Replace("\r", "").Split('\n');

            foreach (var line in lines)
            {
                var l = line?.Trim();
                // Пропускаем пустые строки и комментарии
                if (string.IsNullOrEmpty(l) || l.StartsWith("#")) continue;

                int eq = l.IndexOf('=');
                if (eq <= 0) continue;

                string role = l.Substring(0, eq).Trim(); // Например: KEYBOARD
                string path = l.Substring(eq + 1).Trim(); // Например: /system/drivers/kbd.drv

                if (!TryReadFileBytesCached(path, out var drvBytes))
                {
                    Console.WriteLine($"[DriverManager] Warning: Driver not found -> {path}");
                    continue;
                }

                if (!DriverPackageFormat.Unpack(drvBytes, out var manifestBytes, out var payloadBytes))
                {
                    Console.WriteLine($"[DriverManager] Error: Failed to unpack {path} (corrupted)");
                    continue;
                }

                string rawName = path.Substring(path.LastIndexOf('/') + 1).Replace(".drv", "");
                var pkg = new DriverPackage { PackageName = rawName, PackagePath = path };

                pkg.ManifestBytes = manifestBytes;
                pkg.PayloadBytes = payloadBytes;

                foreach (var kv in DriverPackageFormat.ParseManifest(manifestBytes))
                {
                    pkg.Manifest[kv.Key] = kv.Value;
                }

                // Читаем подпись и ключ ПРЯМО ИЗ МАНИФЕСТА
                pkg.PubKeyId = pkg.GetManifest("pubkey") ?? "";
                string sigHex = pkg.GetManifest("signature") ?? "";
                if (!string.IsNullOrEmpty(sigHex))
                {
                    try
                    {
                        pkg.SignatureBytes = Enumerable.Range(0, sigHex.Length)
                             .Where(x => x % 2 == 0)
                             .Select(x => Convert.ToByte(sigHex.Substring(x, 2), 16))
                             .ToArray();
                    }
                    catch { }
                }

                _packages[pkg.DriverId] = pkg;
                SetStatus(pkg.DriverId, DriverLifecycleState.Loaded, DriverErrorReason.None, "package loaded", "load");
            }
        }

        private bool CheckDependencies(DriverPackage pkg, out string message)
        {
            message = "OK";
            foreach (string dep in pkg.Depends)
            {
                if (!_statuses.TryGetValue(dep, out var st) || st.State != DriverLifecycleState.Started)
                {
                    message = $"missing dep: {dep}";
                    return false;
                }
            }
            return true;
        }

        private void SetStatus(string id, DriverLifecycleState state, DriverErrorReason reason, string message, string phase, bool enabled = false)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            if (!_statuses.TryGetValue(id, out var st))
            {
                st = new DriverStatus { DriverId = id };
                _statuses[id] = st;
            }
            st.State = state;
            st.Reason = reason;
            st.Message = message ?? "";
            st.Phase = phase ?? "";
            st.Enabled = enabled;
            st.UpdatedAtUtc = DateTime.UtcNow;
        }

        public void MaybeCrashForRequired(DriverPackage pkg, Exception? ex)
        {
            if (pkg == null || !pkg.IsRequired) return;

            if (!_statuses.TryGetValue(pkg.DriverId, out var status))
            {
                status = new DriverStatus
                {
                    DriverId = pkg.DriverId,
                    State = DriverLifecycleState.Failed,
                    Reason = DriverErrorReason.InitFailed,
                    Message = "required driver failed without status",
                    Phase = "unknown",
                    UpdatedAtUtc = DateTime.UtcNow,
                    Enabled = true
                };
            }
            SystemCrash.ShowCritical(status, pkg, ex);
        }

        private bool TryReadFileBytesCached(string path, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (string.IsNullOrWhiteSpace(path)) return false;

            if (_fileCacheHandles.TryGetValue(path, out var handle))
            {
                if (MemoryManager.TryReadCopy(handle, out bytes)) { _cacheHits++; return true; }
                _fileCacheHandles.Remove(path);
            }

            _cacheMisses++;
            if (!BazFs.TryReadFileBytes(path, out var fromDisk)) return false;
            if (MemoryManager.TryAllocCopy(fromDisk, out var newHandle)) _fileCacheHandles[path] = newHandle;

            bytes = fromDisk;
            return true;
        }
    }
}