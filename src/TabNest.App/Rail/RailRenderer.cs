using TabNest.Core.Layout;
using TabNest.Core.Models;
using TabNest.Interop;

namespace TabNest.App.Rail;

/// <summary>轨道一次绘制所需的全部状态。不可变，由编排层生成后交给 UI 线程消费。</summary>
public sealed record RailRenderState
{
    public required RailLayout Layout { get; init; }
    public required IReadOnlyList<TabItem> Tabs { get; init; }
    public required WindowIdentity ActiveIdentity { get; init; }
    public required RailTheme Theme { get; init; }
    public required int Dpi { get; init; }

    public WindowIdentity? HoveredIdentity { get; init; }
    public WindowIdentity? HoveredCloseIdentity { get; init; }

    /// <summary>
    /// 处于最小化状态的成员。
    ///
    /// 它们的标签必须画得出区别（灰字 + 后缀），否则用户最小化一个成员后
    /// 看分组栏毫无变化，会认定"根本没最小化"—— 这是用户的原话。
    /// </summary>
    public IReadOnlySet<WindowIdentity> MinimizedIdentities { get; init; } =
        new HashSet<WindowIdentity>();

    /// <summary>光标是否停在「关闭整组」按钮上。</summary>
    public bool IsCloseGroupHovered { get; init; }

    /// <summary>光标是否停在「整组最小化」按钮上。</summary>
    public bool IsMinimizeHovered { get; init; }

    /// <summary>拖拽重排时的插入指示线位置，null 表示不显示。</summary>
    public int? DropIndicatorIndex { get; init; }

    /// <summary>标签是否使用圆角。对应设置里的「标签样式：传统 / 圆形」。</summary>
    public bool RoundedTabs { get; init; } = true;

    /// <summary>
    /// 是否绘制标签后方的背景条。
    /// 关掉之后分组栏背景与窗口同色，标签像是直接浮在窗口上方。
    /// </summary>
    public bool ShowBackgroundBar { get; init; } = true;

    /// <summary>拖动标签重排是否需要按住修饰键。防止在标签栏上误拖导致顺序变乱。</summary>
    public bool RequireModifierToMoveTabs { get; init; }

    /// <summary>关闭按钮显示策略。渲染与命中都按它判断，布局侧不再预先裁决。</summary>
    public CloseButtonPolicy CloseButton { get; init; } = CloseButtonPolicy.ActiveTabOnly;

    /// <summary>按窗口句柄取图标。为 null 时不绘制图标。</summary>
    public Func<WindowIdentity, nint>? IconProvider { get; init; }
}

/// <summary>
/// 绘制标签轨道。
///
/// 全部为矢量绘制 + ClearType 文本，没有任何位图皮肤，因此在 100%/125%/150%
/// 缩放下都保持清晰 —— Groupy 在高 DPI 下发糊的根因是要拉伸位图皮肤（Default.spak），
/// 与绘图 API 无关。
/// </summary>
internal static class RailRenderer
{
    private const int TabCornerRadius = 6;
    private const int AccentBarWidth = 3;

    /// <summary>
    /// 绘制分组栏。
    ///
    /// 走分层窗口（逐像素 alpha）而不是 <c>SetWindowRgn</c>：区域是二值掩码，
    /// 圆角只能是硬像素阶梯，用户看到的就是左右两个上角有锯齿，而区域**无法**抗锯齿。
    /// 分层窗口按 alpha 与桌面混合，半覆盖的边缘像素得以半透明呈现。
    /// 分层表面创建失败时退回普通绘制 —— 宁可有锯齿，也不能不画。
    /// </summary>
    public static void Paint(nint hwnd, RailRenderState state)
    {
        var width = state.Layout.Bounds.Width;
        var height = state.Layout.Bounds.Height;

        using var layered = LayeredSurface.Begin(
            hwnd, width, height, state.Layout.TopCornerRadius);

        if (layered is not null)
        {
            PaintContent(layered.Canvas, state, width, height);

            if (layered.Commit())
            {
                // 分层窗口的内容由 UpdateLayeredWindow 提交，但 WM_PAINT 仍需被认领，
                // 否则系统会认为这块无效区域没被处理，反复投递重绘。
                MarkPainted(hwnd);
                return;
            }
        }

        using var paint = BufferedPaint.Begin(hwnd, width, height);
        if (paint is null)
        {
            return;
        }

        PaintContent(paint.Canvas, state, width, height);
    }

    private static void MarkPainted(nint hwnd)
    {
        using var paint = BufferedPaint.Begin(hwnd, 1, 1);
        _ = paint;
    }

    private static void PaintContent(
        GdiCanvas canvas, RailRenderState state, int width, int height)
    {
        // 背景条关掉时也必须填满，否则 GDI 会留下上一帧的残像 ——
        // 只是填成与窗口同色，视觉上标签像直接浮在窗口上方。
        canvas.FillRect(
            PixelRect.FromSize(0, 0, width, height),
            state.ShowBackgroundBar ? state.Theme.Background : state.Theme.WindowBlend);

        PaintMenuButton(canvas, state);
        PaintMinimizeButton(canvas, state);
        PaintCloseGroupButton(canvas, state);

        foreach (var layout in state.Layout.Tabs)
        {
            var tab = FindTab(state.Tabs, layout.Identity);
            if (tab is not null)
            {
                PaintTab(canvas, state, layout, tab);
            }
        }

        PaintDropIndicator(canvas, state, height);
    }

    private static void PaintTab(
        GdiCanvas canvas, RailRenderState state, TabLayout layout, TabItem tab)
    {
        var theme = state.Theme;
        var isMinimized = state.MinimizedIdentities.Contains(tab.Identity);

        // 最小化的标签绝不按"活动"画：窗口都不在屏幕上了，
        // 高亮它等于告诉用户"这个窗口正显示着"。
        var isActive = tab.Identity == state.ActiveIdentity && !isMinimized;
        var isHovered = state.HoveredIdentity == tab.Identity;

        // 半径为 0 时 FillRoundRect 会退化成 FillRect，正是「传统（直角）」标签样式。
        var radius = state.RoundedTabs ? TabCornerRadius : 0;

        // **每个标签都画出形状**，不只是活动与悬停的那个。
        //
        // 早期只给活动/悬停标签填背景，其余留空。后果是「标签样式：传统 / 圆形」
        // 这个设置几乎看不出区别 —— 全条分组栏上只有一个标签有形状，
        // 改它的圆角自然没什么可看的。给非活动标签一个比背景略深的底之后，
        // 每个标签的轮廓都在，圆角与直角一眼可辨，标签边界也清楚多了。
        var fill = isActive
            ? theme.ActiveTabBackground
            : isHovered ? theme.HoverTabBackground : theme.InactiveTabBackground;

        canvas.FillRoundRect(layout.Bounds, fill, radius);

        // 组颜色只画一条细窄竖条，不做大面积填充 —— 颜色服务于识别，不该成为装饰负担。
        if (tab.AccentColor is { } accent)
        {
            canvas.FillRect(
                PixelRect.FromSize(
                    layout.Bounds.Left + 2,
                    layout.Bounds.Top + 6,
                    AccentBarWidth,
                    layout.Bounds.Height - 12),
                accent);
        }

        // 无响应的窗口用警示色，让用户一眼看出是哪个标签卡住了，而不是整条轨道显示为正常。
        var textColor = !tab.IsResponding
            ? theme.DegradedText
            : isActive ? theme.ActiveText : theme.InactiveText;

        // 窗口图标。图标区宽度为 0 表示用户关掉了这一项，此时布局也已收回它的位置。
        if (layout.IconBounds.Width > 0 && state.IconProvider is { } provider)
        {
            var icon = provider(tab.Identity);

            if (icon != 0)
            {
                canvas.DrawIcon(icon, layout.IconBounds);
            }
        }

        var title = !tab.IsResponding
            ? $"{tab.DisplayTitle}（无响应）"
            : isMinimized
                ? $"{tab.DisplayTitle}（已最小化）"
                : tab.DisplayTitle;

        canvas.DrawText(title, layout.TextBounds, textColor, state.Dpi);

        // 关闭按钮的显示与否由策略 + 悬停共同决定，与命中测试调用同一个判定，
        // 保证"画出来的"和"点得到的"永远一致。
        var showClose = layout.CloseBounds.Width > 0
            && TabRailLayoutEngine.ShouldShowClose(
                tab.Identity, state.ActiveIdentity, state.HoveredIdentity, state.CloseButton);

        if (showClose)
        {
            if (state.HoveredCloseIdentity == tab.Identity)
            {
                canvas.FillRoundRect(layout.CloseBounds, theme.HoverTabBackground, 4);
            }

            canvas.DrawCross(layout.CloseBounds, textColor, layout.CloseBounds.Width / 4);
        }
    }

    /// <summary>
    /// 画左侧菜单按钮。
    /// 用三条横线（汉堡图标）而非字形，避免依赖特定字体是否收录该字符 ——
    /// 这在中文系统和不同 Windows 版本上并不总是成立。
    /// </summary>
    private static void PaintMenuButton(GdiCanvas canvas, RailRenderState state)
    {
        var bounds = state.Layout.MenuButton;
        if (bounds.Width <= 0)
        {
            return;
        }

        var lineWidth = Math.Max(10, bounds.Width / 2);
        var lineHeight = Math.Max(1, bounds.Height / 20);
        var gap = Math.Max(3, bounds.Height / 8);

        var x = bounds.Left + ((bounds.Width - lineWidth) / 2);
        var centerY = bounds.Top + (bounds.Height / 2);

        for (var i = -1; i <= 1; i++)
        {
            canvas.FillRect(
                PixelRect.FromSize(x, centerY + (i * gap) - (lineHeight / 2), lineWidth, lineHeight),
                state.Theme.InactiveText);
        }
    }

    /// <summary>
    /// 画右侧「整组最小化」按钮：一条横线，照抄窗口标题栏最小化按钮的样子。
    ///
    /// 悬停用普通高亮而不是警示色 —— 收起分组是可逆的，与「关闭整组」不同量级。
    /// </summary>
    private static void PaintMinimizeButton(GdiCanvas canvas, RailRenderState state)
    {
        var bounds = state.Layout.MinimizeButton;
        if (bounds.Width <= 0)
        {
            return;
        }

        if (state.IsMinimizeHovered)
        {
            canvas.FillRoundRect(bounds, state.Theme.HoverTabBackground, 4);
        }

        // 横线宽度取按钮宽度的一半左右，线宽跟随 DPI，至少 1 像素。
        var lineWidth = Math.Max(8, bounds.Width / 2);
        var thickness = Math.Max(1, state.Dpi / 96);

        canvas.FillRect(
            PixelRect.FromSize(
                bounds.Left + ((bounds.Width - lineWidth) / 2),
                bounds.Top + ((bounds.Height - thickness) / 2),
                lineWidth,
                thickness),
            state.Theme.InactiveText);
    }

    /// <summary>
    /// 画右侧「关闭整组」按钮。
    /// 用比标签关闭按钮更大的叉，以示区别 —— 这个按钮会一次关掉组内所有窗口，
    /// 和关掉单个标签完全不是一个量级的操作。
    /// </summary>
    private static void PaintCloseGroupButton(GdiCanvas canvas, RailRenderState state)
    {
        var bounds = state.Layout.CloseGroupButton;
        if (bounds.Width <= 0)
        {
            return;
        }

        var hovered = state.IsCloseGroupHovered;

        if (hovered)
        {
            // 悬停时用警示红，让用户在点下去之前意识到这不是普通的关闭。
            canvas.FillRoundRect(bounds, 0xFFC42B1C, 4);
        }

        var size = Math.Min(bounds.Width, bounds.Height);
        var square = PixelRect.FromSize(
            bounds.Left + ((bounds.Width - size) / 2),
            bounds.Top + ((bounds.Height - size) / 2),
            size,
            size);

        canvas.DrawCross(
            square,
            hovered ? 0xFFFFFFFF : state.Theme.InactiveText,
            size / 3);
    }

    private static void PaintDropIndicator(GdiCanvas canvas, RailRenderState state, int height)
    {
        if (state.DropIndicatorIndex is not { } index)
        {
            return;
        }

        var tabs = state.Layout.Tabs;

        var x = tabs.Count == 0
            ? state.Layout.MenuButton.Right
            : index >= tabs.Count
                ? tabs[^1].Bounds.Right
                : tabs[index].Bounds.Left;

        canvas.FillRect(
            PixelRect.FromSize(x - 1, 2, 2, height - 4),
            state.Theme.DropHighlight);
    }

    private static TabItem? FindTab(IReadOnlyList<TabItem> tabs, WindowIdentity identity)
    {
        foreach (var tab in tabs)
        {
            if (tab.Identity == identity)
            {
                return tab;
            }
        }

        return null;
    }
}
