using System.Runtime.InteropServices;

namespace TabNest.Interop.Native;

internal static partial class Ole32
{
    private const string Lib = "ole32.dll";

    /// <summary>进程内 COM 服务器。shell 的 TaskbarList 就是进程内对象。</summary>
    public const uint CLSCTX_INPROC_SERVER = 0x1;

    /// <summary>
    /// 创建 COM 对象。
    ///
    /// 刻意不用 <c>Activator.CreateInstance(Type.GetTypeFromCLSID(...))</c>：
    /// 那条路径经由反射拿类型，裁剪器无法静态分析（实测报 IL2072），
    /// 会让整个应用无法裁剪 —— 而裁剪是发布体积从 101MB 降到 11MB 的前提。
    /// 直接 P/Invoke 既没有这个问题，也少一层运行时类型解析。
    /// </summary>
    [LibraryImport(Lib)]
    public static partial int CoCreateInstance(
        in Guid rclsid,
        nint pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out nint ppv);
}
