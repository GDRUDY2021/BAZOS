using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BAZOS.FS;
using BAZOS.Runtime;

namespace BAZOS.Drivers
{
    public sealed class DriverManager
    {
        public const string DriversRoot = DriverPackageFormat.DriversRoot;
        public const string DriversConfigPath = "/system/config/drivers.cfg";

        private readonly Dictionary<string, DriverPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _enabled = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DriverStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);

        public SecurityPolicy Policy { get; private set; } = new SecurityPolicy();
        public Keyring Keyring { get; private set; } = new Keyring();

        public IEnumerable<DriverPackage> Packages => _packages.Values;
        public IEnumerable<DriverStatus> Statuses => _statuses.Values;

        // Built-in drivers compiled into the kernel. Packages from FS can enable/disable them.
        private readonly Dictionary<string, IDriver> _builtIn = new(StringComparer.OrdinalIgnoreCase);

        public void RegisterBuiltIn(IDriver driver)
        {
            if (driver == null || string.IsNullOrWhiteSpace(driver.Id))
                return;
            _builtIn[driver.Id.Trim()] = driver;
        }

        public void Reload()
        {
            Policy = SecurityPolicy.Load();
            Keyring = Keyring.Load();

            _packages.Clear();
            _enabled.Clear();
            _statuses.Clear();

            EnsureMinimalSysDriverPackage();
            LoadEnabledList();
            LoadPackagesFromFs();
        }

        public void ApplyEnabled()
        {
            var ctx = new DriverContext(Policy.DevMode);

            // 1) Start VM packages enabled in config
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

                if (!string.Equals(pkg.EntryInit, "Init", StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus(pkg.DriverId, DriverLifecycleState.Failed, DriverErrorReason.InitFailed, $"unsupported entry_init \"{pkg.EntryInit}\"", "entry-check", enabled: true);
                    Console.WriteLine($"driver {pkg.DriverId}: unsupported entry_init \"{pkg.EntryInit}\"");
                    MaybeCrashForRequired(pkg, null);
                    continue;
                }

                if (!CheckDependencies(pkg, out var depMsg))
                {
                    SetStatus(pkg.DriverId, DriverLifecycleState.Failed, DriverErrorReason.MissingDep, depMsg, "deps-check", enabled: true);
                    Console.WriteLine($"driver {pkg.DriverId}: blocked ({depMsg})");
                    MaybeCrashForRequired(pkg, null);
                    continue;
                }

                SetStatus(pkg.DriverId, DriverLifecycleState.Verified, DriverErrorReason.None, "dependencies OK", "deps-check", enabled: true);

                if (!VerifyPackageWithReason(pkg, out var verifyReason, out var verifyMsg))
                {
                    SetStatus(pkg.DriverId, DriverLifecycleState.Failed, verifyReason, verifyMsg, "verify", enabled: true);
                    Console.WriteLine($"driver {pkg.DriverId}: blocked ({verifyMsg})");
                    MaybeCrashForRequired(pkg, null);
                    continue;
                }

                SetStatus(pkg.DriverId, DriverLifecycleState.Verified, DriverErrorReason.None, verifyMsg, "verify", enabled: true);

                if (!VmModule.TryLoad(pkg.PayloadBytes, out var vm, out var loadErr))
                {
                    SetStatus(pkg.DriverId, DriverLifecycleState.Failed, DriverErrorReason.InitFailed, loadErr, "vm-load", enabled: true);
                    Console.WriteLine($"driver {pkg.DriverId}: vm load failed: {loadErr}");
                    MaybeCrashForRequired(pkg, null);
                    continue;
                }

                if (!VmRuntime.RunInit(vm, out var runErr))
                {
                    SetStatus(pkg.DriverId, DriverLifecycleState.Failed, DriverErrorReason.InitFailed, runErr, "vm-init", enabled: true);
                    Console.WriteLine($"driver {pkg.DriverId}: init failed: {runErr}");
                    MaybeCrashForRequired(pkg, null);
                    continue;
                }

                SetStatus(pkg.DriverId, DriverLifecycleState.Started, DriverErrorReason.None, "started (vm1)", "vm-init", enabled: true);
                Console.WriteLine($"driver {pkg.DriverId}: started (vm1)");
            }

            // 2) Start built-in drivers (legacy path)
            foreach (var kv in _builtIn)
            {
                string id = kv.Key;
                var drv = kv.Value;

                if (!_enabled.Contains(id))
                {
                    SetStatus(id, DriverLifecycleState.Stopped, DriverErrorReason.None, "disabled", "enabled-check");
                    continue;
                }

                // If there is a package for this driver, require it to verify in release mode.
                if (_packages.TryGetValue(id, out var pkg))
                {
                    if (!VerifyPackageWithReason(pkg, out var verifyReason, out var msg))
                    {
                        SetStatus(id, DriverLifecycleState.Failed, verifyReason, msg, "verify", enabled: true);
                        Console.WriteLine($"driver {id}: blocked ({msg})");
                        MaybeCrashForRequired(pkg, null);
                        continue;
                    }
                }
                else
                {
                    // no package; allow in dev mode only
                    if (!Policy.DevMode)
                    {
                        SetStatus(id, DriverLifecycleState.Failed, DriverErrorReason.PolicyBlocked, "no package", "verify", enabled: true);
                        Console.WriteLine($"driver {id}: blocked (no package)");
                        continue;
                    }
                }

                SetStatus(id, DriverLifecycleState.Enabled, DriverErrorReason.None, "enabled", "enabled-check", enabled: true);
                try
                {
                    drv.Init(ctx);
                    SetStatus(id, DriverLifecycleState.Started, DriverErrorReason.None, "started", "init", enabled: true);
                    Console.WriteLine($"driver {id}: started");
                }
                catch (Exception ex)
                {
                    SetStatus(id, DriverLifecycleState.Failed, DriverErrorReason.InitFailed, ex.Message, "init", enabled: true);
                    Console.WriteLine($"driver {id}: init failed: {ex.Message}");
                    if (pkg != null)
                        MaybeCrashForRequired(pkg, ex);
                }
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
            var ok = VerifyPackageWithReason(pkg, out _, out message);
            return ok;
        }

        private bool VerifyPackageWithReason(DriverPackage pkg, out DriverErrorReason reason, out string message)
        {
            reason = DriverErrorReason.None;
            message = "";

            bool hasSignature = pkg.SignatureBytes != null && pkg.SignatureBytes.Length > 0;

            if (!hasSignature)
            {
                if (Policy.DevMode && Policy.AllowUnsigned)
                {
                    message = "unsigned (allowed in dev_mode)";
                    return true;
                }
                message = "unsigned (blocked)";
                reason = DriverErrorReason.PolicyBlocked;
                return false;
            }

            if (string.IsNullOrWhiteSpace(pkg.PubKeyId))
            {
                message = "missing pubkey.id";
                reason = DriverErrorReason.BadSignature;
                return false;
            }

            if (!Keyring.TryGet(pkg.PubKeyId.Trim(), out var pubKey))
            {
                message = "pubkey not trusted";
                reason = DriverErrorReason.BadSignature;
                return false;
            }

            if (pubKey.Length != 32)
            {
                message = "bad pubkey length";
                reason = DriverErrorReason.BadSignature;
                return false;
            }

            if (pkg.SignatureBytes.Length != 64)
            {
                message = "bad signature length";
                reason = DriverErrorReason.BadSignature;
                return false;
            }

            var hash = DriverCrypto.Sha256(pkg.ManifestBytes, pkg.PayloadBytes);
            bool ok = DriverCrypto.VerifyEd25519(pubKey, pkg.SignatureBytes, hash);
            message = ok ? "OK" : "bad signature";
            if (!ok)
                reason = DriverErrorReason.BadSignature;
            return ok;
        }

        public bool TryGetStatus(string driverId, out DriverStatus status)
            => _statuses.TryGetValue(driverId, out status);

        private void LoadEnabledList()
        {
            if (!BazFs.IsMounted)
                return;

            if (!BazFs.TryReadFileBytes(DriversConfigPath, out var bytes))
                return;

            var text = Encoding.ASCII.GetString(bytes);
            var lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i]?.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                    continue;

                if (line.StartsWith("enable ", StringComparison.OrdinalIgnoreCase))
                {
                    var id = line.Substring("enable ".Length).Trim();
                    if (id.Length > 0)
                        _enabled.Add(id);
                }
            }
        }

        private void EnsureMinimalSysDriverPackage()
        {
            if (!BazFs.IsMounted)
                return;

            // /system/drivers/sys.drv/*
            BazFs.CreateDirectory("/system");
            BazFs.CreateDirectory("/system/drivers");
            BazFs.CreateDirectory("/system/drivers/sys.drv");

            string manifest =
@"id=sys.core
type=system
version=0.1.0
runtime=vm1
entry_init=Init
";
            byte[] manifestBytes = Encoding.ASCII.GetBytes(manifest);
            if (manifestBytes.Length <= 512)
                BazFs.CreateFileWithPath("/system/drivers/sys.drv/manifest.txt", manifestBytes, overwrite: true);

            // unsigned dev payload sample
            var code = VmModuleBuilder.BuildSysDrvSampleCode();
            var payload = VmModuleBuilder.BuildSimple(code);
            if (payload.Length <= 512)
                BazFs.CreateFileWithPath("/system/drivers/sys.drv/payload.bin", payload, overwrite: true);

            // Empty key id/signature means unsigned package (allowed in dev mode when policy allows)
            BazFs.CreateFileWithPath("/system/drivers/sys.drv/pubkey.id", Array.Empty<byte>(), overwrite: true);
            BazFs.CreateFileWithPath("/system/drivers/sys.drv/signature.sig", Array.Empty<byte>(), overwrite: true);

            // ensure enabled
            if (!_enabled.Contains("sys.core"))
                _enabled.Add("sys.core");
            SaveEnabledList();
        }

        private void SaveEnabledList()
        {
            if (!BazFs.IsMounted)
                return;

            BazFs.CreateDirectory("/system/config");

            var sb = new StringBuilder();
            sb.AppendLine("# enabled drivers");
            foreach (var id in _enabled.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.Append("enable ").AppendLine(id);

            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            if (bytes.Length > 512)
            {
                Console.WriteLine("DriverManager.SaveEnabledList: drivers.cfg too large (>512 bytes).");
                return;
            }

            BazFs.CreateFileWithPath(DriversConfigPath, bytes, overwrite: true);
        }

        private void LoadPackagesFromFs()
        {
            if (!BazFs.IsMounted)
                return;

            if (!BazFs.TryListDirectory(DriversRoot, out var entries))
                return;

            for (int i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                if (e.Flags != 1)
                    continue;

                string dirName = e.Name;
                if (string.IsNullOrWhiteSpace(dirName))
                    continue;

                string pkgPath = $"{DriversRoot}/{dirName}";
                var pkg = new DriverPackage
                {
                    PackageName = dirName,
                    PackagePath = pkgPath
                };

                if (BazFs.TryReadFileBytes($"{pkgPath}/{DriverPackageFormat.ManifestName}", out var manifestBytes))
                {
                    pkg.ManifestBytes = manifestBytes;
                    var mf = DriverPackageFormat.ParseManifest(manifestBytes);
                    foreach (var kv in mf)
                        pkg.Manifest[kv.Key] = kv.Value;
                }

                if (BazFs.TryReadFileBytes($"{pkgPath}/{DriverPackageFormat.PayloadName}", out var payloadBytes))
                    pkg.PayloadBytes = payloadBytes;

                if (BazFs.TryReadFileBytes($"{pkgPath}/{DriverPackageFormat.PubKeyIdName}", out var keyIdBytes))
                    pkg.PubKeyId = Encoding.ASCII.GetString(keyIdBytes).Trim();

                if (BazFs.TryReadFileBytes($"{pkgPath}/{DriverPackageFormat.SignatureName}", out var sigBytes))
                {
                    var sigText = Encoding.ASCII.GetString(sigBytes).Trim();
                    if (Keyring.TryParseHex(sigText, out var sigRaw))
                        pkg.SignatureBytes = sigRaw;
                }

                _packages[pkg.DriverId] = pkg;
                SetStatus(pkg.DriverId, DriverLifecycleState.Loaded, DriverErrorReason.None, "package loaded", "load");
            }
        }

        private bool CheckDependencies(DriverPackage pkg, out string message)
        {
            message = "OK";
            var deps = pkg.Depends;
            if (deps.Length == 0)
                return true;

            for (int i = 0; i < deps.Length; i++)
            {
                string dep = deps[i];
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
            if (string.IsNullOrWhiteSpace(id))
                return;

            if (!_statuses.TryGetValue(id, out var st))
            {
                st = new DriverStatus
                {
                    DriverId = id
                };
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
            if (pkg == null || !pkg.IsRequired)
                return;

            if (_statuses.TryGetValue(pkg.DriverId, out var status))
            {
                SystemCrash.ShowCritical(status, pkg, ex);
            }
            else
            {
                var synthetic = new DriverStatus
                {
                    DriverId = pkg.DriverId,
                    State = DriverLifecycleState.Failed,
                    Reason = DriverErrorReason.InitFailed,
                    Message = "required driver failed without status",
                    Phase = "unknown",
                    UpdatedAtUtc = DateTime.UtcNow,
                    Enabled = true
                };
                SystemCrash.ShowCritical(synthetic, pkg, ex);
            }
        }
    }
}

