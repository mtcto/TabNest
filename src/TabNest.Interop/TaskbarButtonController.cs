using System.Runtime.InteropServices;

namespace TabNest.Interop;

/// <summary>
/// 控制窗口在任务栏上的按钮。
///
/// 用 <c>ITaskbarList::AddTab</c>/<c>DeleteTab</c> —— shell COM 接口，对任意进程的顶层 HWND 有效，
/// 不修改目标窗口的任何样式，完全可逆。
///
/// 刻意**不用** <c>WS_EX_TOOLWINDOW</c> 达成同样效果：那需要改外部窗口的扩展样式，
/// 且必须隐藏重显才能生效，破坏性高得多。
///
/// COM 对象有线程亲和性，必须始终在创建它的 STA 线程上使用 —— 即 WindowController 的工作线程。
/// </summary>
internal sealed class TaskbarButtonController : IDisposable
{
    private static readonly Guid ClsidTaskbarList = new("56FDF344-FD6D-11D0-958A-006097C9A090");

    private ITaskbarList? _taskbar;
    private bool _initFailed;

    /// <summary>记录被我们隐藏过按钮的窗口，退出时必须全部还原，否则用户会留下再也找不回的窗口。</summary>
    private readonly HashSet<nint> _hidden = [];

    public bool SetVisible(nint hwnd, bool visible)
    {
        var taskbar = EnsureTaskbar();
        if (taskbar is null)
        {
            return false;
        }

        try
        {
            if (visible)
            {
                taskbar.AddTab(hwnd);
                _hidden.Remove(hwnd);
            }
            else
            {
                taskbar.DeleteTab(hwnd);
                _hidden.Add(hwnd);
            }

            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    /// <summary>还原所有被隐藏的任务栏按钮。退出、停用主开关、崩溃恢复时都必须调用。</summary>
    public void RestoreAll()
    {
        if (_taskbar is null || _hidden.Count == 0)
        {
            return;
        }

        foreach (var hwnd in _hidden.ToArray())
        {
            try
            {
                _taskbar.AddTab(hwnd);
            }
            catch (COMException)
            {
                // 窗口可能已经销毁，忽略。
            }
        }

        _hidden.Clear();
    }

    private ITaskbarList? EnsureTaskbar()
    {
        if (_taskbar is not null)
        {
            return _taskbar;
        }

        if (_initFailed)
        {
            return null;
        }

        try
        {
            var type = Type.GetTypeFromCLSID(ClsidTaskbarList, throwOnError: false);
            if (type is null || Activator.CreateInstance(type) is not ITaskbarList instance)
            {
                _initFailed = true;
                return null;
            }

            instance.HrInit();
            _taskbar = instance;
            return _taskbar;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            // 任务栏按钮策略是增强功能，拿不到接口时降级为"保留所有按钮"，
            // 不该让整个产品启动失败。
            _initFailed = true;
            return null;
        }
    }

    public void Dispose()
    {
        RestoreAll();

        if (_taskbar is not null)
        {
            Marshal.FinalReleaseComObject(_taskbar);
            _taskbar = null;
        }
    }

    [ComImport]
    [Guid("56FDF342-FD6D-11D0-958A-006097C9A090")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        void HrInit();

        void AddTab(nint hwnd);

        void DeleteTab(nint hwnd);

        void ActivateTab(nint hwnd);

        void SetActiveAlt(nint hwnd);
    }
}
