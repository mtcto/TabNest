using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TabNest.Core.Grouping;
using TabNest.Core.Models;
using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// 执行窗口指令的单一串行队列。
///
/// 所有跨进程窗口操作都必须经过这里（计划书 §7.3）。原因：
///   1. 并发操作外部窗口会互相干扰 —— 焦点争抢、Z 序反复重排；
///   2. 汇聚成单点后才能统一限流、超时、记录和统计；
///   3. 工作线程是 STA，可以安全持有 ITaskbarList 这类有线程亲和性的 COM 对象。
///
/// 队列线程绝不阻塞在目标进程上：所有同步消息走 <c>SendMessageTimeoutW</c>，
/// 操作前用 <c>IsHungAppWindow</c> 预检。一个卡死的应用不得冻结整个产品。
/// </summary>
public sealed class WindowController : IDisposable
{
    /// <summary>
    /// 等待前台切换生效的时长。
    ///
    /// 刻意取得较短：降级链最多走四级，每级都等 60ms 的话，一次失败的切换就要 240ms，
    /// 远超 100ms 的 P95 目标。前台切换若能成功，通常在十几毫秒内就完成了。
    /// </summary>
    private static readonly TimeSpan ForegroundVerifyTimeout = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan ForegroundPollInterval = TimeSpan.FromMilliseconds(4);

    /// <summary>发送 WM_CLOSE 的超时。目标可能正在弹保存提示，不能无限等。</summary>
    private const uint CloseMessageTimeoutMs = 2000;

    /// <summary>窗口不可见边框的厚度。只随样式与 DPI 变化，缓存以避免拖动时每帧查 DWM。</summary>
    private readonly Dictionary<nint, BorderPadding> _borderPadding = [];

    private readonly BlockingCollection<WorkItem> _queue = new(new ConcurrentQueue<WorkItem>());
    private readonly Thread _worker;
    private readonly TaskbarButtonController _taskbar = new();
    private volatile bool _disposed;

    public WindowController()
    {
        _worker = new Thread(Run)
        {
            Name = "TabNest.WindowController",
            IsBackground = true,
        };

        // STA：ITaskbarList 等 shell COM 对象要求单线程套间。
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    /// <summary>把一批指令按顺序排入队列并等待全部完成。</summary>
    public Task<IReadOnlyList<OperationResult>> ExecuteAsync(
        IEnumerable<WindowAction> actions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actions);

        var list = actions as IList<WindowAction> ?? [.. actions];
        if (list.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<OperationResult>>([]);
        }

        var item = new WorkItem(list, cancellationToken);
        if (_disposed || !TryQueue(item))
        {
            return Task.FromResult<IReadOnlyList<OperationResult>>(
                [.. list.Select(_ => OperationResult.Fail("窗口控制队列已关闭。"))]);
        }

        return item.Completion.Task;
    }

    public Task<IReadOnlyList<OperationResult>> ExecuteAsync(
        WindowAction action,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync([action], cancellationToken);

    /// <summary>
    /// 采集窗口的原始状态。
    ///
    /// 只读操作，不排队 —— 它必须能在指令执行**之前**同步完成：
    /// 先写快照再动窗口，顺序反了就会出现"已经移动了窗口但还没记下原位置"的窗口期，
    /// 此时崩溃将导致用户窗口永久错位。
    /// </summary>
    public static WindowSnapshot? CaptureSnapshot(nint hwnd)
    {
        if (!User32.IsWindow(hwnd))
        {
            return null;
        }

        var placement = WINDOWPLACEMENT.Create();
        if (!User32.GetWindowPlacement(hwnd, ref placement))
        {
            return null;
        }

        var exStyle = (long)User32.GetWindowLongPtr(hwnd, User32.GWL_EXSTYLE);
        var monitor = MonitorLookup.ForWindow(hwnd);

        return new WindowSnapshot
        {
            RestoreBounds = placement.rcNormalPosition.ToPixelRect(),
            CurrentBounds = WindowEnumerator.ReadVisibleBounds(hwnd),
            ShowState = placement.showCmd switch
            {
                User32.SW_SHOWMINIMIZED or User32.SW_MINIMIZE => WindowShowState.Minimized,
                User32.SW_SHOWMAXIMIZED => WindowShowState.Maximized,
                _ => WindowShowState.Normal,
            },
            IsTopmost = (exStyle & User32.WS_EX_TOPMOST) != 0,
            MonitorDeviceName = monitor.DeviceName,
            Dpi = MonitorLookup.DpiForWindow(hwnd),
            WasInTaskbar = (exStyle & User32.WS_EX_TOOLWINDOW) == 0,
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    // ------------------------------------------------------------------
    // 工作线程
    // ------------------------------------------------------------------

    private bool TryQueue(WorkItem item)
    {
        try
        {
            _queue.Add(item);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    private void Run()
    {
        // 强制为本线程创建输入队列。
        // AttachThreadInput 要求参与的两个线程都有输入队列，而工作线程的队列是惰性创建的 ——
        // 不做这一步，焦点降级链的第二级会永远失败，实测表现为 100% 无法转移焦点。
        User32.PeekMessage(out _, 0, 0, 0, User32.PM_NOREMOVE);

        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                var results = new List<OperationResult>(item.Actions.Count);

                foreach (var action in item.Actions)
                {
                    if (item.Cancellation.IsCancellationRequested)
                    {
                        results.Add(OperationResult.Fail("操作已取消。"));
                        continue;
                    }

                    results.Add(Execute(action));
                }

                item.Completion.TrySetResult(results);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TabNest] 窗口控制队列异常终止：{ex}");
        }
        finally
        {
            // 无论如何退出，都要把隐藏过的任务栏按钮还回去。
            _taskbar.Dispose();
        }
    }

    private OperationResult Execute(WindowAction action)
    {
        var hwnd = action.Target.Handle;

        if (!User32.IsWindow(hwnd))
        {
            return OperationResult.Fail("窗口已不存在。");
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            return action switch
            {
                ActivateWindowAction => Activate(hwnd, stopwatch),
                AlignWindowAction align => AlignTo(hwnd, align.Bounds, stopwatch),
                LowerWindowAction => Lower(hwnd, stopwatch),
                RestoreWindowAction restore => Restore(hwnd, restore.Snapshot, stopwatch),
                SetTaskbarButtonAction taskbar => SetTaskbarButton(hwnd, taskbar.Visible, stopwatch),
                RestoreWindowCornersAction => RestoreCorners(hwnd, stopwatch),
                CloseWindowAction => Close(hwnd, stopwatch),
                _ => OperationResult.Fail($"未知指令类型 {action.GetType().Name}。"),
            };
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            return OperationResult.Fail($"执行窗口指令时发生异常：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 四级焦点降级链
    // ------------------------------------------------------------------

    /// <summary>
    /// 激活窗口并转移键盘焦点。
    ///
    /// Windows 的前台锁定机制会让非前台进程的 <c>SetForegroundWindow</c> 静默失败
    /// （只闪任务栏图标）。Groupy 不受此限制是因为它的代码注入在目标进程内、
    /// 天然持有前台权限；TabNest 阶段一在进程外，必须逐级降级。
    ///
    /// 每一级之后都用 <c>GetForegroundWindow</c> 核验实际结果，而不是相信返回值。
    /// </summary>
    private static OperationResult Activate(nint hwnd, Stopwatch stopwatch)
    {
        // 假死窗口不参与激活：等它是没有意义的，只会拖慢队列。
        if (User32.IsHungAppWindow(hwnd))
        {
            return OperationResult.Fail("目标窗口无响应，已跳过激活。");
        }

        // 最小化的窗口必须先恢复，否则前台切换没有意义。
        if (User32.IsIconic(hwnd))
        {
            User32.ShowWindow(hwnd, User32.SW_RESTORE);
        }

        // 第一级：直接请求。TabNest 自身是前台窗口时（用户刚点了标签）通常能成功。
        User32.SetForegroundWindow(hwnd);
        if (VerifyForeground(hwnd))
        {
            return Done(ActivationLevel.Direct, stopwatch);
        }

        // 第二级：把自己的输入队列附加到当前前台线程，借用它的前台权限。
        if (TryAttachAndActivate(hwnd) && VerifyForeground(hwnd))
        {
            return Done(ActivationLevel.AttachedThreadInput, stopwatch);
        }

        // 第三级：兜底激活。行为等价于用户按 Alt+Tab 选中该窗口。
        User32.SwitchToThisWindow(hwnd, true);
        if (VerifyForeground(hwnd))
        {
            return Done(ActivationLevel.SwitchToThisWindow, stopwatch);
        }

        // 第四级：焦点转移不了，至少把窗口抬到 Z 序顶部让用户看得见。
        var raised = User32.SetWindowPos(
            hwnd, User32.HWND_TOP, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        User32.BringWindowToTop(hwnd);

        stopwatch.Stop();

        if (raised)
        {
            // 这是**降级成功**而非失败：目标窗口已经可见并置于最前，
            // 只是键盘焦点还在别处，用户点一下即可。把它报成彻底失败并不准确。
            return new OperationResult
            {
                Success = true,
                Message = "窗口已置于最前，但键盘焦点未能转移。"
                    + "这通常是因为 TabNest 当前不持有前台权限（系统前台锁定机制所致）。",
                ActivationLevel = ActivationLevel.RaisedOnly,
                Elapsed = stopwatch.Elapsed,
            };
        }

        // 连抬到最前都做不到，才是真正的失败。
        return new OperationResult
        {
            Success = false,
            Message = "无法激活目标窗口：焦点转移与置顶均失败。"
                + "可能是目标应用拒绝激活，或权限不足。",
            ActivationLevel = ActivationLevel.Failed,
            Elapsed = stopwatch.Elapsed,
        };
    }

    /// <summary>
    /// 附加到当前前台窗口的线程输入队列后再请求前台。
    /// 无论中途是否失败，都必须分离 —— 保持附加状态会让两个线程的输入处理长期纠缠在一起。
    /// </summary>
    private static bool TryAttachAndActivate(nint hwnd)
    {
        var foreground = User32.GetForegroundWindow();
        if (foreground == 0 || foreground == hwnd)
        {
            return false;
        }

        var foregroundThread = User32.GetWindowThreadProcessId(foreground, out _);
        var currentThread = Kernel32.GetCurrentThreadId();

        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            return false;
        }

        if (!User32.AttachThreadInput(currentThread, foregroundThread, true))
        {
            return false;
        }

        try
        {
            User32.SetWindowPos(
                hwnd, User32.HWND_TOP, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            return User32.SetForegroundWindow(hwnd);
        }
        finally
        {
            User32.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    /// <summary>
    /// 核验前台窗口是否真的变成了目标。
    /// 前台切换不是同步生效的，因此短暂轮询而非一次性判断；
    /// 但总时长必须有上限，否则每次失败都要拖慢一次切换。
    /// </summary>
    private static bool VerifyForeground(nint hwnd)
    {
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < ForegroundVerifyTimeout)
        {
            if (User32.GetForegroundWindow() == hwnd)
            {
                return true;
            }

            Thread.Sleep(ForegroundPollInterval);
        }

        return User32.GetForegroundWindow() == hwnd;
    }

    // ------------------------------------------------------------------
    // 位置与状态
    // ------------------------------------------------------------------

    /// <summary>
    /// 把窗口的**可见边框**对齐到目标矩形。
    ///
    /// <c>SetWindowPos</c> 操作的是包含不可见拖拽边框的窗口矩形，而组矩形记录的是
    /// DWM 可见边框。两者在 Windows 10/11 上左右各差约 7 像素，直接传入会让成员窗口
    /// 每次对齐都往外扩一圈。因此先算出两者的差值再补偿。
    /// </summary>
    private OperationResult AlignTo(nint hwnd, PixelRect target, Stopwatch stopwatch)
    {
        // 这里刻意**不**做 IsHungAppWindow 预检。
        //
        // 对齐已改为异步投递（见下方 SWP_ASYNCWINDOWPOS），向繁忙窗口投递请求是无害的，
        // 它恢复后自然会处理。而预检会让应用每一次短暂繁忙（GC、重绘、后台索引）
        // 都使该成员整个停止跟随，拖动时表现为窗口时断时续地掉队。
        //
        // 激活操作仍然保留预检 —— 那里需要同步确认焦点是否真的转移了。

        // 最大化的窗口无法被 SetWindowPos 定位，必须先还原成普通状态。
        //
        // 早期版本在这里直接跳过对齐，结果是：只要有一个成员窗口处于最大化，
        // 它就永远不会被移动到组矩形，用户看到"有标签栏但窗口根本没合并"。
        // 原始的最大化状态已记在快照里，拆分时会还原回去，所以这里的还原是安全的。
        if (User32.IsZoomed(hwnd))
        {
            User32.ShowWindow(hwnd, User32.SW_RESTORE);
        }

        // 不可见边框的厚度只取决于窗口样式与 DPI，拖动过程中不会变。
        // 每帧每窗口都查一次 DWM 是拖动卡顿的主要来源之一，因此缓存起来。
        if (!_borderPadding.TryGetValue(hwnd, out var pad))
        {
            if (!User32.GetWindowRect(hwnd, out var raw))
            {
                return OperationResult.Fail("无法读取窗口矩形。", Marshal.GetLastWin32Error());
            }

            var visible = WindowEnumerator.ReadVisibleBounds(hwnd);
            var rawRect = raw.ToPixelRect();

            pad = new BorderPadding(
                visible.Left - rawRect.Left,
                visible.Top - rawRect.Top,
                rawRect.Right - visible.Right,
                rawRect.Bottom - visible.Bottom);

            _borderPadding[hwnd] = pad;
        }

        var padLeft = pad.Left;
        var padTop = pad.Top;
        var padRight = pad.Right;
        var padBottom = pad.Bottom;

        var x = target.Left - padLeft;
        var y = target.Top - padTop;
        var width = target.Width + padLeft + padRight;
        var height = target.Height + padTop + padBottom;

        // SWP_ASYNCWINDOWPOS 是这里的关键。
        //
        // 对**其他进程**的窗口调用 SetWindowPos 默认是同步的：它把
        // WM_WINDOWPOSCHANGING/CHANGED 发给目标窗口的线程，并一直等到对方处理完。
        // 目标应用的 UI 线程只要在忙（Java 应用的 GC、IDE 的后台索引、重绘），
        // 我们的控制队列就被一起卡住，实测能停两秒。
        //
        // 后果是拖动时中间帧全被丢弃：分组栏还在跟手，成员窗口却停在原地，
        // 等对齐终于返回又整体跳回来 —— 就是"经过其他窗口就卡住、分组栏和窗口分离"。
        //
        // 异步版本只投递请求不等待，窗口位置的正确性由后续帧保证。
        var ok = User32.SetWindowPos(
            hwnd, 0, x, y, width, height,
            User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER
            | User32.SWP_ASYNCWINDOWPOS);

        // 刻意**不**修改目标窗口的圆角偏好。
        //
        // 曾经在这里把成员窗口改成直角，想让它和分组条拼平。但那是在改用户应用的外观，
        // 窗口原本是圆角就该保持圆角。消除接缝是分组条自己的责任 ——
        // 让它向下多覆盖几个像素盖住圆角区域即可，不该反过来改造别人的窗口。
        //
        // 另外这里每次对齐都调一次 DWM 属性设置，拖动整组时每帧每窗口一次，
        // 是拖动卡顿的原因之一。
        return ok
            ? Done(ActivationLevel.NotAttempted, stopwatch)
            : OperationResult.Fail("移动窗口失败。", Marshal.GetLastWin32Error());
    }

    private static OperationResult RestoreCorners(nint hwnd, Stopwatch stopwatch)
    {
        SetCornerPreference(hwnd, Dwmapi.DWMWCP_DEFAULT);
        return Done(ActivationLevel.NotAttempted, stopwatch);
    }

    /// <summary>设置窗口圆角偏好。跨进程有效，失败时静默忽略（纯视觉增强）。</summary>
    private static void SetCornerPreference(nint hwnd, int preference)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var value = preference;
        Dwmapi.DwmSetWindowAttribute(
            hwnd, Dwmapi.DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
    }

    private static OperationResult Lower(nint hwnd, Stopwatch stopwatch)
    {
        // 同 AlignTo：跨进程的 SetWindowPos 默认同步等待目标线程，必须异步化。
        var ok = User32.SetWindowPos(
            hwnd, User32.HWND_BOTTOM, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE
            | User32.SWP_NOOWNERZORDER | User32.SWP_ASYNCWINDOWPOS);

        return ok
            ? Done(ActivationLevel.NotAttempted, stopwatch)
            : OperationResult.Fail("调整窗口层级失败。", Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// 按快照还原窗口。
    /// 顺序：先恢复位置与显示状态，再恢复置顶属性 —— 置顶会影响 Z 序，放在最后才不会被覆盖。
    /// </summary>
    private OperationResult Restore(nint hwnd, WindowSnapshot snapshot, Stopwatch stopwatch)
    {
        // 应用已经把窗口关掉（隐藏）时绝不还原。
        //
        // 下面的 SetWindowPlacement 带 SW_SHOWNOACTIVATE，会把一个隐藏的窗口
        // 重新显示出来 —— 窗口出现在屏幕上，应用却认为自己已关闭，于是不响应
        // 任何输入，用户只能去托盘重新打开它。这个判断放在这里而不是只放在
        // 编排层，是因为指令入队到真正执行之间还有一段时间窗口，
        // 应用完全可能正好在这期间把窗口关掉。
        if (!User32.IsWindowVisible(hwnd))
        {
            return OperationResult.Fail("窗口已被其应用关闭，跳过还原以免拽出一个点不动的僵尸窗口。");
        }

        // 保险起见改回默认圆角：早期版本改过成员窗口的圆角偏好，
        // 用户可能还留着被改过的窗口，这里顺手复原。
        SetCornerPreference(hwnd, Dwmapi.DWMWCP_DEFAULT);

        var placement = WINDOWPLACEMENT.Create();
        if (!User32.GetWindowPlacement(hwnd, ref placement))
        {
            return OperationResult.Fail("无法读取窗口位置。", Marshal.GetLastWin32Error());
        }

        placement.rcNormalPosition = RECT.FromPixelRect(snapshot.RestoreBounds);
        placement.showCmd = snapshot.ShowState switch
        {
            WindowShowState.Minimized => User32.SW_SHOWMINIMIZED,
            WindowShowState.Maximized => User32.SW_SHOWMAXIMIZED,
            _ => User32.SW_SHOWNOACTIVATE,
        };

        if (!User32.SetWindowPlacement(hwnd, ref placement))
        {
            return OperationResult.Fail("还原窗口位置失败。", Marshal.GetLastWin32Error());
        }

        // 用户原本设了置顶的话必须还回去，静默丢掉是数据损失。
        var currentTopmost = ((long)User32.GetWindowLongPtr(hwnd, User32.GWL_EXSTYLE)
            & User32.WS_EX_TOPMOST) != 0;

        if (currentTopmost != snapshot.IsTopmost)
        {
            User32.SetWindowPos(
                hwnd,
                snapshot.IsTopmost ? User32.HWND_TOPMOST : User32.HWND_NOTOPMOST,
                0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }

        if (snapshot.WasInTaskbar)
        {
            _taskbar.SetVisible(hwnd, true);
        }

        return Done(ActivationLevel.NotAttempted, stopwatch);
    }

    private OperationResult SetTaskbarButton(nint hwnd, bool visible, Stopwatch stopwatch)
    {
        // 拿不到 ITaskbarList 时降级为"保留所有按钮"，不算失败 ——
        // 任务栏按钮策略是增强功能，不该让整条指令链中断。
        _taskbar.SetVisible(hwnd, visible);
        return Done(ActivationLevel.NotAttempted, stopwatch);
    }

    /// <summary>
    /// 请求关闭窗口。
    /// 用带超时的 <c>SendMessageTimeoutW</c>：应用可能正在弹保存提示，
    /// 用普通 SendMessage 会把整个控制队列挂在那个对话框上。
    /// </summary>
    private static OperationResult Close(nint hwnd, Stopwatch stopwatch)
    {
        User32.SendMessageTimeout(
            hwnd, User32.WM_CLOSE, 0, 0,
            User32.SMTO_ABORTIFHUNG, CloseMessageTimeoutMs, out _);

        // 不核验窗口是否真的关闭：应用有权拒绝或弹出保存提示，那是预期行为。
        // 标签的移除由 DESTROY 事件驱动，不在这里推测。
        return Done(ActivationLevel.NotAttempted, stopwatch);
    }

    private static OperationResult Done(ActivationLevel level, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return OperationResult.Ok(level, stopwatch.Elapsed);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();

        // 等待队列排空，确保还原指令真正执行完再退出 ——
        // 退出时留下没还原的窗口是最不可接受的失败。
        if (!_worker.Join(TimeSpan.FromSeconds(5)))
        {
            Debug.WriteLine("[TabNest] 窗口控制队列未能在 5 秒内退出。");
        }

        _queue.Dispose();
    }

    /// <summary>窗口不可见拖拽边框在各边的厚度。</summary>
    private readonly record struct BorderPadding(int Left, int Top, int Right, int Bottom);

    private sealed record WorkItem(IList<WindowAction> Actions, CancellationToken Cancellation)
    {
        public TaskCompletionSource<IReadOnlyList<OperationResult>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
