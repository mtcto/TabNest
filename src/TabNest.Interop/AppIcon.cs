using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// 应用图标。
///
/// 自绘窗口不像 WPF 那样自动继承应用图标 —— 窗口类里 <c>hIcon</c> 不设就是 0，
/// 于是标题栏左上角、Alt+Tab、任务栏全是空白。托盘同理，必须显式给一个句柄。
///
/// 图标句柄在整个进程生命周期内共享且**不销毁**：它们来自模块资源
/// （<c>LR_SHARED</c> 语义），由系统管理；每个窗口类各加载一份再各自销毁，
/// 既浪费又容易在窗口类仍然存活时把句柄销毁掉，导致图标变空白。
/// </summary>
public static class AppIcon
{
    /// <summary>
    /// 主图标组的资源 ID。
    ///
    /// **32512（IDI_APPLICATION），不是 1。** 编译器把 ApplicationIcon 生成的图标组
    /// 放在这个 ID 下，而组里各档单独的图标才编号为 1..N。
    /// 起初写成 1 —— 那正好命中"单个图标"而非"图标组"，LoadImage 返回 0，
    /// 于是托盘与标题栏全是空白，且没有任何报错。实测枚举资源才确认下来。
    ///
    /// 必须加载**图标组**：只有它带着全部尺寸，系统才能按请求尺寸挑最合适的一档。
    /// </summary>
    private const int MainIconResourceId = 32512;

    private static readonly Lazy<nint> LargeIcon = new(() => Load(
        User32.GetSystemMetrics(SM_CXICON), User32.GetSystemMetrics(SM_CYICON)));

    private static readonly Lazy<nint> SmallIcon = new(() => Load(
        User32.GetSystemMetrics(User32.SM_CXSMICON), User32.GetSystemMetrics(User32.SM_CYSMICON)));

    private const int SM_CXICON = 11;
    private const int SM_CYICON = 12;

    /// <summary>大图标，用于 Alt+Tab 与任务栏。取不到时为 0，系统会退回默认图标。</summary>
    public static nint Large => LargeIcon.Value;

    /// <summary>小图标，用于窗口标题栏与托盘。</summary>
    public static nint Small => SmallIcon.Value;

    private static nint Load(int cx, int cy)
    {
        if (cx <= 0 || cy <= 0)
        {
            return 0;
        }

        var module = WindowClass.GetModuleHandle(null);

        if (module == 0)
        {
            return 0;
        }

        // 指定尺寸而不是传 0：传 0 会拿到 ICO 里的第一档，之后由系统缩放。
        // 我们的 ICO 备了 16/20/24/32/40/48/64/128/256 九档，
        // 指定尺寸能让系统直接挑最接近的那一档，高 DPI 下清晰得多。
        return User32.LoadImage(
            module, MainIconResourceId, User32.IMAGE_ICON, cx, cy, User32.LR_DEFAULTCOLOR);
    }
}
