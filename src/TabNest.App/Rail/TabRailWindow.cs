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

    /// <summary>拖动分组条时的位移，单位物理像素。</summary>
    public int DeltaX { get; init; }

    public int DeltaY { get; init; }
}

public enum RailAction
{
    ActivateTab,
    CloseTab,
    DetachTab,
    ReorderTab,
    ShowMenu,

    /// <summary>关闭整组的全部窗口。</summary>
    CloseGroup,

    /// <summary>拖动分组条，整组窗口一起移动。</summary>
    MoveGroup,
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
    private bool _isMovingGroup;
    private PixelPoint _lastGroupDragPoint;

    private nint _owner;

    public TabRailWindow(string groupId, Action<RailInteraction> onInteraction)
    {
        GroupId = groupId;
        _onInteraction = onInteraction;

        // 刻意不加 WS_EX_TOPMOST：置顶会让轨道浮在所有应用之上，
        // 切到别的程序时它还赖在屏幕上，而且其他窗口能穿插到轨道和成员窗口之间。
        // 正确做法是把成员窗口设为轨道的属主，让轨道跟随它一起升降。
        var exStyle = Styles.ToolWindow | Styles.NoActivate;

        CreateWindow(ClassName, "TabNest Rail", Styles.Popup, exStyle, 0, 0, 0, 0);
    }

    public string GroupId { get; }

    /// <summary>更新要显示的内容并重绘。必须在 UI 线程调用。</summary>
    /// <param name="state">渲染状态。</param>
    /// <param name="ownerHwnd">当前活动成员窗口，作为轨道的属主。</param>
    public void Update(RailRenderState state, nint ownerHwnd)
    {
        var previous = _state;
        _state = state;

        // 尝试建立属主关系。成功的话轨道会自动跟随成员窗口在 Z 序中升降。
        //
        // 但**绝不能只依赖它**：SetWindowLongPtr(GWLP_HWNDPARENT) 跨进程设置属主
        // 在文档上就不受支持，失败时也不保证设置错误码，会出现"报告成功但什么都没做"。
        // 那样轨道就是一个普通窗口，静静待在成员窗口后面被完全盖住 ——
        // 表现正是"分组成功了但看不到分组栏"。
        var ownerChanged = ownerHwnd != _owner && ownerHwnd != 0;

        if (ownerChanged)
        {
            _owner = ownerHwnd;
            SetOwner(ownerHwnd);
        }

        SetBounds(state.Layout.Bounds);

        // Z 序**只在属主变化时**维护，不在每次位置更新时做。
        //
        // 拖动窗口时这个方法每秒被调用几十次，每次都调一遍 SetWindowPos 调整 Z 序
        // 是拖动卡顿的主要来源 —— 而位置变化根本不需要动 Z 序。
        // 前台切换导致的层级变动由 RestackAboveOwner 单独处理。
        if (ownerChanged)
        {
            PlaceBelow(_owner);
        }

        // 尺寸或圆角变化后才重设区域：SetWindowRgn 会触发重绘，每帧都调用会闪。
        var bounds = state.Layout.Bounds;
        var radius = state.Layout.TopCornerRadius;

        if (previous is null
            || previous.Layout.Bounds.Width != bounds.Width
            || previous.Layout.Bounds.Height != bounds.Height
            || previous.Layout.TopCornerRadius != radius)
        {
            ApplyTopRoundedRegion(bounds.Width, bounds.Height, radius);
        }

        Invalidate();
    }

    /// <summary>把轨道重新贴到属主窗口正下方。属主的 Z 序变化后调用。</summary>
    public void RestackAboveOwner()
    {
        if (_owner != 0)
        {
            PlaceBelow(_owner);
        }
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

        // 拖动整组：按屏幕坐标算增量，因为轨道本身会跟着一起移动，
        // 用窗口内坐标算会得到一个不断自我抵消的错误增量。
        if (_isPressed && _isMovingGroup)
        {
            if (!CursorPosition.TryGet(out var sx, out var sy))
            {
                return;
            }

            var dx = sx - _lastGroupDragPoint.X;
            var dy = sy - _lastGroupDragPoint.Y;

            if (dx == 0 && dy == 0)
            {
                return;
            }

            _lastGroupDragPoint = new PixelPoint(sx, sy);

            _onInteraction(new RailInteraction
            {
                GroupId = GroupId,
                Action = RailAction.MoveGroup,
                DeltaX = dx,
                DeltaY = dy,
            });

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
        var hoveredCloseGroup = state.Layout.CloseGroupButton.Width > 0
            && state.Layout.CloseGroupButton.Contains(point);

        if (state.HoveredIdentity != hoveredTab
            || state.HoveredCloseIdentity != hoveredClose
            || state.IsCloseGroupHovered != hoveredCloseGroup)
        {
            UpdateState(state with
            {
                HoveredIdentity = hoveredTab,
                HoveredCloseIdentity = hoveredClose,
                IsCloseGroupHovered = hoveredCloseGroup,
            });
        }
    }

    private void OnMouseLeave()
    {
        _mouseTracked = false;

        if (_state is { } state
            && (state.HoveredIdentity is not null
                || state.HoveredCloseIdentity is not null
                || state.IsCloseGroupHovered))
        {
            UpdateState(state with
            {
                HoveredIdentity = null,
                HoveredCloseIdentity = null,
                IsCloseGroupHovered = false,
            });
        }
    }

    private void OnMouseDown(PixelPoint point)
    {
        if (_state is not { } state)
        {
            return;
        }

        // 左侧菜单按钮：按下即弹出，与 Windows 菜单栏的惯例一致。
        if (state.Layout.MenuButton.Width > 0 && state.Layout.MenuButton.Contains(point))
        {
            EmitMenu(state.Layout.MenuButton, default);
            return;
        }

        // 关闭整组按钮在松手时才触发，避免误按下即关掉一整组窗口。
        if (state.Layout.CloseGroupButton.Width > 0
            && state.Layout.CloseGroupButton.Contains(point))
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
            _isMovingGroup = false;
            _pressPoint = point;
            _pressedTab = tab.Identity;
            return;
        }

        // 按在空白处 = 拖动整组，手感与拖普通窗口的标题栏一致。
        if (state.Layout.IsDragArea(point))
        {
            CursorPosition.TryGet(out var sx, out var sy);
            _isPressed = true;
            _isDragging = false;
            _isMovingGroup = true;
            _lastGroupDragPoint = new PixelPoint(sx, sy);
            _pressedTab = default;
        }
    }

    private void OnMouseUp(PixelPoint point)
    {
        if (_state is not { } state)
        {
            return;
        }

        var wasDragging = _isDragging;
        var wasMovingGroup = _isMovingGroup;
        var pressed = _pressedTab;

        _isPressed = false;
        _isDragging = false;
        _isMovingGroup = false;

        if (state.DropIndicatorIndex is not null)
        {
            UpdateState(state with { DropIndicatorIndex = null });
        }

        if (wasMovingGroup)
        {
            return;
        }

        if (state.Layout.CloseGroupButton.Width > 0
            && state.Layout.CloseGroupButton.Contains(point))
        {
            Emit(RailAction.CloseGroup, default);
            return;
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
        if (_state is { } state)
        {
            EmitMenu(null, state.Layout.HitTestTab(point)?.Identity ?? default);
        }
    }

    /// <summary>
    /// 请求弹出菜单。
    /// <paramref name="anchor"/> 非空时菜单贴着该控件的左下角弹出（菜单按钮的惯例），
    /// 否则跟随光标（右键的惯例）。
    /// </summary>
    private void EmitMenu(PixelRect? anchor, WindowIdentity target)
    {
        PixelPoint screenPoint;

        if (anchor is { } rect && _state is { } state)
        {
            var bounds = state.Layout.Bounds;
            screenPoint = new PixelPoint(bounds.Left + rect.Left, bounds.Top + rect.Bottom);
        }
        else
        {
            CursorPosition.TryGet(out var cx, out var cy);
            screenPoint = new PixelPoint(cx, cy);
        }

        _onInteraction(new RailInteraction
        {
            GroupId = GroupId,
            Action = RailAction.ShowMenu,
            Target = target,
            ScreenPoint = screenPoint,
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
