using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WukongAutoBench.Native;

namespace WukongAutoBench.SystemInfo;

internal sealed record GpuInfo(string Name, string? VramGb, string? DriverVersion, string? DriverDate);

internal sealed record MemoryModule(string CapacityGb, int? SpeedMts, string? Manufacturer, string? PartNumber);

internal sealed record SystemSnapshot
{
    public string Cpu { get; init; } = "?";
    public string? CpuDetails { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public string Ram { get; init; } = "?";
    public IReadOnlyList<MemoryModule> RamModules { get; init; } = [];
    public string? Motherboard { get; init; }
    public string? Bios { get; init; }
    public string Os { get; init; } = "?";
    public string? Display { get; init; }
    public string? GameDrive { get; init; }
    public string? PowerPlan { get; init; }
    public string? HardwareGpuScheduling { get; init; }

    /// <summary>Пары «название — значение» для таблицы в отчёте.</summary>
    public IReadOnlyList<(string Name, string Value)> ToRows()
    {
        var rows = new List<(string, string)> { ("CPU", Cpu) };
        if (CpuDetails != null) rows.Add(("", CpuDetails));
        foreach (var g in Gpus)
        {
            var parts = new[] { g.VramGb != null ? $"{g.VramGb} VRAM" : null, g.DriverVersion != null ? $"драйвер {g.DriverVersion}" : null, g.DriverDate }
                .Where(x => x != null);
            rows.Add(("GPU", $"{g.Name} ({string.Join(", ", parts)})"));
        }
        rows.Add(("RAM", Ram));
        foreach (var m in RamModules)
            rows.Add(("", $"{m.CapacityGb} ГБ" + (m.SpeedMts != null ? $" @ {m.SpeedMts} МТ/с" : "") +
                          (m.Manufacturer != null || m.PartNumber != null ? $" — {m.Manufacturer} {m.PartNumber}".TrimEnd() : "")));
        if (Motherboard != null) rows.Add(("Материнская плата", Motherboard + (Bios != null ? $" (BIOS {Bios})" : "")));
        rows.Add(("ОС", Os));
        if (Display != null) rows.Add(("Монитор", Display));
        if (GameDrive != null) rows.Add(("Диск с бенчмарком", GameDrive));
        if (PowerPlan != null) rows.Add(("План электропитания", PowerPlan));
        if (HardwareGpuScheduling != null) rows.Add(("Аппаратное планирование GPU (HAGS)", HardwareGpuScheduling));
        return rows;
    }
}

/// <summary>Характеристики ПК через WMI, реестр и WinAPI. Любой недоступный пункт просто пропускается.</summary>
internal static partial class SystemInfoCollector
{
    public static SystemSnapshot Collect(string? gameDir)
    {
        var cpu = Safe(() => Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize FROM Win32_Processor").FirstOrDefault());
        var cs = Safe(() => Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault());
        var modules = Safe(CollectMemoryModules) ?? [];
        var os = Safe(() => Query("SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem").FirstOrDefault());
        var board = Safe(() => Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault());
        var bios = Safe(() => Query("SELECT SMBIOSBIOSVersion FROM Win32_BIOS").FirstOrDefault());

        string cpuName = Str(cpu, "Name")?.Trim() ?? "?";
        string? cpuDetails = cpu == null ? null :
            $"{Str(cpu, "NumberOfCores")} ядер / {Str(cpu, "NumberOfLogicalProcessors")} потоков, " +
            $"базовая частота {Str(cpu, "MaxClockSpeed")} МГц, L2 {KbToMb(Str(cpu, "L2CacheSize"))} МБ, L3 {KbToMb(Str(cpu, "L3CacheSize"))} МБ";

        var totalBytes = ulong.TryParse(Str(cs, "TotalPhysicalMemory"), out var t) ? t : 0;
        var installedGb = modules.Sum(m => double.Parse(m.CapacityGb, CultureInfo.InvariantCulture));
        var ram = installedGb > 0
            ? $"{installedGb:0} ГБ ({modules.Count} мод.{(modules.FirstOrDefault()?.SpeedMts is { } s ? $", {s} МТ/с" : "")}), доступно системе {totalBytes / 1024.0 / 1024 / 1024:0.0} ГБ"
            : $"{totalBytes / 1024.0 / 1024 / 1024:0.0} ГБ";

        var displayVersion = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null) as string;
        var ubr = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null);
        var osText = os == null ? Environment.OSVersion.VersionString :
            $"{Str(os, "Caption")?.Trim()} {displayVersion} ({Str(os, "OSArchitecture")}), сборка {Str(os, "BuildNumber")}{(ubr != null ? $".{ubr}" : "")}";

        return new SystemSnapshot
        {
            Cpu = cpuName,
            CpuDetails = cpuDetails,
            Gpus = Safe(CollectGpus) ?? [],
            Ram = ram,
            RamModules = modules,
            Motherboard = board == null ? null : $"{Str(board, "Manufacturer")?.Trim()} {Str(board, "Product")?.Trim()}",
            Bios = Str(bios, "SMBIOSBIOSVersion")?.Trim(),
            Os = osText,
            Display = Safe(() => Win32.GetPrimaryDisplayMode() is { } m ? $"{m.Width}x{m.Height} @ {m.RefreshHz} Гц (основной)" : null),
            GameDrive = gameDir == null ? null : Safe(() => DescribeDrive(gameDir)),
            PowerPlan = Safe(GetPowerPlan),
            HardwareGpuScheduling = Safe(() =>
                Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", null) is int v
                    ? (v == 2 ? "вкл" : "выкл") : null),
        };
    }

    private static List<MemoryModule> CollectMemoryModules() =>
        Query("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber FROM Win32_PhysicalMemory")
            .Select(m =>
            {
                var cap = ulong.TryParse(Str(m, "Capacity"), out var c) ? c / 1024.0 / 1024 / 1024 : 0;
                int? speed = int.TryParse(Str(m, "ConfiguredClockSpeed"), out var cfg) && cfg > 0 ? cfg
                    : int.TryParse(Str(m, "Speed"), out var sp) && sp > 0 ? sp : null;
                return new MemoryModule(cap.ToString("0", CultureInfo.InvariantCulture), speed,
                    NullIfBlank(Str(m, "Manufacturer")), NullIfBlank(Str(m, "PartNumber")));
            })
            .ToList();

    private static List<GpuInfo> CollectGpus()
    {
        var vram = ReadVramFromRegistry();
        return Query("SELECT Name, DriverVersion, DriverDate, AdapterRAM FROM Win32_VideoController")
            .Select(g =>
            {
                var name = Str(g, "Name")?.Trim() ?? "?";
                // AdapterRAM — uint32 и «упирается» в 4 ГБ, поэтому объём берём из реестра драйвера.
                string? mem = vram.TryGetValue(name, out var bytes) ? $"{bytes / 1024.0 / 1024 / 1024:0} ГБ"
                    : ulong.TryParse(Str(g, "AdapterRAM"), out var ar) && ar > 0 ? $"{ar / 1024.0 / 1024 / 1024:0.#} ГБ" : null;
                var driver = Str(g, "DriverVersion");
                var date = Str(g, "DriverDate") is { Length: >= 8 } d ? $"от {d[6..8]}.{d[4..6]}.{d[..4]}" : null;
                return new GpuInfo(name, mem, FriendlyDriverVersion(name, driver), date);
            })
            .Where(g => !g.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                        && !g.Name.Contains("Remote", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>NVIDIA: 32.0.15.6636 → 566.36 (номер версии, который показывает панель NVIDIA).</summary>
    internal static string? FriendlyDriverVersion(string gpuName, string? driverVersion)
    {
        if (driverVersion == null) return null;
        if (!gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return driverVersion;
        var digits = string.Concat(driverVersion.Split('.').TakeLast(2));
        if (digits.Length < 5) return driverVersion;
        var tail = digits[^5..];
        return $"{tail[..3]}.{tail[3..]} ({driverVersion})";
    }

    private static Dictionary<string, ulong> ReadVramFromRegistry()
    {
        var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls == null) return result;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            try
            {
                using var key = cls.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string desc) continue;
                var value = key.GetValue("HardwareInformation.qwMemorySize");
                ulong bytes = value switch
                {
                    long l => (ulong)l,
                    byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0),
                    _ => 0,
                };
                if (bytes > 0) result[desc] = bytes;
            }
            catch (System.Security.SecurityException) { }
        }
        return result;
    }

    private static string DescribeDrive(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))!;
        var drive = new DriveInfo(root);
        var text = $"{root.TrimEnd('\\')} {drive.DriveFormat}, свободно {drive.AvailableFreeSpace / 1e9:0} из {drive.TotalSize / 1e9:0} ГБ";

        // Логический диск → раздел → физический диск: модель; тип носителя — из MSFT_PhysicalDisk.
        var letter = root.TrimEnd('\\');
        var models = new List<string>();
        foreach (var part in Query($"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{letter}'}} WHERE AssocClass = Win32_LogicalDiskToPartition"))
        foreach (var disk in Query($"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{Str(part, "DeviceID")}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition"))
        {
            var model = Str(disk, "Model")?.Trim() ?? "?";
            var index = Str(disk, "Index");
            var media = Safe(() => Query($"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId = '{index}'", @"root\Microsoft\Windows\Storage")
                .Select(d => Str(d, "MediaType")).FirstOrDefault()) switch
            {
                "3" => "HDD",
                "4" => "SSD",
                _ => null,
            };
            models.Add(media != null ? $"{model} ({media})" : model);
        }
        return models.Count > 0 ? $"{text}; {string.Join(", ", models.Distinct())}" : text;
    }

    [GeneratedRegex(@"\((?<name>[^)]+)\)\s*\*?\s*$")]
    private static partial Regex PowerPlanNameRegex();

    private static string? GetPowerPlan()
    {
        // powercfg пишет в OEM-кодировке консоли (cp866 на русской Windows).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var p = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme")
        {
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.GetEncoding((int)Win32.GetOEMCP()),
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (p == null) return null;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(5000);
        var m = PowerPlanNameRegex().Match(output);
        return m.Success ? m.Groups["name"].Value.Trim() : null;
    }

    private static List<ManagementBaseObject> Query(string wql) => Query(wql, @"root\cimv2");

    private static List<ManagementBaseObject> Query(string wql, string scope)
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        return searcher.Get().Cast<ManagementBaseObject>().ToList();
    }

    private static string? Str(ManagementBaseObject? o, string prop)
    {
        if (o == null) return null;
        try { return o[prop]?.ToString(); }
        catch (ManagementException) { return null; }
    }

    private static string KbToMb(string? kb) =>
        double.TryParse(kb, out var v) ? (v / 1024).ToString("0.#", CultureInfo.InvariantCulture) : "?";

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static T? Safe<T>(Func<T?> f)
    {
        try { return f(); }
        catch (Exception) { return default; }
    }
}
