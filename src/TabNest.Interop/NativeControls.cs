using System.Runtime.InteropServices;
using TabNest.Core.Models;
using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// 宿主在自绘窗口里的 Win32 原生控件。
///
/// 规则编辑器需要文本输入，而**自绘的文本框必须自己实现输入法**：
/// 处理 WM_IME_COMPOSITION、定位候选窗、绘制组合串下划线。这块既繁琐又容易出
/// 微妙的 bug，而中文输入是必须能用的。原生 EDIT 控件天生带输入法、
/// 无障碍（屏幕阅读器可读）、键盘导航与焦点管理 —— 这四项自绘要一一重造。
///
/// 因此设置中心的策略是：**外壳自绘，输入类控件用原生**。
/// 代价是它们外观是系统默认样式，用 WM_CTLCOLOR* 调配色可以做到基本协调。
/// </summary>
public sealed class NativeControl : IDisposable
{
    private nint _hwnd;
    private bool _disposed;

    private NativeControl(nint hwnd, int id)
    {
        _hwnd = hwnd;
        Id = id;
    }

    /// <summary>控件 ID。父窗口靠它在 WM_COMMAND 里辨认是谁发来的通知。</summary>
    public int Id { get; }

    public nint Handle => _hwnd;

    /// <summary>创建单行文本框。</summary>
    public static NativeControl CreateTextBox(
        nint parent, int id, PixelRect bounds, string text, nint font)
    {
        var hwnd = WindowClass.CreateWindowEx(
            WindowClass.WS_EX_CLIENTEDGE,
            "EDIT",
            text,
            WindowClass.WS_CHILD | WindowClass.WS_VISIBLE | WindowClass.WS_TABSTOP
            | WindowClass.ES_AUTOHSCROLL,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            parent, id, Win32Window.ModuleHandleForControls, 0);

        ApplyFont(hwnd, font);
        return new NativeControl(hwnd, id);
    }

    /// <summary>创建下拉框（只能选，不能输入）。</summary>
    public static NativeControl CreateComboBox(
        nint parent, int id, PixelRect bounds, IReadOnlyList<string> items, int selected, nint font)
    {
        ArgumentNullException.ThrowIfNull(items);

        // 高度要留出下拉列表展开的空间：CreateWindow 传的高度是"控件 + 展开后列表"的总高，
        // 只传控件高度会让下拉列表完全展不开，看起来像点了没反应。
        var hwnd = WindowClass.CreateWindowEx(
            0,
            "COMBOBOX",
            null,
            WindowClass.WS_CHILD | WindowClass.WS_VISIBLE | WindowClass.WS_TABSTOP
            | WindowClass.CBS_DROPDOWNLIST | WindowClass.WS_VSCROLL,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height + (int)(bounds.Height * 8),
            parent, id, Win32Window.ModuleHandleForControls, 0);

        ApplyFont(hwnd, font);

        foreach (var item in items)
        {
            _ = User32.SendMessageString(hwnd, WindowClass.CB_ADDSTRING, 0, item);
        }

        _ = User32.SendMessage(hwnd, WindowClass.CB_SETCURSEL, selected, 0);

        return new NativeControl(hwnd, id);
    }

    /// <summary>创建按钮。</summary>
    public static NativeControl CreateButton(
        nint parent, int id, PixelRect bounds, string text, nint font)
    {
        var hwnd = WindowClass.CreateWindowEx(
            0,
            "BUTTON",
            text,
            WindowClass.WS_CHILD | WindowClass.WS_VISIBLE | WindowClass.WS_TABSTOP
            | WindowClass.BS_PUSHBUTTON,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            parent, id, Win32Window.ModuleHandleForControls, 0);

        ApplyFont(hwnd, font);
        return new NativeControl(hwnd, id);
    }

    /// <summary>创建复选框。</summary>
    public static NativeControl CreateCheckBox(
        nint parent, int id, PixelRect bounds, string text, bool isChecked, nint font)
    {
        var hwnd = WindowClass.CreateWindowEx(
            0,
            "BUTTON",
            text,
            WindowClass.WS_CHILD | WindowClass.WS_VISIBLE | WindowClass.WS_TABSTOP
            | WindowClass.BS_AUTOCHECKBOX,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            parent, id, Win32Window.ModuleHandleForControls, 0);

        ApplyFont(hwnd, font);
        _ = User32.SendMessage(hwnd, WindowClass.BM_SETCHECK, isChecked ? 1 : 0, 0);

        return new NativeControl(hwnd, id);
    }

    /// <summary>读取文本框内容。</summary>
    public string GetText()
    {
        if (_hwnd == 0)
        {
            return string.Empty;
        }

        var length = User32.GetWindowTextLength(_hwnd);

        if (length <= 0)
        {
            return string.Empty;
        }

        Span<char> buffer = length < 512 ? stackalloc char[length + 1] : new char[length + 1];
        var written = User32.GetWindowText(_hwnd, buffer, buffer.Length);

        return written <= 0 ? string.Empty : new string(buffer[..written]);
    }

    /// <summary>下拉框当前选中项索引。</summary>
    public int GetSelectedIndex() =>
        _hwnd == 0 ? -1 : (int)User32.SendMessage(_hwnd, WindowClass.CB_GETCURSEL, 0, 0);

    /// <summary>复选框是否勾选。</summary>
    public bool IsChecked() =>
        _hwnd != 0 && User32.SendMessage(_hwnd, WindowClass.BM_GETCHECK, 0, 0) == 1;

    public void SetBounds(PixelRect bounds)
    {
        if (_hwnd != 0)
        {
            User32.SetWindowPos(
                _hwnd, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
        }
    }

    private static void ApplyFont(nint hwnd, nint font)
    {
        if (hwnd != 0 && font != 0)
        {
            // 不设字体的话原生控件用的是系统位图字体，与自绘部分放在一起
            // 会明显是两个年代的东西。
            _ = User32.SendMessage(hwnd, Gdi.WM_SETFONT, font, 1);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != 0)
        {
            _ = WindowClass.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }
}
