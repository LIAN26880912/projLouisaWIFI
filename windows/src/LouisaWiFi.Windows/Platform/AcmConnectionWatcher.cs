using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LouisaWiFi.Windows.Platform;

/// <summary>
/// Waits for the ACM connection_complete notification of one profile.
/// ManagedNativeWifi 3.1.0 treats the non-final connection_attempt_fail as failure, and its
/// listener also registers MSM, which needs the wiFiControl capability. ACM alone does not.
/// </summary>
internal sealed class AcmConnectionWatcher : IDisposable
{
    internal const uint AcmSource = 0x8;
    internal const uint ConnectionStart = 9;
    internal const uint ConnectionComplete = 10;

    private readonly Guid interfaceId;
    private readonly string profileName;
    private readonly TaskCompletionSource<bool> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly NotificationCallback callback;
    private IntPtr handle;
    private volatile bool started;

    internal AcmConnectionWatcher(Guid interfaceId, string profileName)
    {
        this.interfaceId = interfaceId;
        this.profileName = profileName;
        callback = OnNative;
    }

    /// <summary>Register before requesting the connection so no notification is missed.</summary>
    public static AcmConnectionWatcher Register(Guid interfaceId, string profileName)
    {
        var watcher = new AcmConnectionWatcher(interfaceId, profileName);
        try
        {
            Check(WlanOpenHandle(2, IntPtr.Zero, out _, out watcher.handle));
            Check(WlanRegisterNotification(
                watcher.handle, AcmSource, false, watcher.callback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            return watcher;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
    }

    /// <returns>Final result; false on timeout. Throws when cancelled.</returns>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        return finished == completion.Task && await completion.Task;
    }

    internal void OnNotification(Guid source, uint code, string? profile, uint reasonCode)
    {
        if (source != interfaceId || profile != profileName) return;
        // A late completion of an earlier attempt for the same profile arrives before our start.
        if (code == ConnectionStart) started = true;
        else if (code == ConnectionComplete && started) completion.TrySetResult(reasonCode == 0);
    }

    private void OnNative(IntPtr data, IntPtr context)
    {
        try
        {
            var notification = Marshal.PtrToStructure<NotificationData>(data);
            if (notification.Source != AcmSource ||
                notification.Code is not (ConnectionStart or ConnectionComplete) ||
                notification.Data == IntPtr.Zero ||
                notification.DataSize < Marshal.SizeOf<ConnectionData>())
                return;
            var connection = Marshal.PtrToStructure<ConnectionData>(notification.Data);
            OnNotification(notification.InterfaceId, notification.Code, connection.ProfileName, connection.ReasonCode);
        }
        catch (Exception error)
        {
            // An exception escaping into wlanapi would terminate the process.
            completion.TrySetException(error);
        }
    }

    // Never call from the callback: WlanCloseHandle waits for running callbacks.
    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        WlanCloseHandle(handle, IntPtr.Zero);
        handle = IntPtr.Zero;
    }

    private static void Check(uint result)
    {
        if (result != 0) throw new Win32Exception((int)result, "Windows 無法監聽 Wi-Fi 連線結果。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotificationData
    {
        public uint Source;
        public uint Code;
        public Guid InterfaceId;
        public uint DataSize;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ConnectionData
    {
        public int Mode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public uint SsidLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Ssid;
        public int BssType;
        public int SecurityEnabled;
        public uint ReasonCode;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void NotificationCallback(IntPtr data, IntPtr context);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanRegisterNotification(
        IntPtr handle, uint source, [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicate,
        NotificationCallback callback, IntPtr context, IntPtr reserved, IntPtr previousSource);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
}
