using System.Globalization;
using System.Text.RegularExpressions;

namespace WukongAutoBench.Config;

/// <summary>Одно изменение в ini: где, что было, что стало.</summary>
internal sealed record SettingChange(string Section, string Key, string? OldValue, string NewValue);

/// <summary>
/// Записывает профиль в GameUserSettings.ini Benchmark Tool.
/// Настройки хранятся в трёх местах, и все три должны совпадать, иначе игра при старте «откатит» часть значений:
/// <list type="number">
/// <item>[/Script/GSGameSettings.GSGameUserSettings] — разрешение, режим окна, VSync, лимит FPS (стандартные поля UE);</item>
/// <item>UISettingData в той же секции — значения меню (1 = Low … 5 = Cinematic, ImageQuality = высота рендера в пикселях);</item>
/// <item>[ScalabilityGroups] — группы качества UE (sg.* = значение меню − 1, sg.ResolutionQuality = масштаб в %).</item>
/// </list>
/// </summary>
internal static partial class SettingsApplier
{
    public const string GameSection = "/Script/GSGameSettings.GSGameUserSettings";
    public const string ScalabilitySection = "ScalabilityGroups";

    /// <summary>Пары «ключ UISettingData» → «группа ScalabilityGroups» для групповых настроек качества.</summary>
    private static readonly (string Ui, string Sg)[] QualityGroups =
    [
        ("AntiAliasing", "sg.AntiAliasingQuality"),
        ("PostProcessing", "sg.PostProcessQuality"),
        ("ShadowQuality", "sg.ShadowQuality"),
        ("TextureQuality", "sg.TextureQuality"),
        ("FxQuality", "sg.EffectsQuality"),
        ("MaterialQuality", "sg.ShadingQuality"),
        ("VegetationQuality", "sg.FoliageQuality"),
        ("GlobalIllumination", "sg.GlobalIlluminationQuality"),
        ("ReflectionQuality", "sg.ReflectionQuality"),
    ];

    [GeneratedRegex(@"^(?<w>\d+)(?<sep>\s*[x×X*]\s*)(?<h>\d+)$")]
    private static partial Regex ResolutionValueRegex();

    public static IReadOnlyList<SettingChange> Apply(IniDocument ini, BenchmarkProfile p)
    {
        var changes = new List<SettingChange>();

        var uiLocation = ini.Find("UISettingData")
            ?? throw new InvalidDataException(
                "В GameUserSettings.ini нет строки UISettingData. Запустите Benchmark Tool вручную один раз, " +
                "откройте настройки графики и нажмите «Применить», затем повторите запуск утилиты.");
        var gameSection = uiLocation.Section;

        void Set(string section, string key, string value, bool add = true)
        {
            var existed = ini.Get(section, key) != null;
            if (!existed && !add) return;
            var old = ini.Set(section, key, value, add);
            if (old != value) changes.Add(new SettingChange(section, key, old, value));
        }

        // Стандартные поля UE: ставим во всех секциях, где они есть (UE хранит часть из них и в [/Script/Engine.GameUserSettings]).
        void SetEngine(string key, string value)
        {
            var old = ini.Find(key)?.Value;
            if (ini.SetEverywhere(key, value) == 0) ini.Set(gameSection, key, value);
            if (old != value) changes.Add(new SettingChange(gameSection, key, old, value));
        }

        // 1. Разрешение и режим окна (0 = полноэкранный).
        foreach (var k in new[] { "ResolutionSizeX", "LastUserConfirmedResolutionSizeX", "DesiredScreenWidth", "LastUserConfirmedDesiredScreenWidth" })
            SetEngine(k, p.Width.ToString(CultureInfo.InvariantCulture));
        foreach (var k in new[] { "ResolutionSizeY", "LastUserConfirmedResolutionSizeY", "DesiredScreenHeight", "LastUserConfirmedDesiredScreenHeight" })
            SetEngine(k, p.Height.ToString(CultureInfo.InvariantCulture));
        foreach (var k in new[] { "FullscreenMode", "LastConfirmedFullscreenMode", "PreferredFullscreenMode" })
            SetEngine(k, "0");
        SetEngine("bUseVSync", "False");
        SetEngine("bUseDynamicResolution", "False");
        SetEngine("FrameRateLimit", "0.000000");

        // 2. Группы качества UE.
        var scaleOld = ini.Get(ScalabilitySection, "sg.ResolutionQuality");
        var scale = scaleOld != null && scaleOld.Contains('.')
            ? p.RenderScale.ToString("0.000000", CultureInfo.InvariantCulture)
            : p.RenderScale.ToString(CultureInfo.InvariantCulture);
        Set(ScalabilitySection, "sg.ResolutionQuality", scale);
        Set(ScalabilitySection, "sg.ViewDistanceQuality", p.ViewDistance.ToString(CultureInfo.InvariantCulture));
        foreach (var (_, sg) in QualityGroups)
            Set(ScalabilitySection, sg, p.Quality.ToString(CultureInfo.InvariantCulture));
        // Нестандартная группа: трогаем, только если игра сама её создала.
        Set(ScalabilitySection, "sg.RayTracingQuality", (p.RayTracing ? p.RayTracingLevel - 1 : 0).ToString(CultureInfo.InvariantCulture), add: false);

        // 3. Значения меню.
        var ui = UiSettingData.Parse(uiLocation.Value);
        if (ui.Pairs.Count == 0)
            throw new InvalidDataException($"Не удалось разобрать UISettingData (неизвестный формат): {uiLocation.Value}");
        var uiChanges = new List<SettingChange>();
        void SetUi(string key, string value, bool add = true)
        {
            if (!add && !ui.Contains(key)) return;
            var old = ui.Set(key, value, add);
            if (old != value) uiChanges.Add(new SettingChange(gameSection, $"UISettingData.{key}", old, value));
        }

        SetUi("ScreenMode", "0");
        SetUi("Vsync", "0");
        SetUi("LockFrameRate", "0");
        SetUi("InsertFrame", p.FrameGeneration ? "1" : "0");
        SetUi("ImageQuality", p.RenderHeight.ToString(CultureInfo.InvariantCulture));
        SetUi("QualityLevel", (p.Quality + 1).ToString(CultureInfo.InvariantCulture));
        SetUi("ViewDistance", (p.ViewDistance + 1).ToString(CultureInfo.InvariantCulture));
        SetUi("Rtx", p.RayTracing ? "1" : "0");
        if (p.RayTracing) SetUi("RtxLevel", p.RayTracingLevel.ToString(CultureInfo.InvariantCulture));
        foreach (var (key, _) in QualityGroups)
            SetUi(key, (p.Quality + 1).ToString(CultureInfo.InvariantCulture), add: false);

        // Разрешение в меню: формат значения заранее неизвестен, поэтому меняем только если это явно «Ш x В».
        if (ui["ScreenResolution"] is { } res && ResolutionValueRegex().Match(res) is { Success: true } m)
            SetUi("ScreenResolution", $"{p.Width}{m.Groups["sep"].Value}{p.Height}");

        var serialized = ui.Serialize();
        if (serialized != uiLocation.Value)
            ini.Set(gameSection, "UISettingData", serialized);
        changes.AddRange(uiChanges);

        return changes;
    }
}
