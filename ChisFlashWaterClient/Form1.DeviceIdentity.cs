using System.Management;
using System.Text.RegularExpressions;

namespace ChisFlashWaterClient;

public partial class Form1
{
    private static bool UsbDeviceIdMatches(string? pnpDeviceId, string vid, string pid, string? usbInterface = null)
    {
        string[] parts = (pnpDeviceId ?? string.Empty).Split('\\');
        if (parts.Length < 2 || !string.Equals(parts[0], "USB", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] identifiers = parts[1].Split('&');
        return identifiers.Contains("VID_" + vid, StringComparer.OrdinalIgnoreCase) &&
            identifiers.Contains("PID_" + pid, StringComparer.OrdinalIgnoreCase) &&
            (usbInterface is null || identifiers.Contains(usbInterface, StringComparer.OrdinalIgnoreCase));
    }

    private static bool HasWaterLinkProductIdentity(string? productName) =>
        !string.IsNullOrWhiteSpace(productName) && Regex.IsMatch(productName,
            @"(?<![\p{L}\p{N}])Water[\s_-]*Link(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string ReadUsbProductName(ManagementObject device, string pnpDeviceId)
    {
        var identity = ReadUsbDeviceProperties(device);
        return ResolveUsbProductName(pnpDeviceId, identity.ProductName, identity.ParentId, ReadParentUsbDeviceProperties);
    }

    private static string ResolveUsbProductName(string pnpDeviceId, string? productName, string? parentId,
        Func<string, (string? ProductName, string? ParentId)> readParent)
    {
        string result = productName ?? string.Empty;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pnpDeviceId };
        for (int depth = 0; depth < 3 &&
            UsbDeviceIdMatches(parentId, TargetUsbVid, TargetUsbPid) && visited.Add(parentId!); depth++)
        {
            var identity = readParent(parentId!);
            if (!string.IsNullOrWhiteSpace(identity.ProductName))
                result = identity.ProductName;
            parentId = identity.ParentId;
        }
        return result;
    }

    private static (string? ProductName, string? ParentId) ReadUsbDeviceProperties(ManagementObject device)
    {
        try
        {
            using ManagementBaseObject input = device.GetMethodParameters("GetDeviceProperties");
            input["devicePropertyKeys"] = new[] { "DEVPKEY_Device_BusReportedDeviceDesc", "DEVPKEY_Device_Parent" };
            using ManagementBaseObject output = device.InvokeMethod("GetDeviceProperties", input, null);
            if (Convert.ToUInt32(output["ReturnValue"]) != 0 ||
                output["deviceProperties"] is not ManagementBaseObject[] properties)
                return default;

            string? productName = null;
            string? parentId = null;
            foreach (ManagementBaseObject property in properties)
            {
                using (property)
                {
                    string? key = property["KeyName"]?.ToString();
                    if (key == "DEVPKEY_Device_BusReportedDeviceDesc")
                        productName = property["Data"] as string;
                    else if (key == "DEVPKEY_Device_Parent")
                        parentId = property["Data"] as string;
                }
            }
            return (productName, parentId);
        }
        catch
        {
            return default;
        }
    }

    private static (string? ProductName, string? ParentId) ReadParentUsbDeviceProperties(string pnpDeviceId)
    {
        try
        {
            string escapedId = pnpDeviceId.Replace("\\", "\\\\").Replace("'", "\\'");
            using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID = '{escapedId}'");
            using ManagementObjectCollection devices = searcher.Get();
            foreach (ManagementObject device in devices)
            {
                using (device)
                    return ReadUsbDeviceProperties(device);
            }
        }
        catch
        {
        }
        return default;
    }
}
