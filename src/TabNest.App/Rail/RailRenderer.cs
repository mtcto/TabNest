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

    /// <summary>光标是否停在「关闭整组」按钮上。</summary>
    public bool IsCloseGroupHovered { get; init; }

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

    public static void Paint(nint hwnd, RailRenderState state)
    {
        var width = state.Layout.Bounds.Width;
        var height = state.Layout.Bounds.Height;

        using var paint = BufferedPaint.Begin(hwnd, width, height);
        if (paint is null)
        {
            return;
        }

        var canvas = paint.Canvas;

        // 背景条关掉时也必须填满，否则 GDI 会留下上一帧的残像 ——
        // 只是填成与窗口同色，视觉上标签像直接浮在窗口上方。
        canvas.FillRect(
            PixelRect.FromSize(0, 0, width, height),
            state.ShowBackgroundBar ? state.Theme.Background : state.Theme.WindowBlend);

        PaintMenuButton(canvas, state);
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
        var isActive = tab.Identity == state.ActiveIdentity;
        var isHovered = state.HoveredIdentity == tab.Identity;

        if (isActive || isHovered)
        {
            // 半径为 0 时 FillRoundRect 会退化成 FillRect，正是「传统（直角）」标签样式。
            canvas.FillRoundRect(
                layout.Bounds,
                isActive ? theme.ActiveTabBackground : theme.HoverTabBackground,
                state.RoundedTabs ? TabCornerRadius : 0);
        }

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

        var title = tab.IsResponding ? tab.DisplayTitle : $"{tab.DisplayTitle}（无响应）";
        canvas.DrawText(title, layout.TextBounds, textColor, state.Dpi);


        if (layout.CloseBounds.Width > 0)
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
