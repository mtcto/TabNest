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
    /// <summary>等待前台切换生效的总时长。超过这个时间基本可以认定这一级降级失败了。</summary>
    private static readonly TimeSpan ForegroundVerifyTimeout = TimeSpan.FromMilliseconds(60);

    private static readonly TimeSpan ForegroundPollInterval = TimeSpan.FromMilliseconds(5);

    /// <summary>发送 WM_CLOSE 的超时。目标可能正在弹保存提示，不能无限等。</summary>
    private const uint CloseMessageTimeoutMs = 2000;

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

        // 第三级：至少把窗口抬到 Z 序顶部，让用户看得见它。
        User32.SetWindowPos(
            hwnd, User32.HWND_TOP, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        User32.BringWindowToTop(hwnd);
        if (VerifyForeground(hwnd))
        {
            return Done(ActivationLevel.RaisedOnly, stopwatch);
        }

        // 第四级：兜底。行为等价于用户按 Alt+Tab 选中该窗口。
        User32.SwitchToThisWindow(hwnd, true);
        if (VerifyForeground(hwnd))
        {
            return Done(ActivationLevel.SwitchToThisWindow, stopwatch);
        }

        // 四级全败。不能假装成功 —— UI 需要据此显示"重试/拆分/查看原因"。
        stopwatch.Stop();
        return new OperationResult
        {
            Success = false,
            Message = "四级降级链全部失败，无法把焦点转移到目标窗口。"
                + "这通常由系统前台锁定或目标应用主动拒绝激活导致。",
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
    private static OperationResult AlignTo(nint hwnd, PixelRect target, Stopwatch stopwatch)
    {
        if (User32.IsHungAppWindow(hwnd))
        {
            return OperationResult.Fail("目标窗口无响应，已跳过对齐。");
        }

        // 最大化的窗口不做对齐：强行 SetWindowPos 会让它脱离最大化状态，
        // 用户会看到窗口莫名其妙缩小。
        if (User32.IsZoomed(hwnd))
        {
            return Done(ActivationLevel.NotAttempted, stopwatch);
        }

        if (!User32.GetWindowRect(hwnd, out var raw))
        {
            return OperationResult.Fail("无法读取窗口矩形。", Marshal.GetLastWin32Error());
        }

        var visible = WindowEnumerator.ReadVisibleBounds(hwnd);
        var rawRect = raw.ToPixelRect();

        // 不可见边框在各边上的厚度。
        var padLeft = visible.Left - rawRect.Left;
        var padTop = visible.Top - rawRect.Top;
        var padRight = rawRect.Right - visible.Right;
        var padBottom = rawRect.Bottom - visible.Bottom;

        var x = target.Left - padLeft;
        var y = target.Top - padTop;
        var width = target.Width + padLeft + padRight;
        var height = target.Height + padTop + padBottom;

        var ok = User32.SetWindowPos(
            hwnd, 0, x, y, width, height,
            User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER);

        return ok
            ? Done(ActivationLevel.NotAttempted, stopwatch)
            : OperationResult.Fail("移动窗口失败。", Marshal.GetLastWin32Error());
    }

    private static OperationResult Lower(nint hwnd, Stopwatch stopwatch)
    {
        var ok = User32.SetWindowPos(
            hwnd, User32.HWND_BOTTOM, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER);

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

    private sealed record WorkItem(IList<WindowAction> Actions, CancellationToken Cancellation)
    {
        public TaskCompletionSource<IReadOnlyList<OperationResult>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
