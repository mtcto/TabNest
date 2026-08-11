using TabNest.Core.Models;
using TabNest.Core.Rules;

namespace TabNest.Core.Tests;

/// <summary>
/// 分组资格判定测试。
///
/// 除了判定本身，这里还锁定一条产品约定：**任何拒绝都必须给出原因**，
/// 且绝大多数拒绝要给出可操作建议。这是 TabNest 相对 Groupy 的核心差异化点，
/// 靠测试守住比靠自觉可靠。
/// </summary>
public sealed class EligibilityEvaluatorTests
{
    private static EligibilityContext Context(
        SystemEnvironment? env = null,
        IReadOnlyList<Rule>? rules = null,
        bool allowListOnly = false,
        IReadOnlySet<WindowIdentity>? grouped = null,
        IReadOnlySet<nint>? own = null,
        bool yieldToNativeTabs = true) => new()
        {
            Environment = env ?? SystemEnvironment.Healthy,
            Rules = rules ?? [],
            AllowListOnly = allowListOnly,
            GroupedWindows = grouped ?? new HashSet<WindowIdentity>(),
            OwnWindowHandles = own ?? new HashSet<nint>(),
            YieldToNativeTabs = yieldToNativeTabs,
        };

    [Fact]
    public void 正常窗口可分组()
    {
        var result = EligibilityEvaluator.Evaluate(TestData.Window(1), Context());

        Assert.True(result.IsEligible);
        Assert.Equal(IneligibleReason.None, result.Reason);
    }

    // ------------------------------------------------------------------
    // 系统级前置条件优先报告
    // ------------------------------------------------------------------

    [Fact]
    public void DWM未启用时拒绝并优先报告()
    {
        var window = TestData.Window(1, toolWindow: true, cloaked: true);
        var context = Context(env: new SystemEnvironment(false, true, IntegrityLevel.Medium));

        var result = EligibilityEvaluator.Evaluate(window, context);

        // 窗口自身也有问题，但系统级问题一改就全部恢复，必须先报最根本的那一层。
        Assert.Equal(IneligibleReason.DwmDisabled, result.Reason);
    }

    [Fact]
    public void 未启用拖动时显示窗口内容时拒绝()
    {
        var context = Context(env: new SystemEnvironment(true, false, IntegrityLevel.Medium));

        var result = EligibilityEvaluator.Evaluate(TestData.Window(1), context);

        Assert.Equal(IneligibleReason.DragFullWindowsDisabled, result.Reason);
        Assert.NotNull(result.Suggestion);
    }

    // ------------------------------------------------------------------
    // 窗口自身属性
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(true, false, false, false, IneligibleReason.Cloaked)]
    [InlineData(false, true, false, false, IneligibleReason.ToolWindow)]
    [InlineData(false, false, true, false, IneligibleReason.OwnedPopup)]
    [InlineData(false, false, false, true, IneligibleReason.NotResponding)]
    public void 窗口属性导致的拒绝(
        bool cloaked, bool toolWindow, bool hasOwner, bool notResponding, IneligibleReason expected)
    {
        var window = TestData.Window(
            1, cloaked: cloaked, toolWindow: toolWindow,
            hasOwner: hasOwner, notResponding: notResponding);

        Assert.Equal(expected, EligibilityEvaluator.Evaluate(window, Context()).Reason);
    }

    [Fact]
    public void 无标题窗口被拒绝()
    {
        var result = EligibilityEvaluator.Evaluate(TestData.Window(1, title: "   "), Context());

        Assert.Equal(IneligibleReason.NoTitle, result.Reason);
    }

    [Fact]
    public void 桌面与任务栏窗口被拒绝()
    {
        var window = TestData.Window(1, className: "Shell_TrayWnd");

        Assert.Equal(
            IneligibleReason.ShellWindow,
            EligibilityEvaluator.Evaluate(window, Context()).Reason);
    }

    [Fact]
    public void 桌面窗口报告为Shell窗口而非工具窗口()
    {
        // 实测发现的问题：桌面（Progman）同时也是工具窗口，若工具窗检查排在前面，
        // 就会报"这是工具窗口"—— 技术上没错，但对用户毫无帮助。更具体的原因必须优先。
        var desktop = TestData.Window(1, title: "Program Manager", className: "Progman", toolWindow: true);

        Assert.Equal(
            IneligibleReason.ShellWindow,
            EligibilityEvaluator.Evaluate(desktop, Context()).Reason);
    }

    [Fact]
    public void 过小的窗口被拒绝()
    {
        var window = TestData.Window(1, bounds: PixelRect.FromSize(0, 0, 40, 30));

        var result = EligibilityEvaluator.Evaluate(window, Context());

        Assert.Equal(IneligibleReason.TooSmall, result.Reason);
        Assert.Contains("40", result.Explanation);
    }

    // ------------------------------------------------------------------
    // 权限
    // ------------------------------------------------------------------

    [Fact]
    public void 提权窗口被拒绝且不诱导用户提权()
    {
        var window = TestData.Window(1, integrity: IntegrityLevel.High);

        var result = EligibilityEvaluator.Evaluate(window, Context());

        Assert.Equal(IneligibleReason.IntegrityLevelMismatch, result.Reason);
        Assert.Contains("UIPI", result.Explanation);
        Assert.Contains("不自动提权", result.Suggestion!);
    }

    [Fact]
    public void TabNest自己的窗口被排除()
    {
        var window = TestData.Window(1);
        var context = Context(own: new HashSet<nint> { window.Identity.Handle });

        Assert.Equal(
            IneligibleReason.OwnWindow,
            EligibilityEvaluator.Evaluate(window, context).Reason);
    }

    // ------------------------------------------------------------------
    // 原生标签让位
    // ------------------------------------------------------------------

    [Fact]
    public void 默认避让已有原生标签的应用()
    {
        var window = TestData.Window(1, processName: "explorer.exe");

        var result = EligibilityEvaluator.Evaluate(window, Context());

        Assert.Equal(IneligibleReason.NativeTabbedApp, result.Reason);
        Assert.Contains("双重标签", result.Explanation);
    }

    [Fact]
    public void 关闭避让后原生标签应用可分组()
    {
        var window = TestData.Window(1, processName: "explorer.exe");

        var result = EligibilityEvaluator.Evaluate(window, Context(yieldToNativeTabs: false));

        Assert.True(result.IsEligible);
    }

    // ------------------------------------------------------------------
    // 用户规则
    // ------------------------------------------------------------------

    [Fact]
    public void 命中阻止规则时给出规则ID以便跳转()
    {
        List<Rule> rules =
        [
            new()
            {
                Id = "rule-7",
                Name = "屏蔽密码管理器",
                Action = RuleAction.Block,
                Conditions = [new RuleCondition { ProcessName = "app.exe" }],
            },
        ];

        var result = EligibilityEvaluator.Evaluate(TestData.Window(1), Context(rules: rules));

        Assert.Equal(IneligibleReason.BlockedByRule, result.Reason);
        Assert.Equal("rule-7", result.MatchedRuleId);
        Assert.Contains("屏蔽密码管理器", result.Explanation);
    }

    [Fact]
    public void 仅允许指定应用模式下未列入的应用被拒绝()
    {
        var result = EligibilityEvaluator.Evaluate(
            TestData.Window(1, processName: "app.exe"),
            Context(allowListOnly: true));

        Assert.Equal(IneligibleReason.NotInAllowList, result.Reason);
        Assert.Contains("app.exe", result.Suggestion!);
    }

    [Fact]
    public void 仅允许指定应用模式下列入的应用可分组()
    {
        List<Rule> rules =
        [
            new()
            {
                Id = "allow-1",
                Name = "允许",
                Action = RuleAction.Allow,
                Conditions = [new RuleCondition { ProcessName = "app.exe" }],
            },
        ];

        var result = EligibilityEvaluator.Evaluate(
            TestData.Window(1),
            Context(rules: rules, allowListOnly: true));

        Assert.True(result.IsEligible);
    }

    [Fact]
    public void 阻止规则在仅允许模式下依然生效()
    {
        List<Rule> rules =
        [
            new()
            {
                Id = "allow-1",
                Name = "允许",
                Action = RuleAction.Allow,
                Conditions = [new RuleCondition { ProcessName = "app.exe" }],
            },
            new()
            {
                Id = "block-1",
                Name = "阻止",
                Action = RuleAction.Block,
                Conditions = [new RuleCondition { ProcessName = "app.exe" }],
            },
        ];

        var result = EligibilityEvaluator.Evaluate(
            TestData.Window(1),
            Context(rules: rules, allowListOnly: true));

        Assert.Equal(IneligibleReason.BlockedByRule, result.Reason);
    }

    // ------------------------------------------------------------------
    // 状态冲突
    // ------------------------------------------------------------------

    [Fact]
    public void 已分组的窗口被拒绝()
    {
        var window = TestData.Window(1);
        var context = Context(grouped: new HashSet<WindowIdentity> { window.Identity });

        var result = EligibilityEvaluator.Evaluate(window, context);

        Assert.Equal(IneligibleReason.AlreadyGrouped, result.Reason);
        Assert.NotNull(result.Suggestion);
    }

    // ------------------------------------------------------------------
    // 警告而非拒绝
    // ------------------------------------------------------------------

    [Fact]
    public void UWP窗口可分组但带警告()
    {
        var result = EligibilityEvaluator.Evaluate(TestData.Window(1, isUwp: true), Context());

        // 把警告当拒绝会让产品显得比实际更不兼容。
        Assert.True(result.IsEligible);
        Assert.Equal(EligibilitySeverity.AllowedWithWarning, result.Severity);
        Assert.NotNull(result.Suggestion);
    }

    // ------------------------------------------------------------------
    // 产品约定：拒绝必须可解释
    // ------------------------------------------------------------------

    [Fact]
    public void 任何拒绝都必须给出非空原因()
    {
        var cases = new (string Name, WindowInfo Window, EligibilityContext Context)[]
        {
            ("DWM 关闭", TestData.Window(1),
                Context(env: new SystemEnvironment(false, true, IntegrityLevel.Medium))),
            ("拖动内容关闭", TestData.Window(1),
                Context(env: new SystemEnvironment(true, false, IntegrityLevel.Medium))),
            ("cloaked", TestData.Window(1, cloaked: true), Context()),
            ("无标题", TestData.Window(1, title: ""), Context()),
            ("工具窗", TestData.Window(1, toolWindow: true), Context()),
            ("从属窗口", TestData.Window(1, hasOwner: true), Context()),
            ("Shell 窗口", TestData.Window(1, className: "Progman"), Context()),
            ("过小", TestData.Window(1, bounds: PixelRect.FromSize(0, 0, 10, 10)), Context()),
            ("无响应", TestData.Window(1, notResponding: true), Context()),
            ("提权", TestData.Window(1, integrity: IntegrityLevel.High), Context()),
            ("原生标签", TestData.Window(1, processName: "notepad.exe"), Context()),
            ("仅允许模式", TestData.Window(1), Context(allowListOnly: true)),
        };

        foreach (var (name, window, context) in cases)
        {
            var result = EligibilityEvaluator.Evaluate(window, context);

            Assert.False(result.IsEligible);
            Assert.False(
                string.IsNullOrWhiteSpace(result.Explanation),
                $"「{name}」的拒绝没有给出原因");
        }
    }
}
