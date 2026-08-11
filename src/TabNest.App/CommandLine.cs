namespace TabNest.App;

internal enum RunKind
{
    Interactive,
    Diagnose,
    Benchmark,
    Version,
    Help,
}

internal sealed record RunMode(RunKind Kind, bool Json, bool IncludeAll)
{
    /// <summary>命令行模式需要控制台输出；常驻模式不需要，附加控制台反而会干扰用户 shell。</summary>
    public bool NeedsConsole => Kind is not RunKind.Interactive;
}

internal static class CommandLine
{
    public static RunMode Parse(string[] args)
    {
        var kind = RunKind.Interactive;
        var json = false;
        var includeAll = false;

        foreach (var raw in args)
        {
            switch (Normalize(raw))
            {
                case "diagnose":
                    kind = RunKind.Diagnose;
                    break;
                case "benchmark":
                    kind = RunKind.Benchmark;
                    break;
                case "version":
                case "v":
                    kind = RunKind.Version;
                    break;
                case "help":
                case "h":
                case "?":
                    kind = RunKind.Help;
                    break;
                case "json":
                    json = true;
                    break;
                case "all":
                    includeAll = true;
                    break;
                default:
                    // 未知参数不静默忽略 —— 静默忽略会让用户以为选项生效了。
                    kind = RunKind.Help;
                    break;
            }
        }

        return new RunMode(kind, json, includeAll);
    }

    private static string Normalize(string arg) =>
        arg.TrimStart('-', '/').ToLowerInvariant();
}
