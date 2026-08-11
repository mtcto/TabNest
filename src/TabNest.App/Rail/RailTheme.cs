namespace TabNest.App.Rail;

/// <summary>
/// 轨道配色。ARGB。
///
/// 暗色优先验证（计划书 §8.4），因为开发者场景以暗色为主；
/// 亮色同步适配但不作为默认调试目标。
/// </summary>
public sealed record RailTheme
{
    public uint Background { get; init; }
    public uint ActiveTabBackground { get; init; }
    public uint HoverTabBackground { get; init; }
    public uint ActiveText { get; init; }
    public uint InactiveText { get; init; }
    public uint Border { get; init; }

    /// <summary>降级态（无响应窗口）的文字颜色。</summary>
    public uint DegradedText { get; init; }

    /// <summary>拖放目标高亮。</summary>
    public uint DropHighlight { get; init; }

    /// <summary>
    /// 默认主题：灰白底、深色字。
    ///
    /// 开发者场景里窗口以深色为主（IDE、终端），浅灰背景与它们对比强烈，
    /// 分组栏的边界一眼可辨；早期的近黑背景会和深色标题栏糊成一片，
    /// 用户根本看不出分组栏在哪、有没有生效。
    ///
    /// 活动标签取纯白、比背景更亮，是浏览器标签栏的通行做法 ——
    /// 当前标签"浮起"，其余"沉下"。
    /// </summary>
    public static RailTheme Default { get; } = new()
    {
        Background = 0xFFE8E8E8,
        ActiveTabBackground = 0xFFFFFFFF,
        HoverTabBackground = 0xFFF2F2F2,
        ActiveText = 0xFF1A1A1A,
        InactiveText = 0xFF5A5A5A,
        Border = 0xFFCCCCCC,
        DegradedText = 0xFFB06000,
        DropHighlight = 0xFF0078D4,
    };

    public static RailTheme Light { get; } = new()
    {
        Background = 0xFFF3F3F3,
        ActiveTabBackground = 0xFFFFFFFF,
        HoverTabBackground = 0xFFEAEAEA,
        ActiveText = 0xFF1A1A1A,
        InactiveText = 0xFF5A5A5A,
        Border = 0xFFD8D8D8,
        DegradedText = 0xFFB07000,
        DropHighlight = 0xFF0078D4,
    };
}
