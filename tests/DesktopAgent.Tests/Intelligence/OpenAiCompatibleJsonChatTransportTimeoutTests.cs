using System.Net;
using System.Text;
using System.Text.Json;
using DesktopAgent.Application.Intelligence;

namespace DesktopAgent.Tests.Intelligence;

public sealed class OpenAiCompatibleJsonChatTransportTimeoutTests
{
    [Fact]
    public async Task Constructor_DisablesHttpClientBuiltInTimeout_SoProviderOptionsOwnDeadline()
    {
        using var httpClient = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);
            return CompletionResponse("{\"ok\":true}");
        }))
        {
            Timeout = TimeSpan.FromMilliseconds(20),
        };

        var transport = new OpenAiCompatibleJsonChatTransport(
            httpClient,
            new OpenAiCompatibleProviderOptions(
                new Uri("https://example.test/v1"),
                "model",
                secretReference: null,
                timeout: TimeSpan.FromSeconds(2)),
            new NullSecretResolver());

        Assert.Equal(Timeout.InfiniteTimeSpan, httpClient.Timeout);

        var result = await transport.CompleteJsonAsync("system", "user");

        Assert.Equal(IntelligenceProviderStatus.Success, result.Status);
        Assert.Equal("{\"ok\":true}", result.Content);
    }

    [Fact]
    public async Task ProviderOptionsTimeout_RemainsTheActualCancellationAuthority()
    {
        using var httpClient = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CompletionResponse("{\"never\":true}");
        }))
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        var transport = new OpenAiCompatibleJsonChatTransport(
            httpClient,
            new OpenAiCompatibleProviderOptions(
                new Uri("https://example.test/v1"),
                "model",
                secretReference: null,
                timeout: TimeSpan.FromMilliseconds(50)),
            new NullSecretResolver());

        var result = await transport.CompleteJsonAsync("system", "user");

        Assert.Equal(IntelligenceProviderStatus.Timeout, result.Status);
        Assert.Contains("0.05", result.DiagnosticMessage, StringComparison.Ordinal);
        Assert.Equal(Timeout.InfiniteTimeSpan, httpClient.Timeout);
    }

    private static HttpResponseMessage CompletionResponse(string messageContent)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new { message = new { content = messageContent } },
                    },
                }),
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class NullSecretResolver : IIntelligenceSecretResolver
    {
        public ValueTask<string?> ResolveSecretAsync(
            string secretReference,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        internal DelegateHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
