using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// TabNest 是 WinExe（无控制台子系统），常驻运行时不该弹出黑窗口。
/// 但 --diagnose / --benchmark 需要把结果输出给用户，因此在命令行模式下
/// 附加到父进程（通常是终端）的控制台。
/// </summary>
public static class ConsoleBridge
{
    private static bool _attached;

    /// <summary>
    /// 尝试附加父进程控制台。
    /// 若父进程没有控制台（例如从资源管理器双击启动），则不分配新窗口 ——
    /// 凭空弹出一个控制台窗口对用户是意外行为，此时输出静默丢弃即可。
    /// </summary>
    /// <returns>是否成功接上可见的控制台。</returns>
    public static bool AttachToParent()
    {
        if (_attached)
        {
            return true;
        }

        _attached = Kernel32.AttachConsole(Kernel32.AttachParentProcess);
        if (_attached)
        {
            RedirectStandardStreams();
        }

        return _attached;
    }

    /// <summary>
    /// AttachConsole 之后，进程的 stdout/stderr 句柄仍指向附加前的状态，
    /// 必须重新打开才能真正写到新附加的控制台。
    /// </summary>
    private static void RedirectStandardStreams()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stderr);
    }
}
