using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Persistence.DesktopLayouts.Workspaces.Execution;

internal static class DesktopWorkspaceExecutionPlanSnapshotJsonSerializer
{
    internal const int CurrentPayloadFormatVersion = 1;
    private const string HashPrefix = "workspace-execution-payload-v1:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    internal static string Serialize(DesktopWorkspaceExecutionPlanSnapshot plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var payload = new PlanPayload(
            plan.ConfirmationId,
            plan.PreviewId,
            plan.FreshObservationId,
            plan.PreviewFingerprint,
            plan.ExactPlanFingerprint,
            plan.ExecutionPlanFingerprint,
            plan.MoveItemCount,
            plan.UsesTemporaryStaging,
            plan.Steps.Select(static step => new StepPayload(
                step.StepIndex,
                step.StableId,
                step.DisplayName,
                (int)step.Kind,
                step.FromDisplayRef,
                step.FromPixelPosition.X,
                step.FromPixelPosition.Y,
                step.ToDisplayRef,
                step.ToPixelPosition.X,
                step.ToPixelPosition.Y,
                step.ToSlotId)).ToList());
        var canonicalPayload = JsonSerializer.Serialize(payload, JsonOptions);
        var envelope = new EnvelopePayload(ComputeHash(canonicalPayload), payload);
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    internal static DesktopWorkspaceExecutionPlanSnapshot Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Workspace execution plan payload 为空。");

        EnvelopePayload envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EnvelopePayload>(json, JsonOptions)
                ?? throw new InvalidDataException("Workspace execution plan envelope 反序列化为 null。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Workspace execution plan payload JSON 无效。", ex);
        }

        if (string.IsNullOrWhiteSpace(envelope.PayloadHash) || envelope.Plan is null)
            throw new InvalidDataException("Workspace execution plan envelope 缺少 payloadHash 或 plan。");

        var canonicalPayload = JsonSerializer.Serialize(envelope.Plan, JsonOptions);
        var expectedHash = ComputeHash(canonicalPayload);
        if (!string.Equals(envelope.PayloadHash, expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException("Workspace execution plan payload 完整性校验失败。");

        var payload = envelope.Plan;
        if (payload.Steps is null)
            throw new InvalidDataException("Workspace execution plan payload 缺少 steps。");

        var steps = payload.Steps.Select((step, index) =>
        {
            if (step is null)
                throw new InvalidDataException($"Workspace execution plan step #{index} 为 null。");
            if (!Enum.IsDefined((DesktopWorkspaceExecutionStepKind)step.Kind))
                throw new InvalidDataException($"Workspace execution plan step #{index} Kind={step.Kind} 无效。");
            return new DesktopWorkspaceExecutionStep(
                step.StepIndex,
                step.StableId,
                step.DisplayName,
                (DesktopWorkspaceExecutionStepKind)step.Kind,
                step.FromDisplayRef,
                new PixelPoint(step.FromX, step.FromY),
                step.ToDisplayRef,
                new PixelPoint(step.ToX, step.ToY),
                step.ToSlotId);
        }).ToArray();

        return new DesktopWorkspaceExecutionPlanSnapshot(
            payload.ConfirmationId,
            payload.PreviewId,
            payload.FreshObservationId,
            payload.PreviewFingerprint,
            payload.ExactPlanFingerprint,
            payload.ExecutionPlanFingerprint,
            steps,
            payload.MoveItemCount,
            payload.UsesTemporaryStaging);
    }

    private static string ComputeHash(string canonicalPayload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload));
        return HashPrefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record EnvelopePayload(
        string PayloadHash,
        PlanPayload? Plan);

    private sealed record PlanPayload(
        Guid ConfirmationId,
        Guid PreviewId,
        Guid FreshObservationId,
        string PreviewFingerprint,
        string ExactPlanFingerprint,
        string ExecutionPlanFingerprint,
        int MoveItemCount,
        bool UsesTemporaryStaging,
        List<StepPayload?>? Steps);

    private sealed record StepPayload(
        int StepIndex,
        string StableId,
        string DisplayName,
        int Kind,
        string FromDisplayRef,
        int FromX,
        int FromY,
        string ToDisplayRef,
        int ToX,
        int ToY,
        string? ToSlotId);
}
