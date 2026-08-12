namespace TabNest.App.Settings;

/// <summary>
/// 设置中心配色。
///
/// 明暗两套，跟随系统。颜色集中在这里而不是散落在绘制代码里 ——
/// 自绘 UI 一旦让每处各自挑颜色，做暗色主题时就得逐行去找。
/// </summary>
public sealed record SettingsTheme
{
    // 背景层次：窗口 → 导航栏 → 卡片。三层要能区分但不能对比过强。
    public required uint WindowBackground { get; init; }
    public required uint NavBackground { get; init; }
    public required uint CardBackground { get; init; }
    public required uint CardHoverBackground { get; init; }

    public required uint NavItemHover { get; init; }
    public required uint NavItemSelected { get; init; }
    public required uint NavIndicator { get; init; }

    public required uint Border { get; init; }
    public required uint Separator { get; init; }

    public required uint Text { get; init; }

    /// <summary>说明文字。承载"为什么"，必须能读但不与标题争夺注意力。</summary>
    public required uint TextSecondary { get; init; }

    /// <summary>禁用态文字。</summary>
    public required uint TextDisabled { get; init; }

    /// <summary>强调色，用于选中卡片边框、开关开启态。</summary>
    public required uint Accent { get; init; }

    public required uint AccentText { get; init; }

    /// <summary>开关关闭态的轨道颜色。</summary>
    public required uint ToggleOff { get; init; }

    public required uint ToggleKnob { get; init; }

    /// <summary>警示文字，用于"该功能尚未提供"这类说明。</summary>
    public required uint Warning { get; init; }

    public required uint ScrollThumb { get; init; }

    /// <summary>图示里代表窗口主体的填充色。</summary>
    public required uint IllustrationFill { get; init; }

    /// <summary>图示里代表标签/高亮部分的颜色。</summary>
    public required uint IllustrationAccent { get; init; }

    public static SettingsTheme Light { get; } = new()
    {
        WindowBackground = 0xFFF3F3F3,
        NavBackground = 0xFFEBEBEB,
        CardBackground = 0xFFFFFFFF,
        CardHoverBackground = 0xFFF8F8F8,
        NavItemHover = 0xFFE0E0E0,
        NavItemSelected = 0xFFD8D8D8,
        NavIndicator = 0xFF0067C0,
        Border = 0xFFD0D0D0,
        Separator = 0xFFDCDCDC,
        Text = 0xFF1A1A1A,
        TextSecondary = 0xFF606060,
        TextDisabled = 0xFFA0A0A0,
        Accent = 0xFF0067C0,
        AccentText = 0xFFFFFFFF,
        ToggleOff = 0xFFB0B0B0,
        ToggleKnob = 0xFFFFFFFF,
        Warning = 0xFF8A5A00,
        ScrollThumb = 0xFFB8B8B8,
        IllustrationFill = 0xFFE8E8E8,
        IllustrationAccent = 0xFF0067C0,
    };

    public static SettingsTheme Dark { get; } = new()
    {
        WindowBackground = 0xFF202020,
        NavBackground = 0xFF2A2A2A,
        CardBackground = 0xFF2F2F2F,
        CardHoverBackground = 0xFF383838,
        NavItemHover = 0xFF383838,
        NavItemSelected = 0xFF404040,
        NavIndicator = 0xFF4CC2FF,
        Border = 0xFF454545,
        Separator = 0xFF3A3A3A,
        Text = 0xFFF0F0F0,
        TextSecondary = 0xFFAAAAAA,
        TextDisabled = 0xFF6A6A6A,
        Accent = 0xFF4CC2FF,
        AccentText = 0xFF1A1A1A,
        ToggleOff = 0xFF6A6A6A,
        ToggleKnob = 0xFFFFFFFF,
        Warning = 0xFFE0B060,
        ScrollThumb = 0xFF5A5A5A,
        IllustrationFill = 0xFF3A3A3A,
        IllustrationAccent = 0xFF4CC2FF,
    };
}
