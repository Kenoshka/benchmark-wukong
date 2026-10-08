using WukongAutoBench.Config;
using WukongAutoBench.Reporting;
using WukongAutoBench.Results;
using WukongAutoBench.SystemInfo;
using Xunit;

namespace WukongAutoBench.Tests;

public class ResultParsingTests
{
    private static BenchmarkResult LoadSample() =>
        BenchmarkResult.TryLoad(Path.Combine(AppContext.BaseDirectory, "TestData", "benchmark_result.sample.json"))!;

    [Fact]
    public void ParsesSummaryFieldsAndRecords()
    {
        var r = LoadSample();
        Assert.NotNull(r);
        Assert.Equal(117.8, r.FpsAvg);
        Assert.Equal(60.3, r.FpsMin);
        Assert.Equal(98.3, r.Fps95);
        Assert.Equal("NVIDIA GeForce RTX 4060 Ti", r.Field("GPUModel"));
        Assert.Equal(1, r.IntField("QualityLevel"));
        Assert.Equal(6, r.Records.Count);
    }

    [Fact]
    public void ComputesFrameStatsAndBottleneck()
    {
        var stats = FrameStats.From(LoadSample().Records)!;
        Assert.Equal(6, stats.Samples);
        Assert.Equal(115.0, stats.MedianFps, 3);
        Assert.True(stats.Low1Fps < 52 && stats.Low1Fps >= 50);
        Assert.Equal(1 / 6.0, stats.GpuBoundShare!.Value, 6);   // только кадр с 19 мс из 20 упёрся в GPU
        Assert.Equal("CPU", stats.Bottleneck);
        Assert.Equal(5300, stats.PeakVramMb);
    }

    [Fact]
    public void RejectsNonBenchmarkJson()
    {
        Assert.Null(BenchmarkResult.TryParse("{\"foo\": 1}"));
        Assert.Null(BenchmarkResult.TryParse("{\"FPSAvg\": 1")); // файл ещё дописывается
    }

    [Fact]
    public void VerifyMatchesCpuProfile()
    {
        var checks = ReportModel.Verify(BenchmarkProfile.Cpu(), LoadSample());
        Assert.All(checks.Where(c => c.Ok != null), c => Assert.True(c.Ok, c.Name));
        Assert.Contains(checks, c => c.Name.StartsWith("Разрешение") && c.Ok == true);
    }

    [Fact]
    public void Percentile_Interpolates()
    {
        Assert.Equal(2.5, FrameStats.Percentile([1, 2, 3, 4], 0.5));
        Assert.Equal(4, FrameStats.Percentile([1, 2, 3, 4], 1.0));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4060 Ti", "32.0.15.6636", "566.36 (32.0.15.6636)")]
    [InlineData("AMD Radeon RX 7800 XT", "31.0.24033.1003", "31.0.24033.1003")]
    public void FriendlyDriverVersion(string gpu, string raw, string expected) =>
        Assert.Equal(expected, SystemInfoCollector.FriendlyDriverVersion(gpu, raw));
}
