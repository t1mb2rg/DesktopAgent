namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// Workspace Designer 的 provider-contract repair decorator。
///
/// 它只处理 Provider 已经响应、但响应未通过本地 Workspace Design 合同的 InvalidResponse。
/// repair 仍然绑定同一个 BlueprintId / Revision / ParentRevision，不创建新的 Blueprint revision；
/// 真正已经形成合法 Blueprint 后的 realization failure 仍由 DesktopWorkspaceDesignLoopCoordinator
/// 负责产生下一 immutable revision。
/// </summary>
public sealed class ContractRepairingDesktopWorkspaceDesigner : IDesktopWorkspaceDesigner
{
    private const int MaxRepairDiagnosticChars = 1536;
    private readonly IDesktopWorkspaceDesigner _inner;

    public ContractRepairingDesktopWorkspaceDesigner(IDesktopWorkspaceDesigner inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<DesktopWorkspaceDesignProviderResult> DesignAsync(
        DesktopWorkspaceDesignRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var first = await _inner.DesignAsync(request, cancellationToken).ConfigureAwait(false);
        if (first.Status != IntelligenceProviderStatus.InvalidResponse)
            return first;

        var repairRequest = TryCreateRepairRequest(request, first.DiagnosticMessage);
        if (repairRequest is null)
            return first;

        return await _inner.DesignAsync(repairRequest, cancellationToken).ConfigureAwait(false);
    }

    private static DesktopWorkspaceDesignRequest? TryCreateRepairRequest(
        DesktopWorkspaceDesignRequest request,
        string? diagnosticMessage)
    {
        var diagnostic = string.IsNullOrWhiteSpace(diagnosticMessage)
            ? "The previous Workspace Blueprint did not satisfy the local design contract."
            : diagnosticMessage.Trim();
        if (diagnostic.Length > MaxRepairDiagnosticChars)
            diagnostic = diagnostic[..MaxRepairDiagnosticChars];

        var repairNote =
            "\n\n[LOCAL WORKSPACE DESIGN CONTRACT REPAIR]\n" +
            "The previous response was rejected before a Blueprint revision was created. " +
            "Return a complete replacement for the SAME requested revision and preserve the user's design intent.\n" +
            $"Local contract diagnostic: {diagnostic}\n" +
            "For EVERY zone/reserved-region bounds [left, top, width, height], validate all arithmetic before returning JSON: " +
            "0 <= left <= 1, 0 <= top <= 1, 0 < width <= 1, 0 < height <= 1, " +
            "left + width <= 1, and top + height <= 1. " +
            "Do not clamp mechanically; choose a valid normalized design rectangle that preserves the intended layout.";

        if (request.UserGuidance.Length + repairNote.Length > DesktopWorkspaceDesignRequest.MaxUserGuidanceChars)
            return null;

        return new DesktopWorkspaceDesignRequest(
            request.RevisionTarget,
            request.DesktopSummary,
            request.UserGuidance + repairNote,
            request.Displays,
            request.Categories,
            request.CriticalItems,
            request.PreviousBlueprint,
            request.RealizationDiagnostics);
    }
}
