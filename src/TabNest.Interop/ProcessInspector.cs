using System.Diagnostics;
using System.Runtime.InteropServices;
using TabNest.Core.Models;
using TabNest.Interop.Native;

namespace TabNest.Interop;

/// <summary>进程的一次查询结果。</summary>
public sealed record ProcessFacts
{
    public required int ProcessId { get; init; }

    /// <summary>可执行文件名（小写，含扩展名）。查询失败时为空字符串。</summary>
    public required string Name { get; init; }

    public required string? Path { get; init; }

    /// <summary>进程启动时间的 UTC ticks。0 表示查询失败，此时窗口身份的防复用能力会退化。</summary>
    public required long StartTicks { get; init; }

    public required IntegrityLevel IntegrityLevel { get; init; }

    public required bool IsPackaged { get; init; }

    public static ProcessFacts Unknown(int pid) => new()
    {
        ProcessId = pid,
        Name = string.Empty,
        Path = null,
        StartTicks = 0,
        IntegrityLevel = IntegrityLevel.Unknown,
        IsPackaged = false,
    };
}

/// <summary>
/// 查询窗口所属进程的信息。
///
/// 带缓存：窗口枚举会对同一批进程反复查询，而 <c>OpenProcess</c> + 令牌查询相对昂贵。
/// 缓存键包含进程启动时间，因此进程 ID 被复用时不会命中过期条目。
/// </summary>
public sealed class ProcessInspector
{
    private readonly Dictionary<int, ProcessFacts> _cache = [];
    private readonly Lock _gate = new();

    /// <summary>当前进程自身的完整性级别。用于判定目标窗口是否权限更高。</summary>
    public IntegrityLevel OwnIntegrityLevel { get; } = QueryOwnIntegrityLevel();

    public ProcessFacts Inspect(int processId)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(processId, out var cached))
            {
                return cached;
            }
        }

        var facts = Query(processId);

        lock (_gate)
        {
            _cache[processId] = facts;
        }

        return facts;
    }

    /// <summary>丢弃缓存。进程退出后应调用，避免 PID 复用时读到旧数据。</summary>
    public void Invalidate(int processId)
    {
        lock (_gate)
        {
            _cache.Remove(processId);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    private static ProcessFacts Query(int processId)
    {
        if (processId <= 0)
        {
            return ProcessFacts.Unknown(processId);
        }

        var handle = Kernel32.OpenProcess(
            Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);

        if (handle == 0)
        {
            // 受保护进程与其他用户的进程会走到这里，属于正常情况，不是错误。
            return ProcessFacts.Unknown(processId);
        }

        try
        {
            var path = QueryImagePath(handle);
            var name = string.IsNullOrEmpty(path)
                ? string.Empty
                : System.IO.Path.GetFileName(path).ToLowerInvariant();

            return new ProcessFacts
            {
                ProcessId = processId,
                Name = name,
                Path = path,
                StartTicks = QueryStartTicks(processId),
                IntegrityLevel = QueryIntegrityLevel(handle),
                IsPackaged = QueryIsPackaged(handle),
            };
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    private static string? QueryImagePath(nint processHandle)
    {
        Span<char> buffer = stackalloc char[512];
        var size = (uint)buffer.Length;

        return Kernel32.QueryFullProcessImageName(processHandle, 0, buffer, ref size)
            ? new string(buffer[..(int)size])
            : null;
    }

    private static long QueryStartTicks(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 进程已退出或无权限。窗口身份会退化为"句柄 + PID"，
            // 防复用能力下降但不至于出错。
            return 0;
        }
    }

    private static bool QueryIsPackaged(nint processHandle)
    {
        uint length = 0;
        var result = Kernel32.GetPackageFullName(processHandle, ref length, []);

        // 非打包应用返回 APPMODEL_ERROR_NO_PACKAGE；打包应用返回缓冲区不足。
        return result != Kernel32.APPMODEL_ERROR_NO_PACKAGE;
    }

    private static IntegrityLevel QueryIntegrityLevel(nint processHandle)
    {
        if (!Advapi32.OpenProcessToken(processHandle, Advapi32.TOKEN_QUERY, out var token))
        {
            return IntegrityLevel.Unknown;
        }

        try
        {
            Advapi32.GetTokenInformation(token, Advapi32.TokenIntegrityLevel, 0, 0, out var size);
            if (size == 0)
            {
                return IntegrityLevel.Unknown;
            }

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!Advapi32.GetTokenInformation(
                        token, Advapi32.TokenIntegrityLevel, buffer, size, out _))
                {
                    return IntegrityLevel.Unknown;
                }

                var label = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buffer);
                return MapRid(ReadLastSubAuthority(label.Label.Sid));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
    }

    /// <summary>完整性级别编码在 SID 的最后一个子权限里。</summary>
    private static uint ReadLastSubAuthority(nint sid)
    {
        if (sid == 0)
        {
            return 0;
        }

        var countPtr = Advapi32.GetSidSubAuthorityCount(sid);
        if (countPtr == 0)
        {
            return 0;
        }

        var count = Marshal.ReadByte(countPtr);
        if (count == 0)
        {
            return 0;
        }

        var valuePtr = Advapi32.GetSidSubAuthority(sid, (uint)(count - 1));
        return valuePtr == 0 ? 0 : (uint)Marshal.ReadInt32(valuePtr);
    }

    private static IntegrityLevel MapRid(uint rid) => rid switch
    {
        >= Advapi32.SECURITY_MANDATORY_SYSTEM_RID => IntegrityLevel.System,
        >= Advapi32.SECURITY_MANDATORY_HIGH_RID => IntegrityLevel.High,
        >= Advapi32.SECURITY_MANDATORY_MEDIUM_RID => IntegrityLevel.Medium,
        >= Advapi32.SECURITY_MANDATORY_LOW_RID => IntegrityLevel.Low,
        _ => IntegrityLevel.Untrusted,
    };

    private static IntegrityLevel QueryOwnIntegrityLevel()
    {
        using var self = Process.GetCurrentProcess();
        var handle = Kernel32.OpenProcess(
            Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)self.Id);

        if (handle == 0)
        {
            return IntegrityLevel.Medium;
        }

        try
        {
            var level = QueryIntegrityLevel(handle);
            return level is IntegrityLevel.Unknown ? IntegrityLevel.Medium : level;
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }
}
