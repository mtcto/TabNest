using TabNest.Core.Models;

namespace TabNest.Core.Rules;

/// <summary>规则求值的结论。</summary>
public sealed record RuleVerdict(RuleAction? Action, string? MatchedRuleId, string? MatchedRuleName)
{
    public static RuleVerdict NoMatch { get; } = new(null, null, null);
}

/// <summary>
/// 用户规则求值。
///
/// **固定优先级：阻止 &gt; 允许 &gt; 自动分组。**
///
/// 这个顺序不可配置，因为阻止表达的是"我不要这个应用被碰"，属于安全边界；
/// 若允许规则能盖过阻止规则，用户就无法可靠地把某个应用排除在外
/// （例如密码管理器、银行客户端）。宁可牺牲灵活性，也要保证阻止一定生效。
///
/// 本类是纯函数，无状态，可安全并发调用。
/// </summary>
public static class RuleEngine
{
    public static RuleVerdict Evaluate(WindowInfo window, IReadOnlyList<Rule> rules)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(rules);

        return Evaluate(window.ProcessName, window.ClassName, window.Title, rules);
    }

    public static RuleVerdict Evaluate(
        string processName,
        string className,
        string title,
        IReadOnlyList<Rule> rules)
    {
        // 按固定优先级依次尝试，先命中先返回。
        foreach (var action in (ReadOnlySpan<RuleAction>)[RuleAction.Block, RuleAction.Allow, RuleAction.AutoGroup])
        {
            var match = FindBest(processName, className, title, rules, action);
            if (match is not null)
            {
                return new RuleVerdict(action, match.Id, match.Name);
            }
        }

        return RuleVerdict.NoMatch;
    }

    /// <summary>
    /// 找出指定类别中优先级最高的命中规则。
    /// 同优先级时取先出现的，保证结果稳定可复现 —— 规则求值结果不稳定会让"为什么被分组"无法解释。
    /// </summary>
    private static Rule? FindBest(
        string processName,
        string className,
        string title,
        IReadOnlyList<Rule> rules,
        RuleAction action)
    {
        Rule? best = null;

        foreach (var rule in rules)
        {
            if (rule.Action != action || !rule.Matches(processName, className, title))
            {
                continue;
            }

            if (best is null || rule.Priority > best.Priority)
            {
                best = rule;
            }
        }

        return best;
    }

    /// <summary>
    /// 找出所有匹配该窗口的规则，无论类别与优先级。
    /// 供设置页的"规则测试"功能使用：用户需要看到全部命中项才能理解为什么某条规则没生效。
    /// </summary>
    public static IReadOnlyList<Rule> FindAllMatches(WindowInfo window, IReadOnlyList<Rule> rules)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(rules);

        var matches = new List<Rule>();
        foreach (var rule in rules)
        {
            if (rule.Matches(window.ProcessName, window.ClassName, window.Title))
            {
                matches.Add(rule);
            }
        }

        return matches;
    }
}
