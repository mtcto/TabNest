using System.Runtime.InteropServices;
using TabNest.Core.Diagnostics;
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
///
/// 接口调用手写 vtable 而不用 [ComImport]：裁剪会关掉内置 COM 互操作，
/// 而这里曾经就是那样在每个发布版里静默失效的，详见 <see cref="ComObject"/>。
/// </summary>
internal sealed unsafe class TaskbarButtonController : IDisposable
{
    private static readonly Guid ClsidTaskbarList = new("56FDF344-FD6D-11D0-958A-006097C9A090");
    private static readonly Guid IidTaskbarList = new("56FDF342-FD6D-11D0-958A-006097C9A090");

    // ITaskbarList vtable：0-2 IUnknown，3 HrInit，4 AddTab，5 DeleteTab，6 ActivateTab，7 SetActiveAlt。
    private const int HrInitSlot = 3;
    private const int AddTabSlot = 4;
    private const int DeleteTabSlot = 5;

    private nint _taskbar;
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

    /// <summary>
    /// 任务栏接口本身是否可用。
    ///
    /// 用来把"拒绝操作这个窗口"和"整个策略压根没在工作"区分开 ——
    /// 两者都表现为操作没发生，但前者是正确行为，后者是必须暴露的缺陷。
    /// </summary>
    public bool IsAvailable => EnsureTaskbar() != 0;

    public bool SetVisible(nint hwnd, bool visible)
    {
        var taskbar = EnsureTaskbar();
        if (taskbar == 0)
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

                Invoke(taskbar, AddTabSlot, hwnd);
                _hidden.Remove(hwnd);
            }
            else
            {
                _ = User32.GetWindowThreadProcessId(hwnd, out var pid);
                Invoke(taskbar, DeleteTabSlot, hwnd);
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
        if (_taskbar == 0 || _hidden.Count == 0)
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

            // 窗口可能已经销毁，AddTab 失败无所谓，忽略返回值。
            Invoke(_taskbar, AddTabSlot, hwnd);
        }

        _hidden.Clear();
    }

    /// <summary>调 ITaskbarList 上一个只收 HWND 的方法。</summary>
    private static int Invoke(nint taskbar, int slot, nint hwnd) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)ComObject.Method(taskbar, slot))(taskbar, hwnd);

    /// <summary>
    /// 这个窗口按系统规则本来就该拥有任务栏按钮吗？
    ///
    /// 只认活着的顶层窗口。子窗口、已销毁的句柄一律拒绝 ——
    /// 对它们调 AddTab 只会凭空造出一个无法交互的幽灵条目。
    /// </summary>
    internal static bool CanOwnTaskbarButton(nint hwnd) =>
        hwnd != 0 && User32.IsWindow(hwnd) && WindowEnumerator.IsTopLevel(hwnd);

    private nint EnsureTaskbar()
    {
        if (_taskbar != 0)
        {
            return _taskbar;
        }

        if (_initFailed)
        {
            return 0;
        }

        // 走 CoCreateInstance 而非 Activator + GetTypeFromCLSID：
        // 后者经由反射解析类型，裁剪器无法静态分析（IL2072），会让整个应用无法裁剪。
        var hr = Ole32.CoCreateInstance(
            in ClsidTaskbarList,
            pUnkOuter: 0,
            Ole32.CLSCTX_INPROC_SERVER,
            in IidTaskbarList,
            out var instance);

        if (hr < 0 || instance == 0)
        {
            // 任务栏按钮策略是增强功能，拿不到接口时降级为"保留所有按钮"，
            // 不该让整个产品启动失败 —— 但**必须留下痕迹**。
            //
            // 这里曾经是静默失败：用 [ComImport] 时裁剪发布版里内置 COM 被关掉，
            // 整个策略在每个发布版里都不工作，而日志里一个字都没有，
            // 从外面完全看不出来。降级可以，无声降级不行。
            FileLog.Warn($"拿不到 ITaskbarList（hr=0x{hr:X8}），任务栏按钮策略本次不可用。");
            _initFailed = true;
            return 0;
        }

        var init = ((delegate* unmanaged[Stdcall]<nint, int>)
            ComObject.Method(instance, HrInitSlot))(instance);

        if (init < 0)
        {
            FileLog.Warn($"ITaskbarList::HrInit 失败（hr=0x{init:X8}），任务栏按钮策略本次不可用。");
            ComObject.Release(instance);
            _initFailed = true;
            return 0;
        }

        _taskbar = instance;
        return _taskbar;
    }

    public void Dispose()
    {
        RestoreAll();

        if (_taskbar != 0)
        {
            ComObject.Release(_taskbar);
            _taskbar = 0;
        }
    }
}
