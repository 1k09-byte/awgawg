using stellarisKIT.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace stellarisKIT.Services
{
    /// <summary>
    /// Read-only device inventory for the affinity page (layout pass: no system
    /// changes). Walks HKLM\SYSTEM\CurrentControlSet\Enum\PCI — graphics, NIC,
    /// USB and audio controllers are all PCI devices — and keeps entries whose
    /// setup class is Display, Net, USB or MEDIA. IRQ/affinity policy writes
    /// land here in the tuning pass.
    /// </summary>
    public class AffinityService
    {
        // PCI covers GPU/NIC/USB controllers; HD Audio codecs enumerate under
        // HDAUDIO; USB audio gear and USB NIC dongles enumerate under USB
        // (hubs and peripherals are filtered by class below).
        private static readonly string[] EnumRoots = new[]
        {
            @"SYSTEM\CurrentControlSet\Enum\PCI",
            @"SYSTEM\CurrentControlSet\Enum\HDAUDIO",
            @"SYSTEM\CurrentControlSet\Enum\USB"
        };

        public Task<List<AffinityDeviceItem>> EnumerateDevicesAsync(CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                var result = new List<AffinityDeviceItem>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var root in EnumRoots)
                    {
                        using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root);
                        if (baseKey is null) continue;
                        foreach (var device in baseKey.GetSubKeyNames())
                        {
                            ct.ThrowIfCancellationRequested();
                            using var deviceKey = baseKey.OpenSubKey(device);
                            if (deviceKey is null) continue;
                            foreach (var instance in deviceKey.GetSubKeyNames())
                            {
                                ct.ThrowIfCancellationRequested();
                                using var key = deviceKey.OpenSubKey(instance);
                                if (key is null) continue;

                                // Skip phantom entries (hardware no longer present —
                                // stale reinstall leftovers that inflate device counts).
                                if (key.GetValue("Phantom") is int phantom && phantom == 1) continue;

                                // ClassGUID usually lives on the instance; fall back to the
                                // parent device key so real devices are never dropped.
                                string classGuid = (key.GetValue("ClassGUID") as string ?? "").Trim();
                                if (string.IsNullOrEmpty(classGuid))
                                    classGuid = (deviceKey.GetValue("ClassGUID") as string ?? "").Trim();
                            string? category = MapClass(classGuid);
                            if (category is null) continue;
                            // USB hubs/functions aren't controllers: from the USB bus
                            // only audio gear (DACs/headsets) and NIC dongles count.
                            if (root.EndsWith(@"\USB", StringComparison.OrdinalIgnoreCase) &&
                                category != "Graphics" && category != "Network" && category != "Audio")
                                continue;

                                string rawName = (key.GetValue("DeviceDesc") as string ?? "").Trim();
                                string name = FriendlyName(rawName);
                                if (string.IsNullOrEmpty(name) || IsVirtualAdapter(name)) continue;
                                // Dedupe by instance path, never by name: identical
                                // adapters (e.g. two matching USB controllers) are
                                // distinct tunable devices.
                                if (!seen.Add(device + "\\" + instance)) continue;

                                result.Add(new AffinityDeviceItem
                                {
                                    Name = name,
                                    Category = category,
                                    DeviceInstanceId = device + "\\" + instance
                                });
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"EnumerateDevices: {ex.Message}");
                }
                DisambiguateDuplicates(result);
                return result;
            }, ct);
        }

        private static string? MapClass(string classGuid)
        {
            string g = classGuid.Trim('{', '}', ' ', '\t').ToLowerInvariant();
            return g switch
            {
                "4d36e968-e325-11ce-bfc1-08002be10318" => "Graphics",
                "4d36e972-e325-11ce-bfc1-08002be10318" => "Network",
                "36fc9e60-c465-11cf-8056-444553540000" => "Usb",
                "4d36e96c-e325-11ce-bfc1-08002be10318" => "Audio",
                _ => null
            };
        }

        // Virtual/host adapters (Hyper-V, VMware, ...) and generic placeholders
        // ("Basic Display", "Remote Display") have no tunable interrupt policy.
        private static bool IsVirtualAdapter(string name)
        {
            return name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                || name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
                || name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Parallels", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Citrix", StringComparison.OrdinalIgnoreCase)
                || name.Contains("QEMU", StringComparison.OrdinalIgnoreCase)
                || name.Contains("VirtIO", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Red Hat", StringComparison.OrdinalIgnoreCase)
                || name.Contains("VMBus", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase);
        }

        // Identical adapters (e.g. four "USB xHCI Compliant Host Controller"
        // instances) are distinct tunable devices but indistinguishable by name,
        // so later duplicates get a vendor suffix ("(AMD)") or a #N fallback —
        // the same convention Device Manager uses.
        private static void DisambiguateDuplicates(List<Models.AffinityDeviceItem> devices)
        {
            foreach (var group in devices.GroupBy(d => d.Name).Where(g => g.Count() > 1))
            {
                bool first = true;
                int n = 1;
                foreach (var item in group)
                {
                    if (first) { first = false; continue; }
                    n++;
                    string vendor = VendorFromId(item.DeviceInstanceId);
                    item.Name = string.IsNullOrEmpty(vendor)
                        ? $"{item.Name} #{n}"
                        : $"{item.Name} ({vendor})";
                }
            }
        }

        private static string VendorFromId(string instanceId)
        {
            if (instanceId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
            if (instanceId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase)) return "AMD";
            if (instanceId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) return "Intel";
            if (instanceId.Contains("VEN_10EC", StringComparison.OrdinalIgnoreCase)) return "Realtek";
            if (instanceId.Contains("VEN_14F1", StringComparison.OrdinalIgnoreCase)) return "Conexant";
            return string.Empty;
        }

        /// <summary>
        /// Read-only snapshot of a device's interrupt state (same keys MSI-utility
        /// tools display). Missing keys yield nulls — the UI renders "—".
        /// Never writes; the tuning pass adds the writers.
        /// </summary>
        public sealed record InterruptInfo(
            bool? MsiSupported,
            uint? MsiLimit,
            uint? MaxMsiLimit,
            int? DevicePolicy,
            int? DevicePriority,
            ulong? AffinityMask);

        public InterruptInfo GetInterruptInfo(string deviceInstanceId)
        {
            bool? msi = null;
            uint? limit = null;
            uint? maxLimit = null;
            int? policy = null;
            int? priority = null;
            ulong? mask = null;
            try
            {
                string mgmt = $@"SYSTEM\CurrentControlSet\Enum\{deviceInstanceId}\Device Parameters\Interrupt Management";
                using var msiKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(mgmt + @"\MessageSignaledInterruptProperties");
                if (msiKey?.GetValue("MSISupported") is int ms) msi = ms != 0;
                if (msiKey?.GetValue("MessageNumberLimit") is int lim) limit = unchecked((uint)lim);
                if (msiKey?.GetValue("MaxMessageNumberLimit") is int mlim) maxLimit = unchecked((uint)mlim);
                using var affKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(mgmt + @"\Affinity Policy");
                if (affKey?.GetValue("DevicePolicy") is int dp) policy = dp;
                if (affKey?.GetValue("DevicePriority") is int dpr) priority = dpr;
                if (affKey?.GetValue("AssignmentSetOverride") is byte[] bytes && bytes.Length > 0)
                {
                    byte[] eight = new byte[8];
                    Buffer.BlockCopy(bytes, 0, eight, 0, Math.Min(bytes.Length, 8));
                    mask = BitConverter.ToUInt64(eight, 0);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetInterruptInfo: {ex.Message}");
            }
            return new InterruptInfo(msi, limit, maxLimit, policy, priority, mask);
        }

        public static string DevicePolicyName(int? v) => v switch
        {
            0 => "IrqPolicyMachineDefault",
            1 => "IrqPolicyAllCloseProcessors",
            2 => "IrqPolicyOneCloseProcessor",
            3 => "IrqPolicyAllMatchingProcessors",
            4 => "IrqPolicySpecifiedProcessors",
            5 => "IrqPolicySpreadMessagesAcrossAllProcessors",
            _ => "—"
        };

        public static string DevicePolicyShort(int? v) => v switch
        {
            0 => "Default",
            1 => "AllClose",
            2 => "OneClose",
            3 => "AllMatching",
            4 => "Specified",
            5 => "Spread",
            _ => "—"
        };

        public static string DevicePriorityName(int? v) => v switch
        {
            0 => "Low",
            1 => "Normal",
            2 => "High",
            _ => "Undefined"
        };

        /// <summary>"2, 3, 4, 5" style list of set processors, like the reference tool.</summary>
        public static string AffinityMaskText(ulong? mask)
        {
            if (mask is null or 0) return "—";
            var bits = new List<int>();
            ulong m = mask.Value;
            for (int i = 0; i < 64; i++)
                if ((m & (1UL << i)) != 0) bits.Add(i);
            return bits.Count == 0 ? "—" : string.Join(", ", bits);
        }

        // DeviceDesc is often "@oemNN.inf,%token%;Friendly Name" — keep the friendly tail.
        private static string FriendlyName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            int semi = raw.LastIndexOf(';');
            string candidate = semi >= 0 && semi < raw.Length - 1 ? raw[(semi + 1)..].Trim() : raw.Trim();
            return string.IsNullOrWhiteSpace(candidate) ? raw.Trim() : candidate;
        }

        // ── Registry Write Methods (tuning pass) ────────────────────────────

        /// <summary>Enables or disables MSI mode. Returns true if the write succeeded.</summary>
        public bool SetMsiEnabled(string deviceId, bool enabled)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                int targetVal = enabled ? 1 : 0;
                Debug.WriteLine($"[AffinityService] Writing MSISupported={targetVal} to {path}");
                
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                key.SetValue("MSISupported", targetVal, Microsoft.Win32.RegistryValueKind.DWord);
                
                object? readback = key.GetValue("MSISupported");
                Debug.WriteLine($"[AffinityService] Read-back MSISupported={readback}");

                if (readback is null || (int)readback != targetVal)
                {
                    Debug.WriteLine("[AffinityService] Read-back failed or mismatched. Write failed.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetMsiEnabled failed: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>Sets the MSI Limit. Returns true if the write succeeded.</summary>
        public bool SetMsiLimit(string deviceId, int limit)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                Debug.WriteLine($"[AffinityService] Writing MessageNumberLimit={limit} to {path}");
                
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                key.SetValue("MessageNumberLimit", limit, Microsoft.Win32.RegistryValueKind.DWord);
                
                object? readback = key.GetValue("MessageNumberLimit");
                Debug.WriteLine($"[AffinityService] Read-back MessageNumberLimit={readback}");

                if (readback is null || (int)readback != limit)
                {
                    Debug.WriteLine("[AffinityService] Read-back failed or mismatched. Write failed.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetMsiLimit failed: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>Sets the Max MSI Limit. Returns true if the write succeeded.</summary>
        public bool SetMaxMsiLimit(string deviceId, int limit)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                Debug.WriteLine($"[AffinityService] Writing MaxMessageNumberLimit={limit} to {path}");
                
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                key.SetValue("MaxMessageNumberLimit", limit, Microsoft.Win32.RegistryValueKind.DWord);
                
                object? readback = key.GetValue("MaxMessageNumberLimit");
                Debug.WriteLine($"[AffinityService] Read-back MaxMessageNumberLimit={readback}");

                if (readback is null || (int)readback != limit)
                {
                    Debug.WriteLine("[AffinityService] Read-back failed or mismatched. Write failed.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetMaxMsiLimit failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Sets DevicePolicy (0-5). Returns true if the write succeeded.</summary>
        public bool SetDevicePolicy(string deviceId, int policy)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                Debug.WriteLine($"[AffinityService] Writing DevicePolicy={policy} to {path}");
                
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                key.SetValue("DevicePolicy", policy, Microsoft.Win32.RegistryValueKind.DWord);
                
                object? readback = key.GetValue("DevicePolicy");
                Debug.WriteLine($"[AffinityService] Read-back DevicePolicy={readback}");

                if (readback is null || (int)readback != policy)
                {
                    Debug.WriteLine("[AffinityService] Read-back failed or mismatched. Write failed.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetDevicePolicy failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Sets DevicePriority (0=Low, 1=Normal, 2=High). Returns true if the write succeeded.</summary>
        public bool SetDevicePriority(string deviceId, int priority)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                Debug.WriteLine($"[AffinityService] Writing DevicePriority={priority} to {path}");

                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                key.SetValue("DevicePriority", priority, Microsoft.Win32.RegistryValueKind.DWord);
                
                object? readback = key.GetValue("DevicePriority");
                Debug.WriteLine($"[AffinityService] Read-back DevicePriority={readback}");

                if (readback is null || (int)readback != priority)
                {
                    Debug.WriteLine("[AffinityService] Read-back failed or mismatched. Write failed.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetDevicePriority failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sets the AssignmentSetOverride bitmask (affinity mask). Pass 0 to clear
        /// (remove the value and let the OS decide). Returns true if the write succeeded.
        /// </summary>
        public bool SetAffinityMask(string deviceId, ulong mask)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                Debug.WriteLine($"[AffinityService] Writing AssignmentSetOverride={mask:X} to {path}");
                
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                if (mask == 0)
                {
                    key.DeleteValue("AssignmentSetOverride", false);
                    object? readback = key.GetValue("AssignmentSetOverride");
                    if (readback is not null)
                    {
                        Debug.WriteLine("[AffinityService] Read-back deletion mismatched. Value still exists.");
                        return false;
                    }
                }
                else
                {
                    byte[] bytes = BitConverter.GetBytes(mask);
                    key.SetValue("AssignmentSetOverride", bytes, Microsoft.Win32.RegistryValueKind.Binary);
                    
                    object? readback = key.GetValue("AssignmentSetOverride");
                    if (readback is byte[] actualBytes)
                    {
                        ulong readMask = actualBytes.Length == 8 ? BitConverter.ToUInt64(actualBytes) : (ulong)BitConverter.ToUInt32(actualBytes);
                        Debug.WriteLine($"[AffinityService] Read-back AssignmentSetOverride={readMask:X}");
                        if (readMask != mask)
                        {
                            Debug.WriteLine("[AffinityService] Read-back failed or mismatched. Write failed.");
                            return false;
                        }
                    }
                    else
                    {
                        Debug.WriteLine("[AffinityService] Read-back failed or mismatched type. Write failed.");
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetAffinityMask failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deletes the affinity-related registry values, effectively restoring the
        /// device to OS defaults for that property. Returns true on success.
        /// </summary>
        public bool ClearAffinityPolicy(string deviceId, string valueName)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                Debug.WriteLine($"[AffinityService] Deleting {valueName} from {path}");
                
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path, writable: true);
                if (key is not null)
                {
                    key.DeleteValue(valueName, throwOnMissingValue: false);
                    object? readback = key.GetValue(valueName);
                    if (readback is not null)
                    {
                        Debug.WriteLine("[AffinityService] Read-back deletion mismatched. Value still exists.");
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClearAffinityPolicy({valueName}) failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Restarts a device via pnputil so the updated interrupt settings take effect.
        /// </summary>
        public static async Task RestartDeviceAsync(string deviceInstanceId)
        {
            try
            {
                // pnputil needs the full PCI\... path. Our DeviceInstanceId stores
                // "VEN_xxxx...\instance"; the full path under Enum\PCI would be
                // "PCI\VEN_xxxx...\instance".
                // Detect the bus prefix from the registry root we enumerated under.
                string fullId = deviceInstanceId;
                foreach (var prefix in new[] { "PCI", "HDAUDIO", "USB" })
                {
                    string testPath = $@"SYSTEM\CurrentControlSet\Enum\{prefix}\{deviceInstanceId}";
                    using var testKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(testPath);
                    if (testKey is not null)
                    {
                        fullId = $"{prefix}\\{deviceInstanceId}";
                        break;
                    }
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/restart-device \"{fullId}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await proc.WaitForExitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine($"pnputil restart timed out for {fullId}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RestartDevice failed: {ex.Message}");
            }
        }
    }
}
