namespace DesktopAgent.Application.Intelligence;

/// <summary>首次桌面理解 Provider 的一次调用结果。</summary>
public sealed class DesktopUnderstandingProviderResult
{
    public IntelligenceProviderStatus Status { get; }
    public DesktopUnderstandingProposal? Proposal { get; }
    public string? DiagnosticMessage { get; }
    public int? HttpStatusCode { get; }

    private DesktopUnderstandingProviderResult(
        IntelligenceProviderStatus status,
        DesktopUnderstandingProposal? proposal,
        string? diagnosticMessage,
        int? httpStatusCode)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == IntelligenceProviderStatus.Success && proposal is null)
            throw new ArgumentException("Success 结果必须包含 Proposal。", nameof(proposal));
        if (status != IntelligenceProviderStatus.Success && proposal is not null)
            throw new ArgumentException("非 Success 结果不能携带 Proposal。", nameof(proposal));
        if (httpStatusCode is < 100 or > 599)
            throw new ArgumentOutOfRangeException(nameof(httpStatusCode));

        Status = status;
        Proposal = proposal;
        DiagnosticMessage = diagnosticMessage;
        HttpStatusCode = httpStatusCode;
    }

    public static DesktopUnderstandingProviderResult Success(DesktopUnderstandingProposal proposal)
        => new(
            IntelligenceProviderStatus.Success,
            proposal ?? throw new ArgumentNullException(nameof(proposal)),
            null,
            null);

    public static DesktopUnderstandingProviderResult Failure(
        IntelligenceProviderStatus status,
        string? diagnosticMessage = null,
        int? httpStatusCode = null)
    {
        if (status == IntelligenceProviderStatus.Success)
            throw new ArgumentException("Failure 不能使用 Success 状态。", nameof(status));
        return new DesktopUnderstandingProviderResult(status, null, diagnosticMessage, httpStatusCode);
    }
}

public interface IDesktopUnderstandingProvider
{
    Task<DesktopUnderstandingProviderResult> UnderstandAsync(
        DesktopUnderstandingRequest request,
        CancellationToken cancellationToken = default);
}
