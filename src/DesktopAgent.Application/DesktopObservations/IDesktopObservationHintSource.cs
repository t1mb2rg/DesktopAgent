namespace DesktopAgent.Application.DesktopObservations;

/// <summary>
/// 一个只会发布 observation hint 的基础设施来源。
/// ActiveEndpoints / Issues 仅用于启动诊断，不会进入 DesktopObservationHint payload。
/// </summary>
public sealed record DesktopObservationHintSourceStartResult
{
    public string Source { get; }
    public bool IsRunning { get; }
    public int ActiveEndpointCount { get; }
    public IReadOnlyList<string> ActiveEndpoints { get; }
    public IReadOnlyList<string> Issues { get; }

    public DesktopObservationHintSourceStartResult(
        string source,
        bool isRunning,
        int activeEndpointCount,
        IReadOnlyList<string> activeEndpoints,
        IReadOnlyList<string> issues)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Source 不能为空。", nameof(source));
        if (activeEndpointCount < 0)
            throw new ArgumentOutOfRangeException(nameof(activeEndpointCount));
        ArgumentNullException.ThrowIfNull(activeEndpoints);
        ArgumentNullException.ThrowIfNull(issues);
        if (isRunning != (activeEndpointCount > 0))
            throw new ArgumentException(
                "IsRunning 必须与 ActiveEndpointCount 是否大于 0 一致。",
                nameof(isRunning));

        Source = source.Trim();
        IsRunning = isRunning;
        ActiveEndpointCount = activeEndpointCount;
        ActiveEndpoints = Array.AsReadOnly(activeEndpoints.ToArray());
        Issues = Array.AsReadOnly(issues.ToArray());
    }
}

/// <summary>
/// M3-A 的可启停 hint source port。
/// 实现只能产生“值得重新观察”的提示，不得提供当前桌面事实，也不得持有 Writer。
/// </summary>
public interface IDesktopObservationHintSource : IDisposable
{
    string Source { get; }
    bool IsRunning { get; }

    DesktopObservationHintSourceStartResult Start();
    void Stop();
}
