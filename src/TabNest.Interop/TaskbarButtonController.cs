using System.Runtime.InteropServices;
using TabNest.Interop.Native;

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
    private static readonly Guid IidTaskbarList = new("56FDF342-FD6D-11D0-958A-006097C9A090");

    private ITaskbarList? _taskbar;
    private bool _initFailed;

    /// <summary>
    /// 被我们隐藏过按钮的窗口，以及隐藏当时它所属的进程 ID。
    /// 退出时必须全部还原，否则用户会留下再也找不回的窗口。
    ///
    /// 记进程 ID 是因为 <b>HWND 会被回收复用</b>：我们隐藏过的窗口销毁之后，
    /// 同一个句柄值可能已经属于另一个进程、甚至另一种窗口。还原时不核对就
    /// 等于对一个陌生窗口调 AddTab。
    /// </summary>
    private readonly Dictionary<nint, uint> _hidden = [];

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
                // 只还原，绝不凭空创造。
                //
                // AddTab 对一个本来没有任务栏按钮的窗口不是"无操作"，而是**给它造一个**。
                // 子窗口（典型如 Chrome Legacy Window：WS_CHILD、不可见、无 WS_EX_APPWINDOW）
                // 按系统规则永远不该出现在任务栏上，一旦被 AddTab，用户就会在
                // Chrome 图标下多出一个点进去什么都没有的幽灵条目 —— 而且它不属于
                // 任何分组，用户既关不掉也找不到来源。
                //
                // 走到这里的句柄有两种可能出错：一是上层把不该管的窗口传了下来；
                // 二是我们隐藏过的窗口已经销毁、句柄被回收给了别的窗口。
                // 两种都在这里挡住 —— 这是唯一的收口，比在每个调用方各判一次可靠。
                if (!CanOwnTaskbarButton(hwnd))
                {
                    _hidden.Remove(hwnd);
                    return false;
                }

                taskbar.AddTab(hwnd);
                _hidden.Remove(hwnd);
            }
            else
            {
                _ = User32.GetWindowThreadProcessId(hwnd, out var pid);
                taskbar.DeleteTab(hwnd);
                _hidden[hwnd] = pid;
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

        foreach (var (hwnd, pid) in _hidden.ToArray())
        {
            // 句柄仍然指向当初那个窗口吗？销毁后被回收复用的句柄不能还原 ——
            // 那会给一个我们从没碰过的窗口造出一个任务栏按钮。
            _ = User32.GetWindowThreadProcessId(hwnd, out var currentPid);

            if (currentPid != pid || !CanOwnTaskbarButton(hwnd))
            {
                continue;
            }

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

    /// <summary>
    /// 这个窗口按系统规则本来就该拥有任务栏按钮吗？
    ///
    /// 只认活着的顶层窗口。子窗口、已销毁的句柄一律拒绝 ——
    /// 对它们调 AddTab 只会凭空造出一个无法交互的幽灵条目。
    /// </summary>
    internal static bool CanOwnTaskbarButton(nint hwnd) =>
        hwnd != 0 && User32.IsWindow(hwnd) && WindowEnumerator.IsTopLevel(hwnd);

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
            // 走 CoCreateInstance 而非 Activator + GetTypeFromCLSID：
            // 后者经由反射解析类型，裁剪器无法静态分析（IL2072），会让整个应用无法裁剪。
            var hr = Ole32.CoCreateInstance(
                in ClsidTaskbarList,
                pUnkOuter: 0,
                Ole32.CLSCTX_INPROC_SERVER,
                in IidTaskbarList,
                out var native);

            if (hr < 0 || native == 0)
            {
                _initFailed = true;
                return null;
            }

            // 拿到原始接口指针后交给运行时包装。GetObjectForIUnknown 会自己 AddRef，
            // 因此必须释放 CoCreateInstance 给我们的那一次引用，否则对象永不析构。
            try
            {
                if (Marshal.GetObjectForIUnknown(native) is not ITaskbarList instance)
                {
                    _initFailed = true;
                    return null;
                }

                instance.HrInit();
                _taskbar = instance;
                return _taskbar;
            }
            finally
            {
                Marshal.Release(native);
            }
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
