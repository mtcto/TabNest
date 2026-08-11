using TabNest.Core.Models;
using TabNest.Interop;

namespace TabNest.App.Rail;

/// <summary>
/// 拖拽时贴在候选目标窗口顶部的提示条。
///
/// 这是拖拽合并唯一的可见反馈。没有它，用户拖完之后完全不知道
/// TabNest 是"没检测到目标"、"检测到了但目标不对"还是"目标被规则拒绝"，
/// 三种情况的表象一模一样，只能反复试。
///
/// 对齐 Groupy 的「将此窗口与拖动窗口分组」提示。
/// </summary>
internal sealed class DropHintWindow : Win32Window
{
    private const string ClassName = "TabNest.DropHint";

    private const string AcceptText = "将此窗口与拖动窗口分组";

    private string _text = AcceptText;
    private bool _isBlocked;
    private int _dpi = 96;
    private PixelRect _bounds;

    public DropHintWindow()
    {
        // 提示条必须置顶：它要盖在候选目标窗口之上，而目标窗口此刻可能在任意层级。
        // 它生命周期只有一次拖拽，不存在轨道那种"长期浮在所有应用之上"的问题。
        var exStyle = Styles.ToolWindow | Styles.NoActivate | Styles.Topmost;

        CreateWindow(ClassName, "TabNest Drop Hint", Styles.Popup, exStyle, 0, 0, 0, 0);
    }

    /// <summary>当前提示指向的窗口。0 表示未显示。</summary>
    public nint TargetHandle { get; private set; }

    /// <summary>
    /// 在目标窗口顶部显示提示。
    /// </summary>
    /// <param name="targetHandle">候选目标。</param>
    /// <param name="targetBounds">目标窗口的可见边框。</param>
    /// <param name="dpi">目标所在显示器的 DPI。</param>
    /// <param name="blockedReason">目标不可分组时的原因；为 null 表示可分组。</param>
    public void ShowFor(nint targetHandle, PixelRect targetBounds, int dpi, string? blockedReason)
    {
        TargetHandle = targetHandle;
        _dpi = dpi;
        _isBlocked = blockedReason is not null;
        _text = blockedReason ?? AcceptText;

        var height = Scale(28);
        var width = Math.Min(Scale(EstimateWidth(_text)), targetBounds.Width);

        // 贴在目标窗口左上角，与 Groupy 的位置一致 ——
        // 放中间会挡住标题文字，放右边容易被最小化/关闭按钮干扰。
        _bounds = PixelRect.FromSize(targetBounds.Left, targetBounds.Top, width, height);

        SetBounds(_bounds);
        Invalidate();
    }

    public void HideHint()
    {
        if (TargetHandle == 0)
        {
            return;
        }

        TargetHandle = 0;
        HideWindow();
    }

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Messages.Paint:
                Paint();
                return 0;

            case Messages.EraseBackground:
                return 1;

            default:
                return null;
        }
    }

    private void Paint()
    {
        using var paint = BufferedPaint.Begin(Handle, _bounds.Width, _bounds.Height);
        if (paint is null)
        {
            return;
        }

        var theme = RailTheme.Default;
        var background = _isBlocked ? 0xFF7A2E2E : theme.ActiveTabBackground;
        var foreground = _isBlocked ? 0xFFFFD0D0 : theme.ActiveText;

        var full = PixelRect.FromSize(0, 0, _bounds.Width, _bounds.Height);

        paint.Canvas.FillRect(full, background);

        // 顶部一条强调色，让提示在任何窗口标题栏配色上都能被一眼看到。
        paint.Canvas.FillRect(
            PixelRect.FromSize(0, 0, _bounds.Width, Scale(2)),
            _isBlocked ? 0xFFE05555 : theme.DropHighlight);

        var padding = Scale(10);
        var textArea = new PixelRect(padding, 0, _bounds.Width - padding, _bounds.Height);

        paint.Canvas.DrawText(_text, textArea, foreground, _dpi, TextTrim.End);
    }

    private int Scale(int value) =>
        (int)Math.Round(value * _dpi / 96.0, MidpointRounding.AwayFromZero);

    /// <summary>粗略估算文本宽度。中文按一个字约 14px 计，够用即可，不必精确测量。</summary>
    private static int EstimateWidth(string text)
    {
        var width = 24;

        foreach (var c in text)
        {
            width += c < 128 ? 8 : 15;
        }

        return width;
    }
}
