namespace TabNest.Core.Models;

/// <summary>热键修饰键。与 Win32 的 MOD_* 取值一致，便于直接传给 RegisterHotKey。</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,

    /// <summary>
    /// 不因按住不放而重复触发。
    ///
    /// 切换类热键必须带上它：按住 Win+` 不放会以键盘重复速率疯狂切换标签，
    /// 一秒钟切几十次，用户完全无法控制停在哪个标签上。
    /// </summary>
    NoRepeat = 0x4000,
}

/// <summary>一个可绑定的功能。</summary>
public enum HotkeyCommand
{
    /// <summary>在当前组内切到下一个标签。</summary>
    NextTab = 0,

    /// <summary>在当前组内切到上一个标签。</summary>
    PreviousTab = 1,

    /// <summary>把当前前台窗口从组里拆出来。</summary>
    DetachCurrent = 2,

    /// <summary>以当前前台窗口为基准，收编同应用的全部未分组窗口。</summary>
    AdoptSameApp = 3,

    /// <summary>拆散当前组。</summary>
    DissolveCurrent = 4,
}

/// <summary>一个热键绑定。</summary>
/// <param name="Command">绑定的功能。</param>
/// <param name="Modifiers">修饰键组合。</param>
/// <param name="VirtualKey">虚拟键码。0 表示未绑定。</param>
/// <param name="Enabled">是否启用。</param>
public readonly record struct HotkeyBinding(
    HotkeyCommand Command,
    HotkeyModifiers Modifiers,
    uint VirtualKey,
    bool Enabled = true)
{
    public bool IsBound => Enabled && VirtualKey != 0;

    /// <summary>
    /// 两个绑定是否会互相冲突。
    ///
    /// 只比较修饰键与键码，不比较功能：同一个组合绑到两个功能上时，
    /// Windows 只会把它交给先注册的那个，后者静默失效 ——
    /// 用户看到的是"这个热键没反应"，而完全不知道原因。
    /// </summary>
    public bool ConflictsWith(HotkeyBinding other) =>
        IsBound && other.IsBound
        && VirtualKey == other.VirtualKey
        && Normalize(Modifiers) == Normalize(other.Modifiers);

    /// <summary>比较修饰键时忽略 NoRepeat —— 它不影响按键组合本身。</summary>
    private static HotkeyModifiers Normalize(HotkeyModifiers m) => m & ~HotkeyModifiers.NoRepeat;

    /// <summary>人类可读的组合描述，例如 “Ctrl + Shift + Tab”。</summary>
    public string Describe()
    {
        if (!IsBound)
        {
            return "未绑定";
        }

        var parts = new List<string>(4);

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(KeyName(VirtualKey));

        return string.Join(" + ", parts);
    }

    private static string KeyName(uint vk) => vk switch
    {
        0x09 => "Tab",
        0xC0 => "`",
        0x1B => "Esc",
        0x20 => "Space",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
        _ => $"0x{vk:X2}",
    };
}

/// <summary>全部热键设置。</summary>
public sealed record HotkeySettings
{
    /// <summary>
    /// 热键总开关。
    ///
    /// 全局热键会抢占整个系统的按键组合，与其他软件冲突时用户需要能一键关掉整组，
    /// 而不是逐条去解绑。
    /// </summary>
    public bool Enabled { get; init; } = true;

    public IReadOnlyList<HotkeyBinding> Bindings { get; init; } = Defaults;

    /// <summary>
    /// 默认绑定。
    ///
    /// Win+` 对齐 Groupy 的默认，且这个组合几乎不与常用软件冲突（macOS 上它是
    /// 同应用窗口切换，Windows 上基本空着）。全部带 NoRepeat：
    /// 按住不放会以键盘重复速率疯狂切换，用户根本停不到想要的标签上。
    ///
    /// 拆分与解散**默认不绑定**：它们是破坏性操作，误触的代价远高于便利，
    /// 应当由用户自己决定要不要给它们分配按键。
    /// </summary>
    public static IReadOnlyList<HotkeyBinding> Defaults { get; } =
    [
        new(HotkeyCommand.NextTab,
            HotkeyModifiers.Windows | HotkeyModifiers.NoRepeat, 0xC0),

        new(HotkeyCommand.PreviousTab,
            HotkeyModifiers.Windows | HotkeyModifiers.Control | HotkeyModifiers.NoRepeat, 0xC0),

        new(HotkeyCommand.DetachCurrent, HotkeyModifiers.None, 0, Enabled: false),
        new(HotkeyCommand.AdoptSameApp, HotkeyModifiers.None, 0, Enabled: false),
        new(HotkeyCommand.DissolveCurrent, HotkeyModifiers.None, 0, Enabled: false),
    ];

    /// <summary>找出互相冲突的绑定对。UI 必须显示它们，否则用户只会看到"热键没反应"。</summary>
    public IReadOnlyList<(HotkeyCommand A, HotkeyCommand B)> FindConflicts()
    {
        var conflicts = new List<(HotkeyCommand, HotkeyCommand)>();

        for (var i = 0; i < Bindings.Count; i++)
        {
            for (var j = i + 1; j < Bindings.Count; j++)
            {
                if (Bindings[i].ConflictsWith(Bindings[j]))
                {
                    conflicts.Add((Bindings[i].Command, Bindings[j].Command));
                }
            }
        }

        return conflicts;
    }

    public HotkeyBinding this[HotkeyCommand command]
    {
        get
        {
            foreach (var b in Bindings)
            {
                if (b.Command == command)
                {
                    return b;
                }
            }

            return new HotkeyBinding(command, HotkeyModifiers.None, 0, Enabled: false);
        }
    }

    public static string DescribeCommand(HotkeyCommand command) => command switch
    {
        HotkeyCommand.NextTab => "切换到下一个标签",
        HotkeyCommand.PreviousTab => "切换到上一个标签",
        HotkeyCommand.DetachCurrent => "把当前窗口拆分出去",
        HotkeyCommand.AdoptSameApp => "收编同应用的全部窗口",
        _ => "拆散当前分组",
    };
}
