using stellarisKIT.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace stellarisKIT.Services
{
    public class InstallerService : IInstallerService
    {
        private static readonly HttpClient _httpClient = new();

        public async Task UninstallBrowserAsync(BrowserInstallItem item, CancellationToken ct)
        {
            await Task.Run(() =>
            {
                string searchKeyword = item.Name.Contains("Zen", StringComparison.OrdinalIgnoreCase) ? "Zen" :
                                       item.Name.Contains("Brave", StringComparison.OrdinalIgnoreCase) ? "Brave" :
                                       item.Name.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase) ? "Vivaldi" :
                                       item.Name.Contains("Helium", StringComparison.OrdinalIgnoreCase) ? "Helium" :
                                       item.Name.Contains("Epic", StringComparison.OrdinalIgnoreCase) ? "Epic Games Launcher" :
                                       item.Name.Contains("Minecraft", StringComparison.OrdinalIgnoreCase) ? "Minecraft Launcher" :
                                       item.Name.Contains("EA", StringComparison.OrdinalIgnoreCase) ? "EA app" :
                                       item.Name.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase) ? "Ubisoft Connect" :
                                       item.Name.Contains("Steam", StringComparison.OrdinalIgnoreCase) ? "Steam" :
                                       item.Name.Contains("Riot", StringComparison.OrdinalIgnoreCase) ? "Riot" :
                                       item.Name.Contains("Discord", StringComparison.OrdinalIgnoreCase) ? "Discord" :
                                       item.Name.Contains("Telegram", StringComparison.OrdinalIgnoreCase) ? "Telegram" :
                                       item.Name.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) ? "WhatsApp" : "Unknown";

                string[] hives = new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };

                string? uninstallString = null;

                // Search strictly in LocalMachine and CurrentUser
                foreach (var baseKey in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
                {
                    foreach (var hive in hives)
                    {
                        using var key = baseKey.OpenSubKey(hive);
                        if (key != null)
                        {
                            foreach (var subKeyName in key.GetSubKeyNames())
                            {
                                using var subKey = key.OpenSubKey(subKeyName);
                                var displayName = subKey?.GetValue("DisplayName") as string;
                                if (displayName != null && displayName.Contains(searchKeyword, StringComparison.OrdinalIgnoreCase))
                                {
                                    uninstallString = subKey?.GetValue("QuietUninstallString") as string ?? subKey?.GetValue("UninstallString") as string;
                                    if (!string.IsNullOrEmpty(uninstallString))
                                    {
                                        break;
                                    }
                                }
                            }
                        }
                        if (uninstallString != null) break;
                    }
                    if (uninstallString != null) break;
                }

                if (!string.IsNullOrEmpty(uninstallString))
                {
                    // Clean up the string if it contains quotes
                    string exePath = uninstallString;
                    string arguments = "";

                    if (exePath.StartsWith("\""))
                    {
                        int endQuote = exePath.IndexOf("\"", 1);
                        if (endQuote > 0)
                        {
                            arguments = exePath.Substring(endQuote + 1).Trim();
                            exePath = exePath.Substring(1, endQuote - 1);
                        }
                    }
                    else
                    {
                        int firstSpace = exePath.IndexOf(" ");
                        if (firstSpace > 0 && !exePath.Contains(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            // If there are spaces but no explicit quotes, try to find .exe
                            int exeIndex = exePath.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                            if (exeIndex > 0)
                            {
                                arguments = exePath.Substring(exeIndex + 4).Trim();
                                exePath = exePath.Substring(0, exeIndex + 4);
                            }
                        }
                    }

                    // Append absolute silent overrides based on target
                    if (searchKeyword == "Zen")
                    {
                        if (!arguments.Contains("/S")) arguments += " /S";
                    }
                    else if (searchKeyword == "Brave" || searchKeyword == "Vivaldi")
                    {
                        if (!arguments.Contains("--uninstall")) arguments += " --uninstall";
                        if (!arguments.Contains("--force-uninstall")) arguments += " --force-uninstall";
                        arguments += " --system-level";
                    }

                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = arguments,
                            UseShellExecute = true,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        using var process = Process.Start(psi);
                        if (process != null)
                        {
                            process.WaitForExit(30000);
                        }
                    }
                    catch { }
                }

                // ==========================================
                // HARD WIPE: Nuke remaining profiles & registry
                // ==========================================
                
                // 1. Force kill remaining processes
                try
                {
                    string[] procNames = searchKeyword switch
                    {
                        "Zen" => new[] { "zen" },
                        "Brave" => new[] { "brave" },
                        "Vivaldi" => new[] { "vivaldi" },
                        "Helium" => new[] { "chrome", "helium" },
                        "Epic Games Launcher" => new[] { "EpicGamesLauncher" },
                        "EA app" => new[] { "EADesktop", "EAClientService" },
                        "Ubisoft Connect" => new[] { "upc", "UbisoftConnect" },
                        "Minecraft Launcher" => new[] { "MinecraftLauncher" },
                        "Steam" => new[] { "steam", "SteamService" },
                        "Riot" => new[] { "RiotClientServices" },
                        "Discord" => new[] { "Discord" },
                        "Telegram" => new[] { "Telegram" },
                        "WhatsApp" => new[] { "WhatsApp" },
                        _ => Array.Empty<string>()
                    };
                    
                    foreach (var procName in procNames)
                    {
                        foreach (var p in Process.GetProcessesByName(procName))
                        {
                            p.Kill();
                        }
                    }
                }
                catch { }

                // 2. Wipe directories
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                
                var pathsToNuke = new List<string>();

                if (searchKeyword == "Zen")
                {
                    pathsToNuke.Add(Path.Combine(localAppData, "Programs", "Zen Browser"));
                    pathsToNuke.Add(Path.Combine(localAppData, "zen"));
                }
                else if (searchKeyword == "Brave")
                {
                    pathsToNuke.Add(Path.Combine(localAppData, "BraveSoftware"));
                    pathsToNuke.Add(Path.Combine(progFiles, "BraveSoftware"));
                    pathsToNuke.Add(Path.Combine(progFilesX86, "BraveSoftware"));
                }
                else if (searchKeyword == "Vivaldi")
                {
                    pathsToNuke.Add(Path.Combine(localAppData, "Vivaldi"));
                    pathsToNuke.Add(Path.Combine(progFiles, "Vivaldi"));
                    pathsToNuke.Add(Path.Combine(progFilesX86, "Vivaldi"));
                }
                else if (searchKeyword == "Helium")
                {
                    // Helium's actual per-user location is %LOCALAPPDATA%\imput\Helium (chrome.exe binary), not %LOCALAPPDATA%\Programs\Helium
                    pathsToNuke.Add(Path.Combine(localAppData, "imput", "Helium"));
                    pathsToNuke.Add(Path.Combine(localAppData, "Helium"));
                    pathsToNuke.Add(Path.Combine(localAppData, "Programs", "Helium"));
                    pathsToNuke.Add(Path.Combine(progFiles, "imput", "Helium"));
                    pathsToNuke.Add(Path.Combine(progFiles, "Helium"));
                    pathsToNuke.Add(Path.Combine(progFilesX86, "imput", "Helium"));
                    pathsToNuke.Add(Path.Combine(progFilesX86, "Helium"));
                }

                foreach (var path in pathsToNuke)
                {
                    if (Directory.Exists(path))
                    {
                        try
                        {
                            Directory.Delete(path, true);
                        }
                        catch { }
                    }
                }

                // 3. Wipe our extension registry keys from both HKLM and HKCU and both Helium/Chromium paths
                try
                {
                    void DeleteForcelist(string path)
                    {
                        try
                        {
                            using var lm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path, true);
                            lm?.DeleteSubKeyTree("ExtensionInstallForcelist", false);
                        }
                        catch { }
                        try
                        {
                            using var cu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path, true);
                            cu?.DeleteSubKeyTree("ExtensionInstallForcelist", false);
                        }
                        catch { }
                    }

                    if (searchKeyword == "Brave")
                    {
                        DeleteForcelist(@"SOFTWARE\Policies\BraveSoftware\Brave");
                    }
                    else if (searchKeyword == "Vivaldi")
                    {
                        DeleteForcelist(@"SOFTWARE\Policies\Vivaldi");
                    }
                    else if (searchKeyword == "Helium")
                    {
                        DeleteForcelist(@"SOFTWARE\Policies\Helium");
                        DeleteForcelist(@"SOFTWARE\Policies\Chromium");
                    }
                }
                catch { }
            });
        }

        private string? GetBrowserInstallDirectory(BrowserInstallItem item)
        {
            if (string.IsNullOrEmpty(item.InstalledCheckPath)) return null;

            var paths = item.InstalledCheckPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in paths)
            {
                string expanded = Environment.ExpandEnvironmentVariables(path.Trim());

                if (expanded.Contains('*'))
                {
                    string baseDir = expanded.Substring(0, expanded.IndexOf('*'));
                    baseDir = Path.GetDirectoryName(baseDir) ?? string.Empty;
                    string searchPattern = Path.GetFileName(expanded);

                    if (Directory.Exists(baseDir))
                    {
                        try
                        {
                            var files = Directory.GetFiles(baseDir, searchPattern, SearchOption.AllDirectories);
                            if (files.Length > 0) return Path.GetDirectoryName(files[0]);
                        }
                        catch { }
                    }
                    continue;
                }

                if (File.Exists(expanded))
                {
                    return Path.GetDirectoryName(expanded);
                }
            }
            return null;
        }

        public bool IsBrowserInstalled(BrowserInstallItem item)
        {
            return GetBrowserInstallDirectory(item) != null;
        }

        /// <summary>
        /// Returns Helium User Data directories for the current profile plus every local
        /// user's profile (elevated runs resolve %LOCALAPPDATA% to the admin profile, so
        /// the interactive user's profile must be covered explicitly).
        /// </summary>
        private static List<string> GetHeliumUserDataDirs()
        {
            var candidates = new List<string>();
            string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
                candidates.Add(Path.Combine(localAppData, "imput", "Helium", "User Data"));

            try
            {
                string usersRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System)[..^7], "Users");
                if (Directory.Exists(usersRoot))
                {
                    foreach (var dir in Directory.GetDirectories(usersRoot))
                    {
                        string p = Path.Combine(dir, "AppData", "Local", "imput", "Helium", "User Data");
                        if (!candidates.Contains(p)) candidates.Add(p);
                    }
                }
            }
            catch { }

            return candidates;
        }

        /// <summary>
        /// Seeds Helium profile prefs so force-installed extensions can download.
        /// Fresh silent installs never complete helium://setup, leaving
        /// ShouldAccessServices()=false (services.enabled=true by default but neither
        /// user_consented nor an explicit HasPrefPath(enabled) is set). The extension
        /// proxy then returns a dummy URL and ExtensionInstallForcelist silently never
        /// fetches: helium://policy shows "managed" but only the built-in uBlock
        /// component (blockjmkbacgjkknlgpkjjiijinjdanf) appears in helium://extensions.
        /// Writing these keys explicitly marks onboarding complete and enables the proxy.
        /// Covers both pref-name variants across Helium builds (user_consented/consented,
        /// completed_onboarding/did_onboarding, ext_proxy/extension_proxy).
        /// </summary>
        private static void EnsureHeliumServicesEnabled()
        {
            try
            {
                var candidates = GetHeliumUserDataDirs();

                // Keys required by ShouldAccessServices() + ShouldAccessExtensionService().
                // Dotted paths are created as nested JSON objects inside Default\Preferences.
                var required = new Dictionary<string, JsonNode>
                {
                    ["helium.services.enabled"] = JsonValue.Create(true)!,
                    ["helium.services.user_consented"] = JsonValue.Create(true)!,
                    ["helium.services.consented"] = JsonValue.Create(true)!,
                    ["helium.completed_onboarding"] = JsonValue.Create(true)!,
                    ["helium.did_onboarding"] = JsonValue.Create(true)!,
                    ["helium.services.ext_proxy"] = JsonValue.Create(true)!,
                    ["helium.services.extension_proxy"] = JsonValue.Create(true)!,
                    ["helium.services.extension_updating"] = JsonValue.Create(true)!,
                };

                foreach (var userData in candidates)
                {
                    try
                    {
                        string prefPath = Path.Combine(userData, "Default", "Preferences");
                        // Fresh silent installs have no profile yet (browser never launched):
                        // create a minimal Preferences file so the proxy is already enabled
                        // on first run instead of waiting for helium://setup. Chromium merges
                        // first-run defaults with an existing Preferences file.
                        if (!File.Exists(prefPath))
                        {
                            try { Directory.CreateDirectory(Path.GetDirectoryName(prefPath)!); } catch { }
                        }
                        if (IsFileLocked(prefPath)) continue; // browser running; keys already seeded on earlier run

                        string json = File.Exists(prefPath) ? File.ReadAllText(prefPath) : "{}";
                        if (string.IsNullOrWhiteSpace(json)) json = "{}";
                        JsonNode? root = JsonNode.Parse(json) ?? new JsonObject();
                        bool changed = false;
                        foreach (var kv in required)
                        {
                            if (SetNestedPref(root, kv.Key, kv.Value)) changed = true;
                        }
                        if (changed)
                        {
                            string tmp = prefPath + ".stellarisKIT.tmp";
                            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
                            File.Move(tmp, prefPath, overwrite: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"EnsureHeliumServicesEnabled failed for {userData}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"EnsureHeliumServicesEnabled failed: {ex.Message}");
            }
        }

        private static bool SetNestedPref(JsonNode root, string dottedPath, JsonNode value)
        {
            string[] parts = dottedPath.Split('.');
            JsonNode current = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (current is not JsonObject obj) return false;
                JsonNode? next = obj[parts[i]];
                if (next is not JsonObject && next is not null && next.GetValueKind() != JsonValueKind.Object)
                {
                    // Existing scalar where we need an object; leave untouched to avoid corrupting prefs.
                    return false;
                }
                if (next is null)
                {
                    var created = new JsonObject();
                    obj[parts[i]] = created;
                    current = created;
                }
                else
                {
                    current = next;
                }
            }
            if (current is JsonObject leaf)
            {
                string last = parts[^1];
                JsonNode? existing = leaf[last];
                if (existing != null && JsonNode.DeepEquals(existing, value)) return false;
                leaf[last] = JsonNode.Parse(value.ToJsonString());
                return true;
            }
            return false;
        }

        private static bool IsFileLocked(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Downloads the selected extensions from the Chrome Web Store, unpacks them from their
        /// CRX files, and makes Helium load them as unpacked extensions on every launch through
        /// the --load-extension switch on the Start Menu/Desktop shortcuts.
        ///
        /// WHY helix cannot install them itself: Helium 0.16.5.1 cannot install extensions via
        /// ExtensionInstallForcelist at all. Its domain substitution rewrites the Web Store
        /// update host and TrkProtocolHandler blocks the updater's manifest request (verified in
        /// chrome_debug.log: "Failed to fetch manifest ... response code:-1"), and policy
        /// validation additionally rejects any substitute update URL (verified with an
        /// http://127.0.0.1 update server - the updater never even fetched it). Helium also
        /// discards hand-written extensions.settings entries on every launch (verified by
        /// multiple relaunch tests, including entries with a correct manifest "key"). Loading
        /// unpacked via --load-extension is the only mechanism that actually starts third-party
        /// extensions: tested live, Helium then registers the extensions itself (location 8)
        /// and runs their background pages / service workers.
        /// </summary>
        private void SeedHeliumExtensionsFromStore(BrowserInstallItem item)
        {
            List<ExtensionItem> selected = new();
            foreach (var ext in item.Extensions)
            {
                if (ext.IsSelected && ext.IsAvailable && !string.IsNullOrEmpty(ext.ChromiumExtensionId))
                    selected.Add(ext);
            }
            if (selected.Count == 0) return;

            string commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string cacheDir = Path.Combine(commonData, "stellarisKIT", "extensions");
            string unpackRoot = Path.Combine(commonData, "stellarisKIT", "unpacked");
            try { Directory.CreateDirectory(cacheDir); } catch { return; }
            try { Directory.CreateDirectory(unpackRoot); } catch { return; }

            // 1. Fetch and unpack each CRX into a stable directory under %ProgramData%.
            var unpackedDirs = new List<string>();
            foreach (var ext in selected)
            {
                try
                {
                    string id = ext.ChromiumExtensionId!;
                    string crxPath = Path.Combine(cacheDir, id + ".crx");
                    if (!TryDownloadCrx(id, crxPath)) continue;
                    if (!TryParseCrx(crxPath, out string? version, out _) || version is null) continue;

                    string slug = MakeSafeName(ext.Name);
                    string dest = Path.Combine(unpackRoot, slug);
                    string manifestPath = Path.Combine(dest, "manifest.json");
                    // Refresh the unpacked copy when the cached CRX version differs.
                    if (Directory.Exists(dest) && File.Exists(manifestPath))
                    {
                        try
                        {
                            string? oldVersion = JsonNode.Parse(File.ReadAllText(manifestPath))?["version"]?.GetValue<string>();
                            if (oldVersion != version)
                            {
                                try { Directory.Delete(dest, true); } catch { }
                            }
                        }
                        catch { }
                    }
                    if (!Directory.Exists(dest) && !TryExtractCrx(crxPath, dest)) continue;

                    unpackedDirs.Add(dest);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"SeedHeliumExtensionsFromStore {ext.Name}: {ex.Message}");
                }
            }
            if (unpackedDirs.Count == 0) return;

            // 2. Make every Helium launch load the extensions unpacked. The --load-extension
            // switch is passed on the Start Menu/Desktop shortcuts so the extensions load
            // regardless of launch method, and the entries Helium itself wrote stay valid.
            string loadFlag = "--load-extension=\"" + string.Join(",", unpackedDirs) + "\"";
            try { File.WriteAllText(Path.Combine(unpackRoot, "helium-load-extension.txt"), loadFlag); } catch { }
            PatchHeliumShortcuts(loadFlag);
        }

        private static string MakeSafeName(string name)
        {
            var builder = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            }
            string result = builder.ToString();
            if (string.IsNullOrEmpty(result)) result = "extension";
            return result.Length > 24 ? result[..24] : result;
        }

        private static string? GetHeliumInstallDir()
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string candidate = Path.Combine(localAppData, "imput", "Helium", "Application", "chrome.exe");
                if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Re-points Helium's Start Menu/Desktop shortcuts to also pass --load-extension, so the
        /// unpacked extensions load on every launch. Re-applied whenever extensions are deployed.
        /// </summary>
        private static void PatchHeliumShortcuts(string loadFlag)
        {
            try
            {
                string? installDir = GetHeliumInstallDir();
                if (installDir is null) return;
                string exe = Path.Combine(installDir, "chrome.exe");

                var searchDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var env in new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                })
                {
                    if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) searchDirs.Add(env);
                }

                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null) return;
                dynamic shell = Activator.CreateInstance(shellType)!;

                foreach (var dir in searchDirs)
                {
                    foreach (var lnk in Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories))
                    {
                        try
                        {
                            dynamic shortcut = shell.CreateShortcut(lnk);
                            string target = (string)shortcut.TargetPath;
                            if (!string.Equals(target, exe, StringComparison.OrdinalIgnoreCase)) continue;

                            string args = (string)shortcut.Arguments;
                            // Drop any previous --load-extension before appending the fresh one
                            // so re-seeding never duplicates or leaves stale paths.
                            args = System.Text.RegularExpressions.Regex.Replace(args, "--load-extension(?:=[^\\s]+)?", string.Empty).Trim();
                            if (args.Length > 0) args += " ";
                            shortcut.Arguments = args + loadFlag;
                            shortcut.Save();
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PatchHeliumShortcuts: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes stale Helium forcelist policies. ExtensionInstallForcelist cannot work on
        /// Helium 0.16.5.1 (updater requests are blocked), and leaving the policy causes repeated
        /// "Failed to fetch manifest" attempts and a misleading "managed" state on every launch.
        /// </summary>
        private static void RemoveHeliumForcelist()
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                foreach (var parent in new[] { @"SOFTWARE\Policies\Helium", @"SOFTWARE\Policies\Chromium" })
                {
                    try
                    {
                        using var key = hive.OpenSubKey(parent, true);
                        key?.DeleteSubKey("ExtensionInstallForcelist", false);
                    }
                    catch { }
                }
            }
        }

        private bool TryDownloadCrx(string id, string crxPath)
        {
            try
            {
                if (File.Exists(crxPath) && new FileInfo(crxPath).Length > 1024)
                {
                    // Reuse cache; corruption (bad magic) below triggers a re-download.
                    if (HasValidCrxMagic(crxPath)) return true;
                    try { File.Delete(crxPath); } catch { }
                }

                string url = $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion=152.0.7977.82&acceptformat=crx2,crx3&x=id%3D{id}%26installsource%3Dondemand%26uc";
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                byte[] data = _httpClient.GetByteArrayAsync(url, cts.Token).GetAwaiter().GetResult();
                if (data.Length < 1024) return false;
                string tmp = crxPath + ".download";
                File.WriteAllBytes(tmp, data);
                File.Move(tmp, crxPath, overwrite: true);
                return HasValidCrxMagic(crxPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryDownloadCrx {id}: {ex.Message}");
                return File.Exists(crxPath) && HasValidCrxMagic(crxPath);
            }
        }

        private static bool HasValidCrxMagic(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] magic = new byte[4];
                if (fs.Read(magic, 0, 4) != 4) return false;
                return magic[0] == (byte)'C' && magic[1] == (byte)'r' && magic[2] == (byte)'2' && magic[3] == (byte)'4';
            }
            catch { return false; }
        }

        private static bool TryParseCrx(string crxPath, out string? version, out JsonNode? manifest)
        {
            version = null;
            manifest = null;
            try
            {
                using var fs = new FileStream(crxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);
                byte[] magic = br.ReadBytes(4);
                if (magic.Length != 4 || magic[0] != (byte)'C' || magic[1] != (byte)'r' || magic[2] != (byte)'2' || magic[3] != (byte)'4')
                    return false;
                uint format = br.ReadUInt32();
                long zipOffset;
                if (format == 3)
                {
                    uint headerSize = br.ReadUInt32();
                    zipOffset = 12 + headerSize;
                }
                else if (format == 2)
                {
                    uint pubkeyLen = br.ReadUInt32();
                    uint sigLen = br.ReadUInt32();
                    zipOffset = 16 + pubkeyLen + sigLen;
                }
                else return false;

                // ZipArchive requires offsets relative to stream position 0, but the zip data
                // starts at zipOffset inside the CRX. Copy the zip portion to a clean stream first.
                fs.Seek(zipOffset, SeekOrigin.Begin);
                var zipBytes = new byte[fs.Length - zipOffset];
                int read = 0;
                while (read < zipBytes.Length)
                {
                    int n = fs.Read(zipBytes, read, zipBytes.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read != zipBytes.Length) return false;
                using var zipStream = new MemoryStream(zipBytes, writable: false);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                var entry = archive.GetEntry("manifest.json");
                if (entry is null) return false;
                using var reader = new StreamReader(entry.Open());
                string json = reader.ReadToEnd();
                manifest = JsonNode.Parse(json);
                version = manifest?["version"]?.GetValue<string>();
                return !string.IsNullOrEmpty(version) && manifest is not null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryParseCrx: {ex.Message}");
                return false;
            }
        }

        private static bool TryExtractCrx(string crxPath, string destDir)
        {
            try
            {
                if (Directory.Exists(destDir))
                {
                    // Already extracted (same id+version); verify it has a manifest.
                    if (File.Exists(Path.Combine(destDir, "manifest.json"))) return true;
                    try { Directory.Delete(destDir, true); } catch { return false; }
                }

                using var fs = new FileStream(crxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);
                br.ReadBytes(4); // magic (validated earlier)
                uint format = br.ReadUInt32();
                long zipOffset = format == 3 ? 12 + br.ReadUInt32() : 16 + br.ReadUInt32() + br.ReadUInt32();
                // Same as TryParseCrx: re-base the zip data to stream position 0 for ZipArchive.
                fs.Seek(zipOffset, SeekOrigin.Begin);
                var zipBytes = new byte[fs.Length - zipOffset];
                int read = 0;
                while (read < zipBytes.Length)
                {
                    int n = fs.Read(zipBytes, read, zipBytes.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read != zipBytes.Length) return false;
                using var zipStream = new MemoryStream(zipBytes, writable: false);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                Directory.CreateDirectory(destDir);
                foreach (var entry in archive.Entries)
                {
                    // Guard against zip-slip entries.
                    string target = Path.GetFullPath(Path.Combine(destDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(Path.GetFullPath(destDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(target, Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
                return File.Exists(Path.Combine(destDir, "manifest.json"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryExtractCrx: {ex.Message}");
                return false;
            }
        }

        private void DeployExtensions(BrowserInstallItem item)
        {
            if (item.Name.Contains("Brave", StringComparison.OrdinalIgnoreCase))
            {
                var chromeInstaller = new ChromiumExtensionInstaller();
                chromeInstaller.InstallExtensions(item.Extensions, @"SOFTWARE\Policies\BraveSoftware\Brave\ExtensionInstallForcelist");
            }
            else if (item.Name.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase))
            {
                var chromeInstaller = new ChromiumExtensionInstaller();
                chromeInstaller.InstallExtensions(item.Extensions, @"SOFTWARE\Policies\Vivaldi\ExtensionInstallForcelist");
            }
            else if (item.Name.Contains("Helium", StringComparison.OrdinalIgnoreCase))
            {
                // Helium 0.16.5.1 cannot install extensions via ExtensionInstallForcelist:
                // its update-host substitution rewrites the Web Store URL and TrkProtocolHandler
                // blocks the updater's request (chrome_debug.log: "Failed to fetch manifest ...
                // response code:-1"), and policy validation rejects any substitute URL. Remove stale
                // forcelist policies (they only cause failed fetch attempts on every launch) and
                // load the extensions unpacked via --load-extension instead (see SeedHeliumExtensionsFromStore).
                RemoveHeliumForcelist();
                EnsureHeliumServicesEnabled();
                SeedHeliumExtensionsFromStore(item);
            }
            else if (item.Name.Contains("Zen", StringComparison.OrdinalIgnoreCase))
            {
                string? installDir = GetBrowserInstallDirectory(item);
                if (installDir != null)
                {
                    var firefoxInstaller = new FirefoxExtensionInstaller();
                    firefoxInstaller.InstallExtensions(item.Extensions, Path.Combine(installDir, "distribution"));
                }
            }
            else if (item.Name.Contains("Firefox", StringComparison.OrdinalIgnoreCase))
            {
                string? installDir = GetBrowserInstallDirectory(item);
                if (installDir != null)
                {
                    var firefoxInstaller = new FirefoxExtensionInstaller();
                    firefoxInstaller.InstallExtensions(item.Extensions, Path.Combine(installDir, "distribution"));
                }
            }
        }

        public async Task InstallBrowserAsync(BrowserInstallItem item, IProgress<BrowserInstallStatus> progress, IProgress<double> downloadProgress, IProgress<string> errorProgress, CancellationToken ct)
        {
            // 1. Check if already installed — still deploy extensions even if browser exists
            if (IsBrowserInstalled(item))
            {
                DeployExtensions(item);
                progress.Report(BrowserInstallStatus.AlreadyInstalled);
                return;
            }

            // 2. Download installer
            progress.Report(BrowserInstallStatus.Downloading);
            downloadProgress.Report(0);
            string tempPath = Path.Combine(Path.GetTempPath(), item.InstallerFileName);
            try
            {
                using var response = await _httpClient.GetAsync(item.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                var totalRead = 0L;
                var bytesRead = 0;
                var lastReport = DateTime.UtcNow;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) != 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                    totalRead += bytesRead;

                    if (totalBytes != -1 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                    {
                        var percentage = (double)totalRead / totalBytes * 100.0;
                        downloadProgress.Report(percentage);
                        lastReport = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                CleanUp(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                CleanUp(tempPath);
                errorProgress.Report($"Download failed: {ex.Message}");
                progress.Report(BrowserInstallStatus.Failed);
                return;
            }

            ct.ThrowIfCancellationRequested();

            // 3. Run installer silently
            progress.Report(BrowserInstallStatus.Installing);
            try
            {
                var isMsi = Path.GetExtension(tempPath).Equals(".msi", StringComparison.OrdinalIgnoreCase);
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = isMsi 
                        ? $"/c start /wait \"\" msiexec.exe /i \"{tempPath}\" {item.SilentInstallArgs}" 
                        : $"/c start /wait \"\" \"{tempPath}\" {item.SilentInstallArgs}",
                    // ShellExecute=false allows us to reliably wait for the cmd wrapper to finish,
                    // while `start` detaches any post-install GUIs (like Epic Games launching itself)
                    // so they don't block the WaitForExit loop by inheriting IO pipes.
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var process = Process.Start(psi);
                if (process is not null)
                {
                    // Wait with 10-minute timeout to avoid infinite "Installing" hang if installer shows UI or crashes
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
                    try
                    {
                        await process.WaitForExitAsync(timeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Timed out, not user-cancelled
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                        errorProgress.Report("Installer timed out after 10 minutes and was terminated. Verification will continue if files were written.");
                        // Don't return immediately; fall through to verification which may succeed if files landed
                    }

                    // Log non-zero exit but don't fail outright; verification step is the source of truth.
                    // NSIS returns 0 on success, 2 on abort, etc. We let the file check decide.
                    try
                    {
                        if (process.HasExited && process.ExitCode != 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"Installer exit code {process.ExitCode} for {item.Name}");
                        }
                    }
                    catch { }
                }
                else
                {
                    // Process.Start can return null with UseShellExecute=true (e.g., UAC). With false it rarely happens,
                    // but handle gracefully by short delay before verification.
                    await Task.Delay(2000, ct);
                }
            }
            catch (OperationCanceledException)
            {
                CleanUp(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                CleanUp(tempPath);
                errorProgress.Report($"Install failed: {ex.Message}");
                progress.Report(BrowserInstallStatus.Failed);
                return;
            }

            // 4. Verify installation – robust polling that fixes "helium was stuck on installing"
            // The old check only looked at %LOCALAPPDATA%\Programs\Helium\Helium.exe which never exists for current Helium builds (chrome.exe under imput\Helium).
            // Now InstalledCheckPath covers all real locations including ...\imput\Helium\Application\chrome.exe.
            bool verified = false;
            for (int i = 0; i < 240; i++) // 240 * 500ms = 120s max, enough for Helium NSIS unpack
            {
                ct.ThrowIfCancellationRequested();
                if (IsBrowserInstalled(item))
                {
                    verified = true;
                    // Short grace period for file locks to release before we write policies
                    await Task.Delay(2000, ct);
                    break;
                }
                await Task.Delay(500, ct);
            }

            if (!verified)
            {
                errorProgress?.Report("Installation failed verification check.");
                progress?.Report(BrowserInstallStatus.Failed);
                return;
            }

            // 5. Deploy extensions after verified install
            DeployExtensions(item);

            progress?.Report(BrowserInstallStatus.Installed);

            // 6. Clean up
            CleanUp(tempPath);
        }

        private static void CleanUp(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
