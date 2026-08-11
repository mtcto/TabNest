using System.Text.Json;
using System.Text.Json.Nodes;

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
    /// <summary>把 <paramref name="value"/> 相对 <paramref name="defaults"/> 的差异序列化为 JSON。</summary>
    public static string Serialize<T>(T value, T defaults, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(options);

        var actual = JsonSerializer.SerializeToNode(value, options);
        var baseline = JsonSerializer.SerializeToNode(defaults, options);

        var diff = Diff(actual, baseline) ?? new JsonObject();
        return diff.ToJsonString(options);
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
