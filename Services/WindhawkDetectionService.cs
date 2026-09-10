using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace stellarisKIT.Services;

/// <summary>
/// What was found on this machine when probing for a Windhawk installation.
/// </summary>
public sealed record WindhawkInstallationInfo
{
    public bool IsInstalled { get; init; }
    public string? Version { get; init; }
    /// <summary>Directory holding windhawk.exe (Program Files install) — null for portable.</summary>
    public string? InstallDirectory { get; init; }
    /// <summary>windhawk-cli.exe path when present (Windhawk 2.0+ ships the CLI).</summary>
    public string? CliPath { get; init; }
    /// <summary>True when a portable installation was detected (no Program Files binary).</summary>
    public bool IsPortable { get; init; }

    public static readonly WindhawkInstallationInfo NotInstalled = new();
}

/// <summary>
/// Detects whether Windhawk is already installed, its version, and whether the
/// 2.0+ command-line interface is available.
///
/// Detection strategy (sources verified against Windhawk's GitHub repo and its
/// winget-pkgs manifests, 2026-09):
/// - The standard installer is NSIS ("windhawk_setup.exe", InstallerType: nullsoft,
///   ElevationRequirement: elevationRequired) and installs the UI into
///   %ProgramFiles%\Windhawk (the binaries), with mod/engine data under
///   %ProgramData%\Windhawk. Portable installs live wherever the user extracts them.
/// - Uninstall registration: NSIS writes the "Windhawk" entry under
///   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall (DisplayVersion,
///   UninstallString, InstallLocation). HKCU is probed too because NSIS installers
///   can write per-user entries when run unelevated.
/// - windhawk-cli.exe ships next to windhawk.exe since 2.0; windhawk-cli.com is
///   also accepted by Rust's clap binary discovery, so probe both extensions.
/// </summary>
public sealed class WindhawkDetectionService
{
    private const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly string[] KnownInstallDirectories =
    {
        // Standard (non-portable) installer locations, Program Files first.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windhawk"),
        @"C:\Program Files\Windhawk",
        // Portable default when extracted from the installer's portable choice.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Windhawk"),
    };

    /// <summary>WindowsPrincipal-based elevation check (no-op app.manifest is assumed).</summary>
    public static bool IsRunningElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity != null && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public WindhawkInstallationInfo Detect()
    {
        // 1. Uninstall registry entries (authoritative for version + install dir).
        var info = DetectFromUninstallRegistry();

        // 2. Known paths (covers portable / registry entry missing).
        if (!info.IsInstalled)
            info = DetectFromKnownPaths();

        return info;
    }

    /// <summary>
    /// Scans both uninstall hives for a "Windhawk" entry. NSIS registers the
    /// standard install; the portable choice typically does not register.
    /// </summary>
    private static WindhawkInstallationInfo DetectFromUninstallRegistry()
    {
        foreach (var baseKey in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(baseKey, RegistryView.Registry64);
                using var uninstall = root.OpenSubKey(UninstallKeyPath);
                if (uninstall is null) continue;

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var subKey = uninstall.OpenSubKey(subKeyName);
                    var displayName = subKey?.GetValue("DisplayName") as string;
                    if (string.IsNullOrEmpty(displayName) ||
                        !displayName.Equals("Windhawk", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? version = subKey?.GetValue("DisplayVersion") as string;
                    string? installLocation = subKey?.GetValue("InstallLocation") as string;
                    string? uninstallString = subKey?.GetValue("UninstallString") as string;

                    // NSIS UninstallString points at the uninstaller exe, e.g.
                    // "C:\Program Files\Windhawk\uninstall.exe". Derive the install
                    // directory from it when InstallLocation is missing.
                    var installDir = ResolveInstallDirectory(installLocation, uninstallString);
                    var cli = FindCli(installDir);

                    return new WindhawkInstallationInfo
                    {
                        IsInstalled = true,
                        Version = string.IsNullOrWhiteSpace(version) ? null : version,
                        InstallDirectory = installDir,
                        CliPath = cli,
                        IsPortable = false,
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WindhawkDetection({baseKey}): {ex.Message}");
            }
        }

        return WindhawkInstallationInfo.NotInstalled;
    }

    private static WindhawkInstallationInfo DetectFromKnownPaths()
    {
        foreach (var dir in KnownInstallDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            string? exe = FindWindhawkExe(dir);
            if (exe is null) continue;

            var version = TryReadFileVersion(exe);
            return new WindhawkInstallationInfo
            {
                IsInstalled = true,
                Version = version,
                InstallDirectory = dir,
                CliPath = FindCli(dir),
                // ProgramData is the portable default root; Program Files is the standard install.
                IsPortable = dir.Contains("ProgramData", StringComparison.OrdinalIgnoreCase),
            };
        }

        return WindhawkInstallationInfo.NotInstalled;
    }

    private static string? ResolveInstallDirectory(string? installLocation, string? uninstallString)
    {
        if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
            return installLocation.TrimEnd('\\');

        if (!string.IsNullOrWhiteSpace(uninstallString))
        {
            // Quote-aware parse: "C:\...\uninstall.exe" or C:\...\uninstall.exe
            var candidate = uninstallString.Trim();
            if (candidate.StartsWith('"'))
            {
                int end = candidate.IndexOf('"', 1);
                if (end > 1) candidate = candidate[1..end];
            }
            else
            {
                int space = candidate.IndexOf(' ');
                if (space > 0) candidate = candidate[..space];
            }

            if (File.Exists(candidate))
                return Path.GetDirectoryName(candidate);
        }

        // Last resort: the standard install path.
        foreach (var dir in KnownInstallDirectories)
        {
            if (Directory.Exists(dir) && FindWindhawkExe(dir) is not null) return dir;
        }

        return null;
    }

    private static string? FindWindhawkExe(string directory)
    {
        foreach (var name in new[] { "windhawk.exe", "Windhawk.exe" })
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Windhawk 2.0 ships windhawk-cli.exe beside windhawk.exe. The Rust binary
    /// is also built as windhawk-cli.com (clap binary name variants), so both
    /// extensions are accepted.
    /// </summary>
    public static string? FindCli(string? installDirectory)
    {
        if (string.IsNullOrEmpty(installDirectory)) return null;
        foreach (var name in new[] { "windhawk-cli.exe", "windhawk-cli.com" })
        {
            var candidate = Path.Combine(installDirectory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? TryReadFileVersion(string exePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(exePath).FileVersion;
        }
        catch
        {
            return null;
        }
    }
}
