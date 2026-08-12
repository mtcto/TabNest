using TabNest.Core.Models;

namespace TabNest.Core.Persistence;

/// <summary>已保存分组里的一个成员。</summary>
public sealed record SavedMember
{
    public required string ProcessName { get; init; }

    /// <summary>
    /// 可执行文件完整路径。
    ///
    /// 光有进程名是不够的：重新启动这个应用时需要知道从哪里启动它，
    /// 而同名可执行文件在系统里可能有多份（各种便携版、多版本共存）。
    /// </summary>
    public required string ProcessPath { get; init; }

    public required string ClassName { get; init; }

    /// <summary>保存时的窗口标题。仅用于展示与匹配提示，不作为唯一标识。</summary>
    public required string Title { get; init; }
}

/// <summary>一个已保存的分组。</summary>
public sealed record SavedWorkspace
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<SavedMember> Members { get; init; }
    public required DateTimeOffset SavedAt { get; init; }

    /// <summary>保存时组所在的矩形。恢复时用它摆放，用户回到熟悉的位置。</summary>
    public PixelRect Bounds { get; init; }
}

/// <summary>全部已保存分组。</summary>
public sealed record SavedWorkspaceSet
{
    public int SchemaVersion { get; init; } = 1;

    public IReadOnlyList<SavedWorkspace> Workspaces { get; init; } = [];

    public static SavedWorkspaceSet Empty { get; } = new();
}

/// <summary>
/// 已保存分组的操作。
///
/// 对齐 Groupy 分组规则页的「已保存分组」：把一组窗口的构成记下来，
/// 以后可以一键把这些应用重新拉起并重新分组。
///
/// **刻意只记录应用身份而不记录窗口句柄**：句柄在应用重启后必然失效，
/// 存了也只是误导。恢复时按进程名与窗口类去匹配当前打开的窗口，
/// 匹配不到的成员如实告诉用户"这个应用没有开着"，而不是假装恢复成功。
/// </summary>
public static class SavedWorkspaces
{
    /// <summary>从一个活动分组生成可保存的记录。</summary>
    public static SavedWorkspace Capture(
        GroupSession group, string name, string id, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(group);

        var members = new List<SavedMember>(group.Tabs.Count);

        foreach (var tab in group.LiveTabs)
        {
            members.Add(new SavedMember
            {
                ProcessName = tab.ProcessName,
                ProcessPath = tab.ProcessPath ?? string.Empty,
                ClassName = tab.ClassName,
                Title = tab.Title,
            });
        }

        return new SavedWorkspace
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? DefaultName(members) : name,
            Members = members,
            SavedAt = now,
            Bounds = group.Bounds,
        };
    }

    /// <summary>
    /// 在当前打开的窗口里，为已保存分组的每个成员找出对应窗口。
    /// </summary>
    /// <returns>匹配到的窗口，以及没能匹配上的成员。</returns>
    public static (IReadOnlyList<WindowInfo> Matched, IReadOnlyList<SavedMember> Missing) Resolve(
        SavedWorkspace workspace,
        IReadOnlyList<WindowInfo> candidates,
        Func<WindowIdentity, bool> isGrouped)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(isGrouped);

        var matched = new List<WindowInfo>(workspace.Members.Count);
        var missing = new List<SavedMember>();

        // 已被用掉的窗口不能再匹配给下一个成员，否则同一应用的两个成员
        // 会都指向同一个窗口，恢复出一个只有一个窗口的"两成员"组。
        var used = new HashSet<WindowIdentity>();

        foreach (var member in workspace.Members)
        {
            var found = FindBest(member, candidates, used, isGrouped);

            if (found is null)
            {
                missing.Add(member);
                continue;
            }

            used.Add(found.Identity);
            matched.Add(found);
        }

        return (matched, missing);
    }

    /// <summary>
    /// 为一个成员挑最合适的窗口。
    ///
    /// 优先级：进程名 + 窗口类 + 标题全中 &gt; 进程名 + 窗口类 &gt; 仅进程名。
    /// 标题参与匹配但不是必要条件 —— 它会随打开的文档变化，
    /// 要求标题相同会让绝大多数恢复失败。
    /// </summary>
    private static WindowInfo? FindBest(
        SavedMember member,
        IReadOnlyList<WindowInfo> candidates,
        HashSet<WindowIdentity> used,
        Func<WindowIdentity, bool> isGrouped)
    {
        WindowInfo? byProcess = null;
        WindowInfo? byClass = null;

        foreach (var window in candidates)
        {
            if (used.Contains(window.Identity) || isGrouped(window.Identity))
            {
                continue;
            }

            if (!string.Equals(window.ProcessName, member.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byProcess ??= window;

            if (!string.Equals(window.ClassName, member.ClassName, StringComparison.Ordinal))
            {
                continue;
            }

            byClass ??= window;

            if (string.Equals(window.Title, member.Title, StringComparison.Ordinal))
            {
                return window;
            }
        }

        return byClass ?? byProcess;
    }

    private static string DefaultName(List<SavedMember> members)
    {
        if (members.Count == 0)
        {
            return "空分组";
        }

        var names = members
            .Select(m => m.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        var text = string.Join(" + ", names);

        return members.Count > names.Count ? $"{text} 等 {members.Count} 个窗口" : text;
    }
}
