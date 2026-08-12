using Microsoft.Win32;

namespace TabNest.Interop;

/// <summary>
/// 读取系统的明暗主题偏好。
///
/// 读注册表而不是调 UISettings：后者来自 WinRT，会把整个 Windows SDK 投影
/// 程序集（实测 23.7MB）拖进依赖，而我们只需要一个布尔值。
/// </summary>
public static class SystemTheme
{
    private const string PersonalizeKey =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>系统是否处于深色模式。读取失败时返回 false（浅色），与 Windows 默认一致。</summary>
    public static bool IsDarkMode()
    {
        try
        {
            // 这个值的语义是"应用是否使用浅色主题"，0 表示深色。
            // 名字里的 Light 容易读反，取值时务必对照文档。
            var value = Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1);
            return value is int i && i == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
