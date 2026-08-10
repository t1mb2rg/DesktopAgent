using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.Intelligence;

public sealed class DesktopRuleDraftProviderResult
{
    public IntelligenceProviderStatus Status { get; }
    public DesktopClassificationRuleDraft? Draft { get; }
    public string? DiagnosticMessage { get; }
    public int? HttpStatusCode { get; }

    private DesktopRuleDraftProviderResult(
        IntelligenceProviderStatus status,
        DesktopClassificationRuleDraft? draft,
        string? diagnosticMessage,
        int? httpStatusCode)
    {
        Status = status;
        Draft = draft;
        DiagnosticMessage = diagnosticMessage;
        HttpStatusCode = httpStatusCode;
    }

    public static DesktopRuleDraftProviderResult Success(DesktopClassificationRuleDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new DesktopRuleDraftProviderResult(IntelligenceProviderStatus.Success, draft, null, null);
    }

    public static DesktopRuleDraftProviderResult Failure(
        IntelligenceProviderStatus status,
        string? diagnosticMessage = null,
        int? httpStatusCode = null)
    {
        if (status == IntelligenceProviderStatus.Success)
            throw new ArgumentException("Failure 不能使用 Success 状态。", nameof(status));

        return new DesktopRuleDraftProviderResult(status, null, diagnosticMessage, httpStatusCode);
    }
}
