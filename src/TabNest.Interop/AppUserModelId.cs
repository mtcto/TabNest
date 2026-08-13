using System.Runtime.InteropServices;
using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>
/// 读取窗口的 AppUserModelID。
///
/// 光看进程名判断"是不是同一个应用"是不够的：Chrome 的 PWA 窗口（安装到桌面的
/// 网页应用）与普通浏览器窗口跑在**同一个 chrome.exe 进程**里，用的还是同一个
/// 窗口类 Chrome_WidgetWin_1 —— 从外部看这两样完全一致。于是只要组里有一个 PWA，
/// 用户新开的任意 Chrome 窗口都会被自动分组当成"同类"吞进去。
///
/// AppUserModelID 才是正确判据，而且不是我们发明的：Windows 任务栏正是按它决定
/// 图标怎么归并 —— PWA 在任务栏上单独占一个图标，用的就是这个值。实测：
///   普通 Chrome 窗口   Chrome
///   Grok PWA 窗口      Chrome._crx_ggjocahimgbfhghnlfcnjemagj
///
/// 多数应用不显式设置这个值（Electron 应用实测返回空），此时回落到进程名比较即可 ——
/// 它们本来就各自独立进程，进程名已经够用。
///
/// **不要把这个调用放进 <see cref="WindowEnumerator.Describe"/>。**
/// 那是跨进程 COM 调用，而 Describe 在拖拽期间每帧都会对光标下的窗口执行 ——
/// 往热路径上加跨进程调用正是当初拖拽卡顿的成因之一。按需读取即可：
/// 自动分组只在新窗口出现时判定一次，成本可以忽略。
/// </summary>
public static unsafe class AppUserModelId
{
    /// <summary>IPropertyStore。vtable：0-2 IUnknown，3 GetCount，4 GetAt，5 GetValue。</summary>
    private static readonly Guid IidPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    private const int GetValueSlot = 5;

    /// <summary>PKEY_AppUserModel_ID。</summary>
    private static readonly ComObject.PropertyKey KeyAppUserModelId =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    /// <summary>
    /// 读取窗口的 AppUserModelID。窗口没有显式设置、或读取失败时返回 <c>null</c>。
    ///
    /// 读不到不是错误：绝大多数桌面应用都不设这个值。调用方应把 null 当成
    /// "这个维度上无法区分"，回落到进程名。
    /// </summary>
    public static string? Read(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        var hr = Shell32.SHGetPropertyStoreForWindow(hwnd, in IidPropertyStore, out var store);

        if (hr < 0 || store == 0)
        {
            return null;
        }

        try
        {
            var getValue =
                (delegate* unmanaged[Stdcall]<nint, ComObject.PropertyKey*, ComObject.PropVariant*, int>)
                ComObject.Method(store, GetValueSlot);

            var key = KeyAppUserModelId;
            var value = default(ComObject.PropVariant);

            if (getValue(store, &key, &value) < 0)
            {
                return null;
            }

            // VT_EMPTY（没设过）和其他类型都当作"无法区分"。
            if (value.Type != ComObject.PropVariant.VtLpwstr || value.Pointer == 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(value.Pointer);
            }
            finally
            {
                // PROPVARIANT 里的字符串由调用方释放。
                Ole32.CoTaskMemFree(value.Pointer);
            }
        }
        finally
        {
            ComObject.Release(store);
        }
    }

    /// <summary>
    /// 两个窗口是否属于同一个应用。
    ///
    /// 两边都读不到 AUMID 时都是 null，判定为相同 —— 此时应由调用方用进程名兜底。
    /// </summary>
    public static bool SameApp(nint a, nint b) =>
        string.Equals(Read(a), Read(b), StringComparison.OrdinalIgnoreCase);
}
