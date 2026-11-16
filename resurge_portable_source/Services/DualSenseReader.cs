using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResurgeLauncherCSharp.Services;

/// <summary>
/// Reads a PS5 DualSense (USB or Bluetooth) so the Controller page can assign buttons by pressing
/// them. The game itself gets DualSense support from the Whiteout runtime.
/// </summary>
internal static class DualSenseReader
{
    private static readonly object Gate = new();
    private static HashSet<string> _held = [];
    private static Thread? _thread;
    private static volatile bool _connected;
    private static DateTime _nextScan = DateTime.MinValue;

    public static bool Connected
    {
        get
        {
            EnsureStarted();
            return _connected;
        }
    }

    public static HashSet<string> Pressed()
    {
        EnsureStarted();
        lock (Gate) return [.. _held];
    }

    private static void EnsureStarted()
    {
        if (_thread is { IsAlive: true } || DateTime.UtcNow < _nextScan) return;
        _nextScan = DateTime.UtcNow.AddSeconds(2);
        string? path = FindDualSense();
        if (path is null) return;
        _thread = new Thread(() => ReadLoop(path)) { IsBackground = true, Name = "DualSense" };
        _thread.Start();
    }

    private static void ReadLoop(string path)
    {
        try
        {
            using var handle = CreateFile(path, 0x80000000 /* GENERIC_READ */, 3, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (handle.IsInvalid) return;
            using var stream = new FileStream(handle, FileAccess.Read, 1, false);
            _connected = true;
            var buffer = new byte[128];
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                var held = Parse(buffer, read);
                if (held is not null)
                    lock (Gate) _held = held;
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException) { }
        finally
        {
            _connected = false;
            lock (Gate) _held = [];
        }
    }

    private static HashSet<string>? Parse(byte[] r, int length)
    {
        int l2, r2, b0, b1, b2;
        if (r[0] == 0x01 && length >= 64) { l2 = r[5]; r2 = r[6]; b0 = r[8]; b1 = r[9]; b2 = r[10]; }        // USB
        else if (r[0] == 0x31 && length >= 12) { l2 = r[6]; r2 = r[7]; b0 = r[9]; b1 = r[10]; b2 = r[11]; }  // Bluetooth full
        else if (r[0] == 0x01 && length >= 10) { l2 = r[8]; r2 = r[9]; b0 = r[5]; b1 = r[6]; b2 = r[7]; }    // Bluetooth simple
        else return null;

        var held = new HashSet<string>();
        int hat = b0 & 0x0F;
        if (hat is 0 or 1 or 7) held.Add("DPAD_UP");
        if (hat is 3 or 4 or 5) held.Add("DPAD_DOWN");
        if (hat is 5 or 6 or 7) held.Add("DPAD_LEFT");
        if (hat is 1 or 2 or 3) held.Add("DPAD_RIGHT");
        if ((b0 & 0x10) != 0) held.Add("BUTTON_X");      // square
        if ((b0 & 0x20) != 0) held.Add("BUTTON_A");      // cross
        if ((b0 & 0x40) != 0) held.Add("BUTTON_B");      // circle
        if ((b0 & 0x80) != 0) held.Add("BUTTON_Y");      // triangle
        if ((b1 & 0x01) != 0) held.Add("BUTTON_LSHLDR");
        if ((b1 & 0x02) != 0) held.Add("BUTTON_RSHLDR");
        if ((b1 & 0x10) != 0 || (b2 & 0x02) != 0) held.Add("BUTTON_BACK");
        if ((b1 & 0x20) != 0) held.Add("BUTTON_START");
        if ((b1 & 0x40) != 0) held.Add("BUTTON_LSTICK");
        if ((b1 & 0x80) != 0) held.Add("BUTTON_RSTICK");
        if (l2 > 120) held.Add("BUTTON_LTRIG");
        if (r2 > 120) held.Add("BUTTON_RTRIG");
        return held;
    }

    // ------------------------------------------------------------------ device discovery

    private static string? FindDualSense()
    {
        HidD_GetHidGuid(out Guid hid);
        IntPtr set = SetupDiGetClassDevs(ref hid, IntPtr.Zero, IntPtr.Zero, 0x12 /* PRESENT | DEVICEINTERFACE */);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var item = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref item); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
                if (size == 0 || size > 4096) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, 8); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W on x64
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref item, detail, size, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    string lower = path.ToLowerInvariant();
                    if (lower.Contains("vid_054c") && (lower.Contains("pid_0ce6") || lower.Contains("pid_0df2")) && IsGamepad(path))
                        return path;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return null;
    }

    private static bool IsGamepad(string path)
    {
        using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out IntPtr data)) return false;
        try
        {
            HidP_GetCaps(data, out HIDP_CAPS caps);
            return caps.UsagePage == 1 && caps.Usage == 5;
        }
        finally { HidD_FreePreparsedData(data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
