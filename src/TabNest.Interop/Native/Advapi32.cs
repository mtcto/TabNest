using System.Runtime.InteropServices;

namespace TabNest.Interop.Native;

internal static partial class Advapi32
{
    private const string Lib = "advapi32.dll";

    public const uint TOKEN_QUERY = 0x0008;

    /// <summary>TokenIntegrityLevel</summary>
    public const int TokenIntegrityLevel = 25;

    // 完整性级别 RID。用于把 SID 的最后一个子权限映射为完整性级别。
    public const uint SECURITY_MANDATORY_UNTRUSTED_RID = 0x00000000;
    public const uint SECURITY_MANDATORY_LOW_RID = 0x00001000;
    public const uint SECURITY_MANDATORY_MEDIUM_RID = 0x00002000;
    public const uint SECURITY_MANDATORY_HIGH_RID = 0x00003000;
    public const uint SECURITY_MANDATORY_SYSTEM_RID = 0x00004000;

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint ProcessHandle, uint DesiredAccess, out nint TokenHandle);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(
        nint TokenHandle,
        int TokenInformationClass,
        nint TokenInformation,
        uint TokenInformationLength,
        out uint ReturnLength);

    [LibraryImport(Lib)]
    public static partial nint GetSidSubAuthority(nint pSid, uint nSubAuthority);

    [LibraryImport(Lib)]
    public static partial nint GetSidSubAuthorityCount(nint pSid);
}
