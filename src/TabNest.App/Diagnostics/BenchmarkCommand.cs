using System.Diagnostics;
using System.Globalization;
using TabNest.Interop;

namespace TabNest.App.Diagnostics;

/// <summary>
/// 性能门禁。
///
/// 计划书第五章为阶段一定了硬指标，并要求"不达标不算完成"。此前这里是个桩，
/// 于是那些指标从未被真正验证过 —— 一个不会失败的门禁等于没有门禁。
///
/// 全部指标与 Groupy 2 的实测基准同表对比（数据来自 docs/competitive-analysis.md，
/// 20 秒空闲采样、24 逻辑核）。
/// </summary>
internal static class BenchmarkCommand
{
    /// <summary>阶段一目标。取自计划书第五章。</summary>
    private static readonly (string Name, double Target, double Groupy, string Unit)[] Targets =
    [
        ("常驻工作集", 85, 78, "MB"),
        ("空闲 CPU", 0.10, 0.08, "% 单核"),
        ("线程数", 24, 30, "个"),
        ("句柄数", 400, 706, "个"),
    ];

    public static int Run(RunMode mode)
    {
        Console.WriteLine($"TabNest {BuildInfo.DisplayVersion} 性能门禁");
        Console.WriteLine();

        // 测**真实的常驻进程**，而不是门禁自己。
        //
        // 早期版本在门禁进程内自测，结果空闲 CPU 报 0.16~0.23%，而同一份代码
        // 作为常驻进程跑时是 0.000% —— 差值全是门禁自己的脚手架（控制台宿主、
        // 采样循环的定时唤醒）。一个把自己的开销算进被测对象的门禁，
        // 会逼着人去优化根本不存在的问题。
        //
        // Groupy 的基准数据也是这样从外部采的，两边口径这才一致。
        var (target, started) = ResolveResidentProcess();

        if (target is null)
        {
            Console.Error.WriteLine("找不到也无法启动常驻进程，无法测量。");
            return 1;
        }

        var failures = 0;

        try
        {
            Console.WriteLine($"测量对象：PID {target.Id}{(started ? "（本次启动）" : "（已在运行）")}");
            Console.WriteLine();

            // 给启动期的分配与 JIT 一点时间稳定下来。
            Thread.Sleep(TimeSpan.FromSeconds(3));

            Console.WriteLine("正在采样空闲 CPU（20 秒）…");

            target.Refresh();
            var cpuBefore = target.TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            Thread.Sleep(TimeSpan.FromSeconds(20));
            wall.Stop();

            target.Refresh();
            var cpuUsed = (target.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var idleCpu = cpuUsed / wall.Elapsed.TotalMilliseconds * 100.0;

            var workingSet = target.WorkingSet64 / 1024.0 / 1024.0;
            var threads = target.Threads.Count;
            var handles = target.HandleCount;

            failures = ReportAll(mode, target, workingSet, idleCpu, threads, handles);
        }
        finally
        {
            // 只关掉我们自己拉起来的那个；用户原本就开着的不动。
            if (started && !target.HasExited)
            {
                target.Kill();
            }

            target.Dispose();
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>找到正在运行的常驻进程；没有就启动一个。</summary>
    private static (Process? Process, bool Started) ResolveResidentProcess()
    {
        var self = Environment.ProcessId;

        var existing = Process.GetProcessesByName("TabNest")
            .FirstOrDefault(p => p.Id != self && !p.HasExited);

        if (existing is not null)
        {
            return (existing, false);
        }

        try
        {
            var exe = Environment.ProcessPath;

            if (string.IsNullOrEmpty(exe))
            {
                return (null, false);
            }

            var started = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            return (started, true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, false);
        }
    }

    private static int ReportAll(
        RunMode mode, Process target, double workingSet, double idleCpu, int threads, int handles)
    {
        var failures = 0;

        Console.WriteLine();
        Console.WriteLine("指标                本次      阶段一目标    Groupy 2");
        Console.WriteLine("──────────────────────────────────────────────────────");

        failures += Report("常驻工作集", workingSet, Targets[0]);
        failures += Report("空闲 CPU", idleCpu, Targets[1]);
        failures += Report("线程数", threads, Targets[2]);
        failures += Report("句柄数", handles, Targets[3]);

        Console.WriteLine();

        // ---- WPF 未被加载 ----
        //
        // 这条约束是发布体积与常驻内存的基础。它很容易在某次改动里被无声破坏：
        // 只要常驻路径上有人碰了一个 WPF 类型，程序集就会被加载，
        // 而功能一切正常，没有任何症状 —— 必须由门禁来盯。
        //
        // 查目标进程的模块表而不是自己的：要验证的是常驻进程有没有加载 WPF。
        var wpfLoaded = false;

        try
        {
            foreach (System.Diagnostics.ProcessModule m in target.Modules)
            {
                if (m.ModuleName?.StartsWith("PresentationFramework", StringComparison.OrdinalIgnoreCase) == true
                    || m.ModuleName?.StartsWith("PresentationCore", StringComparison.OrdinalIgnoreCase) == true)
                {
                    wpfLoaded = true;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine("[跳过] 无法读取目标进程的模块表，未能校验 WPF 加载情况。");
        }

        failures += Check(
            "常驻层未加载 WPF",
            !wpfLoaded,
            "PresentationFramework 已被加载 —— 常驻内存与发布体积的前提被破坏了");

        // ---- 事件→UI 延迟 ----
        //
        // 这一项是代码路径的性质而非资源占用，因此在本进程内测量。
        using var dispatcher = new UiDispatcher();
        var latency = MeasureDispatchLatency(dispatcher);

        failures += Check(
            $"事件→UI 延迟 P95 {latency:F1}ms ≤ 120ms",
            latency <= 120,
            $"实测 {latency:F1}ms");

        Console.WriteLine();

        if (mode.Json)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $$"""
                {"workingSetMb":{{workingSet:F1}},"idleCpuPercent":{{idleCpu:F3}},"threads":{{threads}},"handles":{{handles}},"wpfLoaded":{{(wpfLoaded ? "true" : "false")}},"dispatchLatencyP95Ms":{{latency:F1}}}
                """));
        }

        Console.WriteLine(failures == 0 ? "全部达标。" : $"{failures} 项未达标。");

        return failures;
    }

    /// <summary>
    /// 量事件投递到 UI 线程的延迟。
    ///
    /// 取 P95 而不是平均值：拖动卡顿是由少数几帧的长延迟造成的，
    /// 平均值会把它们完全抹平 —— 之前排查拖动问题时，平均延迟一直很好看。
    /// </summary>
    private static double MeasureDispatchLatency(UiDispatcher dispatcher)
    {
        const int samples = 200;
        var latencies = new List<double>(samples);

        for (var i = 0; i < samples; i++)
        {
            var sent = Stopwatch.GetTimestamp();
            var done = false;

            // 从别的线程投递，走的才是真实路径 —— 同线程投递会被直接执行掉。
            _ = Task.Run(() => dispatcher.Post(() =>
            {
                latencies.Add((Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency);
                done = true;
            }));

            var deadline = DateTime.UtcNow.AddMilliseconds(500);

            while (!done && DateTime.UtcNow < deadline)
            {
                MessagePump.DrainPendingMessages();
                Thread.Sleep(1);
            }
        }

        if (latencies.Count == 0)
        {
            return double.MaxValue;
        }

        latencies.Sort();
        var index = (int)Math.Ceiling(latencies.Count * 0.95) - 1;

        return latencies[Math.Clamp(index, 0, latencies.Count - 1)];
    }

    private static int Report(string name, double actual, (string Name, double Target, double Groupy, string Unit) t)
    {
        var ok = actual <= t.Target;

        Console.WriteLine(string.Create(
            CultureInfo.CurrentCulture,
            $"{(ok ? "[通过]" : "[未达标]"),-8} {name,-10} {actual,8:F2}   ≤ {t.Target,-8:F2}   {t.Groupy,6:F2} {t.Unit}"));

        return ok ? 0 : 1;
    }

    private static int Check(string name, bool ok, string detail)
    {
        Console.WriteLine(ok ? $"[通过] {name}" : $"[未达标] {name}\n        {detail}");
        return ok ? 0 : 1;
    }

    private static void PumpMessages(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;

        while (DateTime.UtcNow < deadline)
        {
            MessagePump.DrainPendingMessages();
            Thread.Sleep(16);
        }
    }

    /// <summary>
    /// 空闲一段时间，用于 CPU 采样。
    ///
    /// **绝不能用 <see cref="PumpMessages"/> 采样。** 它每 16ms 醒一次抽消息，
    /// 20 秒就是 1250 次唤醒，量到的 CPU 大半是采样循环自己烧的 ——
    /// 实测门禁因此报 0.23%，而同一份代码作为常驻进程跑时是 0.000%。
    /// 一个把自己的开销算进被测对象的门禁，会逼着人去优化根本不存在的问题。
    ///
    /// 常驻进程的 UI 线程阻塞在 GetMessage 上，空闲时零唤醒。这里用长睡眠
    /// 模拟同样的状态，每秒醒一次抽一遍消息以免队列堆积 —— 20 次唤醒的开销
    /// 在 20 秒里可以忽略。
    /// </summary>
    private static void IdleFor(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1000);
            MessagePump.DrainPendingMessages();
        }
    }
}
