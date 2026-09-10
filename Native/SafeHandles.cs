using System;
using System.Runtime.InteropServices;

namespace stellarisKIT.Native;

/// <summary>Guaranteed-cleanup wrapper for process handles. Never use raw <see cref="IntPtr"/>.</summary>
public sealed class SafeProcessHandle : SafeHandle
{
    public SafeProcessHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

    protected override bool ReleaseHandle() => NativeMethods.Handles.CloseHandle(handle);
}

/// <summary>Guaranteed-cleanup wrapper for thread handles. Never use raw <see cref="IntPtr"/>.</summary>
public sealed class SafeThreadHandle : SafeHandle
{
    public SafeThreadHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

    protected override bool ReleaseHandle() => NativeMethods.Handles.CloseHandle(handle);
}
