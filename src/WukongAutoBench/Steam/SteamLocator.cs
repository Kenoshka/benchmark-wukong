using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WukongAutoBench.Steam;

/// <summary>Где установлен Benchmark Tool и как его запускать.</summary>
internal sealed record BenchmarkInstall(string InstallDir, string? BuildId, string? SteamExe)
{
    /// <summary>Возможные расположения GameUserSettings.ini (бенчмарк хранит конфиг в своей папке, игра — в LOCALAPPDATA).</summary>
    public IReadOnlyList<string> ConfigCandidates =>
    [
        Path.Combine(InstallDir, "b1", "Saved", "Config", "Windows", "GameUserSettings.ini"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "b1", "Saved", "Config", "Windows", "GameUserSettings.ini"),
    ];

    /// <summary>Исполняемый файл: лаунчер UE в корне папки или сам Shipping-бинарник.</summary>
    public string? FindExecutable()
    {
        string[] preferred =
        [
            Path.Combine(InstallDir, "b1.exe"),
            Path.Combine(InstallDir, "b1", "Binaries", "Win64", "b1-Win64-Shipping.exe"),
        ];
        var hit = preferred.FirstOrDefault(File.Exists);
        if (hit != null) return hit;

        return Directory.Exists(InstallDir)
            ? Directory.EnumerateFiles(InstallDir, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => !Path.GetFileName(f).Contains("crash", StringComparison.OrdinalIgnoreCase)
                                     && !Path.GetFileName(f).Contains("redist", StringComparison.OrdinalIgnoreCase))
            : null;
    }
}

internal static partial class SteamLocator
{
    public const int BenchmarkAppId = 3132990;

    [GeneratedRegex("\"path\"\\s+\"(?<p>[^\"]+)\"")]
    private static partial Regex LibraryPathRegex();

    [GeneratedRegex("\"(?<k>installdir|buildid|StateFlags)\"\\s+\"(?<v>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestFieldRegex();

    public static string? FindSteamDir()
    {
        string?[] candidates =
        [
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string,
            @"C:\Program Files (x86)\Steam",
        ];
        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => Path.GetFullPath(c!.Replace('/', '\\')))
            .FirstOrDefault(Directory.Exists);
    }

    public static IReadOnlyList<string> GetLibraries(string steamDir)
    {
        var libs = new List<string> { steamDir };
        var vdf = Path.Combine(steamDir, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in LibraryPathRegex().Matches(File.ReadAllText(vdf)))
                libs.Add(m.Groups["p"].Value.Replace(@"\\", @"\"));
        }
        return libs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    /// <summary>
    /// Ищет установленный бенчмарк: явный путь (если задан), затем appmanifest_3132990.acf во всех библиотеках Steam.
    /// </summary>
    public static BenchmarkInstall? Locate(string? explicitDir)
    {
        var steamDir = FindSteamDir();
        var steamExe = steamDir != null && File.Exists(Path.Combine(steamDir, "steam.exe"))
            ? Path.Combine(steamDir, "steam.exe")
            : null;

        if (!string.IsNullOrWhiteSpace(explicitDir))
            return Directory.Exists(explicitDir) ? new BenchmarkInstall(Path.GetFullPath(explicitDir), null, steamExe) : null;

        if (steamDir == null) return null;

        foreach (var lib in GetLibraries(steamDir))
        {
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{BenchmarkAppId}.acf");
            if (!File.Exists(manifest)) continue;

            var fields = ManifestFieldRegex().Matches(File.ReadAllText(manifest))
                .GroupBy(m => m.Groups["k"].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Groups["v"].Value, StringComparer.OrdinalIgnoreCase);
            if (!fields.TryGetValue("installdir", out var installDir)) continue;

            var dir = Path.Combine(lib, "steamapps", "common", installDir);
            if (Directory.Exists(dir))
                return new BenchmarkInstall(dir, fields.GetValueOrDefault("buildid"), steamExe);
        }

        // Манифеста нет (например, папку скопировали вручную) — пробуем типичное имя папки.
        foreach (var lib in GetLibraries(steamDir))
        {
            var dir = Path.Combine(lib, "steamapps", "common", "Black Myth Wukong Benchmark Tool");
            if (Directory.Exists(dir)) return new BenchmarkInstall(dir, null, steamExe);
        }
        return null;
    }

    public static bool IsSteamRunning() => Process.GetProcessesByName("steam").Length > 0;

    /// <summary>Открывает в Steam диалог установки бесплатного Benchmark Tool.</summary>
    public static void OpenInstallDialog() =>
        Process.Start(new ProcessStartInfo($"steam://install/{BenchmarkAppId}") { UseShellExecute = true });

    /// <summary>Запускает Steam в фоне и ждёт, пока он поднимется (бенчмарку нужен Steam API).</summary>
    public static async Task EnsureSteamRunningAsync(string? steamExe, Action<string> log, CancellationToken ct)
    {
        if (IsSteamRunning()) return;
        if (steamExe == null)
        {
            log("Steam не запущен и steam.exe не найден — бенчмарк может не стартовать.");
            return;
        }

        log("Steam не запущен — запускаю его в фоне…");
        Process.Start(new ProcessStartInfo(steamExe, "-silent") { UseShellExecute = true });
        for (var i = 0; i < 60 && Process.GetProcessesByName("steamwebhelper").Length == 0; i++)
            await Task.Delay(1000, ct);
        await Task.Delay(TimeSpan.FromSeconds(10), ct); // даём клиенту залогиниться
    }
}
