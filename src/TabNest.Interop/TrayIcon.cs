using System.Runtime.InteropServices;
using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>托盘菜单项。</summary>
/// <param name="Id">命令 ID，0 表示分隔线。</param>
/// <param name="Text">显示文本。</param>
/// <param name="Checked">是否打勾。</param>
/// <param name="Enabled">是否可用。</param>
public readonly record struct TrayMenuItem(uint Id, string Text, bool Checked = false, bool Enabled = true)
{
    public static TrayMenuItem Separator => new(0, string.Empty);

    public bool IsSeparator => Id == 0;
}

/// <summary>
/// 系统托盘图标。
///
/// 直接用 <c>Shell_NotifyIcon</c> 而不引入 WPF 托盘库：常驻层不得加载 WPF，
/// 而托盘是常驻层的一部分。代价是这一百多行 P/Invoke，收益是几十 MB 的常驻内存。
/// </summary>
public sealed class TrayIcon : Win32Window
{
    private const string ClassName = "TabNest.TrayHost";
    private const uint IconId = 1;

    private Shell32.NOTIFYICONDATA _data;
    private bool _added;

    /// <summary>用户点击了菜单项。</summary>
    public event Action<uint>? MenuItemClicked;

    /// <summary>用户左键点击了托盘图标。</summary>
    public event Action? Clicked;

    /// <summary>请求托盘菜单内容。每次右键都会调用，以便反映当前状态。</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }

    public TrayIcon(string tooltip)
    {
        // 消息专用窗口：不可见，仅用于接收托盘回调。
        CreateWindow(ClassName, "TabNest", style: 0, exStyle: 0, 0, 0, 0, 0);

        _data = Shell32.NOTIFYICONDATA.Create();
        _data.hWnd = Handle;
        _data.uID = IconId;
        _data.uFlags = Shell32.NIF_MESSAGE | Shell32.NIF_ICON | Shell32.NIF_TIP;
        _data.uCallbackMessage = WindowClass.WM_TRAYICON;
        _data.hIcon = LoadOwnIcon();
        _data.SetTip(tooltip);

        _added = Shell32.Shell_NotifyIcon(Shell32.NIM_ADD, ref _data);

        if (_added)
        {
            _data.uVersionOrTimeout = Shell32.NOTIFYICON_VERSION_4;
            Shell32.Shell_NotifyIcon(Shell32.NIM_SETVERSION, ref _data);
        }
    }

    public void UpdateTooltip(string tooltip)
    {
        if (!_added)
        {
            return;
        }

        _data.SetTip(tooltip);
        _data.uFlags = Shell32.NIF_TIP;
        Shell32.Shell_NotifyIcon(Shell32.NIM_MODIFY, ref _data);
    }

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        if (msg != WindowClass.WM_TRAYICON)
        {
            return null;
        }

        // NOTIFYICON_VERSION_4 下，鼠标消息在 lParam 的低字。
        var mouseMessage = (uint)(lParam & 0xFFFF);

        switch (mouseMessage)
        {
            case WindowClass.WM_LBUTTONUP:
                Clicked?.Invoke();
                return 0;

            case WindowClass.WM_RBUTTONUP:
                ShowMenu();
                return 0;

            default:
                return null;
        }
    }

    private void ShowMenu()
    {
        var items = MenuProvider?.Invoke();
        if (items is null || items.Count == 0)
        {
            return;
        }

        var menu = WindowClass.CreatePopupMenu();
        if (menu == 0)
        {
            return;
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

            User32.GetCursorPos(out var cursor);

            // 弹出菜单前必须把宿主窗口设为前台，否则菜单会在用户点击别处时留在屏幕上不消失。
            // 这是 Shell_NotifyIcon 菜单的经典问题。
            WindowClass.SetForegroundWindow(Handle);

            var command = WindowClass.TrackPopupMenu(
                menu,
                WindowClass.TPM_RIGHTBUTTON | WindowClass.TPM_RETURNCMD,
                cursor.X, cursor.Y, 0, Handle, 0);

            if (command > 0)
            {
                MenuItemClicked?.Invoke((uint)command);
            }
        }
        finally
        {
            WindowClass.DestroyMenu(menu);
        }
    }

    private static nint LoadOwnIcon()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return 0;
        }

        // 只要小图标；大图标句柄同样要释放，否则每次启动泄漏一个 HICON。
        var count = Shell32.ExtractIconEx(path, 0, out var large, out var small, 1);
        if (count == 0)
        {
            return 0;
        }

        if (large != 0)
        {
            Gdi32.DestroyIcon(large);
        }

        return small;
    }

    public override void Dispose()
    {
        if (_added)
        {
            Shell32.Shell_NotifyIcon(Shell32.NIM_DELETE, ref _data);
            _added = false;
        }

        if (_data.hIcon != 0)
        {
            Gdi32.DestroyIcon(_data.hIcon);
            _data.hIcon = 0;
        }

        base.Dispose();
    }
}

internal static partial class Gdi32
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);
}
