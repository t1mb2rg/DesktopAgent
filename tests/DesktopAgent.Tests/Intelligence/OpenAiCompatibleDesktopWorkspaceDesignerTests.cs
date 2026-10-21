using System.Net;
using System.Text;
using System.Text.Json;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;

namespace DesktopAgent.Tests.Intelligence;

public sealed class OpenAiCompatibleDesktopWorkspaceDesignerTests
{
    private const string UserPromptPrefix =
        "Design one Workspace Blueprint revision from this Provider-safe JSON:\n";

    [Fact]
    public async Task Provider_SendsOnlyProviderSafeDesignContext_AndBindsLocalRevisionIdentity()
    {
        string? requestBody = null;
        using var client = CreateClient(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(CreateCompletionJson(CreateValidBlueprintJson()));
        });
        var request = CreateRequest();
        var designer = CreateDesigner(client);

        var result = await designer.DesignAsync(request);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Blueprint);
        Assert.Equal(request.RevisionTarget.BlueprintId, result.Blueprint!.BlueprintId);
        Assert.Equal(1, result.Blueprint.Revision);
        Assert.Null(result.Blueprint.ParentRevision);
        Assert.Equal(3, result.Blueprint.DisplayPolicies.Count);
        Assert.Equal(
            DesktopWorkspaceDisplayPolicyKind.Preserve,
            result.Blueprint.DisplayPolicies.Single(policy => policy.DisplayRef == "secondary_1").Policy);
        Assert.Contains(result.Blueprint.ReservedRegions, static region =>
            region.RegionKey == "wallpaper-center" && region.DisplayRef == "primary");

        var context = ExtractUserContext(requestBody!);
        Assert.Equal("workspace-blueprint-v1", context.GetProperty("contract").GetString());
        Assert.Equal(3, context.GetProperty("displays").GetArrayLength());
        Assert.Equal(3, context.GetProperty("categories").GetArrayLength());
        Assert.Single(context.GetProperty("critical_items").EnumerateArray());
        Assert.Equal("item-0001", context.GetProperty("critical_items")[0].GetProperty("item_ref").GetString());

        var providerContext = context.GetRawText();
        Assert.DoesNotContain(request.RevisionTarget.BlueprintId.ToString(), providerContext, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LOCAL-STABLE-ID", providerContext, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\.\\DISPLAY", providerContext, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pixel", providerContext, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cell", providerContext, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("writer", providerContext, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("observation", providerContext, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_RejectsInventedDisplayRefAtBoundary()
    {
        using var client = CreateClient((_, _) => Task.FromResult(
            JsonResponse(CreateCompletionJson(CreateValidBlueprintJson().Replace(
                "\"secondary_2\"",
                "\"secondary_7\"",
                StringComparison.Ordinal)))));
        var designer = CreateDesigner(client);

        var result = await designer.DesignAsync(CreateRequest());

        Assert.False(result.Succeeded);
        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, result.Status);
        Assert.Contains("显示器", result.DiagnosticMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedesignRequest_SendsPreviousBlueprintAndStructuredDiagnostics_WithoutLocalIdentities()
    {
        string? requestBody = null;
        using var client = CreateClient(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(CreateCompletionJson(CreateValidBlueprintJson()));
        });

        var first = CreateRequest();
        var previous = new DesktopWorkspaceBlueprint(
            first.RevisionTarget.BlueprintId,
            1,
            "上一版设计",
            displayPolicies:
            [
                new("primary", DesktopWorkspaceDisplayPolicyKind.Design),
                new("secondary_1", DesktopWorkspaceDisplayPolicyKind.Preserve),
                new("secondary_2", DesktopWorkspaceDisplayPolicyKind.Design),
            ],
            zones:
            [new("tools", "Tools", "secondary_2", new(0, 0, 0.1, 1))],
            categoryRules:
            [
                new("dev", "tools"),
                new("docs", "tools"),
                new("system", "tools"),
            ],
            itemOverrides:
            [new("item-0001", DesktopWorkspaceItemOverrideKind.Keep)]);
        var request = new DesktopWorkspaceDesignRequest(
            new DesktopWorkspaceDesignRevisionTarget(first.RevisionTarget.BlueprintId, 2, 1),
            first.DesktopSummary,
            first.UserGuidance,
            first.Displays,
            first.Categories,
            first.CriticalItems,
            previous,
            [
                new DesktopWorkspaceDesignRealizationDiagnostic(
                    DesktopWorkspaceDesignRealizationDiagnosticCode.ZoneCapacityInsufficient,
                    "该 Zone 忠实量化后的可用容量不足。",
                    displayRef: "secondary_2",
                    elementKey: "tools",
                    requiredSlots: 37,
                    availableSlots: 31),
            ]);
        var designer = CreateDesigner(client);

        var result = await designer.DesignAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Blueprint!.Revision);
        Assert.Equal(1, result.Blueprint.ParentRevision);

        var context = ExtractUserContext(requestBody!);
        Assert.Equal(1, context.GetProperty("previous_blueprint").GetProperty("revision").GetInt32());
        var diagnostic = Assert.Single(context.GetProperty("realization_diagnostics").EnumerateArray());
        Assert.Equal(37, diagnostic.GetProperty("required_slots").GetInt32());
        Assert.Equal(31, diagnostic.GetProperty("available_slots").GetInt32());
        Assert.False(context.GetProperty("revision").TryGetProperty("blueprint_id", out _));
        Assert.DoesNotContain("LOCAL-STABLE-ID", context.GetRawText(), StringComparison.Ordinal);
    }

    private static DesktopWorkspaceDesignRequest CreateRequest()
        => new(
            new DesktopWorkspaceDesignRevisionTarget(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                1),
            "桌面包含开发工具、文档和一个系统项目。",
            "把所有可移动图标整理到下方副屏；主屏中央留白；左副屏保持现状。",
            [
                new DesktopWorkspaceDesignDisplay(
                    "primary", "主屏", true,
                    new DesktopWorkspaceRelativeRect(0.34, 0, 0.33, 0.50),
                    16d / 9d),
                new DesktopWorkspaceDesignDisplay(
                    "secondary_1", "左副屏", false,
                    new DesktopWorkspaceRelativeRect(0, 0, 0.34, 0.50),
                    16d / 9d),
                new DesktopWorkspaceDesignDisplay(
                    "secondary_2", "下方副屏", false,
                    new DesktopWorkspaceRelativeRect(0.34, 0.50, 0.33, 0.50),
                    16d / 9d),
            ],
            [
                new DesktopWorkspaceDesignCategorySummary("dev", "开发工具", "IDE 和工程工具", 20),
                new DesktopWorkspaceDesignCategorySummary("docs", "文档", "资料与文本", 30),
                new DesktopWorkspaceDesignCategorySummary("system", "系统项目", "Windows 系统桌面项目", 1),
            ],
            [
                new DesktopWorkspaceDesignCriticalItem(
                    "item-0001",
                    "Recycle Bin",
                    "system",
                    "primary",
                    isMovable: false),
            ]);

    private static string CreateValidBlueprintJson() => """
{
  "design_summary": "左副屏保持，主屏中央留白，其余项目整理到下方副屏。",
  "display_policies": [
    { "display_ref": "primary", "policy": "design" },
    { "display_ref": "secondary_1", "policy": "preserve" },
    { "display_ref": "secondary_2", "policy": "design" }
  ],
  "zones": [
    { "zone_key": "tools", "name": "Tools", "display_ref": "secondary_2", "bounds": [0.0, 0.0, 0.45, 1.0], "anchor": "top_left", "packing_order": "row_major" },
    { "zone_key": "docs", "name": "Documents", "display_ref": "secondary_2", "bounds": [0.55, 0.0, 0.45, 1.0], "anchor": "top_left", "packing_order": "row_major" }
  ],
  "zone_relations": [
    { "zone_key": "tools", "relation": "left_of", "other_zone_key": "docs" }
  ],
  "category_rules": [
    { "category_key": "dev", "zone_key": "tools" },
    { "category_key": "docs", "zone_key": "docs" },
    { "category_key": "system", "zone_key": "tools" }
  ],
  "item_overrides": [
    { "item_ref": "item-0001", "kind": "keep" }
  ],
  "reserved_regions": [
    { "region_key": "wallpaper-center", "purpose": "保持壁纸中央可见", "display_ref": "primary", "bounds": [0.20, 0.15, 0.60, 0.70] }
  ]
}
""";

    private static OpenAiCompatibleDesktopWorkspaceDesigner CreateDesigner(HttpClient client)
        => new(
            client,
            new OpenAiCompatibleProviderOptions(new Uri("https://example.test/v1"), "model"),
            new StubSecretResolver());

    private static HttpClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        => new(new DelegateHandler(handler));

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static string CreateCompletionJson(string content)
        => JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { content } },
            },
        });

    private static JsonElement ExtractUserContext(string requestBody)
    {
        using var outer = JsonDocument.Parse(requestBody);
        var content = outer.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString()!;
        Assert.StartsWith(UserPromptPrefix, content, StringComparison.Ordinal);
        using var context = JsonDocument.Parse(content[UserPromptPrefix.Length..]);
        return context.RootElement.Clone();
    }

    private sealed class StubSecretResolver : IIntelligenceSecretResolver
    {
        public ValueTask<string?> ResolveSecretAsync(
            string secretReference,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>("unused");
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
