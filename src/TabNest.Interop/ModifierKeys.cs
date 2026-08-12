using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// 查询修饰键的当前状态。
///
/// 用 <c>GetKeyState</c> 而不是维护自己的按键状态：后者需要低级键盘钩子，
/// 而钩子会让每一次按键都经过我们的进程，且是杀软最敏感的行为之一。
/// 这里只在用户已经开始交互（按下鼠标、松手）时查询一次，成本可忽略。
/// </summary>
public static class ModifierKeys
{
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    /// <summary>Shift 或 Ctrl 是否被按住。用作"需要修饰键"类保护的判据。</summary>
    public static bool IsShiftOrCtrlDown() => IsDown(VK_SHIFT) || IsDown(VK_CONTROL);

    public static bool IsShiftDown() => IsDown(VK_SHIFT);

    public static bool IsCtrlDown() => IsDown(VK_CONTROL);

    public static bool IsAltDown() => IsDown(VK_MENU);

    /// <summary>
    /// 检查某个键是否处于按下状态。
    /// GetKeyState 返回值的**高位**表示按下，低位表示切换态（大写锁定这类），
    /// 判错位会把"大写锁定开着"误判成"Shift 按住了"。
    /// </summary>
    private static bool IsDown(int vk) => (User32.GetKeyState(vk) & 0x8000) != 0;
}
