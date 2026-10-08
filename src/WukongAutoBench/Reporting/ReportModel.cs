using System.Globalization;
using System.Text.RegularExpressions;
using WukongAutoBench.Config;
using WukongAutoBench.Results;
using WukongAutoBench.Runner;
using WukongAutoBench.SystemInfo;

namespace WukongAutoBench.Reporting;

internal sealed record PassReport(
    BenchmarkProfile Profile,
    IReadOnlyList<SettingChange> Changes,
    PassRun? Run,
    string? Error)
{
    public FrameStats? Stats { get; } = Run == null ? null : FrameStats.From(Run.Result.Records);
}

internal sealed record SettingCheck(string Name, string Expected, string? Actual, bool? Ok);

internal sealed record FullReport(
    DateTime Date,
    string? InstallDir,
    string? BuildId,
    SystemSnapshot System,
    IReadOnlyList<PassReport> Passes);

/// <summary>Табличные данные отчёта: одинаковые для консоли, Markdown и JSON.</summary>
internal static partial class ReportModel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string ComputedMark = "*";

    public static string F(double? v, string format = "0.0") => v is { } d && double.IsFinite(d) ? d.ToString(format, Inv) : "—";
    public static string Pct(double? v) => v is { } d ? (d * 100).ToString("0", Inv) + "%" : "—";

    /// <summary>Строки таблицы результатов: метрика → значение для каждого прохода.</summary>
    public static IReadOnlyList<(string Metric, string[] Values)> ResultRows(IReadOnlyList<PassReport> passes)
    {
        string[] Map(Func<PassReport, string> f) => passes.Select(p => p.Run == null ? (p.Error != null ? "ошибка" : "—") : f(p)).ToArray();

        return
        [
            ("Средний FPS (FPSAvg)", Map(p => F(p.Run!.Result.FpsAvg))),
            ("Минимальный FPS (FPSMin)", Map(p => F(p.Run!.Result.FpsMin))),
            ("Максимальный FPS (FPSMax)", Map(p => F(p.Run!.Result.FpsMax))),
            ("FPS95 (из бенчмарка)", Map(p => F(p.Run!.Result.Fps95))),
            ($"Медиана FPS{ComputedMark}", Map(p => F(p.Stats?.MedianFps))),
            ($"1% low FPS{ComputedMark}", Map(p => F(p.Stats?.Low1Fps))),
            ($"0.1% low FPS{ComputedMark}", Map(p => F(p.Stats?.Low01Fps))),
            ($"Среднее время кадра, мс{ComputedMark}", Map(p => F(p.Stats?.AvgFrameTimeMs, "0.00"))),
            ($"CPU frame time, мс{ComputedMark}", Map(p => F(p.Stats?.AvgCpuFrameTimeMs, "0.00"))),
            ($"GPU frame time, мс{ComputedMark}", Map(p => F(p.Stats?.AvgGpuFrameTimeMs, "0.00"))),
            ($"GPU занят от времени кадра{ComputedMark}", Map(p => Pct(p.Stats?.GpuBusyRatio))),
            ($"Кадров, упёршихся в GPU{ComputedMark}", Map(p => Pct(p.Stats?.GpuBoundShare))),
            ($"Ограничивающий компонент{ComputedMark}", Map(p => p.Stats?.Bottleneck ?? "—")),
            ("Средняя загрузка CPU, % (бенчмарк)", Map(p => F(p.Run!.Result.CpuUsageAvg))),
            ("Средняя загрузка GPU, % (бенчмарк)", Map(p => F(p.Run!.Result.GpuUsageAvg))),
            ("Видеопамять, ГБ (бенчмарк)", Map(p => F(p.Run!.Result.VideoMemGb))),
            ($"Пик видеопамяти, ГБ{ComputedMark}", Map(p => F(p.Stats?.PeakVramMb / 1024, "0.0"))),
            ("Сэмплов в Records", Map(p => p.Run!.Result.Records.Count.ToString(Inv))),
            ("Длительность прохода (с запуском)", Map(p => p.Run!.Duration > TimeSpan.Zero ? p.Run.Duration.ToString(@"m\:ss", Inv) : "—")),
        ];
    }

    [GeneratedRegex(@"(\d+)\D+(\d+)")]
    private static partial Regex TwoNumbersRegex();

    /// <summary>
    /// Сверка: какие настройки бенчмарк сам записал в результат и совпадают ли они с запрошенными.
    /// Так видно, что изменения ini действительно применились.
    /// </summary>
    public static IReadOnlyList<SettingCheck> Verify(BenchmarkProfile p, BenchmarkResult r)
    {
        var checks = new List<SettingCheck>();

        var res = r.Field("ScreenResolution");
        var m = res == null ? null : TwoNumbersRegex().Match(res);
        checks.Add(new SettingCheck("Разрешение", p.Resolution, res,
            m is { Success: true } ? m.Groups[1].Value == p.Width.ToString(Inv) && m.Groups[2].Value == p.Height.ToString(Inv) : null));

        void Check(string name, string field, int expected, string expectedText)
        {
            var actual = r.IntField(field);
            checks.Add(new SettingCheck(name, expectedText, r.Field(field), actual == null ? null : actual == expected));
        }

        Check("Пресет качества (QualityLevel)", "QualityLevel", p.Quality + 1, $"{p.Quality + 1} ({BenchmarkProfile.QualityNames[p.Quality]})");
        Check("Масштаб рендера, % (ImageQuality)", "ImageQuality", p.RenderScale, p.RenderScale.ToString(Inv));
        Check("Дальность прорисовки (ViewDistance)", "ViewDistance", p.ViewDistance + 1, $"{p.ViewDistance + 1} ({BenchmarkProfile.QualityNames[p.ViewDistance]})");
        Check("Генерация кадров (InsertFrame)", "InsertFrame", p.FrameGeneration ? 1 : 0, p.FrameGeneration ? "1" : "0");

        // Rtx в результате: 0 = выкл, иначе уровень Full RT (1 Low … 4 Very High).
        var rtx = r.IntField("Rtx");
        checks.Add(new SettingCheck("Full Ray Tracing (Rtx)",
            p.RayTracing ? $"вкл ({p.RayTracingLevel} = {BenchmarkProfile.RayTracingLevelNames[p.RayTracingLevel - 1]})" : "0 (выкл)",
            r.Field("Rtx"),
            rtx == null ? null : p.RayTracing ? rtx > 0 : rtx == 0));

        if (r.Field("ScreenMode") is { } mode)
            checks.Add(new SettingCheck("Режим экрана (ScreenMode)", "полноэкранный (0)", mode, null));
        foreach (var (field, name) in new[] { ("Dlss", "Апскейлер (Dlss, как в меню)"), ("Dx12", "DirectX 12 (Dx12)") })
            if (r.Field(field) is { } v)
                checks.Add(new SettingCheck(name, "не меняется", v, null));

        return checks;
    }

    /// <summary>Сведения, которые сам бенчмарк записал о системе (для сверки с WMI).</summary>
    public static IReadOnlyList<(string Name, string Value)> BenchmarkSystemFields(BenchmarkResult r)
    {
        (string Field, string Name)[] map =
        [
            ("GameVer", "Версия бенчмарка"), ("SysVer", "ОС"), ("CPUModel", "CPU"), ("GPUModel", "GPU"),
            ("GpuDriverVer", "Драйвер GPU"), ("VideoMemSize", "Видеопамять"), ("SysMem", "Оперативная память"),
        ];
        return map.Where(x => r.Field(x.Field) != null).Select(x => (x.Name, r.Field(x.Field)!)).ToList();
    }
}
