using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Poc.Acceptance;

namespace DesktopAgent.Tests.DesktopAutomation;

public sealed class M3AutomaticAcceptanceOptionsTests
{
    [Fact]
    public void NoAcceptanceVariables_ReturnsDisabled()
    {
        var options = M3AutomaticAcceptanceOptions.FromLookup(_ => null);

        Assert.False(options.IsEnabled);
        Assert.Equal("Disabled", options.Describe());
    }

    [Fact]
    public void PartialConfiguration_FailsClosed()
    {
        var values = ValidValues();
        values.Remove(M3AutomaticAcceptanceOptions.MaxStepsVariable);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            M3AutomaticAcceptanceOptions.FromLookup(values.GetValueOrDefault));

        Assert.Contains(
            M3AutomaticAcceptanceOptions.MaxStepsVariable,
            ex.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("validatedopen")]
    [InlineData("ValidatedOpen ")]
    [InlineData("")]
    public void ModeMustMatchExactly(string mode)
    {
        var values = ValidValues();
        values[M3AutomaticAcceptanceOptions.ModeVariable] = mode;

        Assert.Throws<InvalidOperationException>(() =>
            M3AutomaticAcceptanceOptions.FromLookup(values.GetValueOrDefault));
    }

    [Theory]
    [InlineData("I_UNDERSTAND_THIS_ALLOWS_UNATTENDED_REAL_DESKTOP_WRITE")]
    [InlineData("I_UNDERSTAND_THIS_ALLOWS_UNATTENDED_REAL_DESKTOP_WRITES ")]
    [InlineData("")]
    public void ArmPhraseMustMatchExactly(string arm)
    {
        var values = ValidValues();
        values[M3AutomaticAcceptanceOptions.ArmVariable] = arm;

        Assert.Throws<InvalidOperationException>(() =>
            M3AutomaticAcceptanceOptions.FromLookup(values.GetValueOrDefault));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("")]
    public void MoveLimitMustStayInsideAcceptanceHardCap(string value)
    {
        var values = ValidValues();
        values[M3AutomaticAcceptanceOptions.MaxMovesVariable] = value;

        Assert.Throws<InvalidOperationException>(() =>
            M3AutomaticAcceptanceOptions.FromLookup(values.GetValueOrDefault));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("21")]
    [InlineData("-1")]
    [InlineData("2.0")]
    [InlineData("")]
    public void StepLimitMustStayInsideAcceptanceHardCap(string value)
    {
        var values = ValidValues();
        values[M3AutomaticAcceptanceOptions.MaxStepsVariable] = value;

        Assert.Throws<InvalidOperationException>(() =>
            M3AutomaticAcceptanceOptions.FromLookup(values.GetValueOrDefault));
    }

    [Theory]
    [InlineData("True")]
    [InlineData("FALSE")]
    [InlineData("yes")]
    [InlineData("")]
    public void StagingChoiceMustBeExplicitLowercaseBoolean(string value)
    {
        var values = ValidValues();
        values[M3AutomaticAcceptanceOptions.AllowStagingVariable] = value;

        Assert.Throws<InvalidOperationException>(() =>
            M3AutomaticAcceptanceOptions.FromLookup(values.GetValueOrDefault));
    }

    [Fact]
    public void FullyArmedConfiguration_CreatesValidatedOpenRuntimeButStartsInSuggestion()
    {
        var values = ValidValues();
        values[M3AutomaticAcceptanceOptions.MaxMovesVariable] = "3";
        values[M3AutomaticAcceptanceOptions.MaxStepsVariable] = "6";
        values[M3AutomaticAcceptanceOptions.AllowStagingVariable] = "false";

        var options = M3AutomaticAcceptanceOptions.FromLookup(values.GetValueOrDefault);
        var runtime = options.CreateValidatedRuntimePolicy(TimeProvider.System);
        var snapshot = runtime.Snapshot;

        Assert.True(options.IsEnabled);
        Assert.Equal(3, options.MaxMoveCount);
        Assert.Equal(6, options.MaxExecutionStepCount);
        Assert.False(options.AllowTemporaryStaging);
        Assert.True(snapshot.UnattendedGate.IsOpen);
        Assert.Equal(
            DesktopAutomationUnattendedGateReason.None,
            snapshot.UnattendedGate.Reason);
        Assert.Equal(DesktopAutomationMode.Suggestion, snapshot.RequestedMode);
        Assert.Equal(DesktopAutomationMode.Suggestion, snapshot.EffectiveMode);
        Assert.False(snapshot.CanAttemptUnattendedExecution);

        var automatic = runtime.SetRequestedMode(DesktopAutomationMode.Automatic);
        Assert.Equal(DesktopAutomationMode.Automatic, automatic.RequestedMode);
        Assert.Equal(DesktopAutomationMode.Automatic, automatic.EffectiveMode);
        Assert.True(automatic.CanAttemptUnattendedExecution);
    }

    [Fact]
    public void FullyArmedConfiguration_CarriesOnlyExplicitAcceptanceRiskBudget()
    {
        var values = ValidValues();
        values[M3AutomaticAcceptanceOptions.MaxMovesVariable] = "10";
        values[M3AutomaticAcceptanceOptions.MaxStepsVariable] = "20";
        values[M3AutomaticAcceptanceOptions.AllowStagingVariable] = "true";

        var options = M3AutomaticAcceptanceOptions.FromLookup(values.GetValueOrDefault);
        var risk = options.RiskSettings;

        Assert.Equal(10, risk.MaxMoveCount);
        Assert.Equal(20, risk.MaxExecutionStepCount);
        Assert.True(risk.AllowTemporaryStaging);
    }

    [Fact]
    public void DisabledOptions_CannotCreateValidatedRuntime()
    {
        Assert.Throws<InvalidOperationException>(() =>
            M3AutomaticAcceptanceOptions.Disabled.CreateValidatedRuntimePolicy(TimeProvider.System));
    }

    private static Dictionary<string, string?> ValidValues() =>
        new(StringComparer.Ordinal)
        {
            [M3AutomaticAcceptanceOptions.ModeVariable] =
                M3AutomaticAcceptanceOptions.SupportedMode,
            [M3AutomaticAcceptanceOptions.ArmVariable] =
                M3AutomaticAcceptanceOptions.RequiredArmValue,
            [M3AutomaticAcceptanceOptions.MaxMovesVariable] = "2",
            [M3AutomaticAcceptanceOptions.MaxStepsVariable] = "4",
            [M3AutomaticAcceptanceOptions.AllowStagingVariable] = "false",
        };
}
