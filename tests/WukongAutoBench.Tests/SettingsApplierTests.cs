using System.Text;
using WukongAutoBench.Config;
using Xunit;

namespace WukongAutoBench.Tests;

public class SettingsApplierTests
{
    private static IniDocument LoadSample() =>
        IniDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestData", "GameUserSettings.sample.ini"));

    private static UiSettingData Ui(IniDocument ini) =>
        UiSettingData.Parse(ini.Get(SettingsApplier.GameSection, "UISettingData")!);

    [Fact]
    public void CpuProfile_WritesMenuValuesScalabilityGroupsAndResolution()
    {
        var ini = LoadSample();
        SettingsApplier.Apply(ini, BenchmarkProfile.Cpu());

        var ui = Ui(ini);
        Assert.Equal("1", ui["QualityLevel"]);
        Assert.Equal("5", ui["ViewDistance"]);
        Assert.Equal("360", ui["ImageQuality"]);            // 720 * 50%
        Assert.Equal("0", ui["Rtx"]);
        Assert.Equal("0", ui["InsertFrame"]);
        Assert.Equal("0", ui["Vsync"]);
        Assert.Equal("0", ui["LockFrameRate"]);
        Assert.Equal("0", ui["ScreenMode"]);
        Assert.Equal("1", ui["ShadowQuality"]);
        Assert.Equal("1280 × 720", ui["ScreenResolution"]);  // формат «Ш × В» сохранён
        Assert.Equal("2", ui["RtxLevel"]);                  // при выключенном RT уровень не трогаем
        Assert.Equal("3", ui["Dlss"]);                      // апскейлер не трогаем

        Assert.Equal("50", ini.Get("ScalabilityGroups", "sg.ResolutionQuality"));
        Assert.Equal("4", ini.Get("ScalabilityGroups", "sg.ViewDistanceQuality"));
        Assert.Equal("0", ini.Get("ScalabilityGroups", "sg.ShadowQuality"));
        Assert.Equal("0", ini.Get("ScalabilityGroups", "sg.FoliageQuality"));

        Assert.Equal("1280", ini.Get(SettingsApplier.GameSection, "ResolutionSizeX"));
        Assert.Equal("720", ini.Get(SettingsApplier.GameSection, "LastUserConfirmedResolutionSizeY"));
        Assert.Equal("0", ini.Get(SettingsApplier.GameSection, "FullscreenMode"));
        Assert.Equal("False", ini.Get(SettingsApplier.GameSection, "bUseVSync"));
        Assert.Equal("0.000000", ini.Get(SettingsApplier.GameSection, "FrameRateLimit"));
    }

    [Fact]
    public void GpuProfile_EnablesFullRayTracingAtMaxQuality()
    {
        var ini = LoadSample();
        SettingsApplier.Apply(ini, BenchmarkProfile.Gpu(2560, 1440));

        var ui = Ui(ini);
        Assert.Equal("5", ui["QualityLevel"]);
        Assert.Equal("1440", ui["ImageQuality"]);
        Assert.Equal("1", ui["Rtx"]);
        Assert.Equal("4", ui["RtxLevel"]);
        Assert.Equal("5", ui["GlobalIllumination"]);
        Assert.Equal("100", ini.Get("ScalabilityGroups", "sg.ResolutionQuality"));
        Assert.Equal("4", ini.Get("ScalabilityGroups", "sg.TextureQuality"));
        Assert.Equal("2560", ini.Get(SettingsApplier.GameSection, "DesiredScreenWidth"));
    }

    [Fact]
    public void Apply_DoesNotTouchUnrelatedLinesAndKeepsUiFormat()
    {
        var original = LoadSample().ToString();
        var ini = LoadSample();
        SettingsApplier.Apply(ini, BenchmarkProfile.Cpu());
        var patched = ini.ToString();

        var before = original.Split('\n');
        var after = patched.Split('\n');
        Assert.Equal(before.Length, after.Length);
        Assert.Contains("WindowPosX=-1", patched);
        Assert.Contains("AudioQualityLevel=0", patched);
        Assert.Contains("bUseDesiredScreenHeight=False", patched);
        Assert.StartsWith("UISettingData=((\"ScreenMode\", \"0\"),(\"ScreenRatio\", \"0\")", after[1]);
    }

    [Fact]
    public void IniDocument_PreservesBomAndLineEndings()
    {
        var text = "[A]\r\nKey=1\r\n";
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text)).ToArray();
        var doc = IniDocument.FromBytes(bytes);
        doc.Set("A", "Key", "2");
        doc.Set("A", "New", "x");

        var saved = doc.ToBytes();
        Assert.Equal(0xFF, saved[0]);
        Assert.Equal(0xFE, saved[1]);
        Assert.Equal("[A]\r\nKey=2\r\nNew=x\r\n", Encoding.Unicode.GetString(saved, 2, saved.Length - 2));
    }

    [Fact]
    public void Apply_ThrowsWhenUiSettingDataMissing()
    {
        var ini = IniDocument.Parse("[ScalabilityGroups]\nsg.ShadowQuality=2\n");
        Assert.Throws<InvalidDataException>(() => SettingsApplier.Apply(ini, BenchmarkProfile.Cpu()));
    }
}
