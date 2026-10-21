using System.Text.Json;

namespace DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry;

/// <summary>持久化已观察环境和失效水位；不保存几何或执行授权。</summary>
public sealed class JsonGridCalibrationEnvironmentStore
{
    private sealed record State(string? Identity, DateTimeOffset? RequiredAfterUtc);
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonGridCalibrationEnvironmentStore(string path, TimeProvider? clock = null)
    {
        _path = Path.GetFullPath(path);
        _clock = clock ?? TimeProvider.System;
    }

    public Task<DateTimeOffset?> ObserveAsync(string? identity, CancellationToken ct = default)
        => UpdateAsync(identity, invalidate: false, ct);

    public Task<DateTimeOffset?> InvalidateAsync(CancellationToken ct = default)
        => UpdateAsync(null, invalidate: true, ct);

    private async Task<DateTimeOffset?> UpdateAsync(string? identity, bool invalidate, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            State? previous = null;
            if (File.Exists(_path))
            {
                try
                {
                    previous = JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(_path, ct))
                        ?? throw new JsonException("校准环境记录为空。");
                    if (previous.Identity is null && previous.RequiredAfterUtc is null)
                        throw new JsonException("校准环境记录无有效内容。");
                }
                catch (JsonException) { previous = new(null, _clock.GetUtcNow()); }
            }
            if (!invalidate && previous is not null && previous.Identity == identity && identity is not null)
                return previous.RequiredAfterUtc;

            // A transient unavailable observation cannot authorize use, but is not evidence
            // of an environment change. Keep the last known identity for the next retry.
            if (!invalidate && identity is null)
                return previous?.RequiredAfterUtc;

            var required = previous is null && identity is not null && !invalidate
                ? null : (DateTimeOffset?)_clock.GetUtcNow();
            if (previous?.RequiredAfterUtc is { } old && (required is null || old > required))
                required = old;
            var state = new State(invalidate ? previous?.Identity : identity, required);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state), ct);
            File.Move(temporary, _path, overwrite: true);
            return required;
        }
        finally { _gate.Release(); }
    }
}
