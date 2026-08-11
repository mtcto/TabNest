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

    /// <summary>拖拽重排时的插入指示线位置，null 表示不显示。</summary>
    public int? DropIndicatorIndex { get; init; }
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
        canvas.FillRect(PixelRect.FromSize(0, 0, width, height), state.Theme.Background);

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
            canvas.FillRoundRect(
                layout.Bounds,
                isActive ? theme.ActiveTabBackground : theme.HoverTabBackground,
                TabCornerRadius);
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
