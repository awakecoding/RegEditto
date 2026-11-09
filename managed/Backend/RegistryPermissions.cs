using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RegistryEditor.Presentation.Backend;

// Documented ACLUI contract; Windows supplies the permissions UI, not Regedit resources.
[ComVisible(true), Guid("965FC360-16FF-11D0-91CB-00AA00BBB723"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[GeneratedComInterface]
public partial interface ISecurityInformation
{
    [PreserveSig] int GetObjectInformation(out SecurityObjectInfo info);
    [PreserveSig] int GetSecurity(uint requested, out IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] bool defaults);
    [PreserveSig] int SetSecurity(uint information, IntPtr descriptor);
    [PreserveSig] int GetAccessRights(IntPtr objectType, uint flags, out IntPtr access, out uint count, out uint defaultAccess);
    [PreserveSig] int MapGeneric(IntPtr objectType, ref byte aceFlags, ref uint mask);
    [PreserveSig] int GetInheritTypes(out IntPtr types, out uint count);
    [PreserveSig] int PropertySheetPageCallback(IntPtr window, uint message, uint page);
}

[StructLayout(LayoutKind.Sequential)]
public struct SecurityObjectInfo
{
    public uint Flags;
    public IntPtr Instance, ServerName, ObjectName, PageTitle;
    public Guid ObjectType;
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
[GeneratedComClass]
public sealed partial class RegistryPermissions : ISecurityInformation, IDisposable
{
    private readonly string _path;
    private readonly RegistryKey _key;
    private readonly List<IntPtr> _allocations = new();
    private readonly IntPtr _name, _server, _access, _inheritance;
    private readonly bool _canWriteDacl, _canWriteOwner;
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int NotImplemented = unchecked((int)0x80004001);

    public RegistryPermissions(string path)
    {
        _path = path;
        _key = RegistryBackend.Open(path, 0x20000); // READ_CONTROL only, not KEY_READ.
        _canWriteDacl = CanOpen(0x40000);
        _canWriteOwner = CanOpen(0x80000);
        var parsed = RegistryBackend.ParsePath(path);
        _name = String(path);
        _server = parsed.Computer is null ? IntPtr.Zero : String(parsed.Computer);
        var rights = new[] {
            (0xf003fu, "Full Control", 0x30000u), (0x20019u, "Read", 0x30000u),
            (1u, "Query Value", 0x10000u), (2u, "Set Value", 0x10000u),
            (4u, "Create Subkey", 0x10000u), (8u, "Enumerate Subkeys", 0x10000u),
            (16u, "Notify", 0x10000u), (32u, "Create Link", 0x10000u),
            (0x10000u, "Delete", 0x10000u), (0x40000u, "Write DAC", 0x10000u),
            (0x80000u, "Write Owner", 0x10000u), (0x20000u, "Read Control", 0x10000u)
        };
        _access = Allocate(Marshal.SizeOf<SecurityAccess>() * rights.Length);
        for (var i = 0; i < rights.Length; i++)
            Marshal.StructureToPtr(new SecurityAccess { Mask = rights[i].Item1, Name = String(rights[i].Item2), Flags = rights[i].Item3 },
                _access + i * Marshal.SizeOf<SecurityAccess>(), false);
        _inheritance = Allocate(Marshal.SizeOf<SecurityInheritance>() * 3);
        var inheritance = new[] { (0u, "This key only"), (2u, "This key and subkeys"), (10u, "Subkeys only") };
        for (var i = 0; i < inheritance.Length; i++)
            Marshal.StructureToPtr(new SecurityInheritance { Flags = inheritance[i].Item1, Name = String(inheritance[i].Item2) },
                _inheritance + i * Marshal.SizeOf<SecurityInheritance>(), false);
    }

    private bool CanOpen(int access)
    {
        try { using var key = RegistryBackend.Open(_path, access); return true; }
        catch (Win32Exception e) when (e.NativeErrorCode == 5) { return false; }
    }

    public unsafe void Show(IntPtr owner)
    {
        var pointer = ComInterfaceMarshaller<ISecurityInformation>.ConvertToUnmanaged(this);
        try {
            if (!Native.EditSecurity(owner, (IntPtr)pointer)) {
                var error = Marshal.GetLastPInvokeError();
                if (error != 0) throw new Win32Exception(error);
            }
        }
        finally { ComInterfaceMarshaller<ISecurityInformation>.Free(pointer); GC.KeepAlive(this); }
    }

    public int GetObjectInformation(out SecurityObjectInfo info)
    {
        // No elevation flags, privilege adjustment, recursive owner replacement, or audit editing.
        info = new SecurityObjectInfo { Flags = 4u | 16u | 1u | (_canWriteDacl ? 0u : 8u) | (_canWriteOwner ? 0u : 64u),
            ObjectName = _name, ServerName = _server };
        return 0;
    }

    public int GetSecurity(uint requested, out IntPtr descriptor, bool defaults)
    {
        descriptor = IntPtr.Zero;
        if (defaults) return NotImplemented;
        if ((requested & ~7u) != 0) return AccessDenied;
        return HResult(Native.GetSecurityInfo(_key.Handle, 4, requested, out _, out _, out _, out _, out descriptor));
    }

    public int SetSecurity(uint information, IntPtr descriptor)
    {
        if ((information & 8) != 0 || (information & 4) != 0 && !_canWriteDacl || (information & 3) != 0 && !_canWriteOwner)
            return AccessDenied;
        var access = ((information & 4) != 0 ? 0x40000 : 0) | ((information & 3) != 0 ? 0x80000 : 0);
        if (access == 0) return unchecked((int)0x80070057);
        try {
            using var key = RegistryBackend.Open(_path, access);
            IntPtr owner = IntPtr.Zero, group = IntPtr.Zero, dacl = IntPtr.Zero;
            if ((information & 1) != 0 && !Native.GetSecurityDescriptorOwner(descriptor, out owner, out _)) return HResult((uint)Marshal.GetLastPInvokeError());
            if ((information & 2) != 0 && !Native.GetSecurityDescriptorGroup(descriptor, out group, out _)) return HResult((uint)Marshal.GetLastPInvokeError());
            if ((information & 4) != 0 && !Native.GetSecurityDescriptorDacl(descriptor, out _, out dacl, out _)) return HResult((uint)Marshal.GetLastPInvokeError());
            return HResult(Native.SetSecurityInfo(key.Handle, 4, information, owner, group, dacl, IntPtr.Zero));
        }
        catch (Win32Exception e) { return HResult((uint)e.NativeErrorCode); }
    }

    public int GetAccessRights(IntPtr objectType, uint flags, out IntPtr access, out uint count, out uint defaultAccess)
    { access = _access; count = 12; defaultAccess = 1; return 0; }
    public int MapGeneric(IntPtr objectType, ref byte aceFlags, ref uint mask)
    {
        if ((mask & 0x80000000) != 0) mask |= 0x20019;
        if ((mask & 0x40000000) != 0) mask |= 0x20006;
        if ((mask & 0x20000000) != 0) mask |= 0x20019;
        if ((mask & 0x10000000) != 0) mask |= 0xf003f;
        mask &= 0x0fffffff;
        return 0;
    }
    public int GetInheritTypes(out IntPtr types, out uint count) { types = _inheritance; count = 3; return 0; }
    public int PropertySheetPageCallback(IntPtr window, uint message, uint page) => 0;
    private static int HResult(uint error) => error == 0 ? 0 : unchecked((int)(0x80070000 | error));
    private IntPtr Allocate(int size) { var pointer = Marshal.AllocHGlobal(size); _allocations.Add(pointer); return pointer; }
    private IntPtr String(string text) { var pointer = Marshal.StringToHGlobalUni(text); _allocations.Add(pointer); return pointer; }
    public void Dispose()
    {
        _key.Dispose();
        foreach (var pointer in _allocations) Marshal.FreeHGlobal(pointer);
        _allocations.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAccess { public IntPtr Guid; public uint Mask; public IntPtr Name; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityInheritance { public IntPtr Guid; public uint Flags; public IntPtr Name; }
    private static class Native
    {
        [DllImport("aclui.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EditSecurity(IntPtr owner, IntPtr information);
        [DllImport("advapi32.dll")]
        internal static extern uint GetSecurityInfo(SafeRegistryHandle handle, uint objectType, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")]
        internal static extern uint SetSecurityInfo(SafeRegistryHandle handle, uint objectType, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSecurityDescriptorOwner(IntPtr descriptor, out IntPtr owner, [MarshalAs(UnmanagedType.Bool)] out bool defaults);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSecurityDescriptorGroup(IntPtr descriptor, out IntPtr group, [MarshalAs(UnmanagedType.Bool)] out bool defaults);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr dacl, [MarshalAs(UnmanagedType.Bool)] out bool defaults);
    }
}
