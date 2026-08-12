using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace TabNest.Core.Persistence;

/// <summary>
/// 稀疏序列化：只写出与默认值不同的字段。
///
/// 借鉴 Groupy —— 本机实测它的注册表里只存了 9 个值，其余全部走默认。收益有三：
///   1. 配置文件极小，用户改过什么一目了然；
///   2. 未来调整默认值时，没显式改过的用户会自动跟上新默认，而不是被旧默认钉死；
///   3. schema 迁移只需处理实际出现的字段。
///
/// 实现上刻意**不用** <c>JsonIgnoreCondition.WhenWritingDefault</c>：它跳过的是 CLR 默认值
/// （false / 0 / null），而 TabNest 有不少产品默认为 true 的开关（如"避让原生标签"）。
/// 用它会把 <c>false</c> 当成默认值丢掉，导致用户显式关闭的开关在下次启动时又变回开启。
/// 正确做法是与默认实例逐字段差分。
/// </summary>
public static class SparseJson
{
    /// <summary>缩进选项。只影响写出格式，不参与类型解析，因此与裁剪无关。</summary>
    private static readonly JsonSerializerOptions WriterOptions = new() { WriteIndented = true };

    /// <summary>
    /// 把 <paramref name="value"/> 相对 <paramref name="defaults"/> 的差异序列化为 JSON。
    ///
    /// 取的是 <see cref="JsonTypeInfo{T}"/> 而非 <see cref="JsonSerializerOptions"/>：
    /// 后者会走反射解析，使整个应用无法裁剪（见 <see cref="TabNestJsonContext"/>）。
    /// </summary>
    public static string Serialize<T>(T value, T defaults, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(typeInfo);

        var actual = JsonSerializer.SerializeToNode(value, typeInfo);
        var baseline = JsonSerializer.SerializeToNode(defaults, typeInfo);

        var diff = Diff(actual, baseline) ?? new JsonObject();
        return diff.ToJsonString(WriterOptions);
    }

    /// <summary>
    /// 读回稀疏 JSON：把差异合并回默认值，再整体反序列化。
    ///
    /// **不能直接 <c>JsonSerializer.Deserialize</c>。** 那依赖"JSON 里没有的属性保持
    /// 属性初始化器的值"，而这个前提只在反射序列化下成立。源生成对 <c>init</c> 属性
    /// 走参数化构造路径，生成的是：
    ///
    /// <code>
    /// ObjectWithParameterizedConstructorCreator = args =>
    ///     new AppSettings(){ Enabled = (bool)args[1], Grouping = (GroupingSettings)args[8], ... }
    /// </code>
    ///
    /// 每个属性都从 args 强制赋值，JSON 里缺席的槽位是 CLR 默认值
    /// （<c>false</c> / <c>0</c> / <c>null</c>），初始化器被直接覆盖。
    /// 而稀疏写入省略的恰恰是等于默认值的字段 —— 两者相乘的结果是
    /// <c>Enabled: true</c> 被省略、读回变 <c>false</c>，**TabNest 每次重启自我禁用**，
    /// 且 <c>Grouping</c>/<c>Appearance</c> 变 null 导致一分组就空引用崩溃。
    ///
    /// 与其依赖序列化器的缺席语义，不如让读写对称：写的是"相对默认值的差异"，
    /// 读就"把差异合并回默认值"。这样无论序列化器内部如何演进都成立。
    /// </summary>
    public static T Deserialize<T>(string json, T defaults, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(typeInfo);

        var stored = JsonNode.Parse(json);
        var baseline = JsonSerializer.SerializeToNode(defaults, typeInfo);
        var merged = Merge(baseline, stored);

        return JsonSerializer.Deserialize(merged, typeInfo)
            ?? throw new JsonException("合并默认值后反序列化得到 null。");
    }

    /// <summary>
    /// 把 <paramref name="overlay"/> 覆盖到 <paramref name="baseline"/> 上。
    /// 对象逐键递归，其余（数组与标量）整体覆盖 —— 与 <see cref="Diff"/> 的粒度一致。
    /// </summary>
    private static JsonNode? Merge(JsonNode? baseline, JsonNode? overlay)
    {
        if (overlay is null)
        {
            return baseline?.DeepClone();
        }

        if (overlay is JsonObject overlayObj && baseline is JsonObject baselineObj)
        {
            var result = new JsonObject();

            foreach (var (key, value) in baselineObj)
            {
                result[key] = value?.DeepClone();
            }

            foreach (var (key, value) in overlayObj)
            {
                result[key] = Merge(baselineObj[key], value);
            }

            return result;
        }

        // 数组整体替换：写入侧也是整体写出的（"少了一条"与"没改过"必须可区分）。
        return overlay.DeepClone();
    }

    /// <summary>
    /// 计算 <paramref name="actual"/> 相对 <paramref name="baseline"/> 的差异。
    /// 完全相同时返回 null。
    /// </summary>
    private static JsonNode? Diff(JsonNode? actual, JsonNode? baseline)
    {
        if (actual is null)
        {
            return baseline is null ? null : JsonValue.Create((string?)null);
        }

        // 对象逐字段递归。
        if (actual is JsonObject actualObj)
        {
            if (baseline is not JsonObject baselineObj)
            {
                return actualObj.DeepClone();
            }

            var result = new JsonObject();
            foreach (var (key, child) in actualObj)
            {
                var childDiff = Diff(child, baselineObj[key]);
                if (childDiff is not null)
                {
                    result[key] = childDiff;
                }
            }

            return result.Count > 0 ? result : null;
        }

        // 数组不做逐元素差分：规则列表这类集合只要有任何变化，
        // 部分写出会让语义变得难以理解（"少了一条"和"没改过"无法区分）。
        if (actual is JsonArray)
        {
            return JsonNode.DeepEquals(actual, baseline) ? null : actual.DeepClone();
        }

        return JsonNode.DeepEquals(actual, baseline) ? null : actual.DeepClone();
    }
}
