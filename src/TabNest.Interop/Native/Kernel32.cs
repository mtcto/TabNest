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

    // ---- 进程 ----

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(
        uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(
        nint hProcess, uint dwFlags, Span<char> lpExeName, ref uint lpdwSize);

    /// <summary>
    /// 查询进程的包标识。返回 <see cref="APPMODEL_ERROR_NO_PACKAGE"/> 表示非打包应用，
    /// 这是判断 UWP 窗口最可靠的方式 —— 比按窗口类名猜测准确得多。
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetPackageFullName",
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetPackageFullName(
        nint hProcess, ref uint packageFullNameLength, Span<char> packageFullName);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();
}
