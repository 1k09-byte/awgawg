using System;
using System.Collections.Generic;
using stellarisKIT.Models;
using Microsoft.Win32;

namespace stellarisKIT.Services
{
    public class ChromiumExtensionInstaller
    {
        /// <summary>
        /// Installs Chromium extensions via ExtensionInstallForcelist policy.
        /// Writes to both HKLM and HKCU and supports multiple policy paths (Helium, Chromium, Brave, Vivaldi).
        /// This fixes Helium not installing extensions when only Chromium path was written or only HKLM was used for a per-user install.
        /// </summary>
        public void InstallExtensions(IEnumerable<ExtensionItem> extensions, params string[] registryPaths)
            => InstallExtensions(extensions, DefaultUpdateUrl, registryPaths);

        /// <summary>
        /// Same as <see cref="InstallExtensions(IEnumerable{ExtensionItem}, string[])"/> but with an explicit
        /// Web Store update URL. Helium rewrites the Chrome Web Store update host via domain substitution
        /// (clients2.google.com -&gt; clients2.9oo91e.qjz9zk, see helium://policy warning). On machines that are
        /// not enterprise managed, Chromium only force-installs extensions whose update URL matches the expected
        /// Web Store URL, so Helium entries must use the substituted host or they show as [BLOCKED]/Invalid extension ID.
        /// </summary>
        public void InstallExtensions(IEnumerable<ExtensionItem> extensions, string updateUrl, params string[] registryPaths)
        {
            if (registryPaths == null || registryPaths.Length == 0) return;
            if (string.IsNullOrWhiteSpace(updateUrl)) updateUrl = DefaultUpdateUrl;

            // Collect selected extensions once
            var selected = new List<ExtensionItem>();
            foreach (var ext in extensions)
            {
                if (ext.IsSelected && ext.IsAvailable && !string.IsNullOrEmpty(ext.ChromiumExtensionId))
                    selected.Add(ext);
            }

            foreach (var registryPath in registryPaths)
            {
                // Write to both hives for compatibility: HKLM (machine) and HKCU (per-user).
                // Helium per-user installs under %LOCALAPPDATA%\imput read HKCU policies reliably even without elevation.
                TryWriteToHive(RegistryHive.LocalMachine, registryPath, selected, updateUrl);
                TryWriteToHive(RegistryHive.CurrentUser, registryPath, selected, updateUrl);
            }
        }

        public const string DefaultUpdateUrl = "https://clients2.google.com/service/update2/crx";

        /// <summary>
        /// Web Store update URL as rewritten by Helium's domain substitution in this build
        /// (verbatim from the helium://policy warning). Must be used for Helium forcelist entries.
        /// </summary>
        public const string HeliumUpdateUrl = "https://clients2.9oo91e.qjz9zk/service/update2/crx";

        // Backward-compatible overload
        public void InstallExtensions(IEnumerable<ExtensionItem> extensions, string registryPath)
            => InstallExtensions(extensions, DefaultUpdateUrl, new[] { registryPath });

        private void TryWriteToHive(RegistryHive hive, string registryPath, List<ExtensionItem> selected, string updateUrl)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var key = baseKey.CreateSubKey(registryPath, true);
                if (key == null) return;

                // Clean up stale numeric values from previous installs (e.g., user deselected some extensions)
                foreach (var valueName in key.GetValueNames())
                {
                    if (int.TryParse(valueName, out _))
                    {
                        try { key.DeleteValue(valueName, false); } catch { }
                    }
                }

                int index = 1;
                foreach (var ext in selected)
                {
                    key.SetValue(index.ToString(), $"{ext.ChromiumExtensionId};{updateUrl}", RegistryValueKind.String);
                    index++;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to write ExtensionInstallForcelist to {hive}\\{registryPath}: {ex.Message}");
                // Swallow to avoid breaking install pipeline; HKCU fallback may succeed even if HKLM fails
            }
        }
    }
}
