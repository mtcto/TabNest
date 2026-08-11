using TabNest.Core.Rules;

namespace TabNest.Core.Tests;

public sealed class RuleEngineTests
{
    private static Rule Make(
        string id,
        RuleAction action,
        string? process = null,
        string? className = null,
        string? title = null,
        bool partial = false,
        int priority = 0,
        bool enabled = true) => new()
        {
            Id = id,
            Name = id,
            Action = action,
            Enabled = enabled,
            Priority = priority,
            Conditions = [new RuleCondition
            {
                ProcessName = process,
                ClassName = className,
                Title = title,
                PartialMatch = partial,
            }],
        };

    // ------------------------------------------------------------------
    // 优先级：阻止 > 允许 > 自动分组
    // ------------------------------------------------------------------

    [Fact]
    public void 阻止规则优先于允许规则()
    {
        List<Rule> rules =
        [
            Make("allow", RuleAction.Allow, process: "app.exe", priority: 100),
            Make("block", RuleAction.Block, process: "app.exe", priority: 0),
        ];

        var verdict = RuleEngine.Evaluate("app.exe", "Cls", "标题", rules);

        // 即使允许规则优先级数值更高也不行 —— 阻止表达的是安全边界，必须能可靠生效。
        Assert.Equal(RuleAction.Block, verdict.Action);
        Assert.Equal("block", verdict.MatchedRuleId);
    }

    [Fact]
    public void 允许规则优先于自动分组规则()
    {
        List<Rule> rules =
        [
            Make("auto", RuleAction.AutoGroup, process: "app.exe"),
            Make("allow", RuleAction.Allow, process: "app.exe"),
        ];

        Assert.Equal(RuleAction.Allow, RuleEngine.Evaluate("app.exe", "Cls", "标题", rules).Action);
    }

    [Fact]
    public void 同类规则取优先级最高者()
    {
        List<Rule> rules =
        [
            Make("low", RuleAction.Block, process: "app.exe", priority: 1),
            Make("high", RuleAction.Block, process: "app.exe", priority: 9),
        ];

        Assert.Equal("high", RuleEngine.Evaluate("app.exe", "Cls", "标题", rules).MatchedRuleId);
    }

    [Fact]
    public void 同优先级时取先出现的以保证结果可复现()
    {
        List<Rule> rules =
        [
            Make("first", RuleAction.Block, process: "app.exe"),
            Make("second", RuleAction.Block, process: "app.exe"),
        ];

        // 规则求值结果不稳定会让"为什么被分组"无法解释。
        Assert.Equal("first", RuleEngine.Evaluate("app.exe", "Cls", "标题", rules).MatchedRuleId);
    }

    [Fact]
    public void 无规则命中时返回空结论()
    {
        var verdict = RuleEngine.Evaluate("other.exe", "Cls", "标题", [
            Make("r", RuleAction.Block, process: "app.exe"),
        ]);

        Assert.Null(verdict.Action);
    }

    // ------------------------------------------------------------------
    // 匹配语义
    // ------------------------------------------------------------------

    [Fact]
    public void 进程名匹配忽略大小写()
    {
        var rules = new List<Rule> { Make("r", RuleAction.Block, process: "APP.EXE") };

        Assert.Equal(RuleAction.Block, RuleEngine.Evaluate("app.exe", "Cls", "标题", rules).Action);
    }

    [Fact]
    public void 多个维度之间是与的关系()
    {
        var rules = new List<Rule>
        {
            Make("r", RuleAction.Block, process: "app.exe", className: "Other"),
        };

        // 进程名对上了但类名没对上，整条不命中。
        Assert.Null(RuleEngine.Evaluate("app.exe", "Cls", "标题", rules).Action);
    }

    [Fact]
    public void 部分匹配用于标题()
    {
        var rules = new List<Rule>
        {
            Make("r", RuleAction.Block, title: "项目", partial: true),
        };

        Assert.Equal(
            RuleAction.Block,
            RuleEngine.Evaluate("app.exe", "Cls", "我的项目 - 编辑器", rules).Action);
    }

    [Fact]
    public void 完全匹配时部分标题不命中()
    {
        var rules = new List<Rule>
        {
            Make("r", RuleAction.Block, title: "项目", partial: false),
        };

        Assert.Null(RuleEngine.Evaluate("app.exe", "Cls", "我的项目 - 编辑器", rules).Action);
    }

    [Fact]
    public void 多个子条件之间是或的关系()
    {
        var rule = new Rule
        {
            Id = "r",
            Name = "r",
            Action = RuleAction.Block,
            Conditions =
            [
                new RuleCondition { ProcessName = "a.exe" },
                new RuleCondition { ProcessName = "b.exe" },
            ],
        };

        Assert.Equal(RuleAction.Block, RuleEngine.Evaluate("b.exe", "Cls", "标题", [rule]).Action);
    }

    // ------------------------------------------------------------------
    // 安全边界
    // ------------------------------------------------------------------

    [Fact]
    public void 停用的规则不生效()
    {
        var rules = new List<Rule>
        {
            Make("r", RuleAction.Block, process: "app.exe", enabled: false),
        };

        Assert.Null(RuleEngine.Evaluate("app.exe", "Cls", "标题", rules).Action);
    }

    [Fact]
    public void 没有任何条件的规则永不命中()
    {
        var rule = new Rule
        {
            Id = "empty",
            Name = "empty",
            Action = RuleAction.Block,
            Conditions = [],
        };

        // 这几乎总是用户编辑到一半的产物，不能让它意外生效并禁用整个产品。
        Assert.False(rule.IsUsable);
        Assert.Null(RuleEngine.Evaluate("app.exe", "Cls", "标题", [rule]).Action);
    }

    [Fact]
    public void 三个维度全空的条件被识别为通配()
    {
        var condition = new RuleCondition();

        Assert.True(condition.IsCatchAll);
        Assert.True(condition.Matches("任意.exe", "任意类", "任意标题"));
    }

    [Fact]
    public void 规则测试能列出全部命中项()
    {
        List<Rule> rules =
        [
            Make("a", RuleAction.Block, process: "app.exe"),
            Make("b", RuleAction.Allow, process: "app.exe"),
            Make("c", RuleAction.Block, process: "other.exe"),
        ];

        var matches = RuleEngine.FindAllMatches(TestData.Window(1, processName: "app.exe"), rules);

        // 设置页需要展示全部命中项，用户才能理解为什么某条规则没生效。
        Assert.Equal(["a", "b"], matches.Select(r => r.Id));
    }
}
