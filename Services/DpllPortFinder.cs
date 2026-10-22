using System.IO.Ports;
using Microsoft.Win32;

namespace DPLL_Ultrasonic_DAQ.Services;

/// <summary>
/// Utility to automatically discover the DPLL STM32 Virtual COM Port
/// without requiring hardcoded or manually configured COM port numbers.
/// </summary>
public static class DpllPortFinder
{
    public const string DefaultTargetName = "DPLL";
    public const int DefaultVid = 0x0483; // STMicroelectronics VID
    public const int DefaultPid = 0x5740; // STM32 Virtual COM Port PID

    /// <summary>
    /// Searches for an active COM port matching the DPLL USB CDC device.
    /// Checks both USB Hardware ID (VID:PID) and Windows FriendlyName.
    /// </summary>
    /// <param name="targetName">Keyword to look for in FriendlyName (e.g. "DPLL").</param>
    /// <param name="vid">USB Vendor ID (default 0x0483 for STM32).</param>
    /// <param name="pid">USB Product ID (default 0x5740 for STM32 CDC).</param>
    /// <returns>Port name like "COM19", or null if not detected/connected.</returns>
    public static string? FindPort(string targetName = DefaultTargetName, int vid = DefaultVid, int pid = DefaultPid)
    {
        var activePorts = SerialPort.GetPortNames();
        if (activePorts.Length == 0)
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            // On non-Windows (Linux/macOS), if only one CDC ACM or ttyACM port exists, can return it.
            return activePorts.FirstOrDefault(p => p.Contains("ttyACM", StringComparison.OrdinalIgnoreCase));
        }

        try
        {
            // 1. Primary check: Query directly via USB VID & PID registry key.
            //    This is instant (<1ms) and matches even before custom INF is applied.
            string hwPath = $@"SYSTEM\CurrentControlSet\Enum\USB\VID_{vid:X4}&PID_{pid:X4}";
            using var vidPidKey = Registry.LocalMachine.OpenSubKey(hwPath);
            if (vidPidKey != null)
            {
                foreach (var instance in vidPidKey.GetSubKeyNames())
                {
                    using var instKey = vidPidKey.OpenSubKey(instance);
                    using var paramKey = instKey?.OpenSubKey("Device Parameters");
                    var port = paramKey?.GetValue("PortName") as string;

                    if (!string.IsNullOrEmpty(port) && activePorts.Contains(port, StringComparer.OrdinalIgnoreCase))
                    {
                        return port;
                    }
                }
            }

            // 2. Secondary check: Scan USB enumeration for FriendlyName containing the keyword (e.g. "DPLL").
            using var usbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usbKey != null)
            {
                foreach (var devId in usbKey.GetSubKeyNames())
                {
                    using var devKey = usbKey.OpenSubKey(devId);
                    if (devKey == null) continue;

                    foreach (var instance in devKey.GetSubKeyNames())
                    {
                        using var instKey = devKey.OpenSubKey(instance);
                        var friendly = instKey?.GetValue("FriendlyName") as string;
                        if (!string.IsNullOrEmpty(friendly) && friendly.Contains(targetName, StringComparison.OrdinalIgnoreCase))
                        {
                            using var paramKey = instKey?.OpenSubKey("Device Parameters");
                            var port = paramKey?.GetValue("PortName") as string;
                            if (!string.IsNullOrEmpty(port) && activePorts.Contains(port, StringComparer.OrdinalIgnoreCase))
                            {
                                return port;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback gracefully if registry query is restricted
        }

        return null;
    }

    /// <summary>
    /// Lists all currently active COM ports along with their device FriendlyNames.
    /// </summary>
    public static IReadOnlyList<(string Port, string Description)> GetPortDetails()
    {
        var activePorts = SerialPort.GetPortNames();
        var result = new List<(string Port, string Description)>();

        if (!OperatingSystem.IsWindows() || activePorts.Length == 0)
        {
            return activePorts.Select(p => (p, p)).ToList();
        }

        try
        {
            var portDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using var usbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usbKey != null)
            {
                foreach (var devId in usbKey.GetSubKeyNames())
                {
                    using var devKey = usbKey.OpenSubKey(devId);
                    if (devKey == null) continue;

                    foreach (var instance in devKey.GetSubKeyNames())
                    {
                        using var instKey = devKey.OpenSubKey(instance);
                        var friendly = instKey?.GetValue("FriendlyName") as string;
                        using var paramKey = instKey?.OpenSubKey("Device Parameters");
                        var port = paramKey?.GetValue("PortName") as string;

                        if (!string.IsNullOrEmpty(port) && !string.IsNullOrEmpty(friendly))
                        {
                            portDescriptions[port] = friendly;
                        }
                    }
                }
            }

            foreach (var p in activePorts)
            {
                var desc = portDescriptions.TryGetValue(p, out var d) ? d : p;
                result.Add((p, desc));
            }
        }
        catch
        {
            return activePorts.Select(p => (p, p)).ToList();
        }

        return result;
    }
}
