using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using WukongAutoBench.Config;

namespace WukongAutoBench.Reporting;

/// <summary>Отчёт в трёх видах: консоль, report.md и report.json.</summary>
internal static class ReportWriter
{
    private const string Footnote =
        "* посчитано утилитой по покадровым записям Records из результата бенчмарка; «упёрся в GPU» = GPU занят ≥ 90% времени кадра.";

    // ───────────────────────────── консоль ─────────────────────────────

    public static void PrintConsole(FullReport report)
    {
        Console.WriteLine();
        Header("ХАРАКТЕРИСТИКИ КОМПЬЮТЕРА");
        PrintTable(["Компонент", "Значение"], report.System.ToRows().Select(r => new[] { r.Name, r.Value }));

        var withRun = report.Passes.FirstOrDefault(p => p.Run != null);
        if (withRun != null)
        {
            Console.WriteLine();
            Console.WriteLine("  По данным самого бенчмарка:");
            PrintTable(["Поле", "Значение"], ReportModel.BenchmarkSystemFields(withRun.Run!.Result).Select(r => new[] { r.Name, r.Value }));
        }

        Console.WriteLine();
        Header("РЕЗУЛЬТАТЫ");
        PrintTable(["Метрика", .. report.Passes.Select(p => p.Profile.Title)],
            ReportModel.ResultRows(report.Passes).Select(r => (string[])[r.Metric, .. r.Values]));
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  " + Footnote);
        Console.ResetColor();

        foreach (var p in report.Passes.Where(p => p.Error != null))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  {p.Profile.Title}: {p.Error}");
            Console.ResetColor();
        }

        foreach (var p in report.Passes)
        {
            Console.WriteLine();
            Header($"НАСТРОЙКИ: {p.Profile.Title.ToUpperInvariant()}");
            PrintTable(["Параметр", "Значение"], p.Profile.Describe().Select(d => new[] { d.Name, d.Value }));

            if (p.Run != null)
            {
                Console.WriteLine("  Проверка по результату бенчмарка (какие настройки он реально использовал):");
                PrintTable(["Настройка", "Ожидалось", "В результате", ""],
                    ReportModel.Verify(p.Profile, p.Run.Result).Select(c => new[] { c.Name, c.Expected, c.Actual ?? "—", Mark(c.Ok) }));
            }

            Console.WriteLine("  Почему так:");
            foreach (var line in p.Profile.Rationale)
                WriteWrapped("  • ", line);
        }

        Console.WriteLine();
    }

    private static void Header(string title)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"══ {title} " + new string('═', Math.Max(3, 76 - title.Length)));
        Console.ResetColor();
    }

    private static string Mark(bool? ok) => ok switch { true => "✓", false => "✗", null => "" };

    private static void PrintTable(string[] headers, IEnumerable<string[]> rows)
    {
        var all = rows.ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, all.Count == 0 ? 0 : all.Max(r => i < r.Length ? r[i].Length : 0))).ToArray();
        // Широкие значения переносим, чтобы таблица помещалась в стандартные 120 колонок консоли.
        var budget = Math.Max(60, SafeConsoleWidth() - 4 - 3 * (widths.Length - 1));
        while (widths.Sum() > budget)
        {
            var i = Array.IndexOf(widths, widths.Max());
            widths[i] = Math.Max(10, widths[i] - (widths.Sum() - budget));
            if (widths.Sum() > budget && widths[i] == 10) break;
        }

        void Row(string[] cells, bool header = false)
        {
            var wrapped = cells.Select((c, i) => Wrap(c, widths[i])).ToList();
            var height = wrapped.Max(w => w.Count);
            for (var line = 0; line < height; line++)
            {
                var sb = new StringBuilder("  ");
                for (var i = 0; i < widths.Length; i++)
                {
                    var text = i < wrapped.Count && line < wrapped[i].Count ? wrapped[i][line] : "";
                    sb.Append(text.PadRight(widths[i]));
                    if (i < widths.Length - 1) sb.Append(" │ ");
                }
                if (header) Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(sb.ToString().TrimEnd());
                Console.ResetColor();
            }
        }

        Row(headers, header: true);
        Console.WriteLine("  " + string.Join("─┼─", widths.Select(w => new string('─', w))));
        foreach (var r in all) Row(r);
    }

    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > width)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(word);
            while (current.Length > width)
            {
                lines.Add(current.ToString(0, width));
                current.Remove(0, width);
            }
        }
        lines.Add(current.ToString());
        return lines;
    }

    private static void WriteWrapped(string prefix, string text)
    {
        var lines = Wrap(text, Math.Max(40, SafeConsoleWidth() - prefix.Length - 2));
        for (var i = 0; i < lines.Count; i++)
            Console.WriteLine((i == 0 ? prefix : new string(' ', prefix.Length)) + lines[i]);
    }

    private static int SafeConsoleWidth()
    {
        try { return Console.IsOutputRedirected ? 160 : Math.Max(80, Console.WindowWidth); }
        catch (IOException) { return 160; }
    }

    // ───────────────────────────── Markdown ─────────────────────────────

    public static string ToMarkdown(FullReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Black Myth: Wukong Benchmark Tool — отчёт");
        sb.AppendLine();
        sb.AppendLine($"Дата: {report.Date:yyyy-MM-dd HH:mm}  ");
        if (report.InstallDir != null) sb.AppendLine($"Бенчмарк: `{report.InstallDir}`" + (report.BuildId != null ? $" (Steam build {report.BuildId})" : ""));
        sb.AppendLine();

        sb.AppendLine("## Характеристики компьютера");
        sb.AppendLine();
        MdTable(sb, ["Компонент", "Значение"], report.System.ToRows().Select(r => new[] { r.Name, r.Value }));
        var withRun = report.Passes.FirstOrDefault(p => p.Run != null);
        if (withRun != null)
        {
            sb.AppendLine("По данным самого бенчмарка:");
            sb.AppendLine();
            MdTable(sb, ["Поле", "Значение"], ReportModel.BenchmarkSystemFields(withRun.Run!.Result).Select(r => new[] { r.Name, r.Value }));
        }

        sb.AppendLine("## Результаты");
        sb.AppendLine();
        MdTable(sb, ["Метрика", .. report.Passes.Select(p => p.Profile.Title)],
            ReportModel.ResultRows(report.Passes).Select(r => (string[])[r.Metric, .. r.Values]));
        sb.AppendLine(Footnote.Replace("*", "\\*", StringComparison.Ordinal));
        sb.AppendLine();
        foreach (var p in report.Passes.Where(p => p.Error != null))
            sb.AppendLine($"> **{p.Profile.Title}: ошибка** — {p.Error}").AppendLine();

        foreach (var p in report.Passes)
        {
            sb.AppendLine($"## Настройки: {p.Profile.Title}");
            sb.AppendLine();
            MdTable(sb, ["Параметр", "Значение"], p.Profile.Describe().Select(d => new[] { d.Name, d.Value }));

            if (p.Run != null)
            {
                sb.AppendLine("Проверка по результату бенчмарка:");
                sb.AppendLine();
                MdTable(sb, ["Настройка", "Ожидалось", "В результате", ""],
                    ReportModel.Verify(p.Profile, p.Run.Result).Select(c => new[] { c.Name, c.Expected, c.Actual ?? "—", Mark(c.Ok) }));
            }

            sb.AppendLine("**Почему так:**");
            sb.AppendLine();
            foreach (var line in p.Profile.Rationale) sb.AppendLine($"- {line}");
            sb.AppendLine();

            if (p.Changes.Count > 0)
            {
                sb.AppendLine("<details><summary>Что изменено в GameUserSettings.ini</summary>");
                sb.AppendLine();
                MdTable(sb, ["Секция", "Ключ", "Было", "Стало"],
                    p.Changes.Select(c => new[] { c.Section, $"`{c.Key}`", c.OldValue ?? "(не было)", c.NewValue }));
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private static void MdTable(StringBuilder sb, string[] headers, IEnumerable<string[]> rows)
    {
        static string Esc(string s) => s.Replace("|", "\\|", StringComparison.Ordinal);
        sb.AppendLine("| " + string.Join(" | ", headers.Select(Esc)) + " |");
        sb.AppendLine("|" + string.Concat(headers.Select(_ => "---|")));
        foreach (var r in rows) sb.AppendLine("| " + string.Join(" | ", r.Select(Esc)) + " |");
        sb.AppendLine();
    }

    // ───────────────────────────── JSON ─────────────────────────────

    public static string ToJson(FullReport report)
    {
        var model = new
        {
            date = report.Date,
            benchmark = new { installDir = report.InstallDir, steamBuildId = report.BuildId },
            system = report.System,
            passes = report.Passes.Select(p => new
            {
                id = p.Profile.Id,
                title = p.Profile.Title,
                error = p.Error,
                settings = new
                {
                    p.Profile.Width,
                    p.Profile.Height,
                    p.Profile.RenderScale,
                    quality = BenchmarkProfile.QualityNames[p.Profile.Quality],
                    viewDistance = BenchmarkProfile.QualityNames[p.Profile.ViewDistance],
                    rayTracing = p.Profile.RayTracing ? BenchmarkProfile.RayTracingLevelNames[p.Profile.RayTracingLevel - 1] : "Off",
                    p.Profile.FrameGeneration,
                },
                iniChanges = p.Changes,
                result = p.Run == null ? null : new
                {
                    file = p.Run.ResultPath,
                    fpsAvg = p.Run.Result.FpsAvg,
                    fpsMin = p.Run.Result.FpsMin,
                    fpsMax = p.Run.Result.FpsMax,
                    fps95 = p.Run.Result.Fps95,
                    cpuUsageAvg = p.Run.Result.CpuUsageAvg,
                    gpuUsageAvg = p.Run.Result.GpuUsageAvg,
                    videoMemGb = p.Run.Result.VideoMemGb,
                    computed = p.Stats,
                    durationSeconds = Math.Round(p.Run.Duration.TotalSeconds),
                    benchmarkFields = p.Run.Result.Fields,
                    verification = ReportModel.Verify(p.Profile, p.Run.Result),
                },
            }),
        };
        return JsonSerializer.Serialize(model, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        });
    }
}
