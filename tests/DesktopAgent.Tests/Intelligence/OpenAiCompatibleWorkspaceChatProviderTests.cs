using System.Net;
using System.Text;
using System.Text.Json;
using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Intelligence;

public sealed class OpenAiCompatibleWorkspaceChatProviderTests
{
    [Fact]
    public async Task MetadataOnlyToolChunk_CanPrecedeFunctionArguments()
    {
        var metadata = Event(new { tool_calls = new[] { new { index = 0, id = "call-1", type = "function" } } });
        var handler = new Handler(metadata + Tool(0, "edit_design",
            """{"base_revision":0,"operations":[{"kind":"set_summary","summary":"工作布局"}]}""", "tool_calls"));
        using var client = new HttpClient(handler);
        var reply = await Provider(client).ReplyAsync(Context(), new DesktopWorkspaceDesignSession().Snapshot());
        Assert.Single(reply.Commands.OfType<SetWorkspaceSummary>());
        Assert.Equal("primary", Assert.Single(reply.Commands.OfType<SetWorkspaceDisplayPolicy>()).Policy.DisplayRef);
    }

    [Fact]
    public async Task OrdinaryConversation_StreamsTextWithoutRequiringJsonOrDesignChanges()
    {
        var handler = new Handler(Event(new { content = "可以，" }) + Event(new { content = "我们先讨论。" }, "stop") + "data: [DONE]\n\n");
        using var client = new HttpClient(handler);
        var chunks = new List<string>();
        var session = new DesktopWorkspaceDesignSession();
        var reply = await Provider(client).ReplyAsync(Context(), session.Snapshot(), new InlineProgress(chunks.Add));
        Assert.Equal("可以，我们先讨论。", reply.Text);
        Assert.Equal(new[] { "可以，", "我们先讨论。" }, chunks);
        Assert.Empty(reply.Commands);
        Assert.Null(reply.Action);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        Assert.DoesNotContain("private-stable-id", handler.RequestBody!);
        Assert.Equal(0, session.Snapshot().Revision);
    }

    [Fact]
    public async Task FragmentedToolArguments_AreAssembledBeforeReturningCommands()
    {
        var arguments = """{"base_revision":0,"operations":[{"kind":"upsert_zone","zone_key":"work","name":"工作","display_ref":"primary","left":0,"top":0,"width":0.5,"height":1}]}""";
        var split = arguments.Length / 2;
        var handler = new Handler(Tool(0, "edit_design", arguments[..split]) + Tool(0, null, arguments[split..], "tool_calls"));
        using var client = new HttpClient(handler);
        var session = new DesktopWorkspaceDesignSession();
        var reply = await Provider(client).ReplyAsync(Context(), session.Snapshot());
        Assert.Empty(session.Snapshot().State.Draft.Zones);
        Assert.Single(reply.Commands.OfType<UpsertWorkspaceZone>());
        Assert.Single(reply.Commands.OfType<SetWorkspaceDisplayPolicy>());
        Assert.True(session.TryApply(0, reply.Commands).Applied);
        Assert.Equal("work", Assert.Single(session.Snapshot().State.Draft.Zones).ZoneKey);
    }

    [Fact]
    public async Task ModelCanOfferAnAction_WithoutPerformingIt()
    {
        var handler = new Handler(Tool(0, "offer_action", """{"kind":"save_layout","title":"保存为布局 v1？","layout_name":"工作桌面"}""", "tool_calls"));
        using var client = new HttpClient(handler);
        var session = new DesktopWorkspaceDesignSession();
        var reply = await Provider(client).ReplyAsync(Context(), session.Snapshot());
        Assert.Equal(WorkspaceChatActionKind.SaveLayout, reply.Action!.Kind);
        Assert.Equal("工作桌面", reply.Action.LayoutName);
        Assert.Empty(reply.Commands);
        Assert.Equal(0, session.Snapshot().Sequence);
    }

    [Fact]
    public async Task SaveAndPreviewOffersAreBothReturnedWithoutExecutingOrDroppingEither()
    {
        using var client = new HttpClient(new Handler(
            Tool(0, "offer_action", """{"kind":"save_layout","title":"保存这份设计","layout_name":"横条分区"}""") +
            Tool(1, "offer_action", """{"kind":"preview_design","title":"查看精确预览"}""", "tool_calls")));
        var session = new DesktopWorkspaceDesignSession();
        var reply = await Provider(client).ReplyAsync(Context(), session.Snapshot());
        Assert.Equal(new[] { WorkspaceChatActionKind.SaveLayout, WorkspaceChatActionKind.PreviewDesign }, reply.Actions.Select(action => action.Kind));
        Assert.Equal("横条分区", reply.Actions[0].LayoutName);
        Assert.Equal(0, session.Snapshot().Sequence);
    }

    [Fact]
    public async Task RepeatedSameOfferProducesOneAction()
    {
        using var client = new HttpClient(new Handler(
            Tool(0, "offer_action", """{"kind":"save_layout","title":"保存","layout_name":"横条分区"}""") +
            Tool(1, "offer_action", """{"kind":"save_layout","title":"保存设计","layout_name":"横条分区"}""", "tool_calls")));
        var reply = await Provider(client).ReplyAsync(Context(), new DesktopWorkspaceDesignSession().Snapshot());
        Assert.Single(reply.Actions);
    }

    [Fact]
    public async Task ConflictingSaveNamesRejectTheResponseBeforeAnyDesignChange()
    {
        using var client = new HttpClient(new Handler(
            Tool(0, "edit_design", """{"base_revision":0,"operations":[{"kind":"set_summary","summary":"new"}]}""") +
            Tool(1, "offer_action", """{"kind":"save_layout","title":"保存","layout_name":"A"}""") +
            Tool(2, "offer_action", """{"kind":"save_layout","title":"保存","layout_name":"B"}""", "tool_calls")));
        var session = new DesktopWorkspaceDesignSession();
        await Assert.ThrowsAsync<InvalidDataException>(() => Provider(client).ReplyAsync(Context(), session.Snapshot()));
        Assert.Equal(0, session.Snapshot().Revision);
        Assert.Empty(session.Snapshot().State.Draft.Summary);
    }

    [Fact]
    public async Task TextClaimingACardDoesNotInventAnActionOrExecuteAnything()
    {
        using var client = new HttpClient(new Handler(Event(new { content = "下面已经放了保存卡片。" }, "stop")));
        var session = new DesktopWorkspaceDesignSession();
        var reply = await Provider(client).ReplyAsync(Context(), session.Snapshot());
        Assert.Empty(reply.Actions);
        Assert.Equal(0, session.Snapshot().Sequence);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData(null)]
    public async Task IncompleteOrTruncatedResponses_DoNotReturnExecutableChanges(string? finish)
    {
        var handler = new Handler(Tool(0, "edit_design", """{"base_revision":0,"operations":[{"kind":"set_summary","summary":"unfinished"}]}""", finish));
        using var client = new HttpClient(handler);
        var session = new DesktopWorkspaceDesignSession();
        await Assert.ThrowsAsync<InvalidDataException>(() => Provider(client).ReplyAsync(Context(), session.Snapshot()));
        Assert.Equal(0, session.Snapshot().Revision);
    }

    [Fact]
    public async Task IncorrectModelBaseRevision_IsRejected()
    {
        var handler = new Handler(Tool(0, "edit_design", """{"base_revision":99,"operations":[]}""", "tool_calls"));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Provider(client).ReplyAsync(Context(), new DesktopWorkspaceDesignSession().Snapshot()));
    }

    [Fact]
    public async Task UnknownTool_CannotRequestPhysicalExecution()
    {
        var handler = new Handler(Tool(0, "apply_desktop", "{}", "tool_calls"));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Provider(client).ReplyAsync(Context(), new DesktopWorkspaceDesignSession().Snapshot()));
    }

    [Fact]
    public async Task InvalidSecondTool_DiscardsTheEntireResponse()
    {
        var handler = new Handler(Tool(0, "edit_design", """{"base_revision":0,"operations":[{"kind":"set_summary","summary":"valid"}]}""") +
            Tool(1, "apply_desktop", "{}", "tool_calls"));
        using var client = new HttpClient(handler);
        var session = new DesktopWorkspaceDesignSession();
        await Assert.ThrowsAsync<InvalidDataException>(() => Provider(client).ReplyAsync(Context(), session.Snapshot()));
        Assert.Empty(session.Snapshot().State.Draft.Summary);
    }

    [Fact]
    public async Task UnknownDisplay_IsRejectedInsteadOfGuessing()
    {
        var handler = new Handler(Tool(0, "edit_design", """{"base_revision":0,"operations":[{"kind":"upsert_zone","zone_key":"work","name":"工作","display_ref":"unknown","left":0,"top":0,"width":1,"height":1}]}""", "tool_calls"));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Provider(client).ReplyAsync(Context(), new DesktopWorkspaceDesignSession().Snapshot()));
    }

    [Fact]
    public async Task ProviderReturningRegularJsonDespiteStreamingRequest_IsSupported()
    {
        var handler = new Handler(JsonSerializer.Serialize(new { choices = new[] { new { index = 0,
            message = new { content = "可以继续讨论。" }, finish_reason = "stop" } } }), json: true);
        using var client = new HttpClient(handler);
        Assert.Equal("可以继续讨论。", (await Provider(client).ReplyAsync(Context(), new DesktopWorkspaceDesignSession().Snapshot())).Text);
    }

    [Fact]
    public async Task HttpFailure_DoesNotRetryWithAFullBlueprintRequest()
    {
        var handler = new Handler("not supported", status: HttpStatusCode.BadRequest);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).ReplyAsync(Context(), new DesktopWorkspaceDesignSession().Snapshot()));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task UserCancellation_IsRespected()
    {
        using var client = new HttpClient(new WaitingHandler());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(client).ReplyAsync(Context(),
            new DesktopWorkspaceDesignSession().Snapshot(), cancellationToken: cts.Token));
    }

    [Fact]
    public void CurrentSemanticCache_CanCreateConversationContextWithoutInventingZones()
    {
        var semantic = new DesktopSemanticUnderstanding("桌面摘要", [new("docs", "文档", "工作文档")],
            [new("private-stable-id", "docs", 0.9, "文档")]);
        var draft = DesktopSemanticConversationDraftFactory.Create(semantic, "继续设计");
        Assert.Empty(draft.Zones);
        Assert.Null(Assert.Single(draft.Assignments).ZoneKey);
        Assert.Equal("docs", draft.Assignments[0].CategoryKey);
        Assert.Equal("继续设计", draft.IntentSummary);
    }

    [Fact]
    public async Task FragmentedStream_MetadataOverheadDoesNotConsumeContentBudget()
    {
        var response = string.Concat(Enumerable.Repeat(Event(new { content = "字" }), 8000)) +
            Event(new { }, "stop");
        Assert.True(response.Length > 512 * 1024);
        using var client = new HttpClient(new Handler(response));
        var reply = await Provider(client).ReplyAsync(Context(), new DesktopWorkspaceDesignSession().Snapshot());
        Assert.Equal(new string('字', 8000), reply.Text);
    }

    [Fact]
    public async Task OversizedContentAcrossEventsStillFails()
    {
        var response = string.Concat(Enumerable.Repeat(Event(new { content = new string('x', 4096) }), 129)) +
            Event(new { }, "stop");
        using var client = new HttpClient(new Handler(response));
        await Assert.ThrowsAsync<InvalidDataException>(() => Provider(client).ReplyAsync(Context(),
            new DesktopWorkspaceDesignSession().Snapshot()));
    }

    [Fact]
    public async Task ToolArgumentsShareTheContentBudget()
    {
        var response = string.Concat(Enumerable.Repeat(Tool(0, null, new string('x', 4096)), 129));
        using var client = new HttpClient(new Handler(response));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Provider(client).ReplyAsync(Context(),
            new DesktopWorkspaceDesignSession().Snapshot()));
        Assert.Contains("正文或工具参数", error.Message);
    }

    [Fact]
    public async Task IgnoredStreamCommentsStillHaveATransportLimit()
    {
        var response = string.Concat(Enumerable.Repeat(":" + new string('x', 4096) + "\n", 4096));
        using var client = new HttpClient(new Handler(response));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Provider(client).ReplyAsync(Context(),
            new DesktopWorkspaceDesignSession().Snapshot()));
        Assert.Contains("传输量", error.Message);
    }

    [Fact]
    public async Task ExplicitPreservePolicyIsNotOverwrittenByInitialDefaults()
    {
        using var client = new HttpClient(new Handler(Tool(0, "edit_design",
            """{"base_revision":0,"operations":[{"kind":"set_display_policy","display_ref":"primary","policy":"preserve"}]}""", "tool_calls")));
        var reply = await Provider(client).ReplyAsync(Context(), new DesktopWorkspaceDesignSession().Snapshot());
        Assert.Equal(DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints.DesktopWorkspaceDisplayPolicyKind.Preserve,
            Assert.Single(reply.Commands.OfType<SetWorkspaceDisplayPolicy>()).Policy.Policy);
    }

    [Theory]
    [InlineData("unknown", "design")]
    [InlineData("primary", "empty")]
    public async Task InvalidDisplayPolicyIsRejected(string display, string policy)
    {
        var arguments = JsonSerializer.Serialize(new { base_revision = 0, operations = new[] { new { kind = "set_display_policy", display_ref = display, policy } } });
        using var client = new HttpClient(new Handler(Tool(0, "edit_design", arguments, "tool_calls")));
        await Assert.ThrowsAsync<InvalidDataException>(() => Provider(client).ReplyAsync(Context(), new DesktopWorkspaceDesignSession().Snapshot()));
    }

    private static OpenAiCompatibleWorkspaceChatProvider Provider(HttpClient client)
        => new(client, new(new Uri("http://localhost/v1"), "test-model"), new Secrets());

    private static DesktopLayoutConversationRequest Context() => new(
        new("intent", "summary", [new("docs", "文档", "工作文档")]), [new("primary", true, 1)],
        [new("private-stable-id", "Report.txt", "docs", "primary")], DesktopLayoutConversationState.Empty, "先讨论一下");

    private static string Event(object delta, string? finish = null)
        => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\n";

    private static string Tool(int index, string? name, string arguments, string? finish = null)
        => Event(new { tool_calls = new[] { new { index, function = new { name, arguments } } } }, finish);

    private sealed class InlineProgress(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }
    private sealed class Secrets : IIntelligenceSecretResolver
    {
        public ValueTask<string?> ResolveSecretAsync(string secretReference, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);
    }
    private sealed class Handler(string response, bool json = false, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal string? RequestBody;
        internal int RequestCount;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(status) { Content = new StringContent(response, Encoding.UTF8, json ? "application/json" : "text/event-stream") };
        }
    }
    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("must cancel");
        }
    }
}
