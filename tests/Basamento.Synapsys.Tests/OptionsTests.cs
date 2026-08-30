namespace Basamento.Synapsys.Tests;

public sealed class OptionsTests
{
    [Theory]
    [InlineData("250ms", 250)]
    [InlineData("5s", 5_000)]
    [InlineData("2m", 120_000)]
    [InlineData("1h", 3_600_000)]
    [InlineData("00:00:03", 3_000)]
    public void ParsesDurationUnits(string value, double expectedMilliseconds)
    {
        var actual = SynapsysOptions.ParseDuration("duration", value);

        Assert.Equal(expectedMilliseconds, actual.TotalMilliseconds);
    }

    [Fact]
    public void RejectsUnitlessDurationStrings()
    {
        var exception = Assert.Throws<SynapsysConfigurationException>(
            () => SynapsysOptions.ParseDuration("duration", "5000"));

        Assert.Contains("must include a unit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsFractionalHeartbeatInterval()
    {
        var options = ValidOptions();
        options.HeartbeatInterval = TimeSpan.FromMilliseconds(1_500);

        Assert.Throws<SynapsysConfigurationException>(options.ValidateForStartup);
    }

    [Fact]
    public void MissingTokenIsAWarningRatherThanAValidationFailure()
    {
        var options = ValidOptions();
        options.CoreToken = null;

        var warnings = options.ValidateForStartup();

        Assert.Single(warnings);
        Assert.Contains("token", warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsCoreUrlWithCredentialsOrQuery()
    {
        var options = ValidOptions();
        options.CoreUrl = new Uri("https://user:secret@example.test/base?token=value");

        Assert.Throws<SynapsysConfigurationException>(options.ValidateForStartup);
    }

    internal static SynapsysOptions ValidOptions() => new()
    {
        Enabled = true,
        CoreUrl = new Uri("https://core.example.test"),
        CoreToken = "test-token",
        WorkerName = "test-worker",
        Host = "test-host",
        HeartbeatInterval = TimeSpan.FromSeconds(1),
        ConnectTimeout = TimeSpan.FromMilliseconds(200),
        RequestTimeout = TimeSpan.FromSeconds(1),
        ShutdownTimeout = TimeSpan.FromSeconds(1),
        CaptureConsole = true,
    };
}

