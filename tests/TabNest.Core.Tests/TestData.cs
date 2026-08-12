using TabNest.Core.Grouping;
using TabNest.Core.Models;

namespace TabNest.Core.Tests;

/// <summary>构造测试用领域对象。默认值一律取"健康且可分组"，测试只需覆盖关心的那一项。</summary>
internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

    public static WindowIdentity Id(int handle, int pid = 1000, long startTicks = 42) =>
        new(handle, pid, startTicks);

    public static WindowInfo Window(
        int handle,
        string title = "窗口",
        string processName = "app.exe",
        string className = "AppWindow",
        PixelRect? bounds = null,
        IntegrityLevel integrity = IntegrityLevel.Medium,
        bool cloaked = false,
        bool toolWindow = false,
        bool hasOwner = false,
        bool notResponding = false,
        bool isUwp = false,
        int pid = 1000,
        string monitor = @"\\.\DISPLAY1") => new()
        {
            Identity = Id(handle, pid),
            Title = title,
            ClassName = className,
            ProcessName = processName,
            ProcessPath = $@"C:\Apps\{processName}",
            Bounds = bounds ?? PixelRect.FromSize(100, 100, 800, 600),
            ShowState = WindowShowState.Normal,
            IntegrityLevel = integrity,
            Dpi = 96,
            MonitorDeviceName = monitor,
            IsCloaked = cloaked,
            IsTopmost = false,
            IsToolWindow = toolWindow,
            HasOwner = hasOwner,
            IsNotResponding = notResponding,
            IsUwp = isUwp,
            HasCustomTitleBar = false,
            ObservedAt = Now,
        };

    public static WindowSnapshot Snapshot(PixelRect? restore = null, bool restorable = true) => new()
    {
        RestoreBounds = restorable
            ? restore ?? PixelRect.FromSize(100, 100, 800, 600)
            : PixelRect.Empty,
        CurrentBounds = restore ?? PixelRect.FromSize(100, 100, 800, 600),
        ShowState = restorable ? WindowShowState.Normal : WindowShowState.Unknown,
        IsTopmost = false,
        MonitorDeviceName = @"\\.\DISPLAY1",
        Dpi = 96,
        WasInTaskbar = true,
        CapturedAt = Now,
    };

    public static TabCandidate Candidate(
        int handle,
        string title = "窗口",
        string processName = "app.exe") =>
        new(Window(handle, title, processName), Snapshot());

    /// <summary>创建一个含 <paramref name="count"/> 个成员的组，返回管理器与组 ID。</summary>
    public static (GroupSessionManager Manager, string GroupId) GroupOf(int count)
    {
        var manager = new GroupSessionManager();
        var members = new List<TabCandidate>();
        for (var i = 0; i < count; i++)
        {
            members.Add(Candidate(i + 1, $"窗口{i + 1}"));
        }

        var result = manager.CreateGroup(members);
        Assert.True(result.Success, result.Message);
        return (manager, result.Group!.Id);
    }
}
