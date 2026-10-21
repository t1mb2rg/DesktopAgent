using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using DesktopAgent.Persistence.Productization;
using DesktopAgent.WindowsInterop.Intelligence;

namespace DesktopAgent.WinUI;

internal sealed class AiWorkspaceChatService
{
    internal async Task<WorkspaceChatReply> ReplyAsync(DesktopLayoutConversationRequest context,
        DesktopWorkspaceSessionSnapshot session, IProgress<string> progress, CancellationToken ct)
    {
        var store = new JsonDesktopProductSettingsStore(DesktopAgentDataPaths.DefaultSettingsPath);
        await store.InitializeAsync(ct);
        var options = DesktopProductIntelligenceOptionsFactory.Create(await store.GetAsync(ct))
            ?? throw new InvalidOperationException("请先在设置中配置并启用 AI。");
        using var client = new HttpClient();
        var provider = new OpenAiCompatibleWorkspaceChatProvider(client,
            new(options.BaseUri, options.Model, options.SecretReference, TimeSpan.FromMinutes(2)),
            new DpapiIntelligenceSecretStore(DesktopAgentDataPaths.DefaultSecretsDirectory));
        return await provider.ReplyAsync(context, session, progress, ct);
    }
}
