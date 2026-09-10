using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace stellarisKIT.Services;

/// <summary>
/// "Start with Windows" for both deployment modes. The rules engine lives
/// in-process (WMI process-start watcher), so rules only apply while the app
/// runs — this keeps it running across logons.
///
/// Unpackaged: HKCU\...\CurrentVersion\Run value (per the Run-keys doc —
/// command lines there run at user logon).
/// Packaged (MSIX): the exe path is versioned under WindowsApps, so a static
/// Run path would rot on every update — uses the windows.startupTask
/// extension (TaskId below, declared in Package.appxmanifest) instead.
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "stellarisKIT";
    private const string StartupTaskId = "StellarisKitStartup";
    private const int AppmodelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);

    public static bool IsPackaged
    {
        get
        {
            try
            {
                uint len = 0;
                return GetCurrentPackageFullName(ref len, IntPtr.Zero) == ErrorInsufficientBuffer;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<bool> IsEnabledAsync()
    {
        if (IsPackaged)
        {
            return await IsStartupTaskEnabledAsync();
        }

        string? current = ReadRunValue(RunValueName);
        if (current == null)
        {
            return false;
        }

        // Self-heal: dev/unpackaged exe paths move between builds.
        string exe = QuotedExePath();
        if (!string.Equals(current, exe, StringComparison.OrdinalIgnoreCase))
        {
            WriteRunValue(RunValueName, exe);
        }
        return true;
    }

    public async Task<bool> SetEnabledAsync(bool enabled)
    {
        if (IsPackaged)
        {
            return await SetStartupTaskEnabledAsync(enabled);
        }

        try
        {
            if (enabled)
            {
                WriteRunValue(RunValueName, QuotedExePath());
            }
            else
            {
                DeleteRunValue(RunValueName);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string? ReadRunValue(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }

    internal static void WriteRunValue(string name, string command)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new InvalidOperationException("Could not open the Run registry key.");
        key.SetValue(name, command, RegistryValueKind.String);
    }

    internal static void DeleteRunValue(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch
        {
        }
    }

    internal static string QuotedExePath()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                path = proc.MainModule?.FileName;
            }
            catch
            {
            }
        }
        return $"\"{path}\"";
    }

    private static async Task<bool> IsStartupTaskEnabledAsync()
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            return task.State == Windows.ApplicationModel.StartupTaskState.Enabled;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> SetStartupTaskEnabledAsync(bool enabled)
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            if (!enabled)
            {
                task.Disable();
                return true;
            }

            var state = await task.RequestEnableAsync();
            return state == Windows.ApplicationModel.StartupTaskState.Enabled;
        }
        catch
        {
            return false;
        }
    }
}
