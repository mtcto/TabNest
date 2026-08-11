using System.Collections.Concurrent;
using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// 把工作投递到拥有消息循环的 UI 线程。
///
/// 窗口观察线程和控制队列线程都不能直接碰轨道窗口 —— 窗口过程必须始终在创建它的
/// 线程上执行。计划书 §7.3 的"UI 线程只消费快照"也要求有这样一个单向投递通道。
/// </summary>
public sealed class UiDispatcher : Win32Window
{
    private const string ClassName = "TabNest.Dispatcher";

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly int _managedThreadId = Environment.CurrentManagedThreadId;
    private readonly uint _nativeThreadId = Kernel32.GetCurrentThreadId();

    public UiDispatcher()
    {
        // 消息专用窗口，不可见。
        CreateWindow(ClassName, "TabNest.Dispatcher", style: 0, exStyle: 0, 0, 0, 0, 0);
    }

    /// <summary>当前是否已在 UI 线程上。</summary>
    public bool IsOnUiThread => Environment.CurrentManagedThreadId == _managedThreadId;

    /// <summary>投递到 UI 线程执行。已在 UI 线程时直接执行，避免不必要的延迟。</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (IsOnUiThread)
        {
            action();
            return;
        }

        _queue.Enqueue(action);
        User32.PostMessage(Handle, WindowClass.WM_TABNEST_SYNC, 0, 0);
    }

    /// <summary>请求 UI 线程的消息循环退出。可从任意线程调用。</summary>
    public void RequestShutdown() =>
        User32.PostThreadMessage(_nativeThreadId, User32.WM_QUIT, 0, 0);

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        if (msg != WindowClass.WM_TABNEST_SYNC)
        {
            return null;
        }

        // 一次性排空：事件突发时避免每条更新都走一遍消息投递。
        while (_queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // 必须落盘。这里原本只写 Debug.WriteLine，发布运行时完全看不到 ——
                // 结果是编排层任何一次抛异常都表现为"功能静默失效"，无从追查。
                Core.Diagnostics.FileLog.Error("UI 投递任务抛出异常。", ex);
            }
        }

        return 0;
    }

    /// <summary>在当前线程运行消息循环，直到收到 WM_QUIT。</summary>
    public static void RunMessageLoopEntry() => RunMessageLoop();

    /// <summary>在当前线程运行消息循环，直到收到 WM_QUIT。</summary>
    public static void RunMessageLoop()
    {
        while (true)
        {
            var result = User32.GetMessage(out var msg, 0, 0, 0);

            if (result is 0 or -1)
            {
                return;
            }

            User32.TranslateMessage(ref msg);
            User32.DispatchMessage(ref msg);
        }
    }

}
