using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace stellarisKIT.Native;

/// <summary>
/// Resolves a thread entry point through DbgHelp when symbols are available.
/// Uses the Unicode (W) DbgHelp APIs consistently with an explicit
/// SYMBOL_INFOW layout (88-byte header, WCHAR name at offset 88).
/// Mixing SymInitializeW with the ANSI SymFromAddr was the reason symbols
/// silently never resolved and every DWM thread fell back to "(unnamed)".
/// DWM's role names are private/build-specific, so the resolver only emits a
/// friendly role when the symbol itself contains evidence for that role;
/// otherwise it returns the raw symbol so callers can show
/// "module!symbol" instead of a blank description.
/// </summary>
internal sealed class ThreadSymbolResolver : IDisposable
{
    private const uint SymoptUndname = 0x00000002;
    private const uint SymoptDeferredLoads = 0x00000004;
    private const uint SymoptFailCriticalErrors = 0x00000200;
    private const uint SymoptLoadLines = 0x00000010;
    private const uint SymoptCaseInsensitive = 0x00000001;

    // SYMBOL_INFOW layout (verified against live DbgHelp output: reading
    // the name at 88 truncates the first 2 WCHARs, e.g. "RtlSet..." shows
    // as "lSet..."). Header fields: SizeOfStruct@0, TypeIndex@4,
    // Reserved@8 (16 bytes), Index@24, Size@28, ModBase@32, Flags@40,
    // Value@48, Address@56, Register@64, Scope@68, Tag@72, NameLen@76
    // (WCHAR count), MaxNameLen@80 (WCHAR count), Name@84 (WCHARs).
    // SizeOfStruct itself must still be written as 88 (native sizeof
    // includes padding). NameLen/MaxNameLen are counts of WCHARs, not bytes.
    private const int SymbolHeaderSize = 88;
    private const int SymbolNameLenOffset = 76;
    private const int SymbolMaxNameLenOffset = 80;
    private const int SymbolNameOffset = 84;
    private const int SymbolNameCapacity = 1024;

    private readonly Dictionary<long, string?> _cache = new();
    private readonly HashSet<string> _loadedModules = new(StringComparer.OrdinalIgnoreCase);
    private SafeProcessHandle? _processHandle;
    private int _pid;
    private bool _initialized;

    /// <summary>
    /// Returns the raw undecorated symbol for the address, or null when
    /// symbols are unavailable. DWM-only: symbol downloads are expensive
    /// and only DWM needs role classification.
    /// </summary>
    internal string? ResolveRaw(Process process, long address)
    {
        if (address == 0 || !string.Equals(process.ProcessName, "dwm", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (_pid != process.Id)
        {
            Reset();
            _pid = process.Id;
            _processHandle = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryInformation | NativeMethods.ProcessAccess.VirtualMemoryRead,
                false,
                (uint)_pid);
            if (_processHandle.IsInvalid)
            {
                Reset();
                return null;
            }
        }

        if (_cache.TryGetValue(address, out string? cached))
        {
            return cached;
        }

        try
        {
            if (!_initialized)
            {
                string cache = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "stellarisKIT", "SymbolCache");
                Directory.CreateDirectory(cache);
                string symbolPath = $"srv*{cache}*https://msdl.microsoft.com/download/symbols";
                if (!SymInitialize(_processHandle!, symbolPath, false))
                {
                    _cache[address] = null;
                    return null;
                }

                SymSetOptions(
                    SymGetOptions() | SymoptUndname | SymoptDeferredLoads |
                    SymoptFailCriticalErrors | SymoptLoadLines | SymoptCaseInsensitive);
                _initialized = true;
            }

            foreach (ProcessModule module in process.Modules)
            {
                string moduleKey = module.FileName;
                if (!_loadedModules.Add(moduleKey))
                {
                    continue;
                }

                SymLoadModuleEx(
                    _processHandle!,
                    IntPtr.Zero,
                    module.FileName,
                    module.ModuleName,
                    (ulong)module.BaseAddress.ToInt64(),
                    (uint)Math.Max(0, module.ModuleMemorySize),
                    IntPtr.Zero,
                    0);
            }

            string? symbol = TryGetSymbol(address);
            _cache[address] = symbol;
            return symbol;
        }
        catch
        {
            _cache[address] = null;
            return null;
        }
    }

    /// <summary>
    /// Backwards-compatible entry point: returns a friendly DWM role when
    /// the raw symbol contains evidence for one, else the raw symbol itself
    /// (callers previously treated any non-null as a description).
    /// Returns null when no symbol is available.
    /// </summary>
    internal string? Resolve(Process process, long address)
    {
        string? raw = ResolveRaw(process, address);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return ClassifyDwmSymbol(raw) ?? raw;
    }

    internal static string? ClassifyDwmSymbol(string symbol)
    {
        // Precision over recall: DbgHelp often returns only the nearest
        // EXPORT with a huge displacement (e.g. 380KB past
        // CompositionEngine_Uninitialize), so generic namespace keywords
        // ("compositionengine", bare "event"/"rpc"/"sensor") triple-label
        // unrelated threads — observed 3x "DWM Compositor Thread" on one
        // machine. Only tight, role-specific evidence may label a thread;
        // anything else stays unnamed per spec.
        string value = symbol.ToLowerInvariant();
        if (value.Contains("masterinput") || value.Contains("master_input") || value.Contains("inputthread"))
            return "DWM Master Input Thread";
        if (value.Contains("kernelsensor") || value.Contains("kernel_sensor"))
            return "DWM Kernel Sensor Thread";
        if (value.Contains("manipulation") || value.Contains("dmanip"))
            return "DWM Manipulation Thread";
        if (value.Contains("compositor") || value.Contains("compositionthread"))
            return "DWM Compositor Thread";
        if (value.Contains("token"))
            return "DWM Token Thread";
        if (value.Contains("lpc") || value.Contains("initializeport") || value.Contains("portthread"))
            return "DWM LPC Port Thread";
        if (value.Contains("dwmevent") || value.Contains("eventthread"))
            return "uDWM Event Thread";
        return null;
    }

    private string? TryGetSymbol(long address)
    {
        int bufferBytes = SymbolNameOffset + SymbolNameCapacity * 2;
        IntPtr memory = Marshal.AllocHGlobal(bufferBytes);
        try
        {
            for (int i = 0; i < bufferBytes; i++)
                Marshal.WriteByte(memory, i, 0);

            Marshal.WriteInt32(memory, 0, SymbolHeaderSize);
            Marshal.WriteInt32(memory, SymbolMaxNameLenOffset, SymbolNameCapacity);
            if (!SymFromAddr(_processHandle!, (ulong)address, out ulong displacement, memory))
            {
                return null;
            }

            int nameLen = Marshal.ReadInt32(memory, SymbolNameLenOffset);
            if (nameLen <= 0 || nameLen > SymbolNameCapacity)
            {
                return null;
            }

            string? symbol = Marshal.PtrToStringUni(memory + SymbolNameOffset, nameLen);
            return string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim();
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private void Reset()
    {
        if (_initialized && _processHandle is not null && !_processHandle.IsInvalid)
        {
            try { SymCleanup(_processHandle); } catch { }
        }
        _initialized = false;
        _loadedModules.Clear();
        _cache.Clear();
        _processHandle?.Dispose();
        _processHandle = null;
    }

    public void Dispose() => Reset();

    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SymInitialize(SafeProcessHandle process, string? userSearchPath, bool invadeProcess);

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool SymCleanup(SafeProcessHandle process);

    [DllImport("dbghelp.dll")]
    private static extern uint SymSetOptions(uint options);

    [DllImport("dbghelp.dll")]
    private static extern uint SymGetOptions();

    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong SymLoadModuleEx(
        SafeProcessHandle process,
        IntPtr file,
        string imageName,
        string? moduleName,
        ulong baseOfDll,
        uint dllSize,
        IntPtr data,
        uint flags);

    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SymFromAddr(
        SafeProcessHandle process,
        ulong address,
        out ulong displacement,
        IntPtr symbol);
}
