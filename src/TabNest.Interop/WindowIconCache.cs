using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// 取窗口图标并缓存。
///
/// 图标要在分组栏上每帧绘制，而取图标是跨进程操作：
/// <c>WM_GETICON</c> 要发消息给目标窗口，从可执行文件抽取更是要读盘。
/// 拖动时每秒几十帧，不缓存会直接把拖动拖垮。
///
/// 缓存按窗口句柄，容量有上限 —— 分组的窗口数量是个位数到几十，
/// 但长期运行会不断有窗口进出，无上限的缓存等于慢速泄漏 HICON。
/// </summary>
public sealed class WindowIconCache : IDisposable
{
    /// <summary>缓存上限。超过就整体清空重来 —— 分组窗口数远小于此，触发即说明有异常。</summary>
    private const int Capacity = 64;

    private readonly Dictionary<nint, nint> _icons = [];

    /// <summary>由我们自己抽取、需要销毁的图标。窗口自带的图标属于目标应用，绝不能销毁。</summary>
    private readonly List<nint> _owned = [];

    private bool _disposed;

    /// <summary>取窗口图标。返回 0 表示取不到，调用方应跳过绘制而不是画一个占位方块。</summary>
    public nint GetIcon(nint hwnd, string? processPath)
    {
        if (_disposed || hwnd == 0)
        {
            return 0;
        }

        if (_icons.TryGetValue(hwnd, out var cached))
        {
            return cached;
        }

        if (_icons.Count >= Capacity)
        {
            Clear();
        }

        var icon = Resolve(hwnd, processPath);
        _icons[hwnd] = icon;

        return icon;
    }

    /// <summary>窗口关闭或退组时移除，避免缓存里堆着无效句柄。</summary>
    public void Forget(nint hwnd)
    {
        _icons.Remove(hwnd);
    }

    private nint Resolve(nint hwnd, string? processPath)
    {
        // 优先向窗口本身要图标：这是应用当前实际显示的那个，
        // 比从可执行文件抽取更准确（很多应用会在运行时换图标）。
        //
        // 必须用带超时的 SendMessageTimeout：目标应用假死时
        // 普通的 SendMessage 会把我们一起挂住。
        foreach (var type in (nint[])[User32.ICON_SMALL2, User32.ICON_SMALL, User32.ICON_BIG])
        {
            // 返回值非 0 表示消息送达；图标句柄在 out 参数里。
            var sent = User32.SendMessageTimeout(
                hwnd, User32.WM_GETICON, type, 0,
                User32.SMTO_ABORTIFHUNG, 200, out var result);

            if (sent != 0 && result != 0)
            {
                return result;
            }
        }

        // 窗口没给图标就问窗口类要。
        var classIcon = User32.GetClassLongPtr(hwnd, User32.GCLP_HICONSM);

        if (classIcon == 0)
        {
            classIcon = User32.GetClassLongPtr(hwnd, User32.GCLP_HICON);
        }

        if (classIcon != 0)
        {
            return classIcon;
        }

        // 最后才从可执行文件抽取。这一步会读盘，但结果被缓存，只发生一次。
        if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
        {
            var count = Shell32.ExtractIconEx(processPath, 0, out var large, out var small, 1);

            if (count > 0)
            {
                // 只留小图标；大图标同样要销毁，否则每个窗口泄漏一个 HICON。
                if (large != 0)
                {
                    User32.DestroyIcon(large);
                }

                if (small != 0)
                {
                    // 这个是我们自己抽出来的，由我们负责销毁。
                    _owned.Add(small);
                    return small;
                }
            }
        }

        return 0;
    }

    private void Clear()
    {
        foreach (var icon in _owned)
        {
            User32.DestroyIcon(icon);
        }

        _owned.Clear();
        _icons.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
    }
}
