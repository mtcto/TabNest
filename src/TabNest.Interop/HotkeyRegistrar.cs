using TabNest.Core.Diagnostics;
using TabNest.Core.Models;
using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// 注册全局热键。
///
/// 用 <c>RegisterHotKey</c> 而不是低级键盘钩子（WH_KEYBOARD_LL）：
/// 低级钩子会让**每一次按键**都经过我们的进程，一旦我们的处理稍慢，
/// 整个系统的输入都会延迟；而且它是杀毒软件最敏感的行为之一，
/// 几乎必然触发误报。RegisterHotKey 由系统直接投递，零常态开销。
///
/// 代价是组合有限（不能拦截单个按键、不能做和弦），但对窗口切换类功能足够。
///
/// 热键消息投递到注册它的线程，因此必须在 UI 线程上注册，
/// 且承载它的窗口过程要处理 WM_HOTKEY。
/// </summary>
public sealed class HotkeyRegistrar : IDisposable
{
    /// <summary>热键 ID 从这个值开始编号，避开其他组件可能占用的低位 ID。</summary>
    private const int IdBase = 0xA000;

    private readonly nint _hwnd;
    private readonly List<(int Id, HotkeyCommand Command)> _registered = [];
    private bool _disposed;

    public HotkeyRegistrar(nint hwnd)
    {
        _hwnd = hwnd;
    }

    /// <summary>本次注册中被系统拒绝的绑定及原因。UI 应据此提示用户换一个组合。</summary>
    public IReadOnlyList<(HotkeyCommand Command, string Reason)> Failures { get; private set; } = [];

    /// <summary>
    /// 按设置重新注册全部热键。
    /// 先全部注销再注册，避免"改了一个绑定却残留旧的"这类难查的状态。
    /// </summary>
    public void Apply(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        UnregisterAll();

        if (!settings.Enabled || _hwnd == 0)
        {
            return;
        }

        var failures = new List<(HotkeyCommand, string)>();
        var id = IdBase;

        foreach (var binding in settings.Bindings)
        {
            if (!binding.IsBound)
            {
                continue;
            }

            if (User32.RegisterHotKey(_hwnd, id, (uint)binding.Modifiers, binding.VirtualKey))
            {
                _registered.Add((id, binding.Command));
                id++;
                continue;
            }

            // 失败几乎总是因为组合已被别的软件占用。这必须让用户知道 ——
            // 静默失败的表现是"热键没反应"，而用户完全无从判断是没绑上还是功能坏了。
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();

            var reason = error == 1409
                ? "该组合已被其他程序占用"
                : $"注册失败，错误码 {error}";

            failures.Add((binding.Command, reason));

            FileLog.Warn(
                $"热键「{HotkeySettings.DescribeCommand(binding.Command)}」"
                + $"（{binding.Describe()}）{reason}。");
        }

        Failures = failures;

        if (_registered.Count > 0)
        {
            FileLog.Info($"已注册 {_registered.Count} 个全局热键。");
        }
    }

    /// <summary>把热键消息的 ID 翻译成功能。不是我们注册的返回 null。</summary>
    public HotkeyCommand? Resolve(int id)
    {
        foreach (var (registeredId, command) in _registered)
        {
            if (registeredId == id)
            {
                return command;
            }
        }

        return null;
    }

    private void UnregisterAll()
    {
        foreach (var (id, _) in _registered)
        {
            User32.UnregisterHotKey(_hwnd, id);
        }

        _registered.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 必须注销：留下的热键会一直占用那个组合，直到进程退出为止。
        UnregisterAll();
    }
}
