using TabNest.Core.Rules;
using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// 探测系统级前置条件。
///
/// 借鉴 Groupy：它会明确提示"需要启用桌面窗口管理器"和"需要启用拖动时显示窗口内容"，
/// 而不是让功能静默失灵。这两项一旦不满足，产品的核心交互就无法工作，
/// 用户必须被告知而不是自己猜。
/// </summary>
public static class SystemEnvironmentProbe
{
    public static SystemEnvironment Probe(ProcessInspector processes)
    {
        ArgumentNullException.ThrowIfNull(processes);

        return new SystemEnvironment(
            IsDwmEnabled: IsCompositionEnabled(),
            IsDragFullWindowsEnabled: IsDragFullWindowsEnabled(),
            OwnIntegrityLevel: processes.OwnIntegrityLevel);
    }

    private static bool IsCompositionEnabled()
    {
        // Windows 8 以后 DWM 无法关闭，但远程桌面、部分虚拟机显示驱动下仍可能返回 false。
        return Dwmapi.DwmIsCompositionEnabled(out var enabled) != 0 || enabled;
    }

    private static bool IsDragFullWindowsEnabled()
    {
        var value = 0;
        return !User32.SystemParametersInfo(User32.SPI_GETDRAGFULLWINDOWS, 0, ref value, 0)
            || value != 0;
    }
}
