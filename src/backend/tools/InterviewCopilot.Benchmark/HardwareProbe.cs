using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace InterviewCopilot.Benchmark;

/// <summary>Reads CPU name, core count, RAM and OS for the report. Never throws; unknown values become "unknown".</summary>
internal static class HardwareProbe
{
    private const string Unknown = "unknown";

    public static HardwareInfo Detect() => new(
        DetectCpuName(),
        Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        RuntimeInformation.OSDescription);

    /// <summary>Extracts the first "model name" value from /proc/cpuinfo text.</summary>
    public static string? ParseCpuInfoModelName(string cpuInfo)
    {
        var line = cpuInfo.Split('\n').FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal) && l.Contains(':', StringComparison.Ordinal));
        return line?[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
    }

    private static string DetectCpuName()
    {
        try
        {
            string? name = null;
            if (OperatingSystem.IsWindows())
            {
                name = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
            }
            else if (OperatingSystem.IsLinux())
            {
                name = ParseCpuInfoModelName(File.ReadAllText("/proc/cpuinfo"));
            }
            else if (OperatingSystem.IsMacOS())
            {
                name = RunSysctl();
            }

            return string.IsNullOrWhiteSpace(name) ? Unknown : name.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Unknown;
        }
    }

    private static string? RunSysctl()
    {
        var info = new ProcessStartInfo("/usr/sbin/sysctl", "-n machdep.cpu.brand_string")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(info);
        if (process is null)
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }
}
