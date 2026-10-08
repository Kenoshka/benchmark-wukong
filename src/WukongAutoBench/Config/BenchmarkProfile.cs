namespace WukongAutoBench.Config;

/// <summary>Набор графических настроек для одного прохода бенчмарка.</summary>
internal sealed record BenchmarkProfile
{
    public static readonly string[] QualityNames = ["Low", "Medium", "High", "Very High", "Cinematic"];
    public static readonly string[] RayTracingLevelNames = ["Low", "Medium", "High", "Very High"];

    public required string Id { get; init; }
    public required string Title { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Масштаб рендера (Super Resolution), % от выходного разрешения.</summary>
    public required int RenderScale { get; init; }

    /// <summary>Общий пресет и групповые настройки качества: 0 = Low … 4 = Cinematic (как sg.* в UE).</summary>
    public required int Quality { get; init; }

    /// <summary>Дальность прорисовки: 0 = Low … 4 = Cinematic.</summary>
    public required int ViewDistance { get; init; }

    /// <summary>Full Ray Tracing (path tracing) вкл/выкл.</summary>
    public required bool RayTracing { get; init; }

    /// <summary>Уровень Full RT в нумерации меню: 1 = Low … 4 = Very High.</summary>
    public int RayTracingLevel { get; init; } = 1;

    public bool FrameGeneration { get; init; }

    /// <summary>Почему выбраны именно такие настройки (выводится в отчёт).</summary>
    public required IReadOnlyList<string> Rationale { get; init; }

    public int RenderHeight => (int)Math.Round(Height * RenderScale / 100.0);

    public string Resolution => $"{Width}x{Height}";

    /// <summary>Человекочитаемая сводка настроек для отчёта.</summary>
    public IReadOnlyList<(string Name, string Value)> Describe() =>
    [
        ("Разрешение", $"{Width}x{Height}, полноэкранный режим"),
        ("Масштаб рендера (Super Resolution)", $"{RenderScale}% → внутренний рендер ≈ {Width * RenderScale / 100}x{RenderHeight}"),
        ("Пресет качества", QualityNames[Quality]),
        ("Групповые настройки качества", $"{QualityNames[Quality]}: тени, текстуры, эффекты, GI, отражения, растительность, материалы, пост-обработка, AA"),
        ("Дальность прорисовки", QualityNames[ViewDistance]),
        ("Full Ray Tracing", RayTracing ? $"Вкл, уровень {RayTracingLevelNames[RayTracingLevel - 1]}" : "Выкл"),
        ("Генерация кадров", FrameGeneration ? "Вкл" : "Выкл"),
        ("VSync / ограничение FPS / динамическое разрешение", "Выкл / нет / выкл"),
    ];

    /// <summary>
    /// CPU-тест: минимальная работа для видеокарты, при этом максимальное число объектов на сцене.
    /// </summary>
    public static BenchmarkProfile Cpu(int width = 1280, int height = 720) => new()
    {
        Id = "cpu",
        Title = "CPU-тест",
        Width = width,
        Height = height,
        RenderScale = 50,
        Quality = 0,
        ViewDistance = 4,
        RayTracing = false,
        Rationale =
        [
            "1280x720 при масштабе рендера 50% (≈640x360 внутреннего рендера): пикселей в 9 раз меньше, чем в 1080p, и в 36 раз меньше, чем в 4K; " +
            "стоимость кадра для GPU становится почти нулевой, и FPS определяется скоростью, с которой процессор готовит кадры.",
            "Все «пиксельные» настройки (тени, GI, отражения, эффекты, пост-обработка, текстуры, сглаживание) — Low: " +
            "они нагружают в основном видеокарту и на CPU почти не влияют.",
            "Full Ray Tracing выключен — это самая тяжёлая для GPU опция; генерация кадров выключена — " +
            "она дорисовывает кадры на GPU и завышает FPS, не отражая скорость процессора.",
            "Дальность прорисовки — Cinematic: она увеличивает число объектов, draw calls, работу LOD/culling и анимации, " +
            "то есть добавляет работу именно процессору (game/render thread), а на 360p почти ничего не стоит для GPU.",
            "VSync и лимит FPS выключены, чтобы ничто не ограничивало FPS сверху.",
            "Что тест действительно упирается в CPU, проверяется после прогона по покадровым CPU/GPU frame time из результатов бенчмарка.",
        ],
    };

    /// <summary>
    /// GPU-тест: максимальная нагрузка на видеокарту — родное разрешение, 100% рендер, Cinematic, Full RT Very High.
    /// </summary>
    public static BenchmarkProfile Gpu(int width, int height) => new()
    {
        Id = "gpu",
        Title = "GPU-тест",
        Width = width,
        Height = height,
        RenderScale = 100,
        Quality = 4,
        ViewDistance = 4,
        RayTracing = true,
        RayTracingLevel = 4,
        Rationale =
        [
            $"Родное разрешение монитора ({width}x{height}) и масштаб рендера 100%: апскейлер не уменьшает внутреннее разрешение, " +
            "GPU рендерит каждый выходной пиксель (на 100% DLSS/FSR/XeSS работают как сглаживание в полном разрешении).",
            "Пресет и все групповые настройки — Cinematic: максимальное качество теней, GI (Lumen), отражений, растительности, эффектов и текстур.",
            "Full Ray Tracing (path tracing) — уровень Very High: самая тяжёлая для GPU нагрузка в игре, задействует RT-ядра и сильно увеличивает время кадра на GPU.",
            "Генерация кадров выключена: она добавляет «ненастоящие» кадры и скрыла бы реальную производительность рендера.",
            "VSync и лимит FPS выключены, чтобы видеокарта работала на 100% без пауз.",
        ],
    };
}
