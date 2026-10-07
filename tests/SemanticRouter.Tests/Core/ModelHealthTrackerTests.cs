using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.Core;

public class ModelHealthTrackerTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly ModelDefinition _model = Model("claude-cli", 4);

    private ModelHealthTracker Create() => new(
        new ModelSelectionOptions { QuotaCooldown = TimeSpan.FromMinutes(15), FailureCooldown = TimeSpan.FromSeconds(30) }, _time);

    [Fact]
    public void Models_start_available()
    {
        Create().IsAvailable(_model).Should().BeTrue();
    }

    [Fact]
    public void Quota_failures_cool_down_longer_than_transient_failures()
    {
        ModelHealthTracker health = Create();
        ModelDefinition other = Model("other", 1);

        health.ReportFailure(_model, ModelFailureKind.QuotaExceeded);
        health.ReportFailure(other, ModelFailureKind.Unavailable);
        _time.Advance(TimeSpan.FromMinutes(1));

        health.IsAvailable(other).Should().BeTrue();
        health.IsAvailable(_model).Should().BeFalse();
        health.GetCooldowns().Should().ContainKey("claude-cli");

        _time.Advance(TimeSpan.FromMinutes(14));
        health.IsAvailable(_model).Should().BeTrue();
        health.GetCooldowns().Should().BeEmpty();
    }

    [Fact]
    public void Success_clears_a_cooldown()
    {
        ModelHealthTracker health = Create();
        health.ReportFailure(_model, ModelFailureKind.QuotaExceeded);

        health.ReportSuccess(_model);

        health.IsAvailable(_model).Should().BeTrue();
    }

    [Fact]
    public void Zero_cooldown_disables_tracking()
    {
        var health = new ModelHealthTracker(new ModelSelectionOptions { FailureCooldown = TimeSpan.Zero }, _time);

        health.ReportFailure(_model, ModelFailureKind.Unavailable);

        health.IsAvailable(_model).Should().BeTrue();
    }
}
