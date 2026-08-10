using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.Intelligence;

public sealed class DesktopContentAwareClassificationBuildResult
{
    public DesktopItemClassificationRequest Request { get; }
    public DesktopTextExtractionResult ContentAnalysis { get; }

    public DesktopContentAwareClassificationBuildResult(
        DesktopItemClassificationRequest request,
        DesktopTextExtractionResult contentAnalysis)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        ContentAnalysis = contentAnalysis ?? throw new ArgumentNullException(nameof(contentAnalysis));
    }
}

/// <summary>
/// 内容分析的唯一应用层 gate：关闭时不调用 extractor，因此不会发生正文 File IO。
/// </summary>
public sealed class DesktopContentAwareClassificationRequestBuilder
{
    private readonly IDesktopTextContentExtractor _extractor;

    public DesktopContentAwareClassificationRequestBuilder(IDesktopTextContentExtractor extractor)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    }

    public async Task<DesktopContentAwareClassificationBuildResult> BuildAsync(
        DesktopItemFacts item,
        string? localFilePath,
        IEnumerable<IntelligenceZoneOption>? zones,
        bool contentAnalysisEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        if (!contentAnalysisEnabled)
        {
            return new DesktopContentAwareClassificationBuildResult(
                new DesktopItemClassificationRequest(item, zones),
                DesktopTextExtractionResult.WithoutExcerpt(
                    DesktopTextExtractionStatus.Disabled,
                    "内容分析已关闭，未读取正文。"));
        }

        if (!item.IsFileSystemItem || string.IsNullOrWhiteSpace(localFilePath))
        {
            return new DesktopContentAwareClassificationBuildResult(
                new DesktopItemClassificationRequest(
                    item,
                    zones,
                    contentAnalysisEnabled: true),
                DesktopTextExtractionResult.WithoutExcerpt(
                    DesktopTextExtractionStatus.NotApplicable,
                    "当前项目没有可用于正文分析的本地文件路径。"));
        }

        var extraction = await _extractor
            .ExtractAsync(localFilePath, cancellationToken)
            .ConfigureAwait(false);

        return new DesktopContentAwareClassificationBuildResult(
            new DesktopItemClassificationRequest(
                item,
                zones,
                contentAnalysisEnabled: true,
                contentExcerpt: extraction.Excerpt),
            extraction);
    }
}
