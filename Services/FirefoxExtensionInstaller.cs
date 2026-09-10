using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using stellarisKIT.Models;

namespace stellarisKIT.Services
{
    public class FirefoxExtensionInstaller
    {
        public void InstallExtensions(IEnumerable<ExtensionItem> extensions, string distributionPath)
        {
            try 
            {
                string installDir = Environment.ExpandEnvironmentVariables(distributionPath);
                if (!Directory.Exists(installDir))
                {
                    Directory.CreateDirectory(installDir);
                }

                string policyPath = Path.Combine(installDir, "policies.json");

                var extensionSettings = new Dictionary<string, object>();

                foreach (var ext in extensions)
                {
                    if (ext.IsSelected && ext.IsAvailable && !string.IsNullOrEmpty(ext.FirefoxAddonSlug))
                    {
                        string id = ext.FirefoxAddonSlug switch 
                        {
                            "ublock-origin" => "uBlock0@raymondhill.net",
                            "privacy-badger17" => "jid1-MnnxcxisBPnSXQ@jetpack",
                            "istilldontcareaboutcookies" => "idcac-pub@guus.ninja", 
                            "location-guard" => "jid1-HdwPLukcGQeOSh@jetpack",
                            "decentraleyes" => "jid1-BoFifL9Vbdl2zQ@jetpack",
                            "clearurls" => "{74145f27-f039-47ce-a470-a662b129930a}",
                            _ => $"{ext.FirefoxAddonSlug}@zen.extensions"
                        };

                        extensionSettings[id] = new 
                        {
                            installation_mode = "force_installed",
                            install_url = $"https://addons.mozilla.org/firefox/downloads/latest/{ext.FirefoxAddonSlug}/latest.xpi"
                        };
                    }
                }

                if (extensionSettings.Count > 0)
                {
                    var policyData = new { policies = new { ExtensionSettings = extensionSettings } };
                    string json = JsonSerializer.Serialize(policyData, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(policyPath, json);
                }
            } 
            catch (Exception)
            {
                // Silently swallow write failures to avoid breaking normal pipeline
            }
        }
    }
}
