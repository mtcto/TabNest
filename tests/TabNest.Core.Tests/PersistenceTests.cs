using System.Text.Json;
using TabNest.Core.Models;
using TabNest.Core.Persistence;
using TabNest.Core.Rules;

namespace TabNest.Core.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tabnest-test-").FullName;

    private string PathFor(string name) => Path.Combine(_dir, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不该让测试失败。
        }
    }

    // ------------------------------------------------------------------
    // 稀疏读回：差异必须合并回默认值
    //
    // 这组测试是补上来的。此前 147 个测试全绿，产品却一分组就空引用崩溃 ——
    // 因为没有任何一个测试覆盖"磁盘上已存在一个稀疏文件，把它读回来"这条路径。
    // 改用源生成序列化后，它对 init 属性走参数化构造，JSON 里缺席的属性会被
    // CLR 默认值覆盖掉属性初始化器：Enabled 从 true 变 false（TabNest 自我禁用）、
    // Grouping/Appearance 变 null（一用就崩）。
    // ------------------------------------------------------------------

    [Fact]
    public void 读回空稀疏文件得到完整的产品默认值()
    {
        var path = PathFor("empty.json");
        File.WriteAllText(path, "{}");

        var store = new AtomicJsonStore<AppSettings>(path, AppSettings.Default);
        var loaded = store.Load();

        Assert.Equal(LoadOutcome.Loaded, loaded.Outcome);

        var s = loaded.Value;

        // 产品默认为 true 的开关绝不能因为"没写进文件"就变成 false。
        Assert.True(s.Enabled);
        Assert.True(s.ShowHoverBar);
        Assert.Equal(10, s.ClosedTabHistoryLimit);
        Assert.Equal(1, s.SchemaVersion);

        // 嵌套对象绝不能是 null —— 这正是产品崩溃的直接原因。
        Assert.NotNull(s.Appearance);
        Assert.NotNull(s.Grouping);
        Assert.NotNull(s.Rules);

        Assert.True(s.Grouping.YieldToNativeTabs);
        Assert.True(s.Appearance.ShowWindowIcon);
        Assert.Equal(TabVisibility.AlwaysVisible, s.Appearance.Visibility);
    }

    [Fact]
    public void 稀疏往返后未改动的字段保持产品默认值()
    {
        var store = new AtomicJsonStore<AppSettings>(PathFor("s.json"), AppSettings.Default);

        // 只改一个深层字段，其余全部走默认。
        store.Save(AppSettings.Default with
        {
            Grouping = AppSettings.Default.Grouping with { SameApplicationOnly = true },
        });

        var s = store.Load().Value;

        Assert.True(s.Grouping.SameApplicationOnly);

        // 同一对象里没被改的兄弟字段必须保持产品默认值，而不是 CLR 默认值。
        Assert.True(s.Grouping.YieldToNativeTabs);
        Assert.Equal(DragDelay.Third, s.Grouping.Delay);
        Assert.Equal(MiddleClickAction.CloseTab, s.Grouping.MiddleClick);

        // 完全没碰过的其他分支同理。
        Assert.True(s.Enabled);
        Assert.NotNull(s.Appearance);
        Assert.True(s.Appearance.RoundedTabs);
    }

    [Fact]
    public void 读回时显式写入的假值不会被默认值覆盖()
    {
        var store = new AtomicJsonStore<AppSettings>(PathFor("s.json"), AppSettings.Default);

        // Enabled 与 YieldToNativeTabs 产品默认都是 true，用户显式关掉了。
        store.Save(AppSettings.Default with
        {
            Enabled = false,
            Grouping = AppSettings.Default.Grouping with { YieldToNativeTabs = false },
        });

        var s = store.Load().Value;

        // 合并方向必须是"存储覆盖默认"，反了就等于用户永远关不掉这些开关。
        Assert.False(s.Enabled);
        Assert.False(s.Grouping.YieldToNativeTabs);
    }

    [Fact]
    public void 读回会话快照保持嵌套集合非空()
    {
        var path = PathFor("session.json");
        File.WriteAllText(path, "{}");

        var store = new AtomicJsonStore<SessionSnapshot>(path, SessionSnapshot.Empty);
        var snapshot = store.Load().Value;

        Assert.NotNull(snapshot.Groups);
        Assert.Empty(snapshot.Groups);
        Assert.False(snapshot.NeedsRecovery);
    }

    // ------------------------------------------------------------------
    // 稀疏写入
    // ------------------------------------------------------------------

    [Fact]
    public void 未改动的配置写出空对象()
    {
        var store = new AtomicJsonStore<AppSettings>(PathFor("s.json"), AppSettings.Default);

        store.Save(AppSettings.Default);

        Assert.Equal("{}", File.ReadAllText(store.Path).Trim());
    }

    [Fact]
    public void 只写出改动过的字段()
    {
        var store = new AtomicJsonStore<AppSettings>(PathFor("s.json"), AppSettings.Default);

        store.Save(AppSettings.Default with { RunAtStartup = true });

        var json = File.ReadAllText(store.Path);
        Assert.Contains("RunAtStartup", json);
        Assert.DoesNotContain("ShowHoverBar", json);
    }

    [Fact]
    public void 显式关闭默认为真的开关会被写出()
    {
        var store = new AtomicJsonStore<AppSettings>(PathFor("s.json"), AppSettings.Default);
        var settings = AppSettings.Default with
        {
            Grouping = AppSettings.Default.Grouping with { YieldToNativeTabs = false },
        };

        store.Save(settings);

        // 这正是不能用 JsonIgnoreCondition.WhenWritingDefault 的原因：
        // 它会把 false 当默认值丢掉，用户关掉的开关下次启动又会自己打开。
        Assert.Contains("YieldToNativeTabs", File.ReadAllText(store.Path));
        Assert.False(store.Load().Value.Grouping.YieldToNativeTabs);
    }

    [Fact]
    public void 嵌套对象只写出改动的子字段()
    {
        var store = new AtomicJsonStore<AppSettings>(PathFor("s.json"), AppSettings.Default);
        var settings = AppSettings.Default with
        {
            Appearance = AppSettings.Default.Appearance with { TallerTabs = true },
        };

        store.Save(settings);

        var json = File.ReadAllText(store.Path);
        Assert.Contains("TallerTabs", json);
        Assert.DoesNotContain("RoundedTabs", json);
    }

    [Fact]
    public void 往返后配置完全一致()
    {
        var store = new AtomicJsonStore<AppSettings>(PathFor("s.json"), AppSettings.Default);
        var settings = AppSettings.Default with
        {
            Enabled = false,
            TaskbarButtons = TaskbarButtonPolicy.ActiveOnly,
            ClosedTabHistoryLimit = 25,
            Appearance = AppSettings.Default.Appearance with
            {
                Visibility = TabVisibility.AlwaysHidden,
                CloseButton = CloseButtonPolicy.AllTabs,
            },
            Grouping = AppSettings.Default.Grouping with
            {
                Trigger = DragTrigger.RequireShift,
                Delay = DragDelay.OneSecond,
            },
            Rules =
            [
                new Rule
                {
                    Id = "r1",
                    Name = "屏蔽",
                    Action = RuleAction.Block,
                    Conditions = [new RuleCondition { ProcessName = "x.exe", PartialMatch = true }],
                },
            ],
        };

        store.Save(settings);
        var loaded = store.Load().Value;

        Assert.False(loaded.Enabled);
        Assert.Equal(TaskbarButtonPolicy.ActiveOnly, loaded.TaskbarButtons);
        Assert.Equal(25, loaded.ClosedTabHistoryLimit);
        Assert.Equal(TabVisibility.AlwaysHidden, loaded.Appearance.Visibility);
        Assert.Equal(DragTrigger.RequireShift, loaded.Grouping.Trigger);
        Assert.Equal("r1", Assert.Single(loaded.Rules).Id);
    }

    [Fact]
    public void 枚举以名称而非数字保存()
    {
        var store = new AtomicJsonStore<AppSettings>(PathFor("s.json"), AppSettings.Default);

        store.Save(AppSettings.Default with { TaskbarButtons = TaskbarButtonPolicy.ActiveOnly });

        // 数字会在枚举顺序变化时静默改变语义，名称不会。
        Assert.Contains("ActiveOnly", File.ReadAllText(store.Path));
    }

    // ------------------------------------------------------------------
    // 原子性与损坏恢复
    // ------------------------------------------------------------------

    [Fact]
    public void 文件不存在时返回默认值()
    {
        var store = new AtomicJsonStore<AppSettings>(PathFor("missing.json"), AppSettings.Default);

        var result = store.Load();

        Assert.Equal(LoadOutcome.NotFound, result.Outcome);
        Assert.True(result.Value.Enabled);
    }

    [Fact]
    public void 第二次保存会生成备份()
    {
        var store = new AtomicJsonStore<AppSettings>(PathFor("s.json"), AppSettings.Default);

        store.Save(AppSettings.Default with { RunAtStartup = true });
        store.Save(AppSettings.Default with { RunAtStartup = false });

        Assert.True(File.Exists(store.BackupPath));
    }

    [Fact]
    public void 主文件损坏时从备份恢复()
    {
        var store = new AtomicJsonStore<AppSettings>(PathFor("s.json"), AppSettings.Default);
        store.Save(AppSettings.Default with { ClosedTabHistoryLimit = 7 });
        store.Save(AppSettings.Default with { ClosedTabHistoryLimit = 7 });

        File.WriteAllText(store.Path, "{ 这不是合法 JSON");

        var result = store.Load();

        Assert.Equal(LoadOutcome.RecoveredFromBackup, result.Outcome);
        Assert.Equal(7, result.Value.ClosedTabHistoryLimit);
    }

    [Fact]
    public void 主文件与备份都损坏时回退默认值()
    {
        var store = new AtomicJsonStore<AppSettings>(PathFor("s.json"), AppSettings.Default);
        store.Save(AppSettings.Default with { RunAtStartup = true });
        store.Save(AppSettings.Default with { RunAtStartup = true });

        File.WriteAllText(store.Path, "坏了");
        File.WriteAllText(store.BackupPath, "也坏了");

        var result = store.Load();

        Assert.Equal(LoadOutcome.FellBackToDefaults, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.True(result.Value.Enabled);
    }

    [Fact]
    public void 损坏的文件被隔离保留而非删除()
    {
        var store = new AtomicJsonStore<AppSettings>(PathFor("s.json"), AppSettings.Default);
        store.Save(AppSettings.Default);
        File.WriteAllText(store.Path, "坏了");

        store.Load();

        // 用户可能想手工抢救内容，直接删掉等于替他做了决定。
        Assert.True(File.Exists(store.CorruptPath));
        Assert.Equal("坏了", File.ReadAllText(store.CorruptPath));
    }

    [Fact]
    public void 空文件按损坏处理()
    {
        var store = new AtomicJsonStore<AppSettings>(PathFor("s.json"), AppSettings.Default);
        store.Save(AppSettings.Default with { RunAtStartup = true });
        store.Save(AppSettings.Default with { RunAtStartup = true });

        File.WriteAllText(store.Path, "   ");

        // 空文件不是"空配置"，而是写入中断的残骸。
        Assert.Equal(LoadOutcome.RecoveredFromBackup, store.Load().Outcome);
    }

    [Fact]
    public void 保存后不残留临时文件()
    {
        var store = new AtomicJsonStore<AppSettings>(PathFor("s.json"), AppSettings.Default);

        store.Save(AppSettings.Default with { RunAtStartup = true });

        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public void 目录不存在时自动创建()
    {
        var store = new AtomicJsonStore<AppSettings>(
            Path.Combine(_dir, "a", "b", "s.json"),
            AppSettings.Default);

        store.Save(AppSettings.Default with { RunAtStartup = true });

        Assert.True(File.Exists(store.Path));
    }

    // ------------------------------------------------------------------
    // 前向兼容
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // 会话快照
    // ------------------------------------------------------------------

    [Fact]
    public void 会话快照可以序列化含窗口句柄的组()
    {
        // 这条曾经在生产中失败：句柄字段原本是 nint，而 System.Text.Json
        // 明确不支持序列化 IntPtr。写快照排在执行窗口指令之前，异常一抛后面全不执行，
        // 表现为"分组完全没反应"，且异常被上层吞掉，没有任何线索。
        var (manager, _) = TestData.GroupOf(2);
        var store = new AtomicJsonStore<SessionSnapshot>(PathFor("session.json"), SessionSnapshot.Empty);

        var snapshot = SessionSnapshot.Capture(manager.Groups, TestData.Now);

        var exception = Record.Exception(() => store.Save(snapshot));
        Assert.Null(exception);
    }

    [Fact]
    public void 会话快照往返后句柄与身份保持一致()
    {
        var (manager, _) = TestData.GroupOf(2);
        var store = new AtomicJsonStore<SessionSnapshot>(PathFor("session.json"), SessionSnapshot.Empty);

        store.Save(SessionSnapshot.Capture(manager.Groups, TestData.Now));
        var loaded = store.Load().Value;

        var member = loaded.Groups.Single().Members.First();

        // 身份必须完整还原，否则崩溃恢复会把窗口认错 —— 甚至挪动一个无关的新窗口。
        Assert.Equal(TestData.Id(1), member.ToIdentity());
    }

    [Fact]
    public void 有组时快照标记为脏()
    {
        var (manager, _) = TestData.GroupOf(2);

        var snapshot = SessionSnapshot.Capture(manager.Groups, TestData.Now);

        // 脏标志是崩溃判定的依据：非正常退出时它保持 true，下次启动据此还原窗口。
        Assert.True(snapshot.IsDirty);
        Assert.True(snapshot.NeedsRecovery);
    }

    [Fact]
    public void 空快照不需要恢复()
    {
        Assert.False(SessionSnapshot.Empty.NeedsRecovery);
    }

    [Fact]
    public void 未知字段不会导致读取失败()
    {
        var path = PathFor("s.json");
        File.WriteAllText(path, """{ "RunAtStartup": true, "FutureOption": 42 }""");
        var store = new AtomicJsonStore<AppSettings>(path, AppSettings.Default);

        var result = store.Load();

        // 旧版本读到新版本写的配置时不能直接崩，否则降级安装会丢配置。
        Assert.Equal(LoadOutcome.Loaded, result.Outcome);
        Assert.True(result.Value.RunAtStartup);
    }

    [Fact]
    public void 稀疏写出的配置能被后续默认值变更继承()
    {
        var path = PathFor("s.json");
        File.WriteAllText(path, """{ "RunAtStartup": true }""");

        // 模拟产品把某个默认值改掉：没显式配置过的用户应当跟上新默认。
        var newDefaults = AppSettings.Default with { ClosedTabHistoryLimit = 50 };
        var store = new AtomicJsonStore<AppSettings>(path, newDefaults);

        var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))!;
        Assert.True(loaded.RunAtStartup);
        Assert.NotNull(store);
    }
}
