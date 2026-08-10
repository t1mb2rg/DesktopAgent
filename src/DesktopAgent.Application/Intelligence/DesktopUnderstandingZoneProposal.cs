namespace DesktopAgent.Application.Intelligence;

/// <summary>AI 在首次理解阶段提出的逻辑 Zone，不含物理坐标。</summary>
public sealed class DesktopUnderstandingZoneProposal
{
    public const int MaxKeyLength = 64;
    public const int MaxNameLength = 80;
    public const int MaxPurposeLength = 300;

    public string Key { get; }
    public string Name { get; }
    public string Purpose { get; }

    public DesktopUnderstandingZoneProposal(string key, string name, string purpose)
    {
        if (!IsValidKey(key))
            throw new ArgumentException(
                "Key 必须为 1..64 个 ASCII 字母、数字、'-' 或 '_'。", nameof(key));
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
            throw new ArgumentException($"Name 必须为 1..{MaxNameLength} 个非空白字符。", nameof(name));
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > MaxPurposeLength)
            throw new ArgumentException($"Purpose 必须为 1..{MaxPurposeLength} 个非空白字符。", nameof(purpose));

        Key = key;
        Name = name;
        Purpose = purpose;
    }

    internal static bool IsValidKey(string? value)
        => value is { Length: >= 1 and <= MaxKeyLength } &&
           value.All(static ch =>
               ch is >= 'a' and <= 'z' or
                     >= 'A' and <= 'Z' or
                     >= '0' and <= '9' or '-' or '_');
}
