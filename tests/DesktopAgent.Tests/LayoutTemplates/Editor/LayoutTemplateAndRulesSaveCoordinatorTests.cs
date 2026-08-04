using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Poc.LayoutTemplates.Editor;

namespace DesktopAgent.Tests.LayoutTemplates.Editor;

public sealed class FakeTemplateStore : IDesktopLayoutTemplateStore
{
    public DesktopLayoutTemplate? SavedTemplate;
    public bool SaveThrows;
    public bool GetThrows;
    private DesktopLayoutTemplate? _stored;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveAsync(DesktopLayoutTemplate t, CancellationToken ct = default)
    {
        if (SaveThrows) throw new InvalidOperationException("Fake save error");
        _stored = t; SavedTemplate = t; return Task.CompletedTask;
    }
    public Task<DesktopLayoutTemplate?> GetAsync(Guid id, CancellationToken ct = default)
    {
        if (GetThrows) throw new InvalidOperationException("Fake get error");
        return Task.FromResult(_stored?.TemplateId == id ? _stored : null);
    }
    public Task<IReadOnlyList<DesktopLayoutTemplateSummary>> ListAsync(
        int limit = 50, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DesktopLayoutTemplateSummary>>(Array.Empty<DesktopLayoutTemplateSummary>());
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        { _stored = null; return Task.FromResult(true); }
}

public sealed class FakeRuleStore : IDesktopLayoutIconRuleStore
{
    public DesktopLayoutIconRuleSet? SavedRuleSet;
    public bool SaveThrows;
    public int SaveCallCount;
    public bool GetThrows;
    public DesktopLayoutIconRuleSet? ReturnOnGet;
    public bool GetReturnsNull;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveAsync(DesktopLayoutIconRuleSet rs, CancellationToken ct = default)
    {
        SaveCallCount++;
        if (SaveThrows && SaveCallCount == 1) throw new InvalidOperationException("Fake rule save error");
        SavedRuleSet = rs; return Task.CompletedTask;
    }
    public Task<DesktopLayoutIconRuleSet?> GetAsync(Guid id, CancellationToken ct = default)
    {
        if (GetThrows) throw new InvalidOperationException("Fake rule get error");
        if (GetReturnsNull) return Task.FromResult<DesktopLayoutIconRuleSet?>(null);
        return Task.FromResult<DesktopLayoutIconRuleSet?>(ReturnOnGet);
    }
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        { SavedRuleSet = null; return Task.FromResult(true); }
}

public sealed class LayoutTemplateAndRulesSaveCoordinatorTests
{
    private static DesktopLayoutTemplate MakeTemplate(
        Guid? tid = null, Guid? zid = null, string name = "T", int ver = 1)
    {
        zid ??= Guid.NewGuid();
        return new DesktopLayoutTemplate(tid ?? Guid.NewGuid(), name, ver,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(zid.Value, "Z",
                new NormalizedRect(0,0,10000,10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });
    }

    [Fact] public async Task NewTemplate_EmptyRules_Success()
    {
        var ts = new FakeTemplateStore();
        var tid = Guid.NewGuid();
        // Store previous state as an existing template
        ts.SaveAsync(MakeTemplate(tid)).GetAwaiter().GetResult();
        var rs = new FakeRuleStore
        {
            ReturnOnGet = DesktopLayoutIconRuleSet.Empty(tid)
        };
        var t = MakeTemplate(tid, name: "Updated", ver: 2);
        var r = DesktopLayoutIconRuleSet.Empty(tid);
        var result = await LayoutTemplateAndRulesSaveCoordinator.SaveAsync(ts, rs, t, r);
        Assert.Equal(SaveCoordinatorOutcome.Success, result.Outcome);
    }

    [Fact] public async Task TemplateNull_Rejected()
    {
        var result = await LayoutTemplateAndRulesSaveCoordinator.SaveAsync(
            new FakeTemplateStore(), new FakeRuleStore(), null!,
            DesktopLayoutIconRuleSet.Empty(Guid.NewGuid()));
        Assert.Equal(SaveCoordinatorOutcome.FailedBeforeAnyWrite, result.Outcome);
    }

    [Fact] public async Task TemplateIdMismatch_Rejected()
    {
        var t = MakeTemplate(Guid.NewGuid());
        var r = new DesktopLayoutIconRuleSet(Guid.NewGuid(),
            new[] { DesktopLayoutIconRule.ForExclude("a") });
        var result = await LayoutTemplateAndRulesSaveCoordinator.SaveAsync(
            new FakeTemplateStore(), new FakeRuleStore(), t, r);
        Assert.Equal(SaveCoordinatorOutcome.FailedBeforeAnyWrite, result.Outcome);
    }

    [Fact] public async Task PreviousRuleGetFails_Rejected()
    {
        var ts = new FakeTemplateStore();
        var tid = Guid.NewGuid();
        ts.SaveAsync(MakeTemplate(tid)).GetAwaiter().GetResult(); // has previous template
        var rs = new FakeRuleStore { GetThrows = true };
        var t = MakeTemplate(tid);
        var r = DesktopLayoutIconRuleSet.Empty(tid);
        var result = await LayoutTemplateAndRulesSaveCoordinator.SaveAsync(ts, rs, t, r);
        Assert.Equal(SaveCoordinatorOutcome.FailedBeforeAnyWrite, result.Outcome);
        Assert.Contains("previousRuleSet", result.Message);
    }

    [Fact] public async Task RuleSaveFails_Compensates()
    {
        var ts = new FakeTemplateStore();
        var tid = Guid.NewGuid();
        var prevT = MakeTemplate(tid, name: "Prev");
        var prevR = new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForExclude("old") });
        ts.SaveAsync(prevT).GetAwaiter().GetResult(); // store previous

        var rs = new FakeRuleStore
        {
            SaveThrows = true,
            ReturnOnGet = prevR
        };

        var newT = MakeTemplate(tid, name: "New", ver: 2);
        var newR = new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForPin("new") });

        var result = await LayoutTemplateAndRulesSaveCoordinator.SaveAsync(ts, rs, newT, newR);
        Assert.Equal(SaveCoordinatorOutcome.FailedAndCompensated, result.Outcome);
        Assert.NotNull(result.CompensateResult);
    }

    [Fact] public async Task Cancel_BeforeWrite_ZeroSideEffects()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ts = new FakeTemplateStore();
        var t = MakeTemplate(Guid.NewGuid());
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            LayoutTemplateAndRulesSaveCoordinator.SaveAsync(
                ts, new FakeRuleStore(), t,
                DesktopLayoutIconRuleSet.Empty(t.TemplateId), cts.Token));
        Assert.Null(ts.SavedTemplate);
    }
}
