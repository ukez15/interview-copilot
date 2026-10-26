using FluentAssertions;
using InterviewCopilot.Benchmark;

namespace InterviewCopilot.UnitTests.Benchmark;

public sealed class HardwareProbeTests
{
    [Fact]
    public void ParseCpuInfoModelName_ShouldReturnFirstModelName()
    {
        const string CpuInfo = "processor\t: 0\nvendor_id\t: GenuineIntel\nmodel name\t: Intel(R) Core(TM) i7-1165G7 @ 2.80GHz\nmodel name\t: other\n";

        HardwareProbe.ParseCpuInfoModelName(CpuInfo).Should().Be("Intel(R) Core(TM) i7-1165G7 @ 2.80GHz");
    }

    [Fact]
    public void ParseCpuInfoModelName_ShouldReturnNull_WhenAbsent()
    {
        HardwareProbe.ParseCpuInfoModelName("processor\t: 0\n").Should().BeNull();
    }

    [Fact]
    public void Detect_ShouldFillEveryField()
    {
        var info = HardwareProbe.Detect();

        info.Cpu.Should().NotBeNullOrWhiteSpace();
        info.LogicalCores.Should().BeGreaterThan(0);
        info.TotalRamBytes.Should().BeGreaterThan(0);
        info.Os.Should().NotBeNullOrWhiteSpace();
    }
}
