using System.Runtime.InteropServices;

namespace TabNest.Interop.Native;

/// <summary>
/// 手写 vtable 调用的最小 COM 支持。
///
/// **为什么不用 [ComImport]。** 裁剪会把
/// <c>System.Runtime.InteropServices.BuiltInComInterop.IsSupported</c> 关成 false
/// （直接写进发布产物的 runtimeconfig.json），此后
/// <c>Marshal.GetObjectForIUnknown</c> 一律抛 <c>NotSupportedException</c>。
///
/// 这不是理论风险，是踩过的坑：任务栏按钮策略用 [ComImport] 写的 ITaskbarList，
/// Debug 下工作正常、全部测试通过，而**每一个裁剪发布版里它都静默失效** ——
/// 失败被 catch 吞掉，降级成"保留所有按钮"，从外面完全看不出来。
/// 这个坑是靠"拿发布产物再跑一遍回归测试"才发现的，Debug 构建永远发现不了。
///
/// 手写 vtable 不依赖任何特性开关，裁剪与 NativeAOT 下行为一致，
/// 代价只是每个接口多写几行偏移量。COM 的 vtable 布局是 ABI 的一部分，
/// 前三项恒为 QueryInterface / AddRef / Release，之后按接口声明顺序排列。
/// </summary>
internal static unsafe class ComObject
{
    private const int ReleaseSlot = 2;

    /// <summary>取 vtable 第 <paramref name="slot"/> 项的函数指针。</summary>
    public static nint Method(nint instance, int slot) => ((nint**)instance)[0][slot];

    /// <summary>释放接口指针。</summary>
    public static void Release(nint instance)
    {
        if (instance != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Method(instance, ReleaseSlot))(instance);
        }
    }

    /// <summary>
    /// PROPVARIANT 的最小可用布局。
    ///
    /// x64 下共 24 字节：类型 2 字节 + 3 个保留 ushort 凑满 8 字节头，
    /// 之后是 16 字节联合体。我们只读 VT_LPWSTR，取联合体首字段当指针；
    /// <see cref="Tail"/> 只为把结构体撑到正确大小 —— 少了它，
    /// COM 会往调用栈上多写 8 字节。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        /// <summary>VT_LPWSTR。</summary>
        public const ushort VtLpwstr = 31;

        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public nint Pointer;
        public nint Tail;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }
}
