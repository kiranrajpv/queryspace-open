using System.Management;

namespace QuerySpace;

public static class HardwareDetectionService
{
    public static string GpuSummary()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController");
            var adapters = new List<string>();
            foreach (ManagementObject adapter in searcher.Get())
            {
                using (adapter)
                {
                    var name = Convert.ToString(adapter["Name"])?.Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var memory = AdapterMemory(adapter["AdapterRAM"]);
                    var connection = Convert.ToString(adapter["PNPDeviceID"])?.StartsWith("PCI", StringComparison.OrdinalIgnoreCase) == true
                        ? "PCI display adapter"
                        : "display adapter";
                    adapters.Add(memory == null ? $"{name} ({connection})" : $"{name} ({memory}, {connection})");
                }
            }
            return adapters.Count == 0
                ? "No Windows GPU adapter was reported. Foundry Local will use its CPU fallback."
                : "Detected GPU adapter(s): " + string.Join("; ", adapters);
        }
        catch (Exception ex)
        {
            return "GPU detection was unavailable: " + ex.Message;
        }
    }

    private static string? AdapterMemory(object? value)
    {
        if (value == null) return null;
        try
        {
            var bytes = Convert.ToUInt64(value);
            return bytes == 0 ? null : $"{bytes / 1024d / 1024d / 1024d:F1} GB reported";
        }
        catch { return null; }
    }
}
