using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RegistryEditor.Presentation.Backend;
using Xunit;

namespace RegistryEditor.Backend.Tests;

public class RegistryPermissionsTests
{
    [Fact]
    public void GetSecurity_ReadsOwnerGroupAndDaclDescriptorWithoutChangingFixture()
    {
        using var scope = new RegistryScope();
        using var permissions = new RegistryPermissions(scope.Path);

        var result = permissions.GetSecurity(7, out var descriptor, defaults: false);

        Assert.Equal(0, result);
        Assert.NotEqual(IntPtr.Zero, descriptor);
        try {
            Assert.True(GetSecurityDescriptorOwner(descriptor, out var owner, out var ownerDefaulted));
            Assert.NotEqual(IntPtr.Zero, owner);
            Assert.True(GetSecurityDescriptorGroup(descriptor, out var group, out var groupDefaulted));
            Assert.NotEqual(IntPtr.Zero, group);
            Assert.True(GetSecurityDescriptorDacl(descriptor, out var daclPresent, out var dacl, out var daclDefaulted));
            Assert.True(daclPresent);
            Assert.NotEqual(IntPtr.Zero, dacl);
            Assert.False(ownerDefaulted);
            Assert.False(groupDefaulted);
            Assert.False(daclDefaulted);
        }
        finally {
            Assert.Equal(IntPtr.Zero, LocalFree(descriptor));
        }
    }

    [Fact]
    public void GetSecurity_RejectsDefaultAndUnsupportedOrSaclRequests()
    {
        using var scope = new RegistryScope();
        using var permissions = new RegistryPermissions(scope.Path);

        Assert.Equal(unchecked((int)0x80004001), permissions.GetSecurity(7, out var defaultDescriptor, defaults: true));
        Assert.Equal(IntPtr.Zero, defaultDescriptor);
        Assert.Equal(unchecked((int)0x80070005), permissions.GetSecurity(8, out var unsupportedDescriptor, defaults: false));
        Assert.Equal(IntPtr.Zero, unsupportedDescriptor);
        Assert.Equal(unchecked((int)0x80070005), permissions.GetSecurity(0x10, out var saclDescriptor, defaults: false));
        Assert.Equal(IntPtr.Zero, saclDescriptor);
    }

    [Fact]
    public void GetObjectInformation_ReportsFixtureNameServerAndAccurateReadOnlyFlags()
    {
        using var scope = new RegistryScope();
        using var permissions = new RegistryPermissions(scope.Path);

        Assert.Equal(0, permissions.GetObjectInformation(out var info));

        var expectedFlags = 4u | 16u | 1u;
        if (!CanOpen(scope.Path, 0x40000)) expectedFlags |= 8u;
        if (!CanOpen(scope.Path, 0x80000)) expectedFlags |= 64u;
        Assert.Equal(expectedFlags, info.Flags);
        Assert.Equal(scope.Path, Marshal.PtrToStringUni(info.ObjectName));
        Assert.Equal(IntPtr.Zero, info.ServerName);
        Assert.Equal(IntPtr.Zero, info.Instance);
        Assert.Equal(IntPtr.Zero, info.PageTitle);
        Assert.Equal(Guid.Empty, info.ObjectType);
    }

    [Fact]
    public void GetAccessRights_ExposesEveryRegistryMaskAndRequiredAccessFlag()
    {
        using var scope = new RegistryScope();
        using var permissions = new RegistryPermissions(scope.Path);

        Assert.Equal(0, permissions.GetAccessRights(IntPtr.Zero, uint.MaxValue, out var access, out var count, out var defaultAccess));

        var expected = new (uint Mask, string Name, uint Flags)[] {
            (0xf003f, "Full Control", 0x30000), (0x20019, "Read", 0x30000),
            (1, "Query Value", 0x10000), (2, "Set Value", 0x10000),
            (4, "Create Subkey", 0x10000), (8, "Enumerate Subkeys", 0x10000),
            (16, "Notify", 0x10000), (32, "Create Link", 0x10000),
            (0x10000, "Delete", 0x10000), (0x40000, "Write DAC", 0x10000),
            (0x80000, "Write Owner", 0x10000), (0x20000, "Read Control", 0x10000)
        };
        Assert.Equal(12u, count);
        Assert.Equal(1u, defaultAccess);
        for (var index = 0; index < expected.Length; index++) {
            var entry = Marshal.PtrToStructure<SecurityAccessEntry>(access + index * Marshal.SizeOf<SecurityAccessEntry>());
            Assert.Equal(IntPtr.Zero, entry.Guid);
            Assert.Equal(expected[index].Mask, entry.Mask);
            Assert.Equal(expected[index].Name, Marshal.PtrToStringUni(entry.Name));
            Assert.Equal(expected[index].Flags, entry.Flags);
        }
    }

    [Fact]
    public void GetInheritTypes_ExposesAllThreeInheritanceModes()
    {
        using var scope = new RegistryScope();
        using var permissions = new RegistryPermissions(scope.Path);

        Assert.Equal(0, permissions.GetInheritTypes(out var types, out var count));

        var expected = new[] { (Flags: 0u, Name: "This key only"), (Flags: 2u, Name: "This key and subkeys"), (Flags: 10u, Name: "Subkeys only") };
        Assert.Equal(3u, count);
        for (var index = 0; index < expected.Length; index++) {
            var entry = Marshal.PtrToStructure<SecurityInheritanceEntry>(types + index * Marshal.SizeOf<SecurityInheritanceEntry>());
            Assert.Equal(IntPtr.Zero, entry.Guid);
            Assert.Equal(expected[index].Flags, entry.Flags);
            Assert.Equal(expected[index].Name, Marshal.PtrToStringUni(entry.Name));
        }
    }

    [Theory]
    [InlineData(0x80000000u, 0x20019u)]
    [InlineData(0x40000000u, 0x20006u)]
    [InlineData(0x20000000u, 0x20019u)]
    [InlineData(0x10000000u, 0xf003fu)]
    [InlineData(0u, 0x1234u)]
    public void MapGeneric_MapsEachGenericRightAndPreservesConcreteMaskAndAceFlags(uint input, uint expected)
    {
        using var scope = new RegistryScope();
        using var permissions = new RegistryPermissions(scope.Path);
        byte aceFlags = 0x5a;
        var mask = input == 0 ? 0x1234u : input;

        Assert.Equal(0, permissions.MapGeneric(IntPtr.Zero, ref aceFlags, ref mask));

        Assert.Equal(expected, mask);
        Assert.Equal((byte)0x5a, aceFlags);
    }

    [Fact]
    public void MapGeneric_MapsCombinedRightsAndRetainsConcreteBits()
    {
        using var scope = new RegistryScope();
        using var permissions = new RegistryPermissions(scope.Path);
        byte aceFlags = 0x81;
        var mask = 0xf0000000u | 0x20u;

        permissions.MapGeneric(IntPtr.Zero, ref aceFlags, ref mask);

        Assert.Equal(0xf003fu, mask);
        Assert.Equal((byte)0x81, aceFlags);
    }

    [Fact]
    public void PropertySheetPageCallback_IsAStableNoOp()
    {
        using var scope = new RegistryScope();
        using var permissions = new RegistryPermissions(scope.Path);

        Assert.Equal(0, permissions.PropertySheetPageCallback(IntPtr.Zero, uint.MaxValue, uint.MaxValue));
    }

    [Fact]
    public void ComContract_PreservesDocumentedInterfaceOrderMarshalingAndLayout()
    {
        var interfaceType = typeof(ISecurityInformation);
        Assert.Equal(new Guid("965FC360-16FF-11D0-91CB-00AA00BBB723"), interfaceType.GUID);
        Assert.Equal(ComInterfaceType.InterfaceIsIUnknown, interfaceType.GetCustomAttribute<InterfaceTypeAttribute>()!.Value);
        Assert.True(interfaceType.GetCustomAttribute<ComVisibleAttribute>()!.Value);
        Assert.NotNull(interfaceType.GetCustomAttribute<System.Runtime.InteropServices.Marshalling.GeneratedComInterfaceAttribute>());
        Assert.NotNull(typeof(RegistryPermissions).GetCustomAttribute<System.Runtime.InteropServices.Marshalling.GeneratedComClassAttribute>());

        var methods = interfaceType.GetMethods().OrderBy(method => method.MetadataToken).ToArray();
        Assert.Equal(new[] {
            "GetObjectInformation", "GetSecurity", "SetSecurity", "GetAccessRights",
            "MapGeneric", "GetInheritTypes", "PropertySheetPageCallback"
        }, methods.Select(method => method.Name));
        Assert.All(methods, method => Assert.NotNull(method.GetCustomAttribute<PreserveSigAttribute>()));
        Assert.Equal(UnmanagedType.Bool, methods.Single(method => method.Name == "GetSecurity").GetParameters()[2].GetCustomAttribute<MarshalAsAttribute>()!.Value);

        Assert.Equal(ClassInterfaceType.None, typeof(RegistryPermissions).GetCustomAttribute<ClassInterfaceAttribute>()!.Value);
        Assert.True(typeof(RegistryPermissions).GetCustomAttribute<ComVisibleAttribute>()!.Value);
        var pointerSize = IntPtr.Size;
        var firstPointerOffset = (4 + pointerSize - 1) / pointerSize * pointerSize;
        Assert.Equal(0, Marshal.OffsetOf<SecurityObjectInfo>(nameof(SecurityObjectInfo.Flags)).ToInt32());
        Assert.Equal(firstPointerOffset, Marshal.OffsetOf<SecurityObjectInfo>(nameof(SecurityObjectInfo.Instance)).ToInt32());
        Assert.Equal(firstPointerOffset + pointerSize, Marshal.OffsetOf<SecurityObjectInfo>(nameof(SecurityObjectInfo.ServerName)).ToInt32());
        Assert.Equal(firstPointerOffset + pointerSize * 2, Marshal.OffsetOf<SecurityObjectInfo>(nameof(SecurityObjectInfo.ObjectName)).ToInt32());
        Assert.Equal(firstPointerOffset + pointerSize * 3, Marshal.OffsetOf<SecurityObjectInfo>(nameof(SecurityObjectInfo.PageTitle)).ToInt32());
        var guidOffset = firstPointerOffset + pointerSize * 4;
        Assert.Equal(guidOffset, Marshal.OffsetOf<SecurityObjectInfo>(nameof(SecurityObjectInfo.ObjectType)).ToInt32());
        Assert.Equal((guidOffset + 16 + pointerSize - 1) / pointerSize * pointerSize, Marshal.SizeOf<SecurityObjectInfo>());
        Assert.Equal(LayoutKind.Sequential, typeof(SecurityObjectInfo).StructLayoutAttribute!.Value);
    }

    private static bool CanOpen(string path, int access)
    {
        try {
            using var key = RegistryBackend.Open(path, access);
            return true;
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 5) {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAccessEntry
    {
        public IntPtr Guid;
        public uint Mask;
        public IntPtr Name;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityInheritanceEntry
    {
        public IntPtr Guid;
        public uint Flags;
        public IntPtr Name;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorOwner(IntPtr descriptor, out IntPtr owner, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorGroup(IntPtr descriptor, out IntPtr group, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr dacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
