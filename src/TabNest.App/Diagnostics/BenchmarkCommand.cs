namespace TabNest.App.Diagnostics;

/// <summary>
/// 性能门禁：自测常驻工作集、线程数、句柄数、空闲 CPU、切换 P95、事件→UI P95，
/// 并校验 PresentationFramework 是否未被加载（常驻层不得触碰 WPF）。
/// 结果与 Groupy 2 实测基准同表对比。完整实现见任务 #12。
/// </summary>
internal static class BenchmarkCommand
{
    public static int Run(RunMode mode)
    {
        _ = mode;
        Console.Error.WriteLine("--benchmark 尚未实现（等待常驻层完成）。");
        return 1;
    }
}
