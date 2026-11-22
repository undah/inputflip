using System.Runtime.InteropServices;
using InputFlip.Core.Usb;

namespace InputFlip.Core.Interop;

/// <summary>Lists the USB devices plugged in now (SetupAPI), with the names Windows shows for them.</summary>
public static class UsbDevices
{
    /// <summary>GUID_DEVINTERFACE_USB_DEVICE: one interface per USB device, hubs included. Also used for plug notices.</summary>
    public static readonly Guid DeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    private const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint SPDRP_DEVICEDESC = 0x00, SPDRP_FRIENDLYNAME = 0x0C;

    // DEVPKEY_Device_BusReportedDeviceDesc: the name the device gives itself ("USB Receiver"), often better than
    // Windows' generic one ("USB Composite Device").
    private static readonly DEVPROPKEY BusReportedDeviceDesc = new() { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };

    /// <summary>Plugged-in USB devices, hubs left out, one entry per device id, sorted by name.</summary>
    public static IReadOnlyList<UsbDeviceInfo> ListPresent()
    {
        var result = new Dictionary<string, UsbDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        Guid guid = DeviceInterface;
        IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return [];
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                string? instance = InstanceId(set, ref info);
                if (UsbDeviceId.TryParse(instance) is not { } id) continue;
                string name = Property(set, ref info) ?? RegistryText(set, ref info, SPDRP_FRIENDLYNAME) ?? RegistryText(set, ref info, SPDRP_DEVICEDESC) ?? id.ToString();
                if (name.Contains("hub", StringComparison.OrdinalIgnoreCase)) continue;
                result.TryAdd(id.ToString(), new UsbDeviceInfo(id, name.Trim()));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result.Values.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? InstanceId(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var buffer = new char[512];
        return SetupDiGetDeviceInstanceId(set, ref info, buffer, buffer.Length, out int size) ? new string(buffer, 0, Math.Max(0, size - 1)) : null;
    }

    private static string? Property(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var key = BusReportedDeviceDesc;
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceProperty(set, ref info, ref key, out _, buffer, buffer.Length, out int size, 0)) return null;
        string text = System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Max(0, size)).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? RegistryText(IntPtr set, ref SP_DEVINFO_DATA info, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, buffer, (uint)buffer.Length, out uint size)) return null;
        string text = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA info);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA info, char[] buffer, int bufferSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA info, uint property, out uint regType, byte[] buffer, uint bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
    private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key, out uint type, byte[] buffer, int bufferSize, out int requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
