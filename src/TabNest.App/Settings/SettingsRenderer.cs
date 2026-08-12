using TabNest.Core.Layout;
using TabNest.Core.Models;
using TabNest.Interop;

namespace TabNest.App.Settings;

/// <summary>绘制一帧设置中心所需的全部状态。不可变。</summary>
public sealed record SettingsRenderState
{
    public required SettingsLayout Layout { get; init; }
    public required ContentLayout Content { get; init; }
    public required PageContent Page { get; init; }
    public required SettingsTheme Theme { get; init; }
    public required SettingsMetrics Metrics { get; init; }
    public required int Dpi { get; init; }
    public required int ScrollOffset { get; init; }

    public SettingsPage? HoveredNav { get; init; }

    /// <summary>光标下的开关，用于悬停高亮。</summary>
    public SettingId HoveredToggle { get; init; }

    /// <summary>光标下的卡片，格式为「块序号:选项序号」。</summary>
    public (int Block, int Option)? HoveredCard { get; init; }

    /// <summary>
    /// 光标在内容坐标系里的位置（已扣除滚动偏移）。
    /// 列表行与按钮的悬停高亮直接用它判断，省得为每种元素都维护一份悬停状态。
    /// </summary>
    public PixelPoint PointerPosition { get; init; } = new(-1, -1);
}

/// <summary>
/// 设置中心的绘制。
///
/// 全部矢量绘制，零位图资源。Groupy 用 6.6MB 的位图皮肤画同样的图示卡片，
/// 代价是 150% 缩放下发糊；矢量绘制体积为零且任何 DPI 下都清晰。
/// </summary>
internal static class SettingsRenderer
{
    public static void Paint(nint hwnd, SettingsRenderState s)
    {
        var w = s.Layout.Bounds.Width;
        var h = s.Layout.Bounds.Height;

        using var paint = BufferedPaint.Begin(hwnd, w, h);
        if (paint is null)
        {
            return;
        }

        var c = paint.Canvas;
        var t = s.Theme;

        c.FillRect(s.Layout.Bounds, t.WindowBackground);

        PaintNav(c, s);
        PaintContent(c, s);
        PaintScrollBar(c, s);
    }

    // ------------------------------------------------------------------
    // 导航栏
    // ------------------------------------------------------------------

    private static void PaintNav(GdiCanvas c, SettingsRenderState s)
    {
        var t = s.Theme;
        c.FillRect(s.Layout.NavBounds, t.NavBackground);

        foreach (var item in s.Layout.NavItems)
        {
            var selected = item.Page == s.Page.Page;
            var hovered = s.HoveredNav == item.Page;

            if (selected)
            {
                c.FillRect(item.Bounds, t.NavItemSelected);
                c.FillRect(item.IndicatorBounds, t.NavIndicator);
            }
            else if (hovered)
            {
                c.FillRect(item.Bounds, t.NavItemHover);
            }

            c.DrawStyledText(
                SettingsPages.TitleOf(item.Page),
                item.TextBounds,
                t.Text,
                selected ? TextStyle.BodyStrong : TextStyle.Body,
                s.Dpi);
        }

        // 导航栏与内容区之间的分界。只画一条竖线，不加阴影 ——
        // 阴影在暗色主题下几乎不可见，反而是纯色线两种主题都成立。
        c.FillRect(
            new PixelRect(
                s.Layout.NavBounds.Right - 1,
                s.Layout.NavBounds.Top,
                s.Layout.NavBounds.Right,
                s.Layout.NavBounds.Bottom),
            t.Border);
    }

    // ------------------------------------------------------------------
    // 内容
    // ------------------------------------------------------------------

    private static void PaintContent(GdiCanvas c, SettingsRenderState s)
    {
        var view = s.Layout.ContentBounds;
        if (view.IsEmpty)
        {
            return;
        }

        // 裁剪到视口：滚动时超出的内容必须被切掉，否则会画到导航栏上。
        using var clip = c.PushClip(view);

        var t = s.Theme;
        var m = s.Metrics;
        var dy = view.Top - s.ScrollOffset;

        c.DrawStyledText(
            s.Page.Title, Offset(s.Content.TitleBounds, view.Left, dy),
            t.Text, TextStyle.PageTitle, s.Dpi);

        if (!s.Content.SubtitleBounds.IsEmpty)
        {
            c.DrawStyledText(
                s.Page.Subtitle!, Offset(s.Content.SubtitleBounds, view.Left, dy),
                t.TextSecondary, TextStyle.Caption, s.Dpi, wrap: true);
        }

        for (var i = 0; i < s.Content.Blocks.Count; i++)
        {
            var b = s.Content.Blocks[i];
            var bounds = Offset(b.Bounds, view.Left, dy);

            // 视口外的块直接跳过。长页面滚动时这能省掉大部分绘制。
            if (bounds.Bottom < view.Top || bounds.Top > view.Bottom)
            {
                continue;
            }

            switch (b.Block)
            {
                case SectionBlock section:
                    PaintSection(c, s, b, section, view.Left, dy);
                    break;

                case ToggleBlock toggle:
                    PaintToggleRow(c, s, b, toggle, view.Left, dy);
                    break;

                case ChoiceBlock choice:
                    PaintChoice(c, s, b, choice, i, view.Left, dy);
                    break;

                case InfoBlock info:
                    PaintInfo(c, s, b, info, view.Left, dy);
                    break;

                case ListBlock list:
                    PaintList(c, s, b, list, view.Left, dy);
                    break;

                case AppListBlock appList:
                    PaintAppList(c, s, b, appList, view.Left, dy);
                    break;

                case SwatchBlock swatch:
                    PaintSwatches(c, s, b, swatch, view.Left, dy);
                    break;

                case SeparatorBlock:
                    c.DrawSeparator(
                        bounds.Left, bounds.Right,
                        bounds.Top + (bounds.Height / 2), t.Separator);
                    break;

                default:
                    break;
            }

            _ = m;
        }
    }

    private static void PaintSection(
        GdiCanvas c, SettingsRenderState s, BlockLayout b, SectionBlock section, int ox, int oy)
    {
        c.DrawStyledText(
            section.Title, Offset(b.TitleBounds, ox, oy),
            s.Theme.Text, TextStyle.Section, s.Dpi);

        if (!b.DescriptionBounds.IsEmpty && section.Description is not null)
        {
            c.DrawStyledText(
                section.Description, Offset(b.DescriptionBounds, ox, oy),
                s.Theme.TextSecondary, TextStyle.Caption, s.Dpi, wrap: true);
        }
    }

    private static void PaintToggleRow(
        GdiCanvas c, SettingsRenderState s, BlockLayout b, ToggleBlock toggle, int ox, int oy)
    {
        var t = s.Theme;
        var bounds = Offset(b.Bounds, ox, oy);
        var hovered = s.HoveredToggle == toggle.Id && toggle.IsEnabled;

        c.FillRoundRect(
            bounds,
            hovered ? t.CardHoverBackground : t.CardBackground,
            s.Metrics.CornerRadius);

        c.DrawStyledText(
            toggle.Title, Offset(b.TitleBounds, ox, oy),
            toggle.IsEnabled ? t.Text : t.TextDisabled, TextStyle.Body, s.Dpi, wrap: true);

        if (!b.DescriptionBounds.IsEmpty && toggle.Description is not null)
        {
            c.DrawStyledText(
                toggle.Description, Offset(b.DescriptionBounds, ox, oy),
                t.TextSecondary, TextStyle.Caption, s.Dpi, wrap: true);
        }

        PaintToggle(c, s, Offset(b.ControlBounds, ox, oy), toggle.Value, toggle.IsEnabled);
    }

    /// <summary>画开关。胶囊轨道 + 圆钮，开时钮在右并填强调色。</summary>
    private static void PaintToggle(
        GdiCanvas c, SettingsRenderState s, PixelRect bounds, bool on, bool enabled)
    {
        var t = s.Theme;
        var radius = bounds.Height / 2;

        var track = !enabled ? t.TextDisabled : on ? t.Accent : t.ToggleOff;
        c.FillRoundRect(bounds, track, radius);

        var inset = Math.Max(2, bounds.Height / 8);
        var knobSize = bounds.Height - (inset * 2);
        var knobLeft = on ? bounds.Right - inset - knobSize : bounds.Left + inset;

        // 圆钮用真正的椭圆填充而非圆角矩形：半径等于半边长时，
        // 圆角矩形的四段圆弧接缝在小尺寸下看得出来，是开关上最显眼的锯齿来源。
        c.FillCircle(
            PixelRect.FromSize(knobLeft, bounds.Top + inset, knobSize, knobSize),
            t.ToggleKnob);
    }

    private static void PaintChoice(
        GdiCanvas c, SettingsRenderState s, BlockLayout b, ChoiceBlock choice, int blockIndex, int ox, int oy)
    {
        var t = s.Theme;
        var m = s.Metrics;

        for (var i = 0; i < b.Options.Count && i < choice.Options.Count; i++)
        {
            var option = choice.Options[i];
            var card = Offset(b.Options[i], ox, oy);
            var selected = option.Value == choice.Value;
            var disabled = option.DisabledReason is not null;
            var hovered = s.HoveredCard == (blockIndex, i) && !disabled;

            c.FillRoundRect(
                card,
                hovered ? t.CardHoverBackground : t.CardBackground,
                m.CornerRadius);

            // 选中用 2px 强调色边框；未选中用 1px 常规边框。
            // 不用背景色区分选中：图示本身占了大部分面积，改背景会让图示辨识度下降。
            //
            // 描边必须同样是圆角：填充是圆角却配直角描边，四个角上会露出
            // 直线与圆弧的错位，看起来像边框有毛刺。
            c.DrawRoundRectOutline(
                card,
                selected ? t.Accent : t.Border,
                m.CornerRadius,
                selected ? 2 : 1);

            var pad = m.RowPadding;
            var illo = PixelRect.FromSize(
                card.Left + pad, card.Top + pad,
                card.Width - (pad * 2), m.CardIllustrationHeight);

            PaintIllustration(c, s, illo, option.Illustration, disabled);

            var textTop = illo.Bottom + (pad / 2);
            var titleColor = disabled ? t.TextDisabled : t.Text;

            var titleHeight = c.MeasureTextHeight(
                option.Title, card.Width - (pad * 2), TextStyle.BodyStrong, s.Dpi, wrap: false);

            c.DrawStyledText(
                option.Title,
                new PixelRect(card.Left + pad, textTop, card.Right - pad, textTop + titleHeight),
                titleColor, TextStyle.BodyStrong, s.Dpi);

            // 禁用时把"为什么不可用"顶到说明位置 —— 灰掉却不解释是设置界面里最恼人的事。
            var caption = option.DisabledReason ?? option.Description;

            if (!string.IsNullOrEmpty(caption))
            {
                var capTop = textTop + titleHeight + s.Metrics.RowTextGap;

                // 高度按实际测量给足，而不是"剩下多少画多少"。
                // 卡片高度已由布局按内容算过，这里再夹一次只会把说明又截回去。
                var capHeight = c.MeasureTextHeight(
                    caption, card.Width - (pad * 2), TextStyle.Caption, s.Dpi);

                c.DrawStyledText(
                    caption,
                    new PixelRect(card.Left + pad, capTop, card.Right - pad, capTop + capHeight),
                    disabled ? t.Warning : t.TextSecondary,
                    TextStyle.Caption, s.Dpi, wrap: true);
            }
        }
    }

    private static void PaintInfo(
        GdiCanvas c, SettingsRenderState s, BlockLayout b, InfoBlock info, int ox, int oy)
    {
        var t = s.Theme;

        c.FillRoundRect(Offset(b.Bounds, ox, oy), t.CardBackground, s.Metrics.CornerRadius);

        c.DrawStyledText(
            info.Text, Offset(b.DescriptionBounds, ox, oy),
            info.IsWarning ? t.Warning : t.TextSecondary,
            TextStyle.Caption, s.Dpi, wrap: true);
    }

    private static void PaintList(
        GdiCanvas c, SettingsRenderState s, BlockLayout b, ListBlock list, int ox, int oy)
    {
        var t = s.Theme;

        c.FillRoundRect(Offset(b.Bounds, ox, oy), t.CardBackground, s.Metrics.CornerRadius);

        c.DrawStyledText(
            list.Title, Offset(b.TitleBounds, ox, oy),
            t.Text, TextStyle.BodyStrong, s.Dpi);

        // 列表为空时仍占一行（显示空提示），因此行数是 1 而不是 0。
        // 按钮矩形接在行矩形之后，索引必须用这个行数而非 Items.Count ——
        // 早期用了 Items.Count，列表为空时按钮索引算成 0，
        // 正好和空提示行撞在一起，于是「新建规则」按钮根本没被画出来。
        var rowCount = list.Items.Count == 0 ? 1 : list.Items.Count;

        if (list.Items.Count == 0)
        {
            if (b.Options.Count > 0)
            {
                c.DrawStyledText(
                    list.EmptyText, Offset(b.Options[0], ox, oy),
                    t.TextSecondary, TextStyle.Caption, s.Dpi, wrap: true);
            }

            PaintListButtons(c, s, b, list, rowCount, ox, oy);
            return;
        }

        // 列表行在前，操作按钮在后。
        for (var i = 0; i < list.Items.Count && i < b.Options.Count; i++)
        {
            var row = Offset(b.Options[i], ox, oy);
            var (primary, secondary) = list.Items[i];

            // 可点击的行给个悬停底色，让"这里能点"显而易见。
            if (list.ItemAction is not null && row.Contains(s.PointerPosition))
            {
                c.FillRoundRect(row, t.CardHoverBackground, s.Metrics.CornerRadius / 2);
            }

            var primaryHeight = c.MeasureTextHeight(
                primary, row.Width, TextStyle.Body, s.Dpi, wrap: false);

            c.DrawStyledText(
                primary,
                new PixelRect(row.Left, row.Top, row.Right, row.Top + primaryHeight),
                t.Text, TextStyle.Body, s.Dpi);

            if (!string.IsNullOrEmpty(secondary))
            {
                var secondTop = row.Top + primaryHeight + s.Metrics.RowTextGap;

                if (secondTop < row.Bottom)
                {
                    c.DrawStyledText(
                        secondary,
                        new PixelRect(row.Left, secondTop, row.Right, row.Bottom),
                        t.TextSecondary, TextStyle.Caption, s.Dpi, wrap: true);
                }
            }
        }

        PaintListButtons(c, s, b, list, rowCount, ox, oy);
    }

    private static void PaintListButtons(
        GdiCanvas c, SettingsRenderState s, BlockLayout b, ListBlock list,
        int rowCount, int ox, int oy)
    {
        var t = s.Theme;

        for (var i = 0; i < list.Buttons.Count; i++)
        {
            var index = rowCount + i;

            if (index >= b.Options.Count)
            {
                break;
            }

            var rect = Offset(b.Options[index], ox, oy);
            var hovered = rect.Contains(s.PointerPosition);

            c.FillRoundRect(
                rect,
                hovered ? t.Accent : t.CardHoverBackground,
                s.Metrics.CornerRadius);

            c.DrawRoundRectOutline(rect, t.Border, s.Metrics.CornerRadius);

            c.DrawStyledText(
                list.Buttons[i].Text, rect,
                hovered ? t.AccentText : t.Text,
                TextStyle.Body, s.Dpi, TextAlign.Center);
        }
    }

    /// <summary>画应用清单：带边框的列表 + 右侧「加入列表」「移出列表」。</summary>
    private static void PaintAppList(
        GdiCanvas c, SettingsRenderState s, BlockLayout b, AppListBlock list, int ox, int oy)
    {
        var t = s.Theme;
        var m = s.Metrics;
        var box = Offset(b.TitleBounds, ox, oy);

        c.FillRoundRect(box, t.CardBackground, m.CornerRadius);
        c.DrawRoundRectOutline(box, t.Border, m.CornerRadius);

        if (list.Apps.Count == 0)
        {
            var pad = m.RowPadding;

            c.DrawStyledText(
                list.EmptyText,
                new PixelRect(box.Left + pad, box.Top + pad, box.Right - pad, box.Bottom - pad),
                t.TextSecondary, TextStyle.Caption, s.Dpi, wrap: true);
        }

        for (var i = 0; i < list.Apps.Count && i < b.Options.Count; i++)
        {
            var row = Offset(b.Options[i], ox, oy);
            var selected = i == list.SelectedIndex;

            if (selected)
            {
                c.FillRoundRect(row, t.Accent, m.CornerRadius / 2);
            }
            else if (row.Contains(s.PointerPosition))
            {
                c.FillRoundRect(row, t.CardHoverBackground, m.CornerRadius / 2);
            }

            c.DrawStyledText(
                list.Apps[i],
                new PixelRect(row.Left + (m.RowPadding / 2), row.Top, row.Right, row.Bottom),
                selected ? t.AccentText : t.Text,
                TextStyle.Body, s.Dpi);
        }

        // 两个按钮接在行矩形之后。
        var buttons = new[] { "加入列表", "移出列表" };

        for (var i = 0; i < buttons.Length; i++)
        {
            var index = list.Apps.Count + i;

            if (index >= b.Options.Count)
            {
                break;
            }

            var rect = Offset(b.Options[index], ox, oy);

            // 没有选中项时「移出列表」必须灰显 —— 一个点了没反应的按钮
            // 比一个明确告诉你"现在不能点"的按钮更让人困惑。
            var enabled = i == 0 || list.SelectedIndex >= 0;
            var hovered = enabled && rect.Contains(s.PointerPosition);

            c.FillRoundRect(
                rect,
                hovered ? t.Accent : t.CardBackground,
                m.CornerRadius);

            c.DrawRoundRectOutline(rect, t.Border, m.CornerRadius);

            c.DrawStyledText(
                buttons[i], rect,
                !enabled ? t.TextDisabled : hovered ? t.AccentText : t.Text,
                TextStyle.Body, s.Dpi, TextAlign.Center);
        }
    }

    private static void PaintSwatches(
        GdiCanvas c, SettingsRenderState s, BlockLayout b, SwatchBlock swatch, int ox, int oy)
    {
        var t = s.Theme;

        c.FillRoundRect(Offset(b.Bounds, ox, oy), t.CardBackground, s.Metrics.CornerRadius);

        c.DrawStyledText(
            swatch.Title, Offset(b.TitleBounds, ox, oy),
            t.Text, TextStyle.BodyStrong, s.Dpi);

        if (!b.DescriptionBounds.IsEmpty && swatch.Description is not null)
        {
            c.DrawStyledText(
                swatch.Description, Offset(b.DescriptionBounds, ox, oy),
                t.TextSecondary, TextStyle.Caption, s.Dpi, wrap: true);
        }

        for (var i = 0; i < b.Options.Count && i < swatch.Swatches.Count; i++)
        {
            var rect = Offset(b.Options[i], ox, oy);
            var (name, argb) = swatch.Swatches[i];

            c.FillRoundRect(rect, argb, s.Metrics.CornerRadius / 2);

            // 描边是必需的：浅色样本在浅色卡片上、深色样本在深色卡片上
            // 都会看不出边界，用户以为那里是空的。
            c.DrawRoundRectOutline(rect, t.Border, s.Metrics.CornerRadius / 2);

            c.DrawStyledText(
                name,
                new PixelRect(
                    rect.Left - (s.Metrics.CardGap / 2), rect.Bottom,
                    rect.Right + (s.Metrics.CardGap / 2), rect.Bottom + (s.Metrics.RowHeight / 2)),
                t.TextSecondary, TextStyle.Caption, s.Dpi, TextAlign.Center);
        }
    }

    // ------------------------------------------------------------------
    // 矢量图示
    // ------------------------------------------------------------------

    /// <summary>
    /// 画卡片里的示意图。
    ///
    /// 每个图示都是几个矩形拼出来的窗口示意，刻意保持极简：
    /// 它要传达的是"标签在哪儿"，不是复刻真实界面。
    /// </summary>
    private static void PaintIllustration(
        GdiCanvas c, SettingsRenderState s, PixelRect area, Illustration kind, bool disabled)
    {
        var t = s.Theme;
        var fill = t.IllustrationFill;
        var accent = disabled ? t.TextDisabled : t.IllustrationAccent;
        var unit = Math.Max(3, area.Height / 8);

        switch (kind)
        {
            case Illustration.RailAbove:
                {
                    // 上方一条标签栏，下面是窗口主体，两者分离。
                    var railHeight = unit * 2;
                    var rail = PixelRect.FromSize(area.Left, area.Top, area.Width, railHeight);
                    c.FillRect(rail, fill);

                    var tabWidth = area.Width / 3;
                    c.FillRect(PixelRect.FromSize(area.Left, area.Top, tabWidth, railHeight), accent);

                    c.FillRect(
                        new PixelRect(area.Left, rail.Bottom + 1, area.Right, area.Bottom),
                        fill);
                    break;
                }

            case Illustration.IntegratedTitleBar:
                {
                    // 标签长在标题栏里，与窗口连成一体。
                    var barHeight = unit * 2;
                    c.FillRect(new PixelRect(area.Left, area.Top, area.Right, area.Bottom), fill);

                    var tabWidth = area.Width / 3;
                    c.FillRect(
                        PixelRect.FromSize(area.Left + unit, area.Top, tabWidth, barHeight),
                        accent);
                    break;
                }

            case Illustration.TaskbarAll:
                PaintTaskbar(c, area, fill, accent, buttons: 3, highlighted: 3);
                break;

            case Illustration.TaskbarActiveOnly:
                PaintTaskbar(c, area, fill, accent, buttons: 1, highlighted: 1);
                break;

            case Illustration.TaskbarSingle:
                PaintTaskbar(c, area, fill, accent, buttons: 1, highlighted: 1, wide: true);
                break;

            case Illustration.TabsRounded:
                PaintTabShapes(c, s, area, fill, accent, rounded: true);
                break;

            case Illustration.TabsSquare:
                PaintTabShapes(c, s, area, fill, accent, rounded: false);
                break;

            default:
                break;
        }
    }

    private static void PaintTaskbar(
        GdiCanvas c, PixelRect area, uint fill, uint accent,
        int buttons, int highlighted, bool wide = false)
    {
        // 上半部分是窗口，下半部分是任务栏。
        var barHeight = Math.Max(6, area.Height / 4);
        var windowArea = new PixelRect(area.Left, area.Top, area.Right, area.Bottom - barHeight - 4);
        c.FillRect(windowArea, fill);

        var bar = new PixelRect(area.Left, area.Bottom - barHeight, area.Right, area.Bottom);
        c.FillRect(bar, fill);

        var gap = 3;
        var count = Math.Max(1, buttons);
        var buttonWidth = wide
            ? area.Width / 2
            : ((area.Width / 2) - (gap * (count - 1))) / count;

        var x = area.Left + 2;

        for (var i = 0; i < count; i++)
        {
            c.FillRect(
                PixelRect.FromSize(x, bar.Top + 2, buttonWidth, barHeight - 4),
                i < highlighted ? accent : fill);

            x += buttonWidth + gap;
        }
    }

    private static void PaintTabShapes(
        GdiCanvas c, SettingsRenderState s, PixelRect area, uint fill, uint accent, bool rounded)
    {
        var tabHeight = Math.Max(8, area.Height / 3);
        var tabWidth = area.Width / 3;
        var radius = rounded ? Math.Max(2, tabHeight / 3) : 0;

        var body = new PixelRect(area.Left, area.Top + tabHeight, area.Right, area.Bottom);
        c.FillRect(body, fill);

        var x = area.Left;

        for (var i = 0; i < 2; i++)
        {
            var tab = PixelRect.FromSize(x, area.Top, tabWidth, tabHeight);
            var color = i == 0 ? accent : fill;

            if (rounded)
            {
                c.FillRoundRect(tab, color, radius);
            }
            else
            {
                c.FillRect(tab, color);
            }

            x += tabWidth + 2;
        }

        _ = s;
    }

    private static void PaintScrollBar(GdiCanvas c, SettingsRenderState s)
    {
        if (s.Layout.ScrollThumbBounds.IsEmpty)
        {
            return;
        }

        var thumb = s.Layout.ScrollThumbBounds;

        // 滑块两侧各留 2px，看起来像一根细条而不是贴边的色块。
        var inset = 2;
        c.FillRoundRect(
            new PixelRect(thumb.Left + inset, thumb.Top, thumb.Right - inset, thumb.Bottom),
            s.Theme.ScrollThumb,
            (thumb.Width - (inset * 2)) / 2);
    }

    private static PixelRect Offset(PixelRect r, int dx, int dy) =>
        r.IsEmpty ? r : new PixelRect(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);
}
