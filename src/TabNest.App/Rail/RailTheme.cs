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

    /// <summary>
    /// 非活动标签的底色。
    ///
    /// 必须与背景条有可见差异：早期非活动标签完全不填充，结果整条分组栏上
    /// 只有活动标签有形状，「标签样式：传统 / 圆形」这个设置几乎看不出区别，
    /// 标签之间的边界也全靠猜。
    /// </summary>
    public uint InactiveTabBackground { get; init; }
    public uint ActiveText { get; init; }
    public uint InactiveText { get; init; }
    public uint Border { get; init; }

    /// <summary>降级态（无响应窗口）的文字颜色。</summary>
    public uint DegradedText { get; init; }

    /// <summary>拖放目标高亮。</summary>
    public uint DropHighlight { get; init; }

    /// <summary>
    /// 关闭「背景条」时分组栏的底色。
    ///
    /// 取与窗口标题栏接近的颜色，让标签看起来像直接浮在窗口上方而不是坐在一条灰带上。
    /// 不能直接用透明：分组栏是独立窗口，透明会露出它下面的桌面而不是承载窗口。
    /// </summary>
    public uint WindowBlend { get; init; }

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
        InactiveTabBackground = 0xFFDCDCDC,
        ActiveText = 0xFF1A1A1A,
        InactiveText = 0xFF5A5A5A,
        Border = 0xFFCCCCCC,
        DegradedText = 0xFFB06000,
        DropHighlight = 0xFF0078D4,
        WindowBlend = 0xFFF5F5F5,
    };

    /// <summary>按用户选择的方案取主题。跟随系统时读注册表的浅色/深色偏好。</summary>
    public static RailTheme For(Core.Models.RailColorScheme scheme) => scheme switch
    {
        Core.Models.RailColorScheme.Light => Default,
        Core.Models.RailColorScheme.Dark => Dark,
        _ => Interop.SystemTheme.IsDarkMode() ? Dark : Default,
    };

    /// <summary>深色方案。整体深色桌面下比浅灰底更协调。</summary>
    public static RailTheme Dark { get; } = new()
    {
        Background = 0xFF2B2B2B,
        ActiveTabBackground = 0xFF3C3C3C,
        HoverTabBackground = 0xFF353535,
        InactiveTabBackground = 0xFF262626,
        ActiveText = 0xFFF0F0F0,
        InactiveText = 0xFFA8A8A8,
        Border = 0xFF454545,
        DegradedText = 0xFFE0A050,
        DropHighlight = 0xFF4CC2FF,
        WindowBlend = 0xFF202020,
    };

    public static RailTheme Light { get; } = new()
    {
        Background = 0xFFF3F3F3,
        ActiveTabBackground = 0xFFFFFFFF,
        HoverTabBackground = 0xFFEAEAEA,
        InactiveTabBackground = 0xFFE6E6E6,
        ActiveText = 0xFF1A1A1A,
        InactiveText = 0xFF5A5A5A,
        Border = 0xFFD8D8D8,
        DegradedText = 0xFFB07000,
        DropHighlight = 0xFF0078D4,
        WindowBlend = 0xFFFAFAFA,
    };
}
