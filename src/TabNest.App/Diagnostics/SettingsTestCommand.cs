using TabNest.App.Settings;
using TabNest.Core.Layout;
using TabNest.Core.Models;
using TabNest.Interop;

namespace TabNest.App.Diagnostics;

/// <summary>
/// 设置中心自测。
///
/// 真实创建窗口、真实测量与布局，然后断言"点在开关上确实能命中该开关"。
/// 自绘 UI 最典型的缺陷就是绘制坐标与命中坐标不一致 —— 看着点在控件上却没反应，
/// 而这类问题靠肉眼完全看不出来，只能靠对着布局结果断言。
/// </summary>
internal static class SettingsTestCommand
{
    public static int Run(RunMode mode)
    {
        _ = mode;

        Console.WriteLine($"TabNest {BuildInfo.DisplayVersion} 设置中心自测");
        Console.WriteLine();

        var failures = 0;
        using var dispatcher = new UiDispatcher();

        var applied = new List<AppSettings>();
        var settings = AppSettings.Default;

        using var window = new SettingsWindow(settings, c => applied.Add(c.Settings));

        failures += Check("设置窗口已创建", window.IsCreated, "CreateWindow 失败");

        window.ShowAndActivate();
        PumpMessages(TimeSpan.FromMilliseconds(600));

        failures += Check("设置窗口可见", window.IsVisible, "窗口创建了但不可见");

        var client = window.ClientBounds;
        Console.WriteLine($"客户区 {client.Width}x{client.Height}，DPI {window.Dpi}");
        Console.WriteLine();

        failures += Check(
            "客户区尺寸不小于最小窗口尺寸",
            client.Width > 400 && client.Height > 300,
            $"客户区异常：{client.Width}x{client.Height}");

        // ---- 布局与命中的一致性 ----

        var dpi = window.Dpi;
        var metrics = new SettingsMetrics().ScaleTo(dpi);

        using var probe = Win32Window.MeasureCanvas.Create(window.Handle);

        foreach (var page in SettingsPages.Order)
        {
            var content = SettingsPages.Build(page, settings);
            var viewportWidth = client.Width - metrics.NavWidth;
            var layout = ContentLayout.Measure(probe.Canvas, content, viewportWidth, metrics, dpi);

            failures += Check(
                $"「{SettingsPages.TitleOf(page)}」内容高度为正",
                layout.TotalHeight > 0,
                $"总高度 {layout.TotalHeight}");

            // 每个开关的中心点都必须能命中它自己。
            foreach (var b in layout.Blocks)
            {
                if (b.Block is not ToggleBlock toggle || !toggle.IsEnabled)
                {
                    continue;
                }

                var hit = layout.HitTestToggle(b.ControlBounds.Center);

                failures += Check(
                    $"  开关「{Truncate(toggle.Title)}」可被命中",
                    hit == toggle.Id,
                    $"命中的是 {hit}，期望 {toggle.Id}");
            }

            // 每张可用卡片的中心点都必须能命中，且返回自己的取值。
            foreach (var b in layout.Blocks)
            {
                if (b.Block is not ChoiceBlock choice)
                {
                    continue;
                }

                for (var i = 0; i < b.Options.Count && i < choice.Options.Count; i++)
                {
                    var option = choice.Options[i];
                    var result = layout.HitTestChoice(b.Options[i].Center);

                    if (option.DisabledReason is not null)
                    {
                        failures += Check(
                            $"  禁用卡片「{Truncate(option.Title)}」不可选中",
                            result is null,
                            "禁用项被判定为可选");
                        continue;
                    }

                    failures += Check(
                        $"  卡片「{Truncate(option.Title)}」命中自身取值",
                        result is not null && result.Value.Id == choice.Id
                            && result.Value.Value == option.Value,
                        $"命中结果 {result?.Id}={result?.Value}，期望 {choice.Id}={option.Value}");
                }
            }

            // 块之间不得重叠。重叠意味着测量算错了高度，视觉上是文字叠在一起。
            for (var i = 1; i < layout.Blocks.Count; i++)
            {
                var prev = layout.Blocks[i - 1].Bounds;
                var cur = layout.Blocks[i].Bounds;

                if (cur.Top < prev.Bottom)
                {
                    failures += Check(
                        $"  第 {i} 块不与前一块重叠",
                        false,
                        $"上一块底部 {prev.Bottom}，本块顶部 {cur.Top}");
                }
            }
        }

        Console.WriteLine();

        // ---- 写回的正确性 ----
        //
        // 手写 UI 最难查的 bug 是控件连错字段：界面一切正常，点它却改了别的设置。

        var toggled = SettingsPages.Toggle(AppSettings.Default, SettingId.MasterEnabled);
        failures += Check(
            "切换主开关只改 Enabled",
            !toggled.Enabled && toggled.Appearance == AppSettings.Default.Appearance
                && toggled.Grouping == AppSettings.Default.Grouping,
            "主开关的切换影响了其他字段");

        var hover = SettingsPages.Toggle(AppSettings.Default, SettingId.ShowHoverBar);
        failures += Check(
            "切换悬停条只改 ShowHoverBar",
            hover.ShowHoverBar != AppSettings.Default.ShowHoverBar && hover.Enabled,
            "悬停条的切换影响了主开关");

        var chosen = SettingsPages.Choose(
            AppSettings.Default, SettingId.TaskbarButtons, (int)TaskbarButtonPolicy.ActiveOnly);
        failures += Check(
            "选择任务栏策略写入正确字段",
            chosen.TaskbarButtons == TaskbarButtonPolicy.ActiveOnly,
            $"实际为 {chosen.TaskbarButtons}");

        // 每个可切换的开关都必须真的改变了某个字段。漏接线的开关点了毫无反应。
        foreach (var page in SettingsPages.Order)
        {
            foreach (var block in SettingsPages.Build(page, AppSettings.Default).Blocks)
            {
                if (block is not ToggleBlock toggle || !toggle.IsEnabled)
                {
                    continue;
                }

                var after = SettingsPages.Toggle(AppSettings.Default, toggle.Id);

                failures += Check(
                    $"开关 {toggle.Id} 已接线",
                    after != AppSettings.Default,
                    "切换后设置没有任何变化，说明该开关没有对应的写回分支");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "全部通过。" : $"{failures} 项未通过。");

        return failures == 0 ? 0 : 1;
    }

    private static string Truncate(string s) => s.Length <= 18 ? s : s[..17] + "…";

    private static int Check(string name, bool ok, string detail)
    {
        Console.WriteLine(ok ? $"[通过] {name}" : $"[失败] {name}\n       {detail}");
        return ok ? 0 : 1;
    }

    private static void PumpMessages(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;

        while (DateTime.UtcNow < deadline)
        {
            MessagePump.DrainPendingMessages();
            Thread.Sleep(16);
        }
    }
}
