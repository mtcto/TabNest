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

    public static RailTheme Dark { get; } = new()
    {
        Background = 0xFF202020,
        ActiveTabBackground = 0xFF2D2D2D,
        HoverTabBackground = 0xFF272727,
        ActiveText = 0xFFFFFFFF,
        InactiveText = 0xFFAAAAAA,
        Border = 0xFF3A3A3A,
        DegradedText = 0xFFE0A030,
        DropHighlight = 0xFF4CC2FF,
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
