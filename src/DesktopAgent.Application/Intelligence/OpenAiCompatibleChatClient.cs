using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// M4 OpenAI-compatible Provider 共用的轻量 Chat Completions 传输层。
/// 只负责认证、超时、HTTP、响应大小限制和 choices[0].message.content 提取；
/// 不理解分类、桌面、Zone 或 Writer 语义。
/// </summary>
internal sealed class OpenAiCompatibleChatClient
{
    private const int MaxResponseBytes = 256 * 1024;
    private const int ReadBufferBytes = 8 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly OpenAiCompatibleProviderOptions _options;
    private readonly IIntelligenceSecretResolver _secretResolver;

    public OpenAiCompatibleChatClient(
        HttpClient httpClient,
        OpenAiCompatibleProviderOptions options,
        IIntelligenceSecretResolver secretResolver)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _secretResolver = secretResolver ?? throw new ArgumentNullException(nameof(secretResolver));
    }

    public async Task<OpenAiCompatibleChatResult> CompleteJsonAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt))
            throw new ArgumentException("System prompt 不能为空。", nameof(systemPrompt));
        if (string.IsNullOrWhiteSpace(userPrompt))
            throw new ArgumentException("User prompt 不能为空。", nameof(userPrompt));

        string? apiKey = null;
        if (_options.SecretReference is not null)
        {
            try
            {
                apiKey = await _secretResolver
                    .ResolveSecretAsync(_options.SecretReference, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return OpenAiCompatibleChatResult.Failure(
                    IntelligenceProviderStatus.Cancelled,
                    "解析 Provider Secret 时调用被取消。");
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return OpenAiCompatibleChatResult.Failure(
                    IntelligenceProviderStatus.NotConfigured,
                    "配置引用了 API Secret，但当前 Secret Resolver 没有返回可用值。");
            }
        }

        using var message = BuildRequestMessage(systemPrompt, userPrompt, apiKey);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.Timeout);

        try
        {
            using var response = await _httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return OpenAiCompatibleChatResult.Failure(
                    IntelligenceProviderStatus.Unavailable,
                    $"Provider 返回 HTTP {(int)response.StatusCode}。",
                    (int)response.StatusCode);
            }

            var body = await ReadLimitedUtf8Async(response.Content, timeoutCts.Token)
                .ConfigureAwait(false);
            if (body is null)
            {
                return OpenAiCompatibleChatResult.Failure(
                    IntelligenceProviderStatus.InvalidResponse,
                    $"Provider 响应超过 {MaxResponseBytes} bytes 上限。");
            }

            return ExtractContent(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OpenAiCompatibleChatResult.Failure(
                IntelligenceProviderStatus.Cancelled,
                "Provider 调用被调用方取消。");
        }
        catch (OperationCanceledException)
        {
            return OpenAiCompatibleChatResult.Failure(
                IntelligenceProviderStatus.Timeout,
                $"Provider 调用超过 {_options.Timeout.TotalSeconds:0.###} 秒超时。");
        }
        catch (HttpRequestException ex)
        {
            return OpenAiCompatibleChatResult.Failure(
                IntelligenceProviderStatus.Unavailable,
                $"Provider 网络请求失败：{ex.Message}");
        }
        catch (IOException ex)
        {
            return OpenAiCompatibleChatResult.Failure(
                IntelligenceProviderStatus.Unavailable,
                $"Provider 响应流读取失败：{ex.Message}");
        }
    }

    private HttpRequestMessage BuildRequestMessage(
        string systemPrompt,
        string userPrompt,
        string? apiKey)
    {
        var requestBody = new
        {
            model = _options.Model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            response_format = new { type = "json_object" },
            stream = false,
        };

        var endpoint = new Uri(
            _options.BaseUri.AbsoluteUri.TrimEnd('/') + "/chat/completions",
            UriKind.Absolute);
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody, JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (apiKey is not null)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return message;
    }

    private static OpenAiCompatibleChatResult ExtractContent(string body)
    {
        ChatCompletionResponse? completion;
        try
        {
            completion = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            return OpenAiCompatibleChatResult.Failure(
                IntelligenceProviderStatus.InvalidResponse,
                $"Provider 外层响应不是有效 JSON：{ex.Message}");
        }

        var content = completion?.Choices?.FirstOrDefault()?.Message?.Content;
        return string.IsNullOrWhiteSpace(content)
            ? OpenAiCompatibleChatResult.Failure(
                IntelligenceProviderStatus.InvalidResponse,
                "Provider 响应缺少 choices[0].message.content。")
            : OpenAiCompatibleChatResult.Success(content);
    }

    private static async Task<string?> ReadLimitedUtf8Async(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
            return null;

        await using var stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: ReadBufferBytes);
        var chunk = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);

        try
        {
            var total = 0;
            while (true)
            {
                var read = await stream.ReadAsync(
                        chunk.AsMemory(0, ReadBufferBytes),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                total += read;
                if (total > MaxResponseBytes)
                    return null;

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<ChatChoice>? Choices { get; init; }
    }

    private sealed class ChatChoice
    {
        [JsonPropertyName("message")]
        public ChatMessage? Message { get; init; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }
}

internal sealed class OpenAiCompatibleChatResult
{
    public IntelligenceProviderStatus Status { get; }
    public string? Content { get; }
    public string? DiagnosticMessage { get; }
    public int? HttpStatusCode { get; }

    private OpenAiCompatibleChatResult(
        IntelligenceProviderStatus status,
        string? content,
        string? diagnosticMessage,
        int? httpStatusCode)
    {
        Status = status;
        Content = content;
        DiagnosticMessage = diagnosticMessage;
        HttpStatusCode = httpStatusCode;
    }

    public static OpenAiCompatibleChatResult Success(string content)
        => new(IntelligenceProviderStatus.Success, content, null, null);

    public static OpenAiCompatibleChatResult Failure(
        IntelligenceProviderStatus status,
        string? diagnosticMessage = null,
        int? httpStatusCode = null)
        => new(status, null, diagnosticMessage, httpStatusCode);
}
