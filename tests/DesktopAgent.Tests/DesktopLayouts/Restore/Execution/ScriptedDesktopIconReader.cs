using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

/// <summary>
/// 可脚本化的桌面图标读取器 — 每次调用返回队列中的下一个结果。
/// 队列耗尽后抛出 InvalidOperationException。
/// </summary>
public sealed class ScriptedDesktopIconReader : IDesktopIconReader
{
    private readonly Queue<DesktopIconReadResult> _queue = new();

    public ScriptedDesktopIconReader() { }

    public ScriptedDesktopIconReader(params DesktopIconReadResult[] results)
    {
        foreach (var r in results) _queue.Enqueue(r);
    }

    public ScriptedDesktopIconReader(IReadOnlyList<DesktopIconDescriptor> icons)
        : this(new DesktopIconReadResult(icons, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null))
    { }

    public void Enqueue(DesktopIconReadResult result) => _queue.Enqueue(result);

    public void Enqueue(IReadOnlyList<DesktopIconDescriptor> icons)
        => _queue.Enqueue(new DesktopIconReadResult(icons, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null));

    public DesktopIconReadResult ReadDesktopIcons()
    {
        if (_queue.Count == 0)
            throw new InvalidOperationException("ScriptedDesktopIconReader 队列为空。");
        return _queue.Dequeue();
    }
}
