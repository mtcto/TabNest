namespace TabNest.Interop;

/// <summary>激活窗口时实际用到了降级链的哪一级。用于诊断与成功率统计。</summary>
public enum ActivationLevel
{
    NotAttempted = 0,

    /// <summary>SetForegroundWindow 直接成功。</summary>
    Direct = 1,

    /// <summary>附加前台线程输入队列后成功。</summary>
    AttachedThreadInput = 2,

    /// <summary>仅抬到 Z 序顶部，焦点未必转移。</summary>
    RaisedOnly = 3,

    /// <summary>SwitchToThisWindow 兜底。</summary>
    SwitchToThisWindow = 4,

    /// <summary>四级全部失败。</summary>
    Failed = 5,
}

/// <summary>
/// 单条窗口指令的执行结果。
///
/// 失败必须能被上层看见并如实呈现给用户 —— 计划书要求"切换失败时保留原标签，
/// 显示重试/拆分/查看原因"，静默吞掉失败会让 UI 显示一个并不存在的状态。
/// </summary>
public sealed record OperationResult
{
    public required bool Success { get; init; }

    /// <summary>失败时的 Win32 错误码，0 表示不适用。</summary>
    public int Win32Error { get; init; }

    public string? Message { get; init; }

    public ActivationLevel ActivationLevel { get; init; }

    /// <summary>指令耗时。用于统计切换 P95。</summary>
    public TimeSpan Elapsed { get; init; }

    public static OperationResult Ok(
        ActivationLevel level = ActivationLevel.NotAttempted,
        TimeSpan elapsed = default) => new()
        {
            Success = true,
            ActivationLevel = level,
            Elapsed = elapsed,
        };

    public static OperationResult Fail(string message, int win32Error = 0) => new()
    {
        Success = false,
        Message = message,
        Win32Error = win32Error,
    };
}
