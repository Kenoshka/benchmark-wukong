using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using WukongAutoBench;
using WukongAutoBench.Config;
using WukongAutoBench.Native;
using WukongAutoBench.Reporting;
using WukongAutoBench.Results;
using WukongAutoBench.Runner;
using WukongAutoBench.Steam;
using WukongAutoBench.SystemInfo;

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

Options options;
try
{
    options = Options.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Options.Usage);
    return 2;
}

if (options.Help)
{
    Console.WriteLine(Options.Usage);
    return 0;
}

var outDir = Path.Combine(options.OutDir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture));
Directory.CreateDirectory(outDir);
Log.OpenFile(Path.Combine(outDir, "run.log"));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Log.Warn("Прерывание по Ctrl+C: закрываю бенчмарк и возвращаю настройки…");
    cts.Cancel();
};

int exitCode;
try
{
    exitCode = await App.RunAsync(options, outDir, cts.Token);
}
catch (OperationCanceledException)
{
    Log.Warn("Прервано пользователем.");
    exitCode = 130;
}
catch (Exception ex)
{
    Log.Error(ex.Message);
    exitCode = 1;
}

if (!options.NoPause && !Console.IsInputRedirected && Win32.OwnsConsoleAlone())
{
    Console.WriteLine("Нажмите Enter, чтобы закрыть окно…");
    Console.ReadLine();
}
return exitCode;

internal static class App
{
    public static async Task<int> RunAsync(Options options, string outDir, CancellationToken ct)
    {
        Log.Step("Black Myth: Wukong Benchmark Tool — автоматический CPU/GPU-прогон");
        var install = SteamLocator.Locate(options.GameDir);
        var systemTask = Task.Run(() => SystemInfoCollector.Collect(install?.InstallDir), ct);

        var display = Win32.GetPrimaryDisplayMode();
        var gpuRes = options.GpuResolution ?? (display is { } d ? (d.Width, d.Height) : (1920, 1080));
        var profiles = new[]
        {
            BenchmarkProfile.Cpu(options.CpuResolution.W, options.CpuResolution.H),
            BenchmarkProfile.Gpu(gpuRes.Item1, gpuRes.Item2),
        }.Where(p => options.Passes.Contains(p.Id)).ToList();

        if (options.FromResults.Count > 0)
            return await ReportFromFilesAsync(options, outDir, install, profiles, systemTask);

        if (install == null)
        {
            Log.Error(options.GameDir != null
                ? $"Папка {options.GameDir} не найдена."
                : $"Black Myth: Wukong Benchmark Tool (Steam AppID {SteamLocator.BenchmarkAppId}) не установлен.");
            if (options.Install)
            {
                SteamLocator.OpenInstallDialog();
                Log.Info("Открыл установку в Steam. После загрузки (~30 ГБ) запустите утилиту снова.");
            }
            else
            {
                Log.Info("Установите его в Steam (бесплатно) или запустите утилиту с --install. Свою папку можно указать через --game-dir.");
            }
            return 3;
        }
        Log.Ok($"Бенчмарк: {install.InstallDir}" + (install.BuildId != null ? $" (build {install.BuildId})" : ""));

        foreach (var path in GameConfigFiles.RecoverStaleBackups(install.ConfigCandidates))
            Log.Warn($"Найдена копия настроек от прерванного запуска — восстановил {path}");

        var runnerOptions = new RunnerOptions
        {
            Mode = options.Launch,
            MenuDelay = TimeSpan.FromSeconds(options.MenuDelaySeconds),
            PassTimeout = TimeSpan.FromMinutes(options.TimeoutMinutes),
            StartButton = options.StartButton ?? new RunnerOptions().StartButton,
            ConfirmButton = options.ConfirmButton ?? new RunnerOptions().ConfirmButton,
        };
        var runner = new BenchmarkRunner(install, runnerOptions, ResultWatcher.DefaultDirs(install.InstallDir));

        var configs = install.ConfigCandidates.Where(File.Exists).ToList();
        if (configs.Count == 0)
        {
            if (options.DryRun)
                throw new InvalidOperationException("GameUserSettings.ini ещё не создан — запустите бенчмарк хотя бы раз.");
            Log.Step("GameUserSettings.ini ещё нет — запускаю бенчмарк один раз, чтобы он создал конфиг…");
            await SteamLocator.EnsureSteamRunningAsync(install.SteamExe, Log.Info, ct);
            await runner.CreateConfigAsync(() => install.ConfigCandidates.Any(File.Exists), ct);
            configs = install.ConfigCandidates.Where(File.Exists).ToList();
            if (configs.Count == 0)
                throw new InvalidOperationException(
                    "Бенчмарк не создал GameUserSettings.ini. Запустите его вручную, откройте настройки, нажмите «Применить» и выйдите — затем повторите.");
        }
        foreach (var c in configs) Log.Info($"Конфиг: {c}");
        File.Copy(configs[0], Path.Combine(outDir, "GameUserSettings.original.ini"), overwrite: true);

        if (options.DryRun)
            return await DryRunAsync(outDir, install, configs[0], profiles, systemTask);

        await SteamLocator.EnsureSteamRunningAsync(install.SteamExe, Log.Info, ct);

        var files = new GameConfigFiles(configs);
        files.Backup();
        void Cleanup()
        {
            BenchmarkRunner.KillAll();
            files.Restore();
        }
        // Если консоль закроют крестиком — всё равно закрыть игру и вернуть настройки.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
        using var closeHandler = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => Cleanup());
        Win32.SetThreadExecutionState(Win32.ES_CONTINUOUS | Win32.ES_SYSTEM_REQUIRED | Win32.ES_DISPLAY_REQUIRED);

        var passes = new List<PassReport>();
        try
        {
            for (var i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];
                ct.ThrowIfCancellationRequested();
                Log.Step($"Проход {i + 1}/{profiles.Count}: {profile.Title} — {profile.Resolution}, рендер {profile.RenderScale}%, " +
                         $"{BenchmarkProfile.QualityNames[profile.Quality]}, RT {(profile.RayTracing ? "вкл" : "выкл")}");

                var changes = files.Apply(profile);
                Log.Info($"Записано изменений в GameUserSettings.ini: {changes.Count}");
                var passDir = Directory.CreateDirectory(Path.Combine(outDir, profile.Id)).FullName;
                File.Copy(configs[0], Path.Combine(passDir, "GameUserSettings.applied.ini"), overwrite: true);

                try
                {
                    var run = await runner.RunAsync(profile.Title, ct);
                    File.Copy(run.ResultPath, Path.Combine(passDir, "benchmark_result.json"), overwrite: true);
                    var pass = new PassReport(profile, changes, run, null);
                    passes.Add(pass);
                    Log.Ok($"{profile.Title}: средний FPS {ReportModel.F(run.Result.FpsAvg)}, мин {ReportModel.F(run.Result.FpsMin)}, " +
                           $"1% low {ReportModel.F(pass.Stats?.Low1Fps)}; ограничитель — {pass.Stats?.Bottleneck ?? "?"} ({run.ResultPath})");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error($"{profile.Title}: {ex.Message}");
                    passes.Add(new PassReport(profile, changes, null, ex.Message));
                }

                if (i < profiles.Count - 1 && options.CooldownSeconds > 0)
                {
                    Log.Info($"Пауза {options.CooldownSeconds} с перед следующим проходом…");
                    await Task.Delay(TimeSpan.FromSeconds(options.CooldownSeconds), ct);
                }
            }
        }
        finally
        {
            Cleanup();
            Win32.SetThreadExecutionState(Win32.ES_CONTINUOUS);
            Log.Ok("Бенчмарк закрыт, исходные настройки GameUserSettings.ini восстановлены.");
        }

        var report = new FullReport(DateTime.Now, install.InstallDir, install.BuildId, await systemTask, passes);
        WriteReport(report, outDir);
        return passes.All(p => p.Run != null) ? 0 : 1;
    }

    /// <summary>--dry-run: применить профили к копиям конфига и показать изменения, ничего не запуская.</summary>
    private static async Task<int> DryRunAsync(string outDir, BenchmarkInstall install, string config,
        IReadOnlyList<BenchmarkProfile> profiles, Task<SystemSnapshot> systemTask)
    {
        var passes = new List<PassReport>();
        foreach (var profile in profiles)
        {
            var doc = IniDocument.Load(config);
            var changes = SettingsApplier.Apply(doc, profile);
            var passDir = Directory.CreateDirectory(Path.Combine(outDir, profile.Id)).FullName;
            doc.Save(Path.Combine(passDir, "GameUserSettings.applied.ini"));
            Log.Step($"{profile.Title}: {changes.Count} изменений (копия: {Path.Combine(passDir, "GameUserSettings.applied.ini")})");
            foreach (var c in changes)
                Log.Info($"[{c.Section}] {c.Key}: {c.OldValue ?? "(нет)"} → {c.NewValue}");
            passes.Add(new PassReport(profile, changes, null, null));
        }
        Log.Ok("Dry run: реальный конфиг не изменён, бенчмарк не запускался.");
        WriteReport(new FullReport(DateTime.Now, install.InstallDir, install.BuildId, await systemTask, passes), outDir);
        return 0;
    }

    /// <summary>--from-results: отчёт по уже готовым файлам (первый — CPU, второй — GPU).</summary>
    private static async Task<int> ReportFromFilesAsync(Options options, string outDir, BenchmarkInstall? install,
        IReadOnlyList<BenchmarkProfile> profiles, Task<SystemSnapshot> systemTask)
    {
        var passes = new List<PassReport>();
        for (var i = 0; i < options.FromResults.Count && i < profiles.Count; i++)
        {
            var path = options.FromResults[i];
            var result = BenchmarkResult.TryLoad(path);
            passes.Add(result == null
                ? new PassReport(profiles[i], [], null, $"Файл {path} не является результатом бенчмарка.")
                : new PassReport(profiles[i], [], new PassRun(path, result, TimeSpan.Zero, 0), null));
        }
        WriteReport(new FullReport(DateTime.Now, install?.InstallDir, install?.BuildId, await systemTask, passes), outDir);
        return passes.All(p => p.Run != null) ? 0 : 1;
    }

    private static void WriteReport(FullReport report, string outDir)
    {
        ReportWriter.PrintConsole(report);
        var md = Path.Combine(outDir, "report.md");
        var json = Path.Combine(outDir, "report.json");
        File.WriteAllText(md, ReportWriter.ToMarkdown(report), new UTF8Encoding(false));
        File.WriteAllText(json, ReportWriter.ToJson(report), new UTF8Encoding(false));
        Log.Ok($"Отчёт сохранён: {md}");
        Log.Info($"Машиночитаемая версия: {json}");
    }
}
