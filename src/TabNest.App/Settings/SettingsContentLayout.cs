using TabNest.Core.Layout;
using TabNest.Core.Models;
using TabNest.Interop;

namespace TabNest.App.Settings;

/// <summary>一个内容块被测量后的位置。坐标相对内容区原点，不含滚动偏移。</summary>
/// <param name="Block">对应的内容块。</param>
/// <param name="Bounds">整块矩形。</param>
/// <param name="TitleBounds">标题文字区域。</param>
/// <param name="DescriptionBounds">说明文字区域，高度为 0 表示没有说明。</param>
/// <param name="ControlBounds">交互控件矩形（开关）。</param>
/// <param name="Options">选择卡片各项矩形，与 <see cref="ChoiceBlock.Options"/> 一一对应。</param>
public readonly record struct BlockLayout(
    ContentBlock Block,
    PixelRect Bounds,
    PixelRect TitleBounds,
    PixelRect DescriptionBounds,
    PixelRect ControlBounds,
    IReadOnlyList<PixelRect> Options);

/// <summary>
/// 一页内容测量后的完整布局。
///
/// 测量与绘制**必须**分开：命中测试要用和绘制完全相同的坐标，
/// 若绘制时临时算位置、命中时再算一遍，两处只要有一点不一致，
/// 就会出现"看着点在开关上却没反应"这类无从调试的问题。
/// </summary>
public sealed record ContentLayout
{
    public required IReadOnlyList<BlockLayout> Blocks { get; init; }
    public required PixelRect TitleBounds { get; init; }
    public required PixelRect SubtitleBounds { get; init; }

    /// <summary>内容总高度，用于计算滚动条。</summary>
    public required int TotalHeight { get; init; }

    /// <summary>
    /// 测量一页内容。
    ///
    /// 需要 <paramref name="canvas"/> 是因为说明文字换行后的行数只有量过才知道 ——
    /// 写死行高在中英文混排、不同 DPI、不同字体下必然出错。
    /// </summary>
    public static ContentLayout Measure(
        GdiCanvas canvas,
        PageContent page,
        int viewportWidth,
        SettingsMetrics m,
        int dpi)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(m);

        var left = m.ContentPadding;
        var right = Math.Max(left + 1, viewportWidth - m.ContentPadding);
        var width = right - left;

        var y = m.ContentPadding;

        var titleHeight = canvas.MeasureTextHeight(page.Title, width, TextStyle.PageTitle, dpi, wrap: false);
        var titleBounds = new PixelRect(left, y, right, y + titleHeight);
        y += titleHeight;

        var subtitleBounds = PixelRect.Empty;

        if (!string.IsNullOrEmpty(page.Subtitle))
        {
            y += m.RowTextGap;
            var h = canvas.MeasureTextHeight(page.Subtitle, width, TextStyle.Caption, dpi);
            subtitleBounds = new PixelRect(left, y, right, y + h);
            y += h;
        }

        y += m.TitleGap;

        var blocks = new List<BlockLayout>(page.Blocks.Count);

        foreach (var block in page.Blocks)
        {
            switch (block)
            {
                case SectionBlock section:
                    {
                        // 分区标题之前留白，但页面第一个块不留 —— 否则页头下方会出现双倍空隙。
                        if (blocks.Count > 0)
                        {
                            y += m.SectionGap;
                        }

                        var h = canvas.MeasureTextHeight(section.Title, width, TextStyle.Section, dpi, wrap: false);
                        var titleRect = new PixelRect(left, y, right, y + h);
                        var descRect = PixelRect.Empty;
                        var blockTop = y;
                        y += h;

                        if (!string.IsNullOrEmpty(section.Description))
                        {
                            y += m.RowTextGap;
                            var dh = canvas.MeasureTextHeight(section.Description, width, TextStyle.Caption, dpi);
                            descRect = new PixelRect(left, y, right, y + dh);
                            y += dh;
                        }

                        y += m.SectionHeaderGap;

                        blocks.Add(new BlockLayout(
                            block,
                            new PixelRect(left, blockTop, right, y),
                            titleRect,
                            descRect,
                            PixelRect.Empty,
                            []));
                        break;
                    }

                case ToggleBlock toggle:
                    {
                        var blockTop = y;

                        // 开关占右侧固定宽度，文字区域相应收窄，避免长标题压到开关上。
                        var controlLeft = right - m.RowPadding - m.ToggleWidth;
                        var textRight = controlLeft - m.RowPadding;
                        var textWidth = Math.Max(1, textRight - left - m.RowPadding);
                        var textLeft = left + m.RowPadding;

                        var th = canvas.MeasureTextHeight(toggle.Title, textWidth, TextStyle.Body, dpi);
                        var rowTop = y + (m.RowHeight - th) / 2;
                        var titleRect = new PixelRect(textLeft, rowTop, textRight, rowTop + th);

                        var contentBottom = y + m.RowHeight;
                        var descRect = PixelRect.Empty;

                        if (!string.IsNullOrEmpty(toggle.Description))
                        {
                            var dh = canvas.MeasureTextHeight(toggle.Description, textWidth, TextStyle.Caption, dpi);
                            var dTop = y + m.RowHeight - m.RowBottomPadding + m.RowTextGap;
                            descRect = new PixelRect(textLeft, dTop, textRight, dTop + dh);
                            contentBottom = dTop + dh + m.RowBottomPadding;
                        }

                        // 开关竖直对齐到标题行，而不是整块居中 —— 有说明时整块很高，
                        // 居中会让开关飘到标题与说明之间，看起来不知道属于谁。
                        var toggleTop = y + (m.RowHeight - m.ToggleHeight) / 2;
                        var control = PixelRect.FromSize(
                            controlLeft, toggleTop, m.ToggleWidth, m.ToggleHeight);

                        blocks.Add(new BlockLayout(
                            block,
                            new PixelRect(left, blockTop, right, contentBottom),
                            titleRect,
                            descRect,
                            control,
                            []));

                        // 块矩形到 contentBottom 为止，间隔留在块之外 ——
                        // 若把间隔算进块里，悬停高亮会连着间隔一起亮，行与行之间依旧糊成一片。
                        y = contentBottom + m.RowGap;
                        break;
                    }

                case ChoiceBlock choice:
                    {
                        var blockTop = y;
                        var rects = new List<PixelRect>(choice.Options.Count);

                        var x = left;
                        var rowTop = y;
                        var rowHeight = m.CardHeight;

                        foreach (var _ in choice.Options)
                        {
                            // 放不下就换行。窗口被拉窄时卡片必须能折行，
                            // 否则最后一张会被裁掉一半，且用户不知道还有选项。
                            if (x + m.CardWidth > right && x > left)
                            {
                                x = left;
                                rowTop += rowHeight + m.CardGap;
                            }

                            rects.Add(PixelRect.FromSize(x, rowTop, m.CardWidth, m.CardHeight));
                            x += m.CardWidth + m.CardGap;
                        }

                        var bottom = rowTop + rowHeight;

                        blocks.Add(new BlockLayout(
                            block,
                            new PixelRect(left, blockTop, right, bottom),
                            PixelRect.Empty,
                            PixelRect.Empty,
                            PixelRect.Empty,
                            rects));

                        y = bottom;
                        break;
                    }

                case InfoBlock info:
                    {
                        var blockTop = y;
                        var pad = m.RowPadding;
                        var innerWidth = Math.Max(1, width - (pad * 2));
                        var h = canvas.MeasureTextHeight(info.Text, innerWidth, TextStyle.Caption, dpi);

                        var textRect = new PixelRect(left + pad, y + pad, right - pad, y + pad + h);
                        var bottom = y + pad + h + pad;

                        blocks.Add(new BlockLayout(
                            block,
                            new PixelRect(left, blockTop, right, bottom),
                            PixelRect.Empty,
                            textRect,
                            PixelRect.Empty,
                            []));

                        y = bottom;
                        break;
                    }

                case SeparatorBlock:
                    {
                        var blockTop = y;
                        y += m.SectionGap;

                        blocks.Add(new BlockLayout(
                            block,
                            new PixelRect(left, blockTop, right, y),
                            PixelRect.Empty,
                            PixelRect.Empty,
                            PixelRect.Empty,
                            []));
                        break;
                    }

                default:
                    break;
            }
        }

        return new ContentLayout
        {
            Blocks = blocks,
            TitleBounds = titleBounds,
            SubtitleBounds = subtitleBounds,
            TotalHeight = y + m.ContentPadding,
        };
    }

    /// <summary>命中测试开关。<paramref name="point"/> 为已扣除滚动偏移的内容坐标。</summary>
    public SettingId HitTestToggle(PixelPoint point)
    {
        foreach (var b in Blocks)
        {
            // 整行可点，而不只是开关本身 —— 命中区域只有 40×20 像素的开关会很难点，
            // 这是自绘 UI 常见的手感缺陷。
            if (b.Block is ToggleBlock toggle && toggle.IsEnabled && b.Bounds.Contains(point))
            {
                return toggle.Id;
            }
        }

        return SettingId.None;
    }

    /// <summary>命中测试选择卡片，返回被点中的设置项与取值。</summary>
    public (SettingId Id, int Value)? HitTestChoice(PixelPoint point)
    {
        foreach (var b in Blocks)
        {
            if (b.Block is not ChoiceBlock choice)
            {
                continue;
            }

            for (var i = 0; i < b.Options.Count && i < choice.Options.Count; i++)
            {
                if (!b.Options[i].Contains(point))
                {
                    continue;
                }

                var option = choice.Options[i];

                // 禁用项吃掉这次点击而不是穿透：让用户知道点到了，只是不可用。
                return option.DisabledReason is null ? (choice.Id, option.Value) : null;
            }
        }

        return null;
    }
}
