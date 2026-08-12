using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// 观察系统窗口事件。
///
/// 设计要点：
///
/// **零轮询。** 全部依赖 <c>SetWinEventHook</c> 的被动投递。Groupy 空闲 CPU 只有 0.08% 单核，
/// 正是因为 hook 是被动的；任何定时轮询都会让我们输掉这个指标。
///
/// **回调用 <c>[UnmanagedCallersOnly]</c> 静态函数指针。** 传统做法是持有一个托管委托字段防止
/// GC 回收，一旦忘记就会在运行数小时后随机崩溃 —— 这是 WinEvent 最经典的坑。
/// 用函数指针从根本上不存在这个问题。
///
/// **观察线程只投递，不做业务。** 回调里禁止阻塞 IO、进程查询和 UI 操作：
/// WinEvent 回调运行在被观察窗口的消息投递路径上，拖慢它会影响整个系统的响应。
/// </summary>
public sealed class WinEventObserver : IDisposable
{
    /// <summary>队列上限。满了丢弃最旧的位置事件而非无限增长 —— 计划书要求事件风暴不得让队列无限增长。</summary>
    private const int QueueCapacity = 2048;

    /// <summary>
    /// 当前实例。<c>[UnmanagedCallersOnly]</c> 回调必须是静态的，只能通过静态字段找回实例。
    /// TabNest 全进程只有一个观察者，这个约束不构成限制。
    /// </summary>
    private static WinEventObserver? _current;

    private readonly Channel<WindowEvent> _channel;

    /// <summary>
    /// 关注位置变化的窗口集合。
    ///
    /// EVENT_OBJECT_LOCATIONCHANGE 是全系统钩子：任何窗口、任何动画、甚至光标移动都会触发。
    /// 不过滤的话空闲时就有持续的字典写入，实测会把空闲 CPU 推到 0.5% 单核以上。
    /// 而我们真正关心的只有组成员和正在被拖拽的窗口，数量在个位数。
    ///
    /// 用不可变集合 + 整体替换，回调侧只读不锁。
    /// </summary>
    private volatile HashSet<nint> _locationWatchList = [];

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _thread;
    private readonly List<nint> _hooks = [];
    private uint _threadId;
    private volatile bool _disposed;

    public WinEventObserver()
    {
        _channel = Channel.CreateBounded<WindowEvent>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _current = this;

        _thread = new Thread(Run)
        {
            Name = "TabNest.WinEventObserver",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>
    /// 设置需要跟踪位置变化的窗口。
    /// 只有这些窗口的 LOCATIONCHANGE 会被投递，其余直接丢弃 ——
    /// 全系统的位置事件量足以在空闲时也吃掉可观的 CPU。
    /// </summary>
    public void SetLocationWatchList(IEnumerable<nint> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        _locationWatchList = [.. handles];
    }

    /// <summary>事件流。消费者应在自己的线程上读取，不要在 UI 线程同步等待。</summary>
    public ChannelReader<WindowEvent> Events => _channel.Reader;

    /// <summary>已丢弃的事件数。持续增长说明消费者跟不上，是性能问题的早期信号。</summary>
    public long DroppedEvents => Interlocked.Read(ref _dropped);

    private long _dropped;

    // ------------------------------------------------------------------
    // 观察线程
    // ------------------------------------------------------------------

    private unsafe void Run()
    {
        _threadId = Kernel32.GetCurrentThreadId();

        // 分段注册而非一次覆盖 EVENT_MIN..EVENT_MAX：后者会收到海量无关事件
        // （每一次焦点、光标、值变化），全部要跨进程投递到我们进程，白白消耗 CPU。
        var callback = (nint)(delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void>)&OnWinEvent;

        Hook(User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND, callback);
        Hook(User32.EVENT_SYSTEM_MOVESIZESTART, User32.EVENT_SYSTEM_MOVESIZEEND, callback);
        Hook(User32.EVENT_SYSTEM_MINIMIZESTART, User32.EVENT_SYSTEM_MINIMIZEEND, callback);
        Hook(User32.EVENT_OBJECT_DESTROY, User32.EVENT_OBJECT_HIDE, callback);
        Hook(User32.EVENT_OBJECT_LOCATIONCHANGE, User32.EVENT_OBJECT_NAMECHANGE, callback);
        Hook(User32.EVENT_OBJECT_CLOAKED, User32.EVENT_OBJECT_UNCLOAKED, callback);

        if (_hooks.Count == 0)
        {
            Debug.WriteLine("[TabNest] 未能注册任何 WinEvent 钩子，窗口观察不可用。");
            return;
        }

        PumpMessages();
        Unhook();
    }

    private void Hook(uint min, uint max, nint callback)
    {
        var hook = User32.SetWinEventHook(
            min, max,
            hmodWinEventProc: 0,
            pfnWinEventProc: callback,
            idProcess: 0,
            idThread: 0,
            // OUTOFCONTEXT：不把我们的 DLL 注入到被观察进程里。
            // SKIPOWNPROCESS：避免观察 TabNest 自己的窗口造成事件回环。
            dwFlags: User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);

        if (hook != 0)
        {
            _hooks.Add(hook);
        }
        else
        {
            Debug.WriteLine(
                $"[TabNest] 注册 WinEvent 钩子 {min:X}-{max:X} 失败，"
                + $"错误码 {Marshal.GetLastWin32Error()}。");
        }
    }

    /// <summary>
    /// 消息循环。
    /// <c>SetWinEventHook</c> 的 OUTOFCONTEXT 模式要求调用线程有消息循环，
    /// 否则回调永远不会被投递 —— 这是最容易漏掉、且症状为"完全没有事件"的一步。
    /// </summary>
    private void PumpMessages()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            var result = User32.GetMessage(out var msg, 0, 0, 0);

            if (result is 0 or -1)
            {
                break;
            }

            User32.TranslateMessage(ref msg);
            User32.DispatchMessage(ref msg);
        }
    }

    private void Unhook()
    {
        foreach (var hook in _hooks)
        {
            User32.UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }

    // ------------------------------------------------------------------
    // 回调
    // ------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static void OnWinEvent(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        // 只关心窗口本身的事件。控件、菜单项、光标都会以同样的事件类型上报，
        // 不过滤会让事件量高出一两个数量级。
        if (idObject != User32.OBJID_WINDOW || idChild != User32.CHILDID_SELF || hwnd == 0)
        {
            return;
        }

        var observer = _current;
        if (observer is null || observer._disposed)
        {
            return;
        }

        try
        {
            observer.Dispatch(eventType, hwnd);
        }
        catch (Exception ex)
        {
            // 异常绝不能穿出非托管回调边界 —— 那会直接终止进程。
            Debug.WriteLine($"[TabNest] WinEvent 回调异常：{ex}");
        }
    }

    private void Dispatch(uint eventType, nint hwnd)
    {
        var now = Stopwatch.GetTimestamp();

        if (eventType == User32.EVENT_OBJECT_LOCATIONCHANGE)
        {
            // 关注列表过滤：全系统的位置事件绝大多数与我们无关，连暂存都不该做。
            if (!_locationWatchList.Contains(hwnd))
            {
                return;
            }

            // 过滤之后**立即投递，不做延迟合并**。
            //
            // 早期这里攒 16ms 再发，想的是防事件洪水。但关注列表只剩组成员和被拖窗口
            // 这几个句柄，每秒最多百来条，根本不需要合并。
            // 而 Task.Delay(16) 受 Windows 默认约 15.6ms 的定时器精度影响，
            // 实际会等 16~31ms，加上线程调度实测让事件晚到 40 多毫秒 ——
            // 被拖的窗口由系统直接移动、丝滑跟手，成员窗口和分组栏却慢三帧才跟上，
            // 视觉上就是"分组栏脱开又追上"的顿挫。
            //
            // 有界队列本身就提供了背压：消费不过来时丢弃最旧的位置事件，
            // 而位置事件天然可丢 —— 后一条总是比前一条更准确。
            Publish(new WindowEvent(WindowEventKind.LocationChanged, hwnd, now));
            return;
        }

        var kind = eventType switch
        {
            User32.EVENT_SYSTEM_FOREGROUND => WindowEventKind.Foreground,
            User32.EVENT_SYSTEM_MOVESIZESTART => WindowEventKind.MoveSizeStart,
            User32.EVENT_SYSTEM_MOVESIZEEND => WindowEventKind.MoveSizeEnd,
            User32.EVENT_SYSTEM_MINIMIZESTART => WindowEventKind.MinimizeStart,
            User32.EVENT_SYSTEM_MINIMIZEEND => WindowEventKind.MinimizeEnd,
            User32.EVENT_OBJECT_DESTROY => WindowEventKind.Destroyed,
            User32.EVENT_OBJECT_SHOW => WindowEventKind.Shown,
            User32.EVENT_OBJECT_HIDE => WindowEventKind.Hidden,
            User32.EVENT_OBJECT_NAMECHANGE => WindowEventKind.NameChanged,
            User32.EVENT_OBJECT_CLOAKED => WindowEventKind.Cloaked,
            User32.EVENT_OBJECT_UNCLOAKED => WindowEventKind.Uncloaked,
            _ => (WindowEventKind?)null,
        };

        if (kind is null)
        {
            return;
        }

        Publish(new WindowEvent(kind.Value, hwnd, now));
    }

    private void Publish(WindowEvent evt)
    {
        if (!_channel.Writer.TryWrite(evt))
        {
            Interlocked.Increment(ref _dropped);
        }
    }


    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _channel.Writer.TryComplete();

        // 唤醒消息循环让它自然退出并注销钩子。
        // 留下未注销的 WinEvent 钩子是计划书 G6 发布闸门明确禁止的。
        if (_threadId != 0)
        {
            User32.PostThreadMessage(_threadId, User32.WM_QUIT, 0, 0);
        }

        if (!_thread.Join(TimeSpan.FromSeconds(3)))
        {
            Debug.WriteLine("[TabNest] 窗口观察线程未能在 3 秒内退出。");
        }

        _shutdown.Dispose();

        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }
}
