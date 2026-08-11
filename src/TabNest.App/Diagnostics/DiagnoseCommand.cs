namespace TabNest.App.Diagnostics;

/// <summary>
/// 无 UI 诊断模式：列出当前顶层窗口及其可分组性判定原因。
/// Interop 层的第一个可验证里程碑 —— 不依赖任何 UI 即可确认窗口发现与资格判定是否正确。
/// 完整实现见任务 #6。
/// </summary>
internal static class DiagnoseCommand
{
    public static int Run(RunMode mode)
    {
        _ = mode;
        Console.Error.WriteLine("--diagnose 尚未实现（等待 Interop 层完成）。");
        return 1;
    }
}
