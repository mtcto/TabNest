using TabNest.Interop;

namespace TabNest.App.Rail;

/// <summary>
/// 整组最小化时，代表这个分组出现在任务栏上的那一个按钮。
///
/// 用户点分组栏的最小化按钮后，组内窗口全部收起。但它们各自的任务栏按钮还在，
/// 而用户要的是**一个** TabNest 图标代表整个分组，而不是散着三四个按钮。
/// 于是造一个我们自己的窗口顶上去：
///
///   · 以最小化状态创建（SW_SHOWMINNOACTIVE）：占一个任务栏按钮，
///     不占任何屏幕面积，也不抢焦点；
///   · 窗口类带 TabNest 图标（见 Win32Window 的类注册），任务栏上显示的就是它；
///   · 用户点这个按钮时 Windows 会试图还原它 —— 我们截下 SC_RESTORE，
///     还原整个分组，然后销毁自己。
///
/// **为什么恢复由这个窗口驱动，而不是监听成员窗口的还原事件。**
/// 早先实现"整组最小化"走的正是后者：监听成员的 MINIMIZESTART/MINIMIZEEND
/// 再批量收起或恢复其余成员。那条路必然振荡 —— 我们自己发出的 ShowWindow 会引来
/// 同样的事件，而"这是用户做的还是我们做的"在事件里分辨不出来；实测每秒二十几轮，
/// 屏幕疯狂闪烁，用户只能强杀进程。换成代理窗口后，收起与恢复各只有一个显式入口
/// （分组栏按钮、本窗口的 SC_RESTORE），全程没有"猜事件来源"这一步。
/// </summary>
internal sealed class GroupTaskbarProxy : Win32Window
{
    private readonly Action _onRestoreRequested;

    /// <param name="title">任务栏按钮上显示的文字。</param>
    /// <param name="onRestoreRequested">用户点了任务栏按钮时调用。</param>
    public GroupTaskbarProxy(string title, Action onRestoreRequested)
    {
        _onRestoreRequested = onRestoreRequested;

        // 尺寸 1×1 且摆在屏外：这个窗口永远不该被看见，它的全部作用是占一个
        // 任务栏按钮。刻意**不用** WS_EX_TOOLWINDOW —— 那正好是"不进任务栏"的意思。
        CreateWindow(
            className: "TabNest.GroupProxy",
            title: title,
            style: (uint)ProxyStyles.MinimizableWindow,
            exStyle: (uint)ProxyStyles.AppWindow,
            x: -32000, y: -32000, width: 1, height: 1);

        ShowMinimizedNoActivate();
    }

    public void UpdateTitle(string title)
    {
        if (IsCreated)
        {
            SetWindowTitle(title);
        }
    }

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Messages.SysCommand:
                // SC_RESTORE：用户点了任务栏按钮，或从 Alt+Tab 选中了它。
                //
                // 返回 0 表示这条命令已处理，系统不会真去还原这个 1×1 的空窗口 ——
                // 否则屏幕角落会闪出一个空白框。
                if ((wParam.ToInt64() & SystemCommands.Mask) == SystemCommands.Restore)
                {
                    _onRestoreRequested();
                    return 0;
                }

                return null;

            case Messages.Close:
                // 用户在任务栏按钮上选了"关闭窗口"。语义上这是"把收起来的分组打开"，
                // 绝不能真的关掉自己 —— 那样分组就永远回不来了。
                _onRestoreRequested();
                return 0;

            default:
                return null;
        }
    }
}
