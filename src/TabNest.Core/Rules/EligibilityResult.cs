namespace TabNest.Core.Rules;

/// <summary>
/// 分组资格判定结果。
///
/// 这是 TabNest 相对 Groupy 的核心差异化功能。Groupy 在窗口不可分组时基本沉默，
/// 只有"无法分组未响应的窗口"一条提示；用户遇到其他情况只能猜。
///
/// 因此这里的约定是：**任何拒绝都必须同时给出原因和可操作建议**。
/// 只有 Reason 没有 Suggestion 的拒绝视为功能缺陷。
/// </summary>
public sealed record EligibilityResult
{
    public required EligibilitySeverity Severity { get; init; }

    public required IneligibleReason Reason { get; init; }

    /// <summary>为什么会得到这个结论。面向用户，不含技术黑话。</summary>
    public required string Explanation { get; init; }

    /// <summary>用户可以做什么。无法给出可操作建议时置 null（例如系统级限制）。</summary>
    public string? Suggestion { get; init; }

    /// <summary>命中的用户规则 ID。仅当结论由用户规则导致时有值 —— 让用户能直接跳到那条规则。</summary>
    public string? MatchedRuleId { get; init; }

    public bool IsEligible => Severity is not EligibilitySeverity.Blocked;

    public static EligibilityResult Allowed { get; } = new()
    {
        Severity = EligibilitySeverity.Allowed,
        Reason = IneligibleReason.None,
        Explanation = "可以加入标签组。",
    };

    public static EligibilityResult Warn(
        IneligibleReason reason,
        string explanation,
        string? suggestion = null) => new()
        {
            Severity = EligibilitySeverity.AllowedWithWarning,
            Reason = reason,
            Explanation = explanation,
            Suggestion = suggestion,
        };

    public static EligibilityResult Block(
        IneligibleReason reason,
        string explanation,
        string? suggestion = null,
        string? matchedRuleId = null) => new()
        {
            Severity = EligibilitySeverity.Blocked,
            Reason = reason,
            Explanation = explanation,
            Suggestion = suggestion,
            MatchedRuleId = matchedRuleId,
        };

    public override string ToString() => Severity switch
    {
        EligibilitySeverity.Allowed => "可分组",
        EligibilitySeverity.AllowedWithWarning => $"可分组（注意：{Explanation}）",
        _ => $"不可分组：{Explanation}",
    };
}
