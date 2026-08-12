using System.Text.Json.Serialization;
using TabNest.Core.Models;

namespace TabNest.Core.Persistence;

/// <summary>
/// 源生成的 JSON 序列化上下文。
///
/// **这是发布体积的关键，不是可选的现代化改造。**
///
/// 基于反射的 <c>JsonSerializer.Serialize&lt;T&gt;</c> 带有 <c>RequiresUnreferencedCode</c>：
/// 裁剪器无法静态推断出哪些类型会被反射用到，因此只要用到它，整个应用就无法裁剪
/// （实测报 IL2026，直接构建失败）。而不裁剪意味着自包含发布 101MB，
/// 裁剪后是 20MB、单文件压缩后 11MB —— 这条差距决定了产品能否小于 Groupy 的 24.6MB。
///
/// 源生成在编译期把每个类型的读写代码摊开成普通 C# 代码，裁剪器看得见、
/// NativeAOT 也能用。代价是所有要落盘的根类型必须在这里显式登记。
///
/// 新增可持久化类型时**必须**在此加一行 <c>[JsonSerializable]</c>，
/// 否则 <see cref="AtomicJsonStore{T}"/> 会在构造时抛出并指明缺了什么 ——
/// 刻意让它响亮地失败，而不是悄悄退回反射把裁剪重新弄坏。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SessionSnapshot))]
internal sealed partial class TabNestJsonContext : JsonSerializerContext;
