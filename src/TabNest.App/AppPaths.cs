namespace TabNest.App;

/// <summary>
/// TabNest 的本地数据位置。
/// 全部落在 LocalAppData 下，不写注册表、不碰漫游配置 ——
/// 窗口位置与工作区是机器相关的，跟随用户漫游到另一台机器上没有意义。
/// </summary>
internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TabNest");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>组快照。每次窗口写操作前落盘，崩溃后据此恢复。</summary>
    public static string SessionFile => Path.Combine(Root, "session.json");

    /// <summary>已保存的分组。与会话快照分开：前者是用户资产，后者是崩溃恢复的临时数据。</summary>
    public static string WorkspacesFile => Path.Combine(Root, "workspaces.json");

    public static string LogDirectory => Path.Combine(Root, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}
