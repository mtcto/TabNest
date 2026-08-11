using System.Diagnostics;
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

    private readonly UiDispatcher _dispatcher;
    private readonly Orchestrator _orchestrator;
    private readonly TrayIcon _tray;
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
        // 单实例：两个 TabNest 同时管理窗口会互相争抢焦点和位置，
        // 后果是用户窗口在两者之间来回跳。
        using var singleInstance = new Mutex(true, @"Local\TabNest.SingleInstance", out var isFirst);

        if (!isFirst)
        {
            Console.Error.WriteLine("TabNest 已在运行。");
            return 1;
        }

        using var host = new InteractiveHost();
        host.Start();

        UiDispatcher.RunMessageLoop();

        return 0;
    }

    private void Start()
    {
        Debug.WriteLine($"[TabNest] 已启动，数据目录 {AppPaths.Root}");
        UpdateTrayTooltip();
    }

    private IReadOnlyList<TrayMenuItem> BuildMenu()
    {
        var settings = _orchestrator.Settings;
        var groups = _orchestrator.GroupCount;

        return
        [
            new TrayMenuItem(MenuToggleEnabled, "启用 TabNest", Checked: settings.Enabled),
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
        _orchestrator.Dispose();
        _tray.Dispose();
        _dispatcher.Dispose();
    }
}
