namespace DesktopAgent.Application.Intelligence;

public enum DesktopTextExtractionStatus
{
    Success = 0,
    Disabled = 1,
    NotApplicable = 2,
    UnsupportedType = 3,
    NoUsableText = 4,
    Unavailable = 5,
}

public sealed class DesktopTextExtractionResult
{
    public DesktopTextExtractionStatus Status { get; }
    public DesktopItemContentExcerpt? Excerpt { get; }
    public string? DiagnosticMessage { get; }

    private DesktopTextExtractionResult(
        DesktopTextExtractionStatus status,
        DesktopItemContentExcerpt? excerpt,
        string? diagnosticMessage)
    {
        Status = status;
        Excerpt = excerpt;
        DiagnosticMessage = diagnosticMessage;
    }

    public static DesktopTextExtractionResult Success(DesktopItemContentExcerpt excerpt)
    {
        ArgumentNullException.ThrowIfNull(excerpt);
        return new DesktopTextExtractionResult(DesktopTextExtractionStatus.Success, excerpt, null);
    }

    public static DesktopTextExtractionResult WithoutExcerpt(
        DesktopTextExtractionStatus status,
        string? diagnosticMessage = null)
    {
        if (status == DesktopTextExtractionStatus.Success)
            throw new ArgumentException("WithoutExcerpt 不能使用 Success 状态。", nameof(status));

        return new DesktopTextExtractionResult(status, null, diagnosticMessage);
    }
}

public interface IDesktopTextContentExtractor
{
    Task<DesktopTextExtractionResult> ExtractAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}
