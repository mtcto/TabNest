namespace TabNest.Core.Rules;

public enum RuleAction
{
    /// <summary>禁止该窗口参与分组。</summary>
    Block = 0,

    /// <summary>允许该窗口参与分组。启用"仅允许指定应用"模式时，只有命中允许规则的窗口才可分组。</summary>
    Allow = 1,

    /// <summary>自动把匹配的窗口合并到同一组。</summary>
    AutoGroup = 2,
}

/// <summary>
/// 单条匹配条件。
///
/// 三个字段之间是 **AND**：都填了就必须同时满足。留空表示不约束该维度。
/// 这与 Groupy 的"可按进程、窗口类、标题文本的组合匹配"一致。
/// </summary>
public sealed record RuleCondition
{
    /// <summary>进程可执行文件名，例如 idea64.exe。比较时忽略大小写。</summary>
    public string? ProcessName { get; init; }

    /// <summary>窗口类名。</summary>
    public string? ClassName { get; init; }

    /// <summary>窗口标题。</summary>
    public string? Title { get; init; }

    /// <summary>
    /// 是否使用部分匹配（包含）而非完全相等。对应 Groupy 的"使用部分字符串匹配"。
    /// 标题几乎总是需要部分匹配 —— 完整标题会随打开的文档变化。
    /// </summary>
    public bool PartialMatch { get; init; }

    /// <summary>
    /// 三个维度全空的条件匹配任意窗口。
    /// Groupy 明确支持这种"空规则匹配全部"（"Allow blank rules for grouping everything"），
    /// 但它极其危险 —— 一条空的阻止规则会禁用整个产品，因此调用方必须显式确认。
    /// </summary>
    public bool IsCatchAll =>
        string.IsNullOrWhiteSpace(ProcessName)
        && string.IsNullOrWhiteSpace(ClassName)
        && string.IsNullOrWhiteSpace(Title);

    public bool Matches(string processName, string className, string title)
    {
        return Test(ProcessName, processName)
            && Test(ClassName, className)
            && Test(Title, title);

        bool Test(string? pattern, string actual)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return true;
            }

            return PartialMatch
                ? actual.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                : string.Equals(actual, pattern, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>
/// 一条用户规则。
///
/// 一条规则可含多个子条件（对应 Groupy 的 subrule），子条件之间是 **OR**：
/// 命中任意一条即视为命中该规则。
/// </summary>
public sealed record Rule
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required RuleAction Action { get; init; }

    public bool Enabled { get; init; } = true;

    public IReadOnlyList<RuleCondition> Conditions { get; init; } = [];

    /// <summary>数值越大越优先。同类规则之间用它决胜；不同类之间由 RuleEngine 的固定优先级决定。</summary>
    public int Priority { get; init; }

    /// <summary>没有任何条件的规则永不匹配 —— 这几乎总是用户编辑到一半的产物，不能让它意外生效。</summary>
    public bool IsUsable => Conditions.Count > 0;

    public bool Matches(string processName, string className, string title)
    {
        if (!Enabled || !IsUsable)
        {
            return false;
        }

        foreach (var condition in Conditions)
        {
            if (condition.Matches(processName, className, title))
            {
                return true;
            }
        }

        return false;
    }
}
