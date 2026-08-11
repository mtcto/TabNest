using System.Reflection;

namespace TabNest.App;

internal static class BuildInfo
{
    public static string DisplayVersion { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var asm = typeof(BuildInfo).Assembly;
        var informational = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return asm.GetName().Version?.ToString() ?? "0.0.0";
        }

        // SDK 会在版本号后附加 "+<commit sha>"，展示时截掉。
        var plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    }
}
