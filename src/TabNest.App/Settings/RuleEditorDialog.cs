using TabNest.Core.Models;
using TabNest.Core.Rules;
using TabNest.Interop;

namespace TabNest.App.Settings;

/// <summary>
/// 编辑单条规则的对话框。
///
/// **外壳自绘，输入控件用 Win32 原生控件。**
///
/// 自绘的文本框必须自己实现输入法：处理 WM_IME_COMPOSITION、定位候选窗、
/// 绘制组合串下划线。这块既繁琐又容易出微妙的 bug，而规则里要填进程名与窗口标题，
/// 中文输入是必须能用的。原生 EDIT 还天生带无障碍（屏幕阅读器可读）、
/// 键盘导航与焦点管理 —— 这四项自绘要一一重造。
///
/// 代价是原生控件外观是系统默认样式；用 WM_CTLCOLOR* 调配色可做到与外壳基本协调。
/// </summary>
public sealed class RuleEditorDialog : Win32Window
{
    private const string ClassName = "TabNest.RuleEditor";

    private const int IdName = 100;
    private const int IdAction = 101;
    private const int IdProcess = 102;
    private const int IdClass = 103;
    private const int IdTitle = 104;
    private const int IdPartial = 105;
    private const int IdOk = 106;
    private const int IdCancel = 107;

    private static readonly string[] ActionNames = ["阻止分组", "允许分组", "自动分组"];

    private readonly List<NativeControl> _controls = [];
    private readonly SettingsTheme _theme;
    private readonly Rule _original;

    private NativeControl? _name;
    private NativeControl? _action;
    private NativeControl? _process;
    private NativeControl? _class;
    private NativeControl? _title;
    private NativeControl? _partial;

    private nint _labelFont;
    private nint _backgroundBrush;

    /// <summary>用户点了确定时的结果；取消时为 null。</summary>
    public Rule? Result { get; private set; }

    private RuleEditorDialog(Rule rule, SettingsTheme theme)
    {
        _original = rule;
        _theme = theme;
    }

    /// <summary>
    /// 打开对话框并等待用户完成。返回编辑后的规则，取消则返回 null。
    /// </summary>
    public static Rule? Show(nint owner, Rule rule, SettingsTheme theme)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(theme);

        using var dialog = new RuleEditorDialog(rule, theme);
        dialog.Create(owner);
        dialog.RunModalLoop(owner);

        return dialog.Result;
    }

    private bool _closed;

    private void Create(nint owner)
    {
        var dpi = owner != 0 ? (int)User32Dpi.ForWindow(owner) : 96;
        int S(int v) => v * dpi / 96;

        var width = S(460);
        var height = S(380);

        CreateWindow(
            ClassName,
            "编辑规则",
            (uint)(User32Styles.Caption | User32Styles.SysMenu | User32Styles.DialogFrame),
            exStyle: 0,
            x: CwUseDefault, y: CwUseDefault,
            width, height);

        // 对话框跟随所有者：所有者关闭时它不该留在屏幕上。
        if (owner != 0)
        {
            SetOwner(owner);
        }

        BuildControls(dpi);
        ShowAndActivate();
    }

    private const int CwUseDefault = unchecked((int)0x80000000);

    private void BuildControls(int dpi)
    {
        int S(int v) => v * dpi / 96;

        using var probe = MeasureCanvas.Create(Handle);
        _labelFont = probe.Canvas.FontFor(TextStyle.Body, dpi);

        var pad = S(16);
        var labelWidth = S(96);
        var rowHeight = S(26);
        var gap = S(12);
        var fieldLeft = pad + labelWidth;
        var fieldWidth = ClientBounds.Width - fieldLeft - pad;

        var y = pad;

        _name = Add(NativeControl.CreateTextBox(
            Handle, IdName, PixelRect.FromSize(fieldLeft, y, fieldWidth, rowHeight),
            _original.Name, _labelFont));
        y += rowHeight + gap;

        _action = Add(NativeControl.CreateComboBox(
            Handle, IdAction, PixelRect.FromSize(fieldLeft, y, fieldWidth, rowHeight),
            ActionNames, (int)_original.Action, _labelFont));
        y += rowHeight + gap;

        // 三个匹配维度取第一条件的值。多条件规则由 settings.json 编辑，
        // 界面上强行展开会让这个对话框复杂度翻倍，而多条件是少数情况。
        var condition = _original.Conditions.Count > 0
            ? _original.Conditions[0]
            : new RuleCondition();

        _process = Add(NativeControl.CreateTextBox(
            Handle, IdProcess, PixelRect.FromSize(fieldLeft, y, fieldWidth, rowHeight),
            condition.ProcessName ?? string.Empty, _labelFont));
        y += rowHeight + gap;

        _class = Add(NativeControl.CreateTextBox(
            Handle, IdClass, PixelRect.FromSize(fieldLeft, y, fieldWidth, rowHeight),
            condition.ClassName ?? string.Empty, _labelFont));
        y += rowHeight + gap;

        _title = Add(NativeControl.CreateTextBox(
            Handle, IdTitle, PixelRect.FromSize(fieldLeft, y, fieldWidth, rowHeight),
            condition.Title ?? string.Empty, _labelFont));
        y += rowHeight + gap;

        _partial = Add(NativeControl.CreateCheckBox(
            Handle, IdPartial, PixelRect.FromSize(fieldLeft, y, fieldWidth, rowHeight),
            "标题使用「包含」而非完全相等", condition.PartialMatch, _labelFont));
        y += rowHeight + S(24);

        var buttonWidth = S(96);
        var buttonTop = ClientBounds.Height - pad - rowHeight;

        Add(NativeControl.CreateButton(
            Handle, IdOk,
            PixelRect.FromSize(
                ClientBounds.Width - pad - (buttonWidth * 2) - gap, buttonTop, buttonWidth, rowHeight),
            "确定", _labelFont));

        Add(NativeControl.CreateButton(
            Handle, IdCancel,
            PixelRect.FromSize(ClientBounds.Width - pad - buttonWidth, buttonTop, buttonWidth, rowHeight),
            "取消", _labelFont));

        _ = y;
    }

    private NativeControl Add(NativeControl control)
    {
        _controls.Add(control);
        return control;
    }

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Messages.Paint:
                PaintBackground();
                return 0;

            case Messages.EraseBackground:
                return 1;

            case Messages.Command:
                OnCommand((int)(wParam & 0xFFFF));
                return 0;

            // 让原生控件的背景与自绘外壳一致，否则会是"深色面板上一块块白底"。
            case Messages.CtlColorStatic:
            case Messages.CtlColorBtn:
                return HandleCtlColor(wParam);

            case Messages.Close:
                _closed = true;
                HideWindow();
                return 0;

            default:
                return null;
        }
    }

    private void OnCommand(int id)
    {
        switch (id)
        {
            case IdOk:
                Result = Collect();
                _closed = true;
                HideWindow();
                break;

            case IdCancel:
                Result = null;
                _closed = true;
                HideWindow();
                break;

            default:
                break;
        }
    }

    /// <summary>把控件里的内容收成一条规则。</summary>
    private Rule Collect()
    {
        var name = _name?.GetText().Trim() ?? string.Empty;
        var action = (RuleAction)Math.Max(0, _action?.GetSelectedIndex() ?? 0);

        var condition = new RuleCondition
        {
            ProcessName = Nullable(_process?.GetText()),
            ClassName = Nullable(_class?.GetText()),
            Title = Nullable(_title?.GetText()),
            PartialMatch = _partial?.IsChecked() ?? false,
        };

        // 三个维度全空的条件会匹配**任何**窗口。一条空的阻止规则等于禁用整个产品，
        // 因此空条件不予保存 —— 让规则退化成"没有条件"，而领域层规定
        // 没有条件的规则永不生效，这样最坏情况也只是这条规则不起作用。
        var conditions = condition.IsCatchAll
            ? Array.Empty<RuleCondition>()
            : [condition];

        return _original with
        {
            Name = string.IsNullOrWhiteSpace(name) ? "未命名规则" : name,
            Action = action,
            Conditions = conditions,
        };

        static string? Nullable(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private void PaintBackground()
    {
        var client = ClientBounds;

        using var paint = BufferedPaint.Begin(Handle, client.Width, client.Height);
        if (paint is null)
        {
            return;
        }

        var c = paint.Canvas;
        var dpi = Dpi;
        int S(int v) => v * dpi / 96;

        c.FillRect(client, _theme.WindowBackground);

        var pad = S(16);
        var rowHeight = S(26);
        var gap = S(12);
        var labelWidth = S(96);

        var labels = new[]
        {
            "规则名称", "动作", "进程名", "窗口类", "窗口标题",
        };

        var y = pad;

        foreach (var label in labels)
        {
            c.DrawStyledText(
                label,
                new PixelRect(pad, y, pad + labelWidth - S(8), y + rowHeight),
                _theme.Text, TextStyle.Body, dpi);

            y += rowHeight + gap;
        }

        // 底部说明：这三个字段留空意味着什么，必须写清楚。
        var hintTop = y + rowHeight + S(8);

        c.DrawStyledText(
            "进程名、窗口类、窗口标题之间是「且」的关系：都填了就必须同时满足，留空表示不限制。\n"
            + "三项全部留空的规则不会生效 —— 那会匹配任何窗口，一条空的阻止规则等于禁用整个产品。",
            new PixelRect(pad, hintTop, client.Width - pad, hintTop + S(60)),
            _theme.TextSecondary, TextStyle.Caption, dpi, wrap: true);
    }

    private nint HandleCtlColor(nint hdc)
    {
        _backgroundBrush = ControlColors.Apply(
            hdc, _theme.Text, _theme.WindowBackground, _backgroundBrush);

        return _backgroundBrush;
    }

    /// <summary>
    /// 跑一个模态消息循环，直到对话框关闭。
    ///
    /// 期间禁用所有者窗口，这正是"模态"的实质 —— 不禁用的话用户可以回到设置窗口
    /// 继续操作，而这个对话框还悬在那里，两边的状态会打架。
    /// </summary>
    private void RunModalLoop(nint owner)
    {
        var reEnable = owner != 0 && User32Modal.Enable(owner, false);

        try
        {
            while (!_closed && IsCreated)
            {
                MessagePump.DrainPendingMessages();
                Thread.Sleep(8);
            }
        }
        finally
        {
            if (reEnable)
            {
                User32Modal.Enable(owner, true);
            }
        }
    }

    public new void Dispose()
    {
        foreach (var control in _controls)
        {
            control.Dispose();
        }

        _controls.Clear();

        if (_backgroundBrush != 0)
        {
            ControlColors.ReleaseBrush(_backgroundBrush);
            _backgroundBrush = 0;
        }

        base.Dispose();
    }
}
