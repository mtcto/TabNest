using TabNest.Core.Models;

namespace TabNest.Core.Grouping;

/// <summary>批量收编的范围。</summary>
public enum AdoptionScope
{
    /// <summary>同一应用（进程名相同）的全部未分组窗口。</summary>
    SameApplication = 0,

    /// <summary>同一应用、且与基准窗口在同一显示器上的未分组窗口。</summary>
    SameApplicationOnMonitor = 1,

    /// <summary>同一窗口类的全部未分组窗口。比进程名更精确，能区分同一应用的不同窗口种类。</summary>
    SameWindowClass = 2,

    /// <summary>与基准窗口同一显示器上的全部未分组窗口，不限应用。</summary>
    SameMonitor = 3,
}

/// <summary>批量收编的一次选窗结果。</summary>
/// <param name="Scope">使用的范围。</param>
/// <param name="Windows">选中的窗口，已按 Z 序排列，基准窗口排在最前。</param>
/// <param name="Skipped">被排除的窗口及原因，用于向用户解释"为什么少了几个"。</param>
public readonly record struct AdoptionPlan(
    AdoptionScope Scope,
    IReadOnlyList<WindowInfo> Windows,
    IReadOnlyList<(WindowInfo Window, string Reason)> Skipped);

/// <summary>
/// 批量收编的选窗逻辑。
///
/// 计划书 §4.6 把它列为 P0：把某个应用的十几个窗口逐个拖进一个组，
/// 比一次点击慢一个数量级，而这正是重度用户最常见的诉求。
/// Groupy 的上下文菜单里有这一组命令，计划书原稿完全遗漏了。
///
/// 做成纯函数是有意的：选窗规则的正确性几乎全在边界条件上
/// （已在别的组里的要不要算？基准窗口自己算不算？同应用但在别的显示器上呢？），
/// 这些用单元测试逐条钉死，比开着十几个真实窗口手点可靠得多。
/// </summary>
public static class BatchAdoption
{
    /// <summary>
    /// 挑出应当被收编的窗口。
    /// </summary>
    /// <param name="anchor">基准窗口，即用户右键点击的那个。它始终排在结果第一位。</param>
    /// <param name="candidates">全部候选窗口，通常是枚举器过滤后的可见顶层窗口。</param>
    /// <param name="scope">收编范围。</param>
    /// <param name="isGrouped">判断某个窗口是否已经在别的组里。</param>
    /// <param name="isEligible">资格判定，返回 null 表示可分组，否则返回不可分组的原因。</param>
    public static AdoptionPlan Plan(
        WindowInfo anchor,
        IReadOnlyList<WindowInfo> candidates,
        AdoptionScope scope,
        Func<WindowIdentity, bool> isGrouped,
        Func<WindowInfo, string?> isEligible)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(isGrouped);
        ArgumentNullException.ThrowIfNull(isEligible);

        // 基准窗口无条件入选：用户是在它身上发起的操作，
        // 把它自己排除掉会得到一个不含它的组，与意图完全相反。
        var selected = new List<WindowInfo> { anchor };
        var skipped = new List<(WindowInfo, string)>();

        foreach (var window in candidates)
        {
            if (window.Identity == anchor.Identity)
            {
                continue;
            }

            if (!MatchesScope(anchor, window, scope))
            {
                continue;
            }

            // 已在别的组里的窗口静默跳过，不进 Skipped：
            // 用户选的是"收编未分组窗口"，把已分组的列成"被跳过"纯属噪声。
            if (isGrouped(window.Identity))
            {
                continue;
            }

            if (isEligible(window) is { } reason)
            {
                skipped.Add((window, reason));
                continue;
            }

            selected.Add(window);
        }

        return new AdoptionPlan(scope, selected, skipped);
    }

    /// <summary>某个窗口是否落在给定范围内。</summary>
    private static bool MatchesScope(WindowInfo anchor, WindowInfo window, AdoptionScope scope) =>
        scope switch
        {
            AdoptionScope.SameApplication =>
                SameProcess(anchor, window),

            AdoptionScope.SameApplicationOnMonitor =>
                SameProcess(anchor, window) && SameMonitor(anchor, window),

            AdoptionScope.SameWindowClass =>
                string.Equals(anchor.ClassName, window.ClassName, StringComparison.Ordinal),

            AdoptionScope.SameMonitor =>
                SameMonitor(anchor, window),

            _ => false,
        };

    /// <summary>
    /// 是否同一应用。
    ///
    /// 按进程**名**而非进程 ID 比较：同一个应用的多个窗口经常分属不同进程
    /// （Chrome、Edge、以及任何多进程架构的编辑器），用 PID 会把它们判成不同应用，
    /// 而"收编该应用的全部窗口"恰恰最需要它们被算作一家。
    /// </summary>
    private static bool SameProcess(WindowInfo a, WindowInfo b) =>
        !string.IsNullOrEmpty(a.ProcessName)
        && string.Equals(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase);

    private static bool SameMonitor(WindowInfo a, WindowInfo b) =>
        !string.IsNullOrEmpty(a.MonitorDeviceName)
        && string.Equals(a.MonitorDeviceName, b.MonitorDeviceName, StringComparison.Ordinal);

    /// <summary>菜单项文案。带上实际会被收编的数量，用户点之前就知道会发生什么。</summary>
    public static string DescribeScope(AdoptionScope scope, WindowInfo anchor, int count) =>
        scope switch
        {
            AdoptionScope.SameApplication =>
                $"收编 {anchor.ProcessName} 的全部窗口（{count} 个）",

            AdoptionScope.SameApplicationOnMonitor =>
                $"收编 {anchor.ProcessName} 在本显示器的窗口（{count} 个）",

            AdoptionScope.SameWindowClass =>
                $"收编同类窗口（{count} 个）",

            _ => $"收编本显示器的全部窗口（{count} 个）",
        };
}
