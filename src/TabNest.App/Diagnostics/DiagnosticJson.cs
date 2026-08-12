using System.Text.Json.Serialization;

namespace TabNest.App.Diagnostics;

/// <summary>
/// <c>--diagnose --json</c> 的输出结构。
///
/// 刻意用具名记录而非匿名类型：匿名类型只能走反射序列化，而反射序列化
/// 会让整个应用无法裁剪（见 TabNestJsonContext）。为了一个诊断开关
/// 把发布体积从 11MB 推到 101MB 显然不划算。
///
/// 字段名统一 camelCase，由 <see cref="DiagnosticJsonContext"/> 的命名策略产生 ——
/// 早期匿名类型版本里显式写的字段是 camelCase、直接取属性的却是 PascalCase
/// （dpi 与 IsCloaked 混在同一个对象里），脚本消费方需要记两套规则。
/// </summary>
internal sealed record DiagnosticReport
{
    public required string Version { get; init; }
    public required EnvironmentReport Environment { get; init; }
    public required IReadOnlyList<WindowReport> Windows { get; init; }
}

internal sealed record EnvironmentReport
{
    public required bool DwmEnabled { get; init; }
    public required bool DragFullWindows { get; init; }
    public required string IntegrityLevel { get; init; }
}

internal sealed record BoundsReport
{
    public required int Left { get; init; }
    public required int Top { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

internal sealed record WindowReport
{
    /// <summary>句柄的十六进制文本。用文本而非数字，避免脚本语言把它当浮点数丢精度。</summary>
    public required string Handle { get; init; }

    public required int ProcessId { get; init; }
    public required string Title { get; init; }
    public required string ProcessName { get; init; }
    public required string ClassName { get; init; }
    public required BoundsReport Bounds { get; init; }
    public required int Dpi { get; init; }
    public required string Monitor { get; init; }
    public required bool IsCloaked { get; init; }
    public required bool IsToolWindow { get; init; }
    public required bool HasOwner { get; init; }
    public required bool IsNotResponding { get; init; }
    public required bool IsUwp { get; init; }
    public required bool HasCustomTitleBar { get; init; }
    public required string IntegrityLevel { get; init; }
    public required bool Eligible { get; init; }
    public required string Severity { get; init; }
    public required string Reason { get; init; }
    public required string Explanation { get; init; }
    public string? Suggestion { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DiagnosticReport))]
internal sealed partial class DiagnosticJsonContext : JsonSerializerContext;
