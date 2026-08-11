namespace TabNest.Core.Models;

/// <summary>
/// 分组窗口在任务栏上的呈现方式。对齐 Groupy 总览页的三选一。
///
/// 实现手段是 <c>ITaskbarList::AddTab</c>/<c>DeleteTab</c>：shell COM 接口，
/// 对任意进程的顶层 HWND 有效，不修改目标窗口样式，完全可逆。
///
/// 刻意不用 <c>WS_EX_TOOLWINDOW</c> 达成同样效果 —— 那需要修改外部窗口的扩展样式
/// 并隐藏重显才能生效，破坏性远高于前者。
/// </summary>
public enum TaskbarButtonPolicy
{
    /// <summary>组内每个窗口各自保留任务栏按钮。默认值，最不意外。</summary>
    ShowAll = 0,

    /// <summary>只保留活动窗口的任务栏按钮。</summary>
    ActiveOnly = 1,

    /// <summary>整组合并为一个任务栏按钮。需要额外的代理窗口，阶段一不实现。</summary>
    SingleCombined = 2,
}
