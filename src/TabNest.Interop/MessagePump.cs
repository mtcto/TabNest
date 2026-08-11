using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// 手动抽干线程消息队列。
///
/// 用于测试与诊断：编排层把工作投递到 UI 线程后，必须有人跑消息循环这些工作才会执行。
/// 正式运行时由 <see cref="UiDispatcher.RunMessageLoop"/> 承担，
/// 但测试代码需要在跑完一段后继续往下断言，不能被阻塞式循环卡住。
/// </summary>
public static class MessagePump
{
    /// <summary>处理当前排队的全部消息后立即返回。</summary>
    public static void DrainPendingMessages()
    {
        while (User32.PeekMessage(out var msg, 0, 0, 0, User32.PM_REMOVE))
        {
            if (msg.message == User32.WM_QUIT)
            {
                return;
            }

            User32.TranslateMessage(ref msg);
            User32.DispatchMessage(ref msg);
        }
    }
}
