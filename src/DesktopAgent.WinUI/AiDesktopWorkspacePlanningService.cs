using System.Text;
using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Equivalence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Persistence.Productization;
using DesktopAgent.WindowsInterop.Intelligence;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopObservations;
using DesktopAgent.WindowsInterop.Displays;

namespace DesktopAgent.WinUI;

internal enum AiDesktopWorkspacePlanningProductStatus
{
    Success = 0,
    AiDisabled = 1,
    GeometryUnavailable = 2,
    PreparationFailed = 3,
    ProviderFailed = 4,
    CompileBlocked = 5,
    DesignUnresolved = 6,
    EquivalenceFailed = 7,
    PreviewFailed = 8,
}

internal sealed record AiDesktopWorkspacePlanningProductResult(
    AiDesktopWorkspacePlanningProductStatus Status,
    DesktopWorkspaceFrozenPreview? Preview,
    int DesignAttempts,
    IntelligenceProviderStatus? ProviderStatus,
    int? HttpStatusCode,
    IReadOnlyList<DesktopExplorerWorkspaceGeometryIssue> GeometryIssues,
    IReadOnlyList<DesktopWorkspaceCompileIssue> CompileIssues,
    IReadOnlyList<DesktopWorkspaceEquivalenceIssue> EquivalenceIssues,
    string? Message,
    string? PersistenceWarning = null)
{
    internal bool Succeeded =>
        Status == AiDesktopWorkspacePlanningProductStatus.Success && Preview is not null;
}

/// <summary>
/// ADR-0005 Workspace Blueprint 产品级只读入口。
/// 复用语义仍匹配的 Full Understanding，以 fresh Observation 生成 Explorer-native Frozen Preview；
/// 成功且通过独立 equivalence 后，把本轮完整 immutable Blueprint revision history 写入 Workspace Library。
/// Provider-safe design guidance 与 durable SourceUserIntent 明确分离：前者可包含 request-local item refs，
/// 后者只保存用户实际表达过的布局要求。Library persistence 从不保存 Exact Plan / Frozen Preview /
/// Confirmation，也不授予 Candidate、Recovery 或 Writer authority。
/// </summary>
internal sealed class AiDesktopWorkspacePlanningService
{
    internal static readonly TimeSpan ProductDesignTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 网格实测事实的唯一读取入口（Diagnostics 探针是唯一写入方）。
    /// 未实测的显示器读到空集合，Adapter 回退推断并把来源写进 Provenance。
    /// </summary>
    private readonly DesktopExplorerGridFactReader _workspaceGridFactReader =
        new(new SqliteExplorerGridMeasurementStore(DesktopAgentDataPaths.DefaultDatabasePath));

    internal async Task<AiDesktopWorkspacePlanningProductResult> PlanAsync(
        DesktopUnderstandingDraft understanding,
        DesktopObservation observation,
        DesktopLayoutConversationState conversationState,
        string? sourceUserIntent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(understanding);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(conversationState);

        var settingsStore = new JsonDesktopProductSettingsStore(
            DesktopAgentDataPaths.DefaultSettingsPath);
        await settingsStore.InitializeAsync(cancellationToken);
        var settings = await settingsStore.GetAsync(cancellationToken);
        var configuredOptions = DesktopProductIntelligenceOptionsFactory.Create(settings);
        if (configuredOptions is null)
        {
            return Failure(
                AiDesktopWorkspacePlanningProductStatus.AiDisabled,
                message: "AI 当前已关闭，请先在设置中配置并启用 Provider。",
                providerStatus: IntelligenceProviderStatus.NotConfigured);
        }

        // 缓存的 Observation 只证明语义理解的来源，不能成为新预览的几何起点。
        // 在 AI 设计之前重新读取；最终 Apply 仍独立执行严格 fresh check。
        DesktopObservationCaptureResult capture;
        using (var staExecutor = new StaThreadExecutor())
        {
            var observationSource = new WindowsDesktopObservationSource(
                new NativeDisplayTopologyReader(),
                new DesktopShellIconReader(),
                new DesktopShellIconPositioningCapabilitiesReader(),
                staExecutor);
            capture = await observationSource.CaptureAsync(cancellationToken);
        }
        if (!capture.IsCaptured || capture.Observation is null)
        {
            return Failure(
                AiDesktopWorkspacePlanningProductStatus.PreparationFailed,
                message: "无法重新读取当前桌面，未生成精确预览：" +
                         string.Join("；", capture.Issues.Select(static issue => issue.Message)));
        }

        var freshObservation = capture.Observation;
        var geometryResult = DesktopExplorerWorkspaceGeometryAdapter.Build(
            freshObservation,
            await TryReadGridFactsAsync(freshObservation, cancellationToken).ConfigureAwait(false));
        if (!geometryResult.Succeeded || geometryResult.Geometry is null)
        {
            return Failure(
                AiDesktopWorkspacePlanningProductStatus.GeometryUnavailable,
                message: geometryResult.Issues.FirstOrDefault()?.Message ??
                         "当前 DesktopObservation 无法建立可靠的 Explorer-native geometry。",
                geometryIssues: geometryResult.Issues);
        }

        DesktopWorkspaceDesignPreparation preparation;
        try
        {
            preparation = DesktopWorkspaceDesignPreparationFactory.CreateFromFreshObservation(
                understanding,
                observation,
                freshObservation,
                geometryResult.Geometry,
                conversationState);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException)
        {
            return Failure(
                AiDesktopWorkspacePlanningProductStatus.PreparationFailed,
                message: ex.Message);
        }

        var options = new OpenAiCompatibleProviderOptions(
            configuredOptions.BaseUri,
            configuredOptions.Model,
            configuredOptions.SecretReference,
            ProductDesignTimeout);
        using var httpClient = new HttpClient();
        var secretStore = new DpapiIntelligenceSecretStore(
            DesktopAgentDataPaths.DefaultSecretsDirectory);
        IDesktopWorkspaceDesigner designer = new ContractRepairingDesktopWorkspaceDesigner(
            new OpenAiCompatibleDesktopWorkspaceDesigner(
                httpClient,
                options,
                secretStore));
        var coordinator = new DesktopWorkspaceDesignLoopCoordinator(designer);

        var loop = await coordinator.DesignAndCompileAsync(
            preparation.Request,
            preparation.SemanticUnderstanding,
            geometryResult.Geometry,
            preparation.ItemFacts,
            preparation.ItemRefBindings,
            cancellationToken);
        if (!loop.Succeeded)
            return FromLoopFailure(loop);

        try
        {
            var preview = DesktopWorkspaceFrozenPreviewFactory.Create(
                loop.Blueprint!,
                geometryResult.Geometry,
                loop.Plan!,
                loop.Certificate!,
                preparation.SourceSnapshot,
                preparation.ItemRefBindings);

            string? persistenceWarning = null;
            try
            {
                await PersistSuccessfulBlueprintAsync(
                    loop.BlueprintRevisions,
                    sourceUserIntent,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                persistenceWarning =
                    $"Workspace Preview 已形成，但 Blueprint 没有写入长期 Library：{ex.Message}";
            }

            return new AiDesktopWorkspacePlanningProductResult(
                AiDesktopWorkspacePlanningProductStatus.Success,
                preview,
                loop.DesignAttempts,
                IntelligenceProviderStatus.Success,
                HttpStatusCode: null,
                GeometryIssues: [],
                CompileIssues: [],
                EquivalenceIssues: [],
                Message: null,
                PersistenceWarning: persistenceWarning);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return Failure(
                AiDesktopWorkspacePlanningProductStatus.PreviewFailed,
                loop.DesignAttempts,
                message: ex.Message);
        }
    }

    private static async Task PersistSuccessfulBlueprintAsync(
        IReadOnlyList<DesktopWorkspaceBlueprint> revisions,
        string? sourceUserIntent,
        CancellationToken cancellationToken)
    {
        if (revisions.Count == 0)
            throw new InvalidDataException("成功的 Workspace Design 没有可持久化 revision history。");

        var finalBlueprint = revisions[^1];
        var durableUserIntent = string.IsNullOrWhiteSpace(sourceUserIntent)
            ? null
            : sourceUserIntent.Trim();
        var name = BuildAssetName(durableUserIntent, finalBlueprint);
        var createdAt = DateTimeOffset.UtcNow;

        var store = new SqliteDesktopWorkspaceBlueprintStore(
            DesktopAgentDataPaths.DefaultDatabasePath);
        await store.InitializeAsync(cancellationToken);

        var first = new DesktopWorkspaceBlueprintRevisionRecord(
            revisions[0],
            createdAt,
            sourceUnderstandingId: null,
            sourceUserIntent: durableUserIntent);
        await store.CreateAsync(name, first, cancellationToken);

        foreach (var blueprint in revisions.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.AppendRevisionAsync(
                new DesktopWorkspaceBlueprintRevisionRecord(
                    blueprint,
                    createdAt,
                    sourceUnderstandingId: null,
                    sourceUserIntent: durableUserIntent),
                cancellationToken);
        }
    }

    private static string BuildAssetName(
        string? sourceUserIntent,
        DesktopWorkspaceBlueprint blueprint)
    {
        var source = string.IsNullOrWhiteSpace(sourceUserIntent)
            ? blueprint.DesignSummary
            : sourceUserIntent;
        var compact = CollapseWhitespace(source);
        if (compact.Length == 0)
            return "AI Workspace";
        return compact.Length <= 48
            ? compact
            : $"{compact[..47].TrimEnd()}…";
    }

    private static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
                builder.Append(' ');
            builder.Append(character);
            pendingSpace = false;
        }
        return builder.ToString();
    }

    private static AiDesktopWorkspacePlanningProductResult FromLoopFailure(
        DesktopWorkspaceDesignLoopResult loop)
        => loop.Status switch
        {
            DesktopWorkspaceDesignLoopStatus.ProviderFailed => Failure(
                AiDesktopWorkspacePlanningProductStatus.ProviderFailed,
                loop.DesignAttempts,
                loop.DiagnosticMessage ?? "Workspace Designer 没有返回可用 Blueprint。",
                loop.ProviderStatus),
            DesktopWorkspaceDesignLoopStatus.CompileBlocked => Failure(
                AiDesktopWorkspacePlanningProductStatus.CompileBlocked,
                loop.DesignAttempts,
                loop.CompileIssues.FirstOrDefault()?.Message ?? "当前本地事实阻塞 Workspace Compile。",
                compileIssues: loop.CompileIssues),
            DesktopWorkspaceDesignLoopStatus.DesignUnresolved => Failure(
                AiDesktopWorkspacePlanningProductStatus.DesignUnresolved,
                loop.DesignAttempts,
                "Workspace Designer 在有界重试预算内仍未形成可忠实落地的 Blueprint。",
                compileIssues: loop.CompileIssues),
            DesktopWorkspaceDesignLoopStatus.EquivalenceFailed => Failure(
                AiDesktopWorkspacePlanningProductStatus.EquivalenceFailed,
                loop.DesignAttempts,
                loop.EquivalenceIssues.FirstOrDefault()?.Message ?? "Exact Plan 未通过独立语义等价验证。",
                equivalenceIssues: loop.EquivalenceIssues),
            _ => Failure(
                AiDesktopWorkspacePlanningProductStatus.PreviewFailed,
                loop.DesignAttempts,
                $"Workspace Design loop 返回未识别状态：{loop.Status}。"),
        };

    /// <summary>
    /// 读取当前拓扑下的实测网格事实。存储故障或事实缺失都不应让计划路径失败：
    /// Adapter 会回退到推断，并把每个显示器的来源写进 Provenance。
    /// </summary>
    private async Task<IReadOnlyList<ExplorerGridMeasurement>?> TryReadGridFactsAsync(
        DesktopObservation observation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _workspaceGridFactReader
                .TryReadActiveAsync(observation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static AiDesktopWorkspacePlanningProductResult Failure(
        AiDesktopWorkspacePlanningProductStatus status,
        int designAttempts = 0,
        string? message = null,
        IntelligenceProviderStatus? providerStatus = null,
        int? httpStatusCode = null,
        IReadOnlyList<DesktopExplorerWorkspaceGeometryIssue>? geometryIssues = null,
        IReadOnlyList<DesktopWorkspaceCompileIssue>? compileIssues = null,
        IReadOnlyList<DesktopWorkspaceEquivalenceIssue>? equivalenceIssues = null)
        => new(
            status,
            Preview: null,
            designAttempts,
            providerStatus,
            httpStatusCode,
            geometryIssues ?? [],
            compileIssues ?? [],
            equivalenceIssues ?? [],
            message);
}
