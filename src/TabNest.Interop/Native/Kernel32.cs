using System.Runtime.InteropServices;

namespace TabNest.Interop.Native;

/// <summary>
/// kernel32.dll 声明。
/// 所有 P/Invoke 必须集中在 TabNest.Interop.Native 命名空间下，不得散落到其他项目。
/// </summary>
internal static partial class Kernel32
{
    /// <summary>附加到父进程控制台。用于 WinExe 在命令行模式下把输出送回用户的 shell。</summary>
    public const int AttachParentProcess = -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllocConsole();
}
