using TabNest.Core.Models;
using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>弹出菜单项。<see cref="Id"/> 为 0 表示分隔线。</summary>
public readonly record struct PopupMenuItem(
    uint Id,
    string Text,
    bool Enabled = true,
    bool Checked = false)
{
    public static PopupMenuItem Separator => new(0, string.Empty);

    public bool IsSeparator => Id == 0;
}

/// <summary>
/// 原生弹出菜单。
///
/// 常驻层不得加载 WPF，因此右键菜单和轨道菜单都走 <c>TrackPopupMenu</c>。
/// 好处是外观与系统完全一致，且没有任何额外依赖。
/// </summary>
public static class PopupMenuBuilder
{
    /// <summary>
    /// 在指定屏幕位置弹出菜单并等待用户选择。
    /// </summary>
    /// <param name="ownerHwnd">宿主窗口，菜单消息会发给它。</param>
    /// <param name="at">屏幕坐标。</param>
    /// <param name="items">菜单项。</param>
    /// <returns>用户选中的命令 ID；取消返回 0。</returns>
    public static uint Show(nint ownerHwnd, PixelPoint at, IReadOnlyList<PopupMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return 0;
        }

        var menu = WindowClass.CreatePopupMenu();
        if (menu == 0)
        {
            return 0;
        }

        try
        {
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    WindowClass.AppendMenu(menu, WindowClass.MF_SEPARATOR, 0, null);
                    continue;
                }

                var flags = WindowClass.MF_STRING;

                if (item.Checked)
                {
                    flags |= WindowClass.MF_CHECKED;
                }

                if (!item.Enabled)
                {
                    flags |= WindowClass.MF_GRAYED;
                }

                WindowClass.AppendMenu(menu, flags, item.Id, item.Text);
            }

            // 弹出前必须把宿主设为前台，否则用户点击别处时菜单不会消失，
            // 会一直挂在屏幕上。这是 TrackPopupMenu 的经典陷阱。
            WindowClass.SetForegroundWindow(ownerHwnd);

            var command = WindowClass.TrackPopupMenu(
                menu,
                WindowClass.TPM_RIGHTBUTTON | WindowClass.TPM_RETURNCMD,
                at.X, at.Y, 0, ownerHwnd, 0);

            return command > 0 ? (uint)command : 0;
        }
        finally
        {
            WindowClass.DestroyMenu(menu);
        }
    }
}
