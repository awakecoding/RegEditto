using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RegistryEditor.Presentation.Backend;

public sealed class RegistryWatcher : IDisposable
{
    private readonly RegistryKey _key;
    private readonly AutoResetEvent _changed = new(false);
    public RegistryWatcher(string path)
    {
        if (RegistryBackend.ParsePath(path).Computer is not null) throw new ArgumentException("Registry notifications are local only.");
        _key = RegistryBackend.Open(path, 16);
        try { Arm(); }
        catch (Win32Exception) { _key.Dispose(); _changed.Dispose(); throw; }
    }
    private void Arm()
    {
        var error = RegNotifyChangeKeyValue(_key.Handle, false, 0x1000000f, _changed.SafeWaitHandle, true);
        if (error != 0) throw new Win32Exception(error);
    }
    public Task WaitForChangeAsync(CancellationToken cancellation) => Task.Run(() => {
        var result = WaitHandle.WaitAny(new[] { cancellation.WaitHandle, _changed });
        if (result == 0) cancellation.ThrowIfCancellationRequested();
        Arm();
    }, cancellation);
    public void Dispose() { _key.Dispose(); _changed.Dispose(); }
    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool subtree, uint filter, SafeWaitHandle changed, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
