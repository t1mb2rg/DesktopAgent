using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.Automation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

namespace DesktopAgent.Application.DesktopAutomation;

/// <summary>
/// 一块显示器在"当前这一刻"的自动执行资格：证据 + 授权。
///
/// 两者必须一起读：证据回答"图标会落在哪"，授权回答"人是否已经同意它自己动手"。
/// 分开两次抓取会出现"用 A 时刻的证据配 B 时刻的授权"这种拼接状态，
/// 所以由同一个 provider 从同一次桌面观察里同时得出。
/// </summary>
public sealed record DesktopGridDisplayClearance
{
    public DesktopGeometryEvidence GeometryEvidence { get; }

    public bool IsAuthorizedForUnattendedExecution { get; }

    public DesktopGridDisplayClearance(
        DesktopGeometryEvidence geometryEvidence,
        bool isAuthorizedForUnattendedExecution)
    {
        if (!Enum.IsDefined(geometryEvidence))
            throw new ArgumentOutOfRangeException(nameof(geometryEvidence), "GeometryEvidence 无效。");

        // 授权脱离实测证据就不成立：授权绑定的是"实测过的那套网格"，
        // 网格本身没实测过（或已随拓扑失效）时，授权没有可依附的对象。
        // 这里主动降级而不是抛异常：调用方仍需要拿到真实的证据等级去解释原因。
        if (geometryEvidence != DesktopGeometryEvidence.Measured)
            isAuthorizedForUnattendedExecution = false;

        GeometryEvidence = geometryEvidence;
        IsAuthorizedForUnattendedExecution = isAuthorizedForUnattendedExecution;
    }

    public static DesktopGridDisplayClearance Unknown { get; } =
        new(DesktopGeometryEvidence.Unknown, isAuthorizedForUnattendedExecution: false);
}

/// <summary>
/// A 方案（推断默认 + 受控执行 + 探测做可选校准）里"这块屏现在能不能自己动手"的唯一查询入口。
///
/// 它只回答一个问题：**这块显示器在当前拓扑下，既有实测事实、又有人给过的站定授权吗？**
/// 读不到观察、没有事实、事实属于别的显示器或别的拓扑、授权已撤销或绑定别的拓扑，
/// 一律返回不可授权——这里不猜、也不把"从来没测过/从来没同意过"和"测过又失效/同意过又失效"混为一谈：都不能授权。
///
/// 监督执行不经过这里：用户确认过的动作是 DirectUserRequest，与证据等级和站定授权都无关。
/// </summary>
public interface IDesktopGridDisplayClearanceProvider
{
    Task<DesktopGridDisplayClearance> GetClearanceAsync(
        string? targetDeviceName,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class DesktopGridDisplayClearanceProvider : IDesktopGridDisplayClearanceProvider
{
    private readonly IDesktopObservationSource _observationSource;
    private readonly IExplorerGridMeasurementStore _measurementStore;
    private readonly IExplorerGridDisplayAuthorizationStore _authorizationStore;

    public DesktopGridDisplayClearanceProvider(
        IDesktopObservationSource observationSource,
        IExplorerGridMeasurementStore measurementStore,
        IExplorerGridDisplayAuthorizationStore authorizationStore)
    {
        _observationSource = observationSource ?? throw new ArgumentNullException(nameof(observationSource));
        _measurementStore = measurementStore ?? throw new ArgumentNullException(nameof(measurementStore));
        _authorizationStore = authorizationStore ?? throw new ArgumentNullException(nameof(authorizationStore));
    }

    public async Task<DesktopGridDisplayClearance> GetClearanceAsync(
        string? targetDeviceName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetDeviceName))
            return DesktopGridDisplayClearance.Unknown;

        var deviceName = targetDeviceName.Trim();

        DesktopObservationCaptureResult capture;
        try
        {
            capture = await _observationSource.CaptureAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 观察不到当前桌面 = 资格不可用。fail closed：绝不退回"上次那条事实/上次那次授权"。
            return DesktopGridDisplayClearance.Unknown;
        }

        if (!capture.IsCaptured || capture.Observation is null)
            return DesktopGridDisplayClearance.Unknown;

        // 与 Adapter / 写事实方共用同一口径：身份算不出来就不查任何一张表。
        var fingerprint = DesktopExplorerWorkspaceGeometryAdapter.ComputeTopologyIdentity(capture.Observation);
        if (string.IsNullOrWhiteSpace(fingerprint))
            return DesktopGridDisplayClearance.Unknown;

        ExplorerGridMeasurement? measurement;
        ExplorerGridDisplayAuthorization? authorization;
        try
        {
            // 与 DesktopExplorerGridFactReader 同一调用顺序：先 Initialize，再读取。
            await _measurementStore.InitializeAsync(cancellationToken);
            await _authorizationStore.InitializeAsync(cancellationToken);
            measurement = await _measurementStore.GetActiveAsync(deviceName, fingerprint, cancellationToken);
            authorization = await _authorizationStore.GetActiveAsync(deviceName, fingerprint, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DesktopGridDisplayClearance.Unknown;
        }

        // 显式复核拓扑：store 的查询条件已经是它，但"存得进读不出"这类错位必须在这里也拦一次。
        var evidence = measurement is not null && measurement.MatchesTopology(fingerprint)
            ? DesktopGeometryEvidenceMapping.From(measurement.Source)
            : DesktopGeometryEvidence.Unknown;

        // AuthorizesUnattendedExecution 自己会拒绝已撤销与指纹不符的记录；
        // 授权为 null（这台屏在当前拓扑下从未被同意过）同样不构成授权。
        var isAuthorized = authorization is not null
            && authorization.AuthorizesUnattendedExecution(fingerprint);

        return new DesktopGridDisplayClearance(evidence, isAuthorized);
    }
}
