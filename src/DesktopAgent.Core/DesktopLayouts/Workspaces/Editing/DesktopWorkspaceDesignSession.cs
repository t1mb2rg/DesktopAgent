using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;

public enum DesktopWorkspaceMessageRole { User, Assistant, LocalResult }
public sealed record DesktopWorkspaceChatMessage(Guid MessageId, DesktopWorkspaceMessageRole Role,
    string Text, DateTimeOffset CreatedAtUtc);
public sealed record DesktopWorkspaceEditState(DesktopWorkspaceDesignDraft Draft, DesktopWorkspaceConversationIntent Intent);

public abstract record DesktopWorkspaceDesignCommand;
public sealed record UpsertWorkspaceZone(DesktopWorkspaceBlueprintZone Zone) : DesktopWorkspaceDesignCommand;
public sealed record RemoveWorkspaceZone(string ZoneKey) : DesktopWorkspaceDesignCommand;
public sealed record SetWorkspaceSummary(string Summary) : DesktopWorkspaceDesignCommand;
public sealed record SetWorkspaceDisplayPolicy(DesktopWorkspaceBlueprintDisplayPolicy Policy) : DesktopWorkspaceDesignCommand;
public sealed record AssignWorkspaceCategory(string CategoryKey, string ZoneKey) : DesktopWorkspaceDesignCommand;
public sealed record RemoveWorkspaceCategoryRule(string CategoryKey) : DesktopWorkspaceDesignCommand;
public sealed record ReplaceWorkspaceDraft(DesktopWorkspaceDesignDraft Draft) : DesktopWorkspaceDesignCommand;
public sealed record ReplaceWorkspaceIntent(DesktopWorkspaceConversationIntent Intent) : DesktopWorkspaceDesignCommand;

public enum DesktopWorkspaceEditStatus { Applied, NoChange, Stale, Invalid, NothingToUndo }
public sealed record DesktopWorkspaceEditResult(DesktopWorkspaceEditStatus Status, long Revision, string? Message = null)
{
    public bool Applied => Status == DesktopWorkspaceEditStatus.Applied;
}

/// <summary>仅包含设计、偏好与聊天记录；不承载 Preview、确认、票据或 Writer payload。</summary>
public sealed record DesktopWorkspaceSessionSnapshot(Guid SessionId, long Revision, long Sequence,
    DesktopWorkspaceEditState State, IReadOnlyList<DesktopWorkspaceEditState> UndoHistory,
    IReadOnlyList<DesktopWorkspaceEditState> RedoHistory, IReadOnlyList<DesktopWorkspaceChatMessage> Messages);

public interface IDesktopWorkspaceSessionStore
{
    Task<DesktopWorkspaceSessionSnapshot?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(DesktopWorkspaceSessionSnapshot snapshot, CancellationToken cancellationToken = default);
}

/// <summary>聊天与手动编辑的单一设计源。修订号始终递增，撤销也不能让旧模型响应重新生效。</summary>
public sealed class DesktopWorkspaceDesignSession
{
    public const int HistoryLimit = 50;
    public const int MessageLimit = 200;
    private readonly object _gate = new();
    private readonly Guid _sessionId;
    private long _revision;
    private long _sequence;
    private DesktopWorkspaceEditState _state;
    private readonly List<DesktopWorkspaceEditState> _undo = [];
    private readonly List<DesktopWorkspaceEditState> _redo = [];
    private readonly List<DesktopWorkspaceChatMessage> _messages = [];

    public event Action? Changed;

    public DesktopWorkspaceDesignSession()
    {
        _sessionId = Guid.NewGuid();
        _state = new(new(Guid.NewGuid()), DesktopWorkspaceConversationIntent.Empty);
    }

    private DesktopWorkspaceDesignSession(DesktopWorkspaceSessionSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        _sessionId = snapshot.SessionId;
        _revision = snapshot.Revision;
        _sequence = snapshot.Sequence;
        _state = snapshot.State;
        _undo.AddRange(snapshot.UndoHistory);
        _redo.AddRange(snapshot.RedoHistory);
        _messages.AddRange(snapshot.Messages);
    }

    public static DesktopWorkspaceDesignSession Restore(DesktopWorkspaceSessionSnapshot snapshot) => new(snapshot);

    public DesktopWorkspaceSessionSnapshot Snapshot()
    {
        lock (_gate)
            return new(_sessionId, _revision, _sequence, _state,
                Array.AsReadOnly(_undo.ToArray()), Array.AsReadOnly(_redo.ToArray()), Array.AsReadOnly(_messages.ToArray()));
    }

    public DesktopWorkspaceEditResult TryApply(long baseRevision, IReadOnlyList<DesktopWorkspaceDesignCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        DesktopWorkspaceEditResult result;
        lock (_gate)
        {
            if (baseRevision != _revision) return new(DesktopWorkspaceEditStatus.Stale, _revision, "设计已变化，旧响应没有覆盖当前设计。");
            if (_revision >= long.MaxValue - 1 || _sequence >= long.MaxValue - 1)
                return new(DesktopWorkspaceEditStatus.Invalid, _revision, "会话版本已超出有效范围。");
            if (commands.Count == 0) return new(DesktopWorkspaceEditStatus.NoChange, _revision);
            var next = _state;
            try
            {
                foreach (var command in commands)
                    next = command switch
                    {
                        UpsertWorkspaceZone upsert => next with { Draft = next.Draft.With(zones:
                            next.Draft.Zones.Where(zone => zone.ZoneKey != upsert.Zone.ZoneKey).Append(upsert.Zone)) },
                        RemoveWorkspaceZone remove => next with { Draft = next.Draft.With(zones:
                            next.Draft.Zones.Where(zone => zone.ZoneKey != remove.ZoneKey)) },
                        SetWorkspaceSummary summary => next with { Draft = next.Draft.With(summary: summary.Summary) },
                        SetWorkspaceDisplayPolicy policy => next with { Draft = next.Draft.With(displayPolicies:
                            next.Draft.DisplayPolicies.Where(item => item.DisplayRef != policy.Policy.DisplayRef).Append(policy.Policy)) },
                        AssignWorkspaceCategory assign => next with { Draft = next.Draft.With(categoryRules:
                            next.Draft.CategoryRules.Where(rule => rule.CategoryKey != assign.CategoryKey)
                                .Append(new(assign.CategoryKey, assign.ZoneKey))) },
                        RemoveWorkspaceCategoryRule remove => next with { Draft = next.Draft.With(categoryRules:
                            next.Draft.CategoryRules.Where(rule => rule.CategoryKey != remove.CategoryKey)) },
                        ReplaceWorkspaceDraft replace => next with { Draft = replace.Draft ?? throw new ArgumentException("设计不能为空。") },
                        ReplaceWorkspaceIntent replace => next with { Intent = replace.Intent ?? throw new ArgumentException("偏好不能为空。") },
                        _ => throw new ArgumentException("不支持的设计命令。"),
                    };
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException)
            {
                return new(DesktopWorkspaceEditStatus.Invalid, _revision, ex.Message);
            }
            Push(_undo, _state);
            _redo.Clear();
            _state = next;
            _revision = checked(_revision + 1);
            _sequence = checked(_sequence + 1);
            result = new(DesktopWorkspaceEditStatus.Applied, _revision);
        }
        Changed?.Invoke();
        return result;
    }

    public DesktopWorkspaceEditResult Undo(long baseRevision) => Traverse(baseRevision, undo: true);
    public DesktopWorkspaceEditResult Redo(long baseRevision) => Traverse(baseRevision, undo: false);

    public void InvalidateExternalContext()
    {
        lock (_gate)
        {
            if (_revision >= long.MaxValue - 1 || _sequence >= long.MaxValue - 1)
                throw new InvalidOperationException("会话版本已超出有效范围。");
            _revision++;
            _sequence++;
        }
        Changed?.Invoke();
    }

    private DesktopWorkspaceEditResult Traverse(long baseRevision, bool undo)
    {
        DesktopWorkspaceEditResult result;
        lock (_gate)
        {
            if (baseRevision != _revision) return new(DesktopWorkspaceEditStatus.Stale, _revision);
            if (_revision >= long.MaxValue - 1 || _sequence >= long.MaxValue - 1)
                return new(DesktopWorkspaceEditStatus.Invalid, _revision, "会话版本已超出有效范围。");
            var from = undo ? _undo : _redo;
            var to = undo ? _redo : _undo;
            if (from.Count == 0) return new(DesktopWorkspaceEditStatus.NothingToUndo, _revision);
            Push(to, _state);
            _state = from[^1];
            from.RemoveAt(from.Count - 1);
            _revision = checked(_revision + 1);
            _sequence = checked(_sequence + 1);
            result = new(DesktopWorkspaceEditStatus.Applied, _revision);
        }
        Changed?.Invoke();
        return result;
    }

    public void AppendMessage(DesktopWorkspaceMessageRole role, string text, DateTimeOffset? atUtc = null)
    {
        if (!Enum.IsDefined(role) || string.IsNullOrWhiteSpace(text)) throw new ArgumentException("聊天消息无效。");
        lock (_gate)
        {
            if (_sequence >= long.MaxValue - 1) throw new InvalidOperationException("会话版本已超出有效范围。");
            _messages.Add(new(Guid.NewGuid(), role, text, (atUtc ?? DateTimeOffset.UtcNow).ToUniversalTime()));
            if (_messages.Count > MessageLimit) _messages.RemoveAt(0);
            _sequence = checked(_sequence + 1);
        }
        Changed?.Invoke();
    }

    public static void ValidateSnapshot(DesktopWorkspaceSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SessionId == Guid.Empty || snapshot.Revision < 0 || snapshot.Sequence < snapshot.Revision ||
            snapshot.Revision == long.MaxValue || snapshot.Sequence == long.MaxValue ||
            snapshot.State?.Draft is null || snapshot.State.Intent is null ||
            snapshot.UndoHistory is null || snapshot.RedoHistory is null || snapshot.Messages is null ||
            snapshot.UndoHistory.Count > HistoryLimit || snapshot.RedoHistory.Count > HistoryLimit || snapshot.Messages.Count > MessageLimit ||
            snapshot.UndoHistory.Concat(snapshot.RedoHistory).Any(state => state?.Draft is null || state.Intent is null) ||
            snapshot.Messages.Any(message => message is null || message.MessageId == Guid.Empty ||
                !Enum.IsDefined(message.Role) || string.IsNullOrWhiteSpace(message.Text)) ||
            snapshot.Messages.Select(message => message.MessageId).Distinct().Count() != snapshot.Messages.Count)
            throw new ArgumentException("设计会话记录无效。");
    }

    private static void Push(List<DesktopWorkspaceEditState> history, DesktopWorkspaceEditState state)
    {
        history.Add(state);
        if (history.Count > HistoryLimit) history.RemoveAt(0);
    }
}
