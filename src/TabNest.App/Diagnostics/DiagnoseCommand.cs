using System.Globalization;
using System.Text;
using System.Text.Json;
using TabNest.Core.Models;
using TabNest.Core.Rules;
using TabNest.Interop;

namespace TabNest.App.Diagnostics;

/// <summary>
/// 无 UI 诊断模式：列出当前顶层窗口及其可分组性判定原因。
///
/// 这是 Interop 层的第一个可验证里程碑 —— 不依赖任何 UI 就能确认窗口发现、
/// 属性采集与资格判定是否正确。同时它本身也是产品功能：
/// 用户遇到"为什么这个窗口不能分组"时，这里给出确定答案。
/// </summary>
internal static class DiagnoseCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int Run(RunMode mode)
    {
        var processes = new ProcessInspector();
        var enumerator = new WindowEnumerator(processes);
        var environment = SystemEnvironmentProbe.Probe(processes);

        var windows = enumerator.Enumerate(includeAll: mode.IncludeAll);

        var context = new EligibilityContext
        {
            Environment = environment,
            OwnWindowHandles = new HashSet<nint>(),
        };

        var rows = new List<DiagnosticRow>(windows.Count);
        foreach (var window in windows)
        {
            rows.Add(new DiagnosticRow(window, EligibilityEvaluator.Evaluate(window, context)));
        }

        if (mode.Json)
        {
            WriteJson(environment, rows);
        }
        else
        {
            WriteText(environment, rows, mode.IncludeAll);
        }

        return 0;
    }

    private static void WriteText(
        SystemEnvironment environment,
        List<DiagnosticRow> rows,
        bool includeAll)
    {
        var output = new StringBuilder();

        output.AppendLine(CultureInfo.CurrentCulture, $"TabNest {BuildInfo.DisplayVersion} 窗口诊断");
        output.AppendLine();

        // 系统级前置条件先报：它们不满足时所有窗口都不可分组，
        // 逐个窗口报"不可分组"只会淹没真正的原因。
        output.AppendLine("系统环境");
        output.AppendLine(CultureInfo.CurrentCulture, $"  桌面窗口管理器（DWM）    {Mark(environment.IsDwmEnabled)}");
        output.AppendLine(CultureInfo.CurrentCulture, $"  拖动时显示窗口内容        {Mark(environment.IsDragFullWindowsEnabled)}");
        output.AppendLine(CultureInfo.CurrentCulture, $"  TabNest 完整性级别        {environment.OwnIntegrityLevel}");
        output.AppendLine();

        var eligible = rows.Count(r => r.Result.IsEligible);
        var filterNote = includeAll ? "（已关闭基础过滤）" : string.Empty;
        output.AppendLine(
            CultureInfo.CurrentCulture,
            $"共发现 {rows.Count} 个窗口，其中 {eligible} 个可分组{filterNote}");
        output.AppendLine();

        foreach (var row in rows)
        {
            AppendRow(output, row);
        }

        AppendSummary(output, rows);

        Console.Write(output.ToString());
    }

    private static void AppendRow(StringBuilder output, DiagnosticRow row)
    {
        var window = row.Window;
        var result = row.Result;

        var badge = result.Severity switch
        {
            EligibilitySeverity.Allowed => "[可分组]",
            EligibilitySeverity.AllowedWithWarning => "[可分组*]",
            _ => "[不可分组]",
        };

        output.AppendLine(CultureInfo.CurrentCulture, $"{badge} {Truncate(window.Title, 60)}");
        output.AppendLine(
            CultureInfo.CurrentCulture,
            $"    进程 {window.ProcessName}  类名 {Truncate(window.ClassName, 32)}  "
            + $"句柄 0x{window.Identity.Handle:X}");
        output.AppendLine(
            CultureInfo.CurrentCulture,
            $"    位置 {window.Bounds}  DPI {window.Dpi}  显示器 {window.MonitorDeviceName}");

        var flags = DescribeFlags(window);
        if (flags.Length > 0)
        {
            output.AppendLine(CultureInfo.CurrentCulture, $"    标记 {flags}");
        }

        if (result.Reason is not IneligibleReason.None
            || result.Severity is not EligibilitySeverity.Allowed)
        {
            output.AppendLine(CultureInfo.CurrentCulture, $"    原因 {result.Explanation}");

            if (!string.IsNullOrWhiteSpace(result.Suggestion))
            {
                output.AppendLine(CultureInfo.CurrentCulture, $"    建议 {result.Suggestion}");
            }

            if (!string.IsNullOrWhiteSpace(result.MatchedRuleId))
            {
                output.AppendLine(CultureInfo.CurrentCulture, $"    规则 {result.MatchedRuleId}");
            }
        }

        output.AppendLine();
    }

    /// <summary>按拒绝原因归类统计。用户一眼就能看出"绝大多数窗口是因为同一个原因被排除的"。</summary>
    private static void AppendSummary(StringBuilder output, List<DiagnosticRow> rows)
    {
        var blocked = rows
            .Where(r => !r.Result.IsEligible)
            .GroupBy(r => r.Result.Reason)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (blocked.Count == 0)
        {
            return;
        }

        output.AppendLine("不可分组原因汇总");
        foreach (var group in blocked)
        {
            output.AppendLine(CultureInfo.CurrentCulture, $"  {group.Count(),4}  {DescribeReason(group.Key)}");
        }
    }

    private static void WriteJson(SystemEnvironment environment, List<DiagnosticRow> rows)
    {
        var windows = new List<WindowReport>(rows.Count);

        foreach (var r in rows)
        {
            windows.Add(new WindowReport
            {
                Handle = r.Window.Identity.Handle.ToString("X", null),
                ProcessId = r.Window.Identity.ProcessId,
                Title = r.Window.Title,
                ProcessName = r.Window.ProcessName,
                ClassName = r.Window.ClassName,
                Bounds = new BoundsReport
                {
                    Left = r.Window.Bounds.Left,
                    Top = r.Window.Bounds.Top,
                    Width = r.Window.Bounds.Width,
                    Height = r.Window.Bounds.Height,
                },
                Dpi = r.Window.Dpi,
                Monitor = r.Window.MonitorDeviceName,
                IsCloaked = r.Window.IsCloaked,
                IsToolWindow = r.Window.IsToolWindow,
                HasOwner = r.Window.HasOwner,
                IsNotResponding = r.Window.IsNotResponding,
                IsUwp = r.Window.IsUwp,
                HasCustomTitleBar = r.Window.HasCustomTitleBar,
                IntegrityLevel = r.Window.IntegrityLevel.ToString(),
                Eligible = r.Result.IsEligible,
                Severity = r.Result.Severity.ToString(),
                Reason = r.Result.Reason.ToString(),
                Explanation = r.Result.Explanation,
                Suggestion = r.Result.Suggestion,
            });
        }

        var payload = new DiagnosticReport
        {
            Version = BuildInfo.DisplayVersion,
            Environment = new EnvironmentReport
            {
                DwmEnabled = environment.IsDwmEnabled,
                DragFullWindows = environment.IsDragFullWindowsEnabled,
                IntegrityLevel = environment.OwnIntegrityLevel.ToString(),
            },
            Windows = windows,
        };

        Console.WriteLine(JsonSerializer.Serialize(payload, DiagnosticJsonContext.Default.DiagnosticReport));
    }

    private static string DescribeFlags(WindowInfo window)
    {
        var flags = new List<string>(6);

        if (window.IsCloaked)
        {
            flags.Add("cloaked");
        }

        if (window.IsToolWindow)
        {
            flags.Add("工具窗");
        }

        if (window.HasOwner)
        {
            flags.Add("有属主");
        }

        if (window.IsNotResponding)
        {
            flags.Add("无响应");
        }

        if (window.IsUwp)
        {
            flags.Add("打包应用");
        }

        if (window.HasCustomTitleBar)
        {
            flags.Add("自绘标题栏");
        }

        if (window.IsTopmost)
        {
            flags.Add("置顶");
        }

        if (window.ShowState is not WindowShowState.Normal)
        {
            flags.Add(window.ShowState is WindowShowState.Maximized ? "最大化" : "最小化");
        }

        return string.Join("、", flags);
    }

    private static string DescribeReason(IneligibleReason reason) => reason switch
    {
        IneligibleReason.DwmDisabled => "桌面窗口管理器未启用",
        IneligibleReason.DragFullWindowsDisabled => "未启用拖动时显示窗口内容",
        IneligibleReason.NotResponding => "窗口无响应",
        IneligibleReason.Cloaked => "窗口被系统隐藏（其他虚拟桌面或已挂起）",
        IneligibleReason.ToolWindow => "工具窗口",
        IneligibleReason.NoTitle => "无标题",
        IneligibleReason.OwnedPopup => "从属窗口（对话框或浮层）",
        IneligibleReason.NotVisible => "不可见",
        IneligibleReason.TooSmall => "窗口过小",
        IneligibleReason.ShellWindow => "Windows 桌面自身的窗口",
        IneligibleReason.IntegrityLevelMismatch => "权限高于 TabNest（UIPI 限制）",
        IneligibleReason.OwnWindow => "TabNest 自己的窗口",
        IneligibleReason.BlockedByRule => "命中阻止规则",
        IneligibleReason.NotInAllowList => "不在允许列表中",
        IneligibleReason.AlreadyGrouped => "已在其他组中",
        IneligibleReason.TargetGroupLocked => "目标组已锁定",
        IneligibleReason.NativeTabbedApp => "应用已内建标签",
        _ => reason.ToString(),
    };

    private static string Mark(bool ok) => ok ? "已启用" : "未启用  ← 需要处理";

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "（无）";
        }

        return value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");
    }

    private sealed record DiagnosticRow(WindowInfo Window, EligibilityResult Result);
}
