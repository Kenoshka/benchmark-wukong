using System.Globalization;
using WukongAutoBench.Runner;

namespace WukongAutoBench;

internal sealed record Options
{
    public string? GameDir { get; init; }
    public string[] Passes { get; init; } = ["cpu", "gpu"];
    public LaunchMode Launch { get; init; } = LaunchMode.Exe;
    public (int W, int H) CpuResolution { get; init; } = (1280, 720);
    public (int W, int H)? GpuResolution { get; init; }
    public string OutDir { get; init; } = Path.Combine(Environment.CurrentDirectory, "results");
    public int MenuDelaySeconds { get; init; } = 20;
    public int TimeoutMinutes { get; init; } = 20;
    public int CooldownSeconds { get; init; } = 15;
    public (double X, double Y)? StartButton { get; init; }
    public (double X, double Y)? ConfirmButton { get; init; }
    public bool DryRun { get; init; }
    public bool Install { get; init; }
    public bool NoPause { get; init; }
    public bool Help { get; init; }
    public IReadOnlyList<string> FromResults { get; init; } = [];

    public const string Usage = """
        WukongAutoBench — автоматический CPU- и GPU-прогон Black Myth: Wukong Benchmark Tool (Steam AppID 3132990).

        Использование:
          WukongAutoBench.exe [параметры]

        Без параметров: находит бенчмарк в библиотеках Steam, по очереди выполняет CPU- и GPU-тест,
        восстанавливает исходные настройки и печатает отчёт (копия — в results\<дата>\report.md / report.json).

        Параметры:
          --game-dir <путь>          папка Benchmark Tool, если автопоиск не нашёл её
          --only cpu|gpu             выполнить только один проход
          --launch exe|steam         как запускать: напрямую exe (по умолчанию) или через steam -applaunch
          --cpu-resolution 1280x720  разрешение CPU-теста
          --gpu-resolution 2560x1440 разрешение GPU-теста (по умолчанию — текущее разрешение монитора)
          --out <папка>              куда складывать отчёты (по умолчанию .\results)
          --menu-delay <сек>         пауза после появления окна до первых нажатий (по умолчанию 20)
          --timeout <мин>            предельное время одного прохода (по умолчанию 20)
          --cooldown <сек>           пауза между проходами для остывания (по умолчанию 15)
          --start-button x,y         координаты кнопки «Тест быстродействия» в долях окна (по умолчанию 0.11,0.455)
          --confirm-button x,y       координаты кнопки «Подтвердить» в долях окна (по умолчанию 0.39,0.59)
          --dry-run                  ничего не запускать: показать, какие настройки будут записаны
          --from-results <cpu> [gpu] построить отчёт по уже готовым файлам результатов бенчмарка
          --install                  если бенчмарк не установлен — открыть его установку в Steam
          --no-pause                 не ждать Enter в конце
          -h, --help                 эта справка
        """;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i].ToLowerInvariant();
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"После {a} нужно значение.");

            o = a switch
            {
                "-h" or "--help" or "/?" => o with { Help = true },
                "--game-dir" => o with { GameDir = Next() },
                "--only" => o with { Passes = [ParsePass(Next())] },
                "--launch" => o with { Launch = Next().ToLowerInvariant() switch
                {
                    "exe" => LaunchMode.Exe,
                    "steam" => LaunchMode.Steam,
                    var v => throw new ArgumentException($"--launch: ожидается exe или steam, получено «{v}»."),
                } },
                "--cpu-resolution" => o with { CpuResolution = ParseResolution(Next()) },
                "--gpu-resolution" => o with { GpuResolution = ParseResolution(Next()) },
                "--out" => o with { OutDir = Path.GetFullPath(Next()) },
                "--menu-delay" => o with { MenuDelaySeconds = ParseInt(Next(), a) },
                "--timeout" => o with { TimeoutMinutes = ParseInt(Next(), a) },
                "--cooldown" => o with { CooldownSeconds = ParseInt(Next(), a) },
                "--start-button" => o with { StartButton = ParsePoint(Next(), a) },
                "--confirm-button" => o with { ConfirmButton = ParsePoint(Next(), a) },
                "--dry-run" => o with { DryRun = true },
                "--install" => o with { Install = true },
                "--no-pause" => o with { NoPause = true },
                "--from-results" => o with { FromResults = TakeFiles(args, ref i) },
                _ => throw new ArgumentException($"Неизвестный параметр «{args[i]}». Справка: --help"),
            };
        }
        return o;
    }

    private static string[] TakeFiles(string[] args, ref int i)
    {
        var files = new List<string>();
        while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && files.Count < 2)
            files.Add(Path.GetFullPath(args[++i]));
        if (files.Count == 0) throw new ArgumentException("После --from-results укажите файл(ы) результата.");
        return [.. files];
    }

    private static string ParsePass(string v) => v.ToLowerInvariant() is "cpu" or "gpu"
        ? v.ToLowerInvariant()
        : throw new ArgumentException($"--only: ожидается cpu или gpu, получено «{v}».");

    internal static (int W, int H) ParseResolution(string v)
    {
        var parts = v.ToLowerInvariant().Split('x', '×', '*');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w >= 640 && h >= 360)
            return (w, h);
        throw new ArgumentException($"Разрешение должно быть вида 1920x1080, получено «{v}».");
    }

    private static int ParseInt(string v, string name) =>
        int.TryParse(v, out var n) && n >= 0 ? n : throw new ArgumentException($"{name}: ожидается неотрицательное число.");

    private static (double, double) ParsePoint(string v, string name)
    {
        var parts = v.Split(',', ';');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            && x is > 0 and < 1 && y is > 0 and < 1)
            return (x, y);
        throw new ArgumentException($"{name}: ожидаются доли окна, например 0.11,0.455.");
    }
}
