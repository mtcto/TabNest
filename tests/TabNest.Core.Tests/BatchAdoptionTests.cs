using TabNest.Core.Grouping;
using TabNest.Core.Models;

namespace TabNest.Core.Tests;

/// <summary>
/// 批量收编的选窗规则。
///
/// 这类逻辑的正确性几乎全在边界条件上，而手工验证需要同时开十几个真实窗口
/// 才能覆盖一条分支，代价高且不可重复。放在这里逐条钉死。
/// </summary>
public sealed class BatchAdoptionTests
{
    private static readonly Func<WindowIdentity, bool> NoneGrouped = _ => false;
    private static readonly Func<WindowInfo, string?> AllEligible = _ => null;

    private static AdoptionPlan Plan(
        WindowInfo anchor,
        IReadOnlyList<WindowInfo> candidates,
        AdoptionScope scope,
        Func<WindowIdentity, bool>? grouped = null,
        Func<WindowInfo, string?>? eligible = null) =>
        BatchAdoption.Plan(
            anchor, candidates, scope,
            grouped ?? NoneGrouped,
            eligible ?? AllEligible);

    [Fact]
    public void 基准窗口始终入选且排在第一位()
    {
        var anchor = TestData.Window(1, processName: "idea64.exe");
        var other = TestData.Window(2, processName: "idea64.exe");

        var plan = Plan(anchor, [other, anchor], AdoptionScope.SameApplication);

        Assert.Equal(2, plan.Windows.Count);
        Assert.Equal(anchor.Identity, plan.Windows[0].Identity);
    }

    [Fact]
    public void 同应用范围按进程名匹配而非进程号()
    {
        // Chrome、Edge 这类多进程应用的窗口分属不同 PID，
        // 用 PID 比较会把同一个应用判成多个应用，"收编该应用全部窗口"就永远只收到一个。
        var anchor = TestData.Window(1, processName: "chrome.exe", pid: 100);
        var sameApp = TestData.Window(2, processName: "chrome.exe", pid: 200);
        var otherApp = TestData.Window(3, processName: "notepad.exe", pid: 300);

        var plan = Plan(anchor, [sameApp, otherApp], AdoptionScope.SameApplication);

        Assert.Equal(2, plan.Windows.Count);
        Assert.DoesNotContain(plan.Windows, w => w.ProcessName == "notepad.exe");
    }

    [Fact]
    public void 进程名比较忽略大小写()
    {
        var anchor = TestData.Window(1, processName: "Chrome.exe");
        var other = TestData.Window(2, processName: "chrome.exe");

        var plan = Plan(anchor, [other], AdoptionScope.SameApplication);

        Assert.Equal(2, plan.Windows.Count);
    }

    [Fact]
    public void 限定显示器时排除其他显示器上的同应用窗口()
    {
        var anchor = TestData.Window(1, processName: "idea64.exe", monitor: @"\\.\DISPLAY1");
        var same = TestData.Window(2, processName: "idea64.exe", monitor: @"\\.\DISPLAY1");
        var elsewhere = TestData.Window(3, processName: "idea64.exe", monitor: @"\\.\DISPLAY2");

        var plan = Plan(anchor, [same, elsewhere], AdoptionScope.SameApplicationOnMonitor);

        Assert.Equal(2, plan.Windows.Count);
        Assert.DoesNotContain(plan.Windows, w => w.Identity == elsewhere.Identity);
    }

    [Fact]
    public void 不限定显示器时同应用跨屏窗口也会被收编()
    {
        var anchor = TestData.Window(1, processName: "idea64.exe", monitor: @"\\.\DISPLAY1");
        var elsewhere = TestData.Window(2, processName: "idea64.exe", monitor: @"\\.\DISPLAY2");

        var plan = Plan(anchor, [elsewhere], AdoptionScope.SameApplication);

        Assert.Equal(2, plan.Windows.Count);
    }

    [Fact]
    public void 窗口类范围比进程名更精确()
    {
        // 同一个应用的主窗口与工具窗常有不同的窗口类，
        // 用户选"收编同类窗口"时不希望把设置面板也拽进来。
        var anchor = TestData.Window(1, processName: "app.exe", className: "MainWindow");
        var sameClass = TestData.Window(2, processName: "app.exe", className: "MainWindow");
        var otherClass = TestData.Window(3, processName: "app.exe", className: "PrefsWindow");

        var plan = Plan(anchor, [sameClass, otherClass], AdoptionScope.SameWindowClass);

        Assert.Equal(2, plan.Windows.Count);
        Assert.DoesNotContain(plan.Windows, w => w.ClassName == "PrefsWindow");
    }

    [Fact]
    public void 本显示器范围收编不同应用的窗口()
    {
        var anchor = TestData.Window(1, processName: "idea64.exe", monitor: @"\\.\DISPLAY1");
        var otherApp = TestData.Window(2, processName: "chrome.exe", monitor: @"\\.\DISPLAY1");
        var elsewhere = TestData.Window(3, processName: "chrome.exe", monitor: @"\\.\DISPLAY2");

        var plan = Plan(anchor, [otherApp, elsewhere], AdoptionScope.SameMonitor);

        Assert.Equal(2, plan.Windows.Count);
        Assert.Contains(plan.Windows, w => w.ProcessName == "chrome.exe");
        Assert.DoesNotContain(plan.Windows, w => w.Identity == elsewhere.Identity);
    }

    [Fact]
    public void 已在其他组里的窗口被静默跳过而不列为被排除()
    {
        // 用户选的是"收编未分组窗口"，把已分组的列进"被跳过"只是噪声。
        var anchor = TestData.Window(1, processName: "idea64.exe");
        var grouped = TestData.Window(2, processName: "idea64.exe");

        var plan = Plan(
            anchor, [grouped], AdoptionScope.SameApplication,
            grouped: id => id == grouped.Identity);

        Assert.Single(plan.Windows);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void 不可分组的窗口被列入被排除并带上原因()
    {
        var anchor = TestData.Window(1, processName: "idea64.exe");
        var blocked = TestData.Window(2, processName: "idea64.exe", title: "无响应窗口");

        var plan = Plan(
            anchor, [blocked], AdoptionScope.SameApplication,
            eligible: w => w.Title == "无响应窗口" ? "窗口无响应" : null);

        Assert.Single(plan.Windows);
        Assert.Single(plan.Skipped);
        Assert.Equal("窗口无响应", plan.Skipped[0].Reason);
    }

    [Fact]
    public void 基准窗口在候选列表里重复出现也只入选一次()
    {
        var anchor = TestData.Window(1, processName: "idea64.exe");

        var plan = Plan(anchor, [anchor, anchor], AdoptionScope.SameApplication);

        Assert.Single(plan.Windows);
    }

    [Fact]
    public void 没有同类窗口时只剩基准窗口而不会误收编()
    {
        var anchor = TestData.Window(1, processName: "idea64.exe");
        var unrelated = TestData.Window(2, processName: "notepad.exe");

        var plan = Plan(anchor, [unrelated], AdoptionScope.SameApplication);

        Assert.Single(plan.Windows);
    }

    [Fact]
    public void 进程名为空时不与其他空进程名窗口误匹配()
    {
        // 取不到进程名的窗口若被当成"同一应用"，会把一批毫不相干的窗口凑成一组。
        var anchor = TestData.Window(1, processName: "");
        var other = TestData.Window(2, processName: "");

        var plan = Plan(anchor, [other], AdoptionScope.SameApplication);

        Assert.Single(plan.Windows);
    }

    [Fact]
    public void 显示器名为空时不与其他空显示器名窗口误匹配()
    {
        var anchor = TestData.Window(1, monitor: "");
        var other = TestData.Window(2, monitor: "");

        var plan = Plan(anchor, [other], AdoptionScope.SameMonitor);

        Assert.Single(plan.Windows);
    }

    [Fact]
    public void 菜单文案带上实际数量()
    {
        var anchor = TestData.Window(1, processName: "idea64.exe");

        var text = BatchAdoption.DescribeScope(AdoptionScope.SameApplication, anchor, 5);

        Assert.Contains("idea64.exe", text, StringComparison.Ordinal);
        Assert.Contains("5", text, StringComparison.Ordinal);
    }
}
