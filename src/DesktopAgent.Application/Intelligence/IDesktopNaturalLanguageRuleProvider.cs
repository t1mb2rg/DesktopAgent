namespace DesktopAgent.Application.Intelligence;

public interface IDesktopNaturalLanguageRuleProvider
{
    Task<DesktopRuleDraftProviderResult> GenerateRuleDraftAsync(
        DesktopNaturalLanguageRuleRequest request,
        CancellationToken cancellationToken = default);
}
