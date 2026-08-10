using System.Buffers;
using System.Text;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// M4-05 的保守文本提取器。只读取明确文本类型，最多读取固定前缀，
/// 不解析 PDF/Office，不执行文件内容，不把路径带入 Provider context。
/// </summary>
public sealed class BoundedDesktopTextContentExtractor : IDesktopTextContentExtractor
{
    public const int MaxSourceBytes = 64 * 1024;

    private static readonly HashSet<string> SupportedExtensions = new(
        new[]
        {
            ".txt", ".md", ".markdown", ".csv", ".json", ".xml", ".yaml", ".yml", ".log",
            ".ini", ".cfg", ".conf", ".cs", ".py", ".js", ".ts", ".java", ".c", ".cpp",
            ".h", ".hpp", ".html", ".htm", ".css", ".sql", ".sh", ".ps1",
        },
        StringComparer.OrdinalIgnoreCase);

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(
        bigEndian: false,
        byteOrderMark: true,
        throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(
        bigEndian: true,
        byteOrderMark: true,
        throwOnInvalidBytes: true);

    public async Task<DesktopTextExtractionResult> ExtractAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("FilePath 不能为空。", nameof(filePath));

        var extension = Path.GetExtension(filePath);
        if (!SupportedExtensions.Contains(extension))
        {
            return DesktopTextExtractionResult.WithoutExcerpt(
                DesktopTextExtractionStatus.UnsupportedType,
                $"扩展名 {extension} 不在 v1.0 文本提取白名单中。");
        }

        if (!Path.IsPathFullyQualified(filePath) || filePath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return DesktopTextExtractionResult.WithoutExcerpt(
                DesktopTextExtractionStatus.NotApplicable,
                "正文提取只接受本机完整文件路径，不访问 UNC/网络路径。");
        }

        var rented = ArrayPool<byte>.Shared.Rent(MaxSourceBytes + 1);
        try
        {
            int total;
            try
            {
                await using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 8 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);

                total = 0;
                while (total < MaxSourceBytes + 1)
                {
                    var read = await stream.ReadAsync(
                            rented.AsMemory(total, MaxSourceBytes + 1 - total),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                        break;
                    total += read;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unavailable(ex.Message);
            }
            catch (IOException ex)
            {
                return Unavailable(ex.Message);
            }

            var sourceWasTruncated = total > MaxSourceBytes;
            var bytesToDecode = Math.Min(total, MaxSourceBytes);
            if (bytesToDecode == 0)
            {
                return DesktopTextExtractionResult.WithoutExcerpt(
                    DesktopTextExtractionStatus.NoUsableText,
                    "文件为空。 ");
            }

            string text;
            try
            {
                text = DecodeText(rented.AsSpan(0, bytesToDecode));
            }
            catch (DecoderFallbackException)
            {
                return DesktopTextExtractionResult.WithoutExcerpt(
                    DesktopTextExtractionStatus.NoUsableText,
                    "文件前缀不是支持的 UTF-8 / BOM UTF-16 文本。 ");
            }

            text = text.Trim();
            if (text.Length == 0 || text.IndexOf('\0') >= 0)
            {
                return DesktopTextExtractionResult.WithoutExcerpt(
                    DesktopTextExtractionStatus.NoUsableText,
                    "文件前缀没有可用文本。 ");
            }

            var characterWasTruncated = text.Length > DesktopItemContentExcerpt.MaxTextCharacters;
            if (characterWasTruncated)
                text = text[..DesktopItemContentExcerpt.MaxTextCharacters];

            return DesktopTextExtractionResult.Success(
                new DesktopItemContentExcerpt(
                    extension,
                    text,
                    sourceWasTruncated || characterWasTruncated));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return StrictUtf8.GetString(bytes[3..]);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return StrictUtf16Le.GetString(bytes[2..]);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return StrictUtf16Be.GetString(bytes[2..]);

        if (bytes.IndexOf((byte)0) >= 0)
            throw new DecoderFallbackException("无 BOM 的 NUL 字节流按二进制处理。");

        return StrictUtf8.GetString(bytes);
    }

    private static DesktopTextExtractionResult Unavailable(string message)
        => DesktopTextExtractionResult.WithoutExcerpt(
            DesktopTextExtractionStatus.Unavailable,
            $"读取正文失败：{message}");
}
