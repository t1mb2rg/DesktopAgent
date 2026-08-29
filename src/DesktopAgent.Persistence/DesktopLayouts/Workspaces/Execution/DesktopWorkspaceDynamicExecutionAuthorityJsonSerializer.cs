using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Persistence.DesktopLayouts.Workspaces.Execution;

internal static class DesktopWorkspaceDynamicExecutionAuthorityJsonSerializer
{
    internal const int CurrentPayloadFormatVersion = 1;
    private const string HashPrefix = "workspace-dynamic-authority-v1:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    internal static string Serialize(DesktopWorkspaceDynamicExecutionAuthoritySnapshot authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var payload = new AuthorityPayload(
            authority.ConfirmationId,
            authority.PreviewId,
            authority.PreviewFingerprint,
            authority.ExactPlanFingerprint,
            authority.GeometryFingerprint,
            authority.ItemGoals.Select(static goal => new GoalPayload(
                goal.StableId,
                goal.DisplayName,
                (int)goal.Disposition,
                goal.IsMovable,
                goal.IsMoveAuthorized,
                goal.FinalDisplayRef,
                goal.FinalPixelPosition.X,
                goal.FinalPixelPosition.Y,
                goal.FinalSlotId)).ToList());
        var canonical = JsonSerializer.Serialize(payload, JsonOptions);
        return JsonSerializer.Serialize(
            new EnvelopePayload(ComputeHash(canonical), payload),
            JsonOptions);
    }

    internal static DesktopWorkspaceDynamicExecutionAuthoritySnapshot Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Dynamic Workspace authority payload 为空。 ");

        EnvelopePayload envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EnvelopePayload>(json, JsonOptions)
                ?? throw new InvalidDataException("Dynamic Workspace authority envelope 反序列化为 null。 ");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Dynamic Workspace authority payload JSON 无效。 ", ex);
        }

        if (string.IsNullOrWhiteSpace(envelope.PayloadHash) || envelope.Authority is null)
            throw new InvalidDataException("Dynamic Workspace authority envelope 缺少 payloadHash 或 authority。 ");

        var canonical = JsonSerializer.Serialize(envelope.Authority, JsonOptions);
        if (!string.Equals(envelope.PayloadHash, ComputeHash(canonical), StringComparison.Ordinal))
            throw new InvalidDataException("Dynamic Workspace authority payload 完整性校验失败。 ");

        var payload = envelope.Authority;
        if (payload.ItemGoals is null)
            throw new InvalidDataException("Dynamic Workspace authority payload 缺少 itemGoals。 ");

        var goals = payload.ItemGoals.Select((goal, index) =>
        {
            if (goal is null)
                throw new InvalidDataException($"Dynamic Workspace authority goal #{index} 为 null。 ");
            if (!Enum.IsDefined((DesktopWorkspaceCompiledItemDisposition)goal.Disposition))
                throw new InvalidDataException($"Dynamic Workspace authority goal #{index} Disposition={goal.Disposition} 无效。 ");

            return new DesktopWorkspaceDynamicExecutionItemGoal(
                goal.StableId,
                goal.DisplayName,
                (DesktopWorkspaceCompiledItemDisposition)goal.Disposition,
                goal.IsMovable,
                goal.IsMoveAuthorized,
                goal.FinalDisplayRef,
                new PixelPoint(goal.FinalX, goal.FinalY),
                goal.FinalSlotId);
        }).ToArray();

        return new DesktopWorkspaceDynamicExecutionAuthoritySnapshot(
            payload.ConfirmationId,
            payload.PreviewId,
            payload.PreviewFingerprint,
            payload.ExactPlanFingerprint,
            payload.GeometryFingerprint,
            goals);
    }

    private static string ComputeHash(string canonicalPayload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload));
        return HashPrefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record EnvelopePayload(
        string PayloadHash,
        AuthorityPayload? Authority);

    private sealed record AuthorityPayload(
        Guid ConfirmationId,
        Guid PreviewId,
        string PreviewFingerprint,
        string ExactPlanFingerprint,
        string GeometryFingerprint,
        List<GoalPayload>? ItemGoals);

    private sealed record GoalPayload(
        string StableId,
        string DisplayName,
        int Disposition,
        bool IsMovable,
        bool IsMoveAuthorized,
        string FinalDisplayRef,
        int FinalX,
        int FinalY,
        string? FinalSlotId);
}
