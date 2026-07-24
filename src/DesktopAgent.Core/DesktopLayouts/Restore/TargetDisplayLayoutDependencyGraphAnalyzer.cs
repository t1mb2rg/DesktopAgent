using System.Collections.ObjectModel;

namespace DesktopAgent.Core.DesktopLayouts.Restore;

/// <summary>
/// 共享的依赖图分析器，M1-05A 和 M1-05B 共同使用。
/// 纯数据，不含 PIDL/COM/HWND/WPF。
/// </summary>
internal static class TargetDisplayLayoutDependencyGraphAnalyzer
{
    /// <summary>
    /// SCC 分析与环检测结果。
    /// </summary>
    public sealed class Result
    {
        /// <summary>是否存在至少一个非平凡 SCC（含自环）。</summary>
        public bool HasCycle { get; }
        /// <summary>规范化环列表：每个 SCC 内 Ordinal 排序，环列表按第一个 StableId 排序。</summary>
        public IReadOnlyList<TargetDisplayLayoutRestoreCycle> Cycles { get; }
        /// <summary>确定性拓扑排序（仅当 HasCycle == false 时有意义）。</summary>
        public IReadOnlyList<string>? TopologicalOrder { get; }

        public Result(
            bool hasCycle,
            IReadOnlyList<TargetDisplayLayoutRestoreCycle> cycles,
            IReadOnlyList<string>? topologicalOrder)
        {
            HasCycle = hasCycle;
            Cycles = cycles;
            TopologicalOrder = topologicalOrder;
        }
    }

    /// <summary>
    /// 分析依赖图中的 SCC 环，返回规范化环列表和确定性拓扑排序。
    /// </summary>
    /// <param name="moveStableIds">MoveRequired 项的 StableId 列表（确定性顺序）。</param>
    /// <param name="dependencies">依赖列表，DependsOn 必须先于 ActionItem 移动。</param>
    /// <returns>分析结果，含 HasCycle、Cycles、TopologicalOrder。</returns>
    public static Result Analyze(
        IReadOnlyList<string> moveStableIds,
        IReadOnlyList<TargetDisplayLayoutRestoreDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(moveStableIds);
        ArgumentNullException.ThrowIfNull(dependencies);

        var moveSidSet = new HashSet<string>(moveStableIds, StringComparer.Ordinal);
        if (moveSidSet.Count != moveStableIds.Count)
            throw new ArgumentException("moveStableIds 包含重复 StableId。", nameof(moveStableIds));

        // 验证 Dependency 两端均为 MoveRequired，使用结构化键检测重复
        var seenDep = new HashSet<(string DependsOn, string ActionItem)>();
        foreach (var dep in dependencies)
        {
            if (!moveSidSet.Contains(dep.DependsOn))
                throw new ArgumentException(
                    $"Dependency DependsOn \"{dep.DependsOn}\" 不在 MoveRequired 集合中。", nameof(dependencies));
            if (!moveSidSet.Contains(dep.ActionItem))
                throw new ArgumentException(
                    $"Dependency ActionItem \"{dep.ActionItem}\" 不在 MoveRequired 集合中。", nameof(dependencies));
            if (!seenDep.Add((dep.DependsOn, dep.ActionItem)))
                throw new ArgumentException(
                    $"重复 Dependency: \"{dep.DependsOn}\" -> \"{dep.ActionItem}\"。", nameof(dependencies));
        }

        if (moveStableIds.Count == 0 || dependencies.Count == 0)
        {
            var topo = moveStableIds.Count > 0
                ? new List<string>(moveStableIds)
                : (IReadOnlyList<string>?)null;
            return new Result(false, Array.Empty<TargetDisplayLayoutRestoreCycle>(), topo);
        }

        // 建立索引映射
        var sidToIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < moveStableIds.Count; i++)
            sidToIdx[moveStableIds[i]] = i;

        var adj = new List<int>[moveStableIds.Count];
        for (var i = 0; i < moveStableIds.Count; i++)
            adj[i] = new List<int>();

        foreach (var dep in dependencies)
        {
            var from = sidToIdx[dep.DependsOn];
            var to = sidToIdx[dep.ActionItem];
            adj[from].Add(to);
        }

        // Tarjan SCC
        var index = 0;
        var stack = new Stack<int>();
        var indices = new int[moveStableIds.Count];
        var lowLink = new int[moveStableIds.Count];
        var onStack = new bool[moveStableIds.Count];
        var allCycles = new List<List<int>>();

        for (var i = 0; i < moveStableIds.Count; i++)
            indices[i] = -1;

        for (var v = 0; v < moveStableIds.Count; v++)
            if (indices[v] == -1)
                StrongConnect(v, ref index, stack, indices, lowLink, onStack, adj, allCycles);

        var cycles = new List<TargetDisplayLayoutRestoreCycle>();
        foreach (var comp in allCycles)
        {
            if (comp.Count > 1 || (comp.Count == 1 && adj[comp[0]].Contains(comp[0])))
            {
                var sids = comp.Select(i => moveStableIds[i])
                    .OrderBy(s => s, StringComparer.Ordinal).ToList();
                cycles.Add(new TargetDisplayLayoutRestoreCycle(sids));
            }
        }

        // 确定性顺序：按第一个 StableId
        cycles = cycles.OrderBy(c => c.StableIds[0], StringComparer.Ordinal).ToList();
        var hasCycle = cycles.Count > 0;

        // 确定性拓扑排序（仅当无环时）
        IReadOnlyList<string>? topoOrder = null;
        if (!hasCycle)
        {
            topoOrder = DeterministicTopologicalSort(moveStableIds, dependencies, sidToIdx, adj);
        }

        return new Result(hasCycle, cycles, topoOrder);
    }

    private static void StrongConnect(
        int v, ref int index, Stack<int> stack,
        int[] indices, int[] lowLink, bool[] onStack,
        List<int>[] adj, List<List<int>> allCycles)
    {
        indices[v] = index;
        lowLink[v] = index;
        index++;
        stack.Push(v);
        onStack[v] = true;

        foreach (var w in adj[v].OrderBy(w => w))
        {
            if (indices[w] == -1)
            {
                StrongConnect(w, ref index, stack, indices, lowLink, onStack, adj, allCycles);
                lowLink[v] = Math.Min(lowLink[v], lowLink[w]);
            }
            else if (onStack[w])
            {
                lowLink[v] = Math.Min(lowLink[v], indices[w]);
            }
        }

        if (lowLink[v] == indices[v])
        {
            var comp = new List<int>();
            int w;
            do
            {
                w = stack.Pop();
                onStack[w] = false;
                comp.Add(w);
            } while (w != v);
            allCycles.Add(comp);
        }
    }

    private static IReadOnlyList<string> DeterministicTopologicalSort(
        IReadOnlyList<string> moveStableIds,
        IReadOnlyList<TargetDisplayLayoutRestoreDependency> dependencies,
        Dictionary<string, int> sidToIdx,
        List<int>[] adj)
    {
        var inDegree = new int[moveStableIds.Count];
        foreach (var dep in dependencies)
        {
            if (sidToIdx.TryGetValue(dep.DependsOn, out var from)
                && sidToIdx.TryGetValue(dep.ActionItem, out var to))
            {
                inDegree[to]++;
            }
        }

        var cmp = Comparer<int>.Create((a, b) =>
        {
            var c = a.CompareTo(b); // 索引顺序即传入的确定性顺序
            if (c != 0) return c;
            return string.CompareOrdinal(moveStableIds[a], moveStableIds[b]);
        });

        var available = new SortedSet<int>(cmp);
        for (var i = 0; i < moveStableIds.Count; i++)
            if (inDegree[i] == 0)
                available.Add(i);

        var sorted = new List<string>();
        while (available.Count > 0)
        {
            var v = available.Min;
            available.Remove(v);
            sorted.Add(moveStableIds[v]);

            foreach (var w in adj[v].OrderBy(w => w, cmp))
            {
                inDegree[w]--;
                if (inDegree[w] == 0)
                    available.Add(w);
            }
        }

        return sorted;
    }
}
