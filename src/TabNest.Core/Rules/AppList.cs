using TabNest.Core.Models;

namespace TabNest.Core.Rules;

/// <summary>
/// 应用清单：一份进程名列表 + 一个"这是允许还是阻止"的开关。
///
/// 这是对 <see cref="Rule"/> 的一层薄封装，而不是新的数据模型。
/// 规则引擎与持久化格式完全不变，只是把界面上暴露的东西收窄到用户真正需要的那点：
///
/// 完整的规则编辑器（进程 + 窗口类 + 标题的组合匹配、部分匹配、多条件、优先级）
/// 表达力更强，但绝大多数人想做的只是"这个应用别参与分组"或"只让这几个应用分组"。
/// 为了覆盖少数复杂场景而让所有人面对一张多字段表单，是把成本摊错了地方。
/// Groupy 的做法同样是一份列表加一个复选框，这个取舍已经被验证过。
///
/// 需要按窗口类或标题精细匹配的，仍可直接编辑 settings.json 里的 rules 数组 ——
/// 引擎照常支持，只是不再有对应的界面。
/// </summary>
public static class AppList
{
    /// <summary>本列表管理的规则用这个前缀标识，与手工编辑的复杂规则区分开。</summary>
    private const string IdPrefix = "app:";

    /// <summary>
    /// 从设置里取出清单中的应用。
    ///
    /// 只认本列表自己创建的规则：用户手工写进 settings.json 的复杂规则不该出现在
    /// 这个简单列表里 —— 那会让它显示成一个光秃秃的进程名，而它真正的匹配条件
    /// （窗口类、标题）在界面上完全看不见，改一下就把人家的规则毁了。
    /// </summary>
    public static IReadOnlyList<string> Apps(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var apps = new List<string>();

        foreach (var rule in settings.Rules)
        {
            if (!IsManaged(rule))
            {
                continue;
            }

            var name = rule.Conditions.Count > 0 ? rule.Conditions[0].ProcessName : null;

            if (!string.IsNullOrWhiteSpace(name))
            {
                apps.Add(name);
            }
        }

        return apps;
    }

    /// <summary>本列表是否管理这条规则。</summary>
    public static bool IsManaged(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule.Id.StartsWith(IdPrefix, StringComparison.Ordinal);
    }

    /// <summary>把一个应用加入清单。已存在则原样返回。</summary>
    public static AppSettings Add(AppSettings settings, string processName)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var name = Normalize(processName);

        if (string.IsNullOrEmpty(name))
        {
            return settings;
        }

        if (Apps(settings).Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
        {
            return settings;
        }

        var rule = new Rule
        {
            Id = IdPrefix + name.ToLowerInvariant(),
            Name = name,
            Action = ActionFor(settings.Grouping.AllowListOnly),
            Conditions = [new RuleCondition { ProcessName = name }],
        };

        return settings with { Rules = [.. settings.Rules, rule] };
    }

    /// <summary>把一个应用移出清单。</summary>
    public static AppSettings Remove(AppSettings settings, string processName)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var name = Normalize(processName);

        var remaining = settings.Rules
            .Where(r => !(IsManaged(r)
                && r.Conditions.Count > 0
                && string.Equals(r.Conditions[0].ProcessName, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return remaining.Count == settings.Rules.Count
            ? settings
            : settings with { Rules = remaining };
    }

    /// <summary>
    /// 切换"仅允许指定应用"。
    ///
    /// 必须同时把清单里所有规则的动作改掉：复选框决定的正是这份列表的含义 ——
    /// 勾上时它是允许清单，不勾时它是阻止清单。只改开关不改规则，
    /// 会让列表里的应用在两种模式下表达完全相反的意思，而界面上看不出任何差别。
    /// </summary>
    public static AppSettings SetAllowListOnly(AppSettings settings, bool allowListOnly)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var action = ActionFor(allowListOnly);

        var rules = settings.Rules
            .Select(r => IsManaged(r) && r.Action != action ? r with { Action = action } : r)
            .ToList();

        return settings with
        {
            Grouping = settings.Grouping with { AllowListOnly = allowListOnly },
            Rules = rules,
        };
    }

    private static RuleAction ActionFor(bool allowListOnly) =>
        allowListOnly ? RuleAction.Allow : RuleAction.Block;

    /// <summary>
    /// 从用户选的路径或进程名归一出可执行文件名。
    ///
    /// 规则按进程名匹配，因此选了完整路径也只取文件名部分 ——
    /// 同一个应用装在不同位置（便携版、多版本）时，用户的意图几乎总是"这个应用"
    /// 而不是"这个路径下的这一份"。
    /// </summary>
    public static string Normalize(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName))
        {
            return string.Empty;
        }

        var trimmed = pathOrName.Trim().Trim('"');

        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        var name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;

        return name.Trim();
    }
}
