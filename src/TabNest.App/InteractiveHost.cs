using System.Diagnostics;
using TabNest.Core.Diagnostics;
using TabNest.Core.Models;
using TabNest.Interop;

namespace TabNest.App;

/// <summary>
/// 常驻模式的宿主。
///
/// 这里刻意只使用 Win32 与领域层，不触碰任何 WPF 类型 ——
/// 只要常驻代码路径不引用 WPF，PresentationFramework 就不会被加载进内存。
/// 该约束由 --benchmark 自动校验。
/// </summary>
internal sealed class InteractiveHost : IDisposable
{
    private const uint MenuToggleEnabled = 1;
    private const uint MenuTaskbarShowAll = 2;
    private const uint MenuTaskbarActiveOnly = 3;
    private const uint MenuDissolveAll = 4;
    private const uint MenuOpenDataFolder = 5;
    private const uint MenuExit = 6;
    private const uint MenuSettings = 7;

    private readonly UiDispatcher _dispatcher;
    private readonly Orchestrator _orchestrator;
    private readonly TrayIcon _tray;

    /// <summary>
    /// 设置窗口按需创建。用户不打开设置，这段代码就一次都不执行，
    /// 常驻内存里也不会有它的任何痕迹。
    /// </summary>
    private Settings.SettingsWindow? _settingsWindow;

    private bool _disposed;

    private InteractiveHost()
    {
        _dispatcher = new UiDispatcher();
        _orchestrator = new Orchestrator(_dispatcher);

        _tray = new TrayIcon($"TabNest {BuildInfo.DisplayVersion}")
        {
            MenuProvider = BuildMenu,
        };

        _tray.MenuItemClicked += OnMenuItemClicked;
    }

    public static int Run()
    {
        AppPaths.EnsureCreated();
        FileLog.Initialize(AppPaths.LogDirectory);

        // 单实例：两个 TabNest 同时管理窗口会互相争抢焦点和位置，
        // 后果是用户窗口在两者之间来回跳。
        using var singleInstance = new Mutex(true, @"Local\TabNest.SingleInstance", out var isFirst);

        if (!isFirst)
        {
            FileLog.Warn("已有实例在运行，本次启动退出。");
            Console.Error.WriteLine("TabNest 已在运行。");
            return 1;
        }

        // 未捕获异常必须落盘。否则进程一声不响地消失，事后完全无法判断原因 ——
        // 这正是上一次退出无从追查的教训。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            FileLog.Error("未捕获异常导致进程终止。", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            FileLog.Error("后台任务异常未被观察。", e.Exception);
            e.SetObserved();
        };

        try
        {
            using var host = new InteractiveHost();
            host.Start();

            UiDispatcher.RunMessageLoop();

            FileLog.Info("消息循环结束，正常退出。");
            return 0;
        }
        catch (Exception ex)
        {
            FileLog.Error("启动或运行期间发生异常。", ex);
            throw;
        }
    }

    private void Start()
    {
        FileLog.Info(
            $"TabNest {BuildInfo.DisplayVersion} 已启动"
            + $"（PID {Environment.ProcessId}，数据目录 {AppPaths.Root}）。");

        UpdateTrayTooltip();
    }

    private IReadOnlyList<TrayMenuItem> BuildMenu()
    {
        var settings = _orchestrator.Settings;
        var groups = _orchestrator.GroupCount;

        return
        [
            // 标签直接写出点击后的动作，而不是写状态名。
            // 早期写作"启用 TabNest"配一个勾选标记，用户在已启用时点它以为是"确认启用"，
            // 实际是取消勾选把产品停掉了 —— 而且停用后所有拖拽都被静默拒绝，非常难察觉。
            new TrayMenuItem(
                MenuToggleEnabled,
                settings.Enabled ? "停用 TabNest（暂停接管窗口）" : "启用 TabNest",
                Checked: settings.Enabled),
            TrayMenuItem.Separator,

            new TrayMenuItem(
                MenuTaskbarShowAll,
                "任务栏：显示全部窗口",
                Checked: settings.TaskbarButtons is TaskbarButtonPolicy.ShowAll),
            new TrayMenuItem(
                MenuTaskbarActiveOnly,
                "任务栏：仅显示活动窗口",
                Checked: settings.TaskbarButtons is TaskbarButtonPolicy.ActiveOnly),
            TrayMenuItem.Separator,

            new TrayMenuItem(
                MenuDissolveAll,
                groups > 0 ? $"拆散全部标签组（{groups} 组）" : "拆散全部标签组",
                Enabled: groups > 0),
            TrayMenuItem.Separator,

            new TrayMenuItem(MenuSettings, "设置…"),
            new TrayMenuItem(MenuOpenDataFolder, "打开数据目录"),
            TrayMenuItem.Separator,

            new TrayMenuItem(MenuExit, "退出"),
        ];
    }

    private void OnMenuItemClicked(uint id)
    {
        switch (id)
        {
            case MenuToggleEnabled:
                _orchestrator.SetEnabled(!_orchestrator.IsEnabled);
                UpdateTrayTooltip();
                break;

            case MenuTaskbarShowAll:
                _orchestrator.SetTaskbarPolicy(TaskbarButtonPolicy.ShowAll);
                break;

            case MenuTaskbarActiveOnly:
                _orchestrator.SetTaskbarPolicy(TaskbarButtonPolicy.ActiveOnly);
                break;

            case MenuDissolveAll:
                _orchestrator.DissolveEverything();
                UpdateTrayTooltip();
                break;

            case MenuSettings:
                OpenSettings();
                break;

            case MenuOpenDataFolder:
                OpenDataFolder();
                break;

            case MenuExit:
                RequestExit();
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// 打开设置中心。
    ///
    /// 窗口只创建一次，关闭时隐藏而非销毁 —— 设置会被反复打开，
    /// 每次重建既慢又会丢掉用户当前所在的页面与滚动位置。
    /// </summary>
    private void OpenSettings()
    {
        try
        {
            if (_settingsWindow is null || !_settingsWindow.IsCreated)
            {
                _settingsWindow = new Settings.SettingsWindow(
                    _orchestrator.Settings,
                    OnSettingsChanged);
            }
            else
            {
                // 已存在的窗口要同步一次：托盘菜单也能改主开关和任务栏策略，
                // 不同步的话打开后看到的是过期状态。
                _settingsWindow.UpdateSettings(_orchestrator.Settings);
            }

            _settingsWindow.ShowAndActivate();
        }
        catch (Exception ex)
        {
            // 设置窗口打不开绝不能拖垮常驻进程 —— 用户还要靠托盘退出。
            FileLog.Error("打开设置中心失败。", ex);
        }
    }

    private void OnSettingsChanged(Settings.SettingsChanged change)
    {
        _orchestrator.ApplySettings(change.Settings);
        UpdateTrayTooltip();
    }

    private void UpdateTrayTooltip()
    {
        var state = _orchestrator.IsEnabled ? "已启用" : "已停用";
        _tray.UpdateTooltip($"TabNest {BuildInfo.DisplayVersion} · {state}");
    }

    private static void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.Root) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Debug.WriteLine($"[TabNest] 打开数据目录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 安全退出：先拆散所有组并还原窗口，再停止消息循环。
    /// 顺序不能反 —— 消息循环一停，轨道窗口和还原指令就再也没机会执行了。
    /// </summary>
    private void RequestExit()
    {
        FileLog.Info("用户从托盘菜单请求退出。");
        _orchestrator.DissolveEverything();
        _dispatcher.RequestShutdown();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Orchestrator 的 Dispose 里会再次还原窗口并卸载 hook，
        // 即使进程是被异常路径拖到这里的，外部窗口也不会留在被接管的状态。
        _settingsWindow?.Dispose();
        _orchestrator.Dispose();
        _tray.Dispose();
        _dispatcher.Dispose();
    }
}
