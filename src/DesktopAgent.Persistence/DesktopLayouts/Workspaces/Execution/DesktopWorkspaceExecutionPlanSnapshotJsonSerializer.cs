using System.Text.Json;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Persistence.DesktopLayouts.Workspaces.Execution;

internal static class DesktopWorkspaceExecutionPlanSnapshotJsonSerializer
{
    internal const int CurrentPayloadFormatVersion = 1;

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
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    internal static DesktopWorkspaceExecutionPlanSnapshot Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Workspace execution plan payload 为空。");

        PlanPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<PlanPayload>(json, JsonOptions)
                ?? throw new InvalidDataException("Workspace execution plan payload 反序列化为 null。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Workspace execution plan payload JSON 无效。", ex);
        }

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
