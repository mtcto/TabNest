using Microsoft.Win32;
using TabNest.Core.Diagnostics;

namespace TabNest.Interop;

/// <summary>
/// 开机自启注册。
///
/// 写 HKCU 的 Run 键而不是计划任务或服务：
///   - 计划任务需要管理员权限，而 TabNest 刻意以普通权限运行
///     （提权后无法操作低完整性级别的窗口，反而有一部分应用分不了组）；
///   - 服务在用户登录前就启动，而我们要操作的是用户会话里的窗口，毫无意义。
///
/// Run 键是每用户的，与"这是我自己的选择"这一语义也吻合。
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TabNest";

    /// <summary>当前是否已注册开机自启。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// 设置开机自启。
    /// </summary>
    /// <param name="enabled">是否启用。</param>
    /// <param name="executablePath">可执行文件路径。</param>
    /// <returns>是否成功写入。失败时调用方应把设置回滚，避免界面显示的状态与实际不符。</returns>
    public static bool Set(bool enabled, string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

            if (key is null)
            {
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                FileLog.Info("已取消开机自启。");
                return true;
            }

            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            // 路径必须加引号：不加的话带空格的安装路径（Program Files）
            // 会被解析成"程序名 + 参数"，开机时启动失败且没有任何提示。
            key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);

            FileLog.Info($"已注册开机自启：{executablePath}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            FileLog.Error("写入开机自启注册表项失败。", ex);
            return false;
        }
    }
}
