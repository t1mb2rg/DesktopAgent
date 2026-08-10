namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 可以发送给 Intelligence Provider 的有限正文片段。
/// 不包含本地文件路径、StableId 或任何 Shell 身份。
/// </summary>
public sealed class DesktopItemContentExcerpt
{
    public const int MaxTextCharacters = 12_000;

    public string SourceExtension { get; }
    public string Text { get; }
    public bool WasTruncated { get; }

    public DesktopItemContentExcerpt(
        string sourceExtension,
        string text,
        bool wasTruncated)
    {
        if (string.IsNullOrWhiteSpace(sourceExtension) ||
            !sourceExtension.StartsWith(".", StringComparison.Ordinal) ||
            sourceExtension.Length == 1)
        {
            throw new ArgumentException("SourceExtension 必须形如 .txt。", nameof(sourceExtension));
        }
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextCharacters)
        {
            throw new ArgumentException(
                $"Text 必须为 1..{MaxTextCharacters} 个字符的非空正文。",
                nameof(text));
        }

        SourceExtension = sourceExtension.ToLowerInvariant();
        Text = text;
        WasTruncated = wasTruncated;
    }
}
