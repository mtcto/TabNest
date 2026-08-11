using TabNest.Core.Layout;
using TabNest.Core.Models;
using TabNest.Interop;

namespace TabNest.App.Rail;

/// <summary>轨道上发生的用户操作。</summary>
public sealed record RailInteraction
{
    public required string GroupId { get; init; }
    public required RailAction Action { get; init; }
    public WindowIdentity Target { get; init; }
    public int InsertionIndex { get; init; }
    public PixelPoint ScreenPoint { get; init; }
}

public enum RailAction
{
    ActivateTab,
    CloseTab,
    DetachTab,
    ReorderTab,
    ShowMenu,
}

/// <summary>
/// 标签轨道窗口。
///
/// 关键样式：
///   <c>WS_EX_NOACTIVATE</c> —— 点击标签时轨道自己不抢焦点。没有它，用户每次切换标签
///     焦点都会先落到轨道上，再由我们转给目标窗口，中间必然闪一下。
///   <c>WS_EX_TOOLWINDOW</c> —— 不出现在任务栏和 Alt+Tab 里。轨道是附属界面，不是独立窗口。
///   <c>WS_POPUP</c> + 置顶 —— 无边框，始终浮在承载窗口之上。
///
/// 这个窗口完全不触碰 WPF 类型，因此常驻运行时 PresentationFramework 不会被加载。
/// </summary>
internal sealed class TabRailWindow : Win32Window
{
    private const string ClassName = "TabNest.TabRail";

    /// <summary>拖拽判定阈值。小于它视为点击，避免手抖把点击变成拖拽。</summary>
    private const int DragThreshold = 6;

    private readonly Action<RailInteraction> _onInteraction;

    private RailRenderState? _state;
    private PixelPoint _pressPoint;
    private WindowIdentity _pressedTab;
    private bool _isPressed;
    private bool _isDragging;
    private bool _mouseTracked;

    public TabRailWindow(string groupId, Action<RailInteraction> onInteraction)
    {
        GroupId = groupId;
        _onInteraction = onInteraction;

        var exStyle = Styles.ToolWindow | Styles.NoActivate | Styles.Topmost;

        CreateWindow(ClassName, "TabNest Rail", Styles.Popup, exStyle, 0, 0, 0, 0);
    }

    public string GroupId { get; }

    /// <summary>更新要显示的内容并重绘。必须在 UI 线程调用。</summary>
    public void Update(RailRenderState state)
    {
        _state = state;
        SetBounds(state.Layout.Bounds);
        Invalidate();
    }

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Messages.Paint:
                if (_state is { } state)
                {
                    RailRenderer.Paint(Handle, state);
                }

                return 0;

            // 背景由我们自己填满，让系统再擦一次只会造成闪烁。
            case Messages.EraseBackground:
                return 1;

            case Messages.MouseMove:
                OnMouseMove(ToPoint(lParam));
                return 0;

            case Messages.MouseLeave:
                OnMouseLeave();
                return 0;

            case Messages.LeftButtonDown:
                OnMouseDown(ToPoint(lParam));
                return 0;

            case Messages.LeftButtonUp:
                OnMouseUp(ToPoint(lParam));
                return 0;

            case Messages.MiddleButtonUp:
                OnMiddleClick(ToPoint(lParam));
                return 0;

            case Messages.RightButtonUp:
                OnRightClick(ToPoint(lParam));
                return 0;

            default:
                return null;
        }
    }

    // ------------------------------------------------------------------
    // 鼠标交互
    // ------------------------------------------------------------------

    private void OnMouseMove(PixelPoint point)
    {
        EnsureMouseTracking();

        if (_state is not { } state)
        {
            return;
        }

        if (_isPressed && !_isDragging && Distance(point, _pressPoint) > DragThreshold)
        {
            _isDragging = true;
        }

        if (_isDragging)
        {
            var index = state.Layout.InsertionIndexAt(point);
            UpdateState(state with { DropIndicatorIndex = index });
            return;
        }

        var hoveredTab = state.Layout.HitTestTab(point)?.Identity;
        var hoveredClose = state.Layout.HitTestCloseButton(point)?.Identity;

        if (state.HoveredIdentity != hoveredTab || state.HoveredCloseIdentity != hoveredClose)
        {
            UpdateState(state with
            {
                HoveredIdentity = hoveredTab,
                HoveredCloseIdentity = hoveredClose,
            });
        }
    }

    private void OnMouseLeave()
    {
        _mouseTracked = false;

        if (_state is { } state
            && (state.HoveredIdentity is not null || state.HoveredCloseIdentity is not null))
        {
            UpdateState(state with { HoveredIdentity = null, HoveredCloseIdentity = null });
        }
    }

    private void OnMouseDown(PixelPoint point)
    {
        if (_state is not { } state)
        {
            return;
        }

        // 关闭按钮优先于标签本体判定，否则点关闭会变成切换标签。
        if (state.Layout.HitTestCloseButton(point) is not null)
        {
            return;
        }

        if (state.Layout.HitTestTab(point) is { } tab)
        {
            _isPressed = true;
            _isDragging = false;
            _pressPoint = point;
            _pressedTab = tab.Identity;
        }
    }

    private void OnMouseUp(PixelPoint point)
    {
        if (_state is not { } state)
        {
            return;
        }

        var wasDragging = _isDragging;
        var pressed = _pressedTab;

        _isPressed = false;
        _isDragging = false;

        if (state.DropIndicatorIndex is not null)
        {
            UpdateState(state with { DropIndicatorIndex = null });
        }

        if (state.Layout.HitTestCloseButton(point) is { } closing)
        {
            Emit(RailAction.CloseTab, closing.Identity);
            return;
        }

        if (wasDragging)
        {
            // 拖出轨道垂直范围 = 拆分。这与浏览器把标签拖出标签栏成为独立窗口的心智一致。
            if (point.Y < -DragThreshold || point.Y > state.Layout.Bounds.Height + DragThreshold)
            {
                Emit(RailAction.DetachTab, pressed);
            }
            else
            {
                Emit(RailAction.ReorderTab, pressed, state.Layout.InsertionIndexAt(point));
            }

            return;
        }

        if (state.Layout.HitTestTab(point) is { } tab)
        {
            Emit(RailAction.ActivateTab, tab.Identity);
        }
    }

    private void OnMiddleClick(PixelPoint point)
    {
        if (_state?.Layout.HitTestTab(point) is { } tab)
        {
            Emit(RailAction.CloseTab, tab.Identity);
        }
    }

    private void OnRightClick(PixelPoint point)
    {
        if (_state is not { } state)
        {
            return;
        }

        var target = state.Layout.HitTestTab(point)?.Identity ?? default;
        
        CursorPosition.TryGet(out var cx, out var cy);

        _onInteraction(new RailInteraction
        {
            GroupId = GroupId,
            Action = RailAction.ShowMenu,
            Target = target,
            ScreenPoint = new PixelPoint(cx, cy),
        });
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private void EnsureMouseTracking()
    {
        if (!_mouseTracked)
        {
            _mouseTracked = TrackMouseLeave();
        }
    }

    private void UpdateState(RailRenderState state)
    {
        _state = state;
        Invalidate();
    }

    private void Emit(RailAction action, WindowIdentity target, int insertionIndex = 0) =>
        _onInteraction(new RailInteraction
        {
            GroupId = GroupId,
            Action = action,
            Target = target,
            InsertionIndex = insertionIndex,
        });

    private static PixelPoint ToPoint(nint lParam) =>
        new((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));

    private static int Distance(PixelPoint a, PixelPoint b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
}
