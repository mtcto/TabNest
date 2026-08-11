namespace TabNest.Interop;

public enum WindowEventKind
{
    Shown,
    Hidden,
    Destroyed,

    /// <summary>前台窗口发生变化。</summary>
    Foreground,

    /// <summary>位置或尺寸变化。高频事件，必须合并。</summary>
    LocationChanged,

    /// <summary>标题变化。</summary>
    NameChanged,

    /// <summary>用户开始拖拽或调整窗口。拖拽合并的起点。</summary>
    MoveSizeStart,

    /// <summary>用户结束拖拽或调整窗口。拖拽合并的提交点。</summary>
    MoveSizeEnd,

    MinimizeStart,
    MinimizeEnd,

    /// <summary>被 DWM 隐藏（切到其他虚拟桌面、UWP 挂起）。</summary>
    Cloaked,

    Uncloaked,
}

/// <param name="Kind">事件类型。</param>
/// <param name="Handle">发生事件的窗口句柄。</param>
/// <param name="TimestampTicks">事件产生时间，用于诊断事件→UI 延迟。</param>
public readonly record struct WindowEvent(WindowEventKind Kind, nint Handle, long TimestampTicks);
