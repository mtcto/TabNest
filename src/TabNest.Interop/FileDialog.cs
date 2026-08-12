using System.Runtime.InteropServices;
using TabNest.Core.Diagnostics;

namespace TabNest.Interop;

/// <summary>
/// 系统的"打开文件"对话框。
///
/// 用经典的 <c>GetOpenFileNameW</c> 而不是 COM 的 IFileOpenDialog：
/// 后者要走 COM 激活与一串接口调用，而我们只需要"让用户挑一个 exe"这一件事。
/// GetOpenFileName 一次调用搞定，且不引入任何 COM 依赖 ——
/// 对裁剪与 NativeAOT 都更友好。
/// </summary>
public static partial class FileDialog
{
    private const uint OFN_FILEMUSTEXIST = 0x00001000;
    private const uint OFN_PATHMUSTEXIST = 0x00000800;
    private const uint OFN_NOCHANGEDIR = 0x00000008;
    private const uint OFN_EXPLORER = 0x00080000;

    /// <summary>
    /// 让用户选一个可执行文件。取消或出错返回 null。
    /// </summary>
    public static unsafe string? PickExecutable(nint owner, string title)
    {
        // 过滤串的格式很特别：每一项是「显示名\0通配符\0」，整串以额外一个 \0 结尾。
        // 少写一个 \0 会让对话框显示空的文件类型下拉框。
        var filter = "可执行文件 (*.exe)\0*.exe\0所有文件 (*.*)\0*.*\0\0";

        var buffer = new char[520];

        fixed (char* file = buffer)
        fixed (char* filterPtr = filter)
        fixed (char* titlePtr = title)
        {
            var ofn = new OPENFILENAME
            {
                lStructSize = (uint)sizeof(OPENFILENAME),
                hwndOwner = owner,
                lpstrFilter = (nint)filterPtr,
                lpstrFile = (nint)file,
                nMaxFile = (uint)buffer.Length,
                lpstrTitle = (nint)titlePtr,

                // NOCHANGEDIR 很关键：不加的话对话框会把**整个进程**的当前目录
                // 改成用户浏览到的最后一个位置，之后所有相对路径都会指向别处。
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER,
            };

            try
            {
                if (!GetOpenFileName(ref ofn))
                {
                    // 用户取消也走这里，不是错误，不记日志。
                    return null;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                FileLog.Error("打开文件选择对话框失败。", ex);
                return null;
            }
        }

        var end = Array.IndexOf(buffer, '\0');
        var path = end >= 0 ? new string(buffer, 0, end) : new string(buffer);

        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public uint lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        public nint lpstrFilter;
        public nint lpstrCustomFilter;
        public uint nMaxCustFilter;
        public uint nFilterIndex;
        public nint lpstrFile;
        public uint nMaxFile;
        public nint lpstrFileTitle;
        public uint nMaxFileTitle;
        public nint lpstrInitialDir;
        public nint lpstrTitle;
        public uint Flags;
        public ushort nFileOffset;
        public ushort nFileExtension;
        public nint lpstrDefExt;
        public nint lCustData;
        public nint lpfnHook;
        public nint lpTemplateName;
        public nint pvReserved;
        public uint dwReserved;
        public uint FlagsEx;
    }

    [LibraryImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOpenFileName(ref OPENFILENAME lpofn);
}
