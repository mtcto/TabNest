using TabNest.Core.Layout;
using TabNest.Core.Models;
using TabNest.Core.Rules;
using TabNest.Interop;

namespace TabNest.App.Settings;

/// <summary>用户在设置中心里做的一次改动。</summary>
/// <param name="Settings">改动后的完整设置。</param>
public sealed record SettingsChanged(AppSettings Settings);

/// <summary>
/// 设置中心窗口。
///
/// **完全不使用 WPF。** 与标签轨道同一套 GDI 自绘 + Win32 栈：
/// WPF 不支持裁剪（SDK 报 NETSDK1168 拒绝构建）也不支持 NativeAOT，
/// 保留它意味着自包含发布 135MB 或强制用户装运行时，而摘掉后是 11MB 零依赖。
///
/// 窗口按需创建：用户不打开设置，这里的代码就一次都不执行，常驻内存不受影响。
/// </summary>
public sealed class SettingsWindow : Win32Window
{
    private const string ClassName = "TabNest.SettingsWindow";

    private readonly Action<SettingsChanged> _onChanged;

    private AppSettings _settings;
    private SettingsPage _page = SettingsPage.Overview;
    private SettingsTheme _theme = SettingsTheme.Light;

    private int _scroll;
    private SettingsPage? _hoveredNav;
    private SettingId _hoveredToggle;
    private (int Block, int Option)? _hoveredCard;
    private PixelPoint _pointerInContent = new(-1, -1);
    private bool _mouseTracked;

    /// <summary>
    /// 上一帧的内容布局。命中测试必须用**绘制时**的布局，
    /// 若在点击时重新测量一遍，两次结果只要有一点出入就会出现"点了没反应"。
    /// </summary>
    private ContentLayout? _content;

    private SettingsLayout? _layout;

    public SettingsWindow(AppSettings settings, Action<SettingsChanged> onChanged)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(onChanged);

        _settings = settings;
        _onChanged = onChanged;

        var metrics = new SettingsMetrics();

        CreateWindow(
            ClassName,
            "TabNest 设置",
            (uint)(User32Styles.OverlappedWindow | User32Styles.ClipChildren),
            exStyle: 0,
            x: CwUseDefault, y: CwUseDefault,
            width: metrics.MinWindowWidth, height: metrics.MinWindowHeight);

        ApplySystemTheme();
    }

    private const int CwUseDefault = unchecked((int)0x80000000);

    /// <summary>把外部改动（例如托盘菜单切换了主开关）同步进来。</summary>
    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        Invalidate();
    }

    /// <summary>跟随系统明暗设置。</summary>
    private void ApplySystemTheme()
    {
        var dark = SystemTheme.IsDarkMode();
        _theme = dark ? SettingsTheme.Dark : SettingsTheme.Light;
        SetTitleBarDarkMode(dark);
        Invalidate();
    }

    protected override nint? WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Messages.Paint:
                PaintFrame();
                return 0;

            case Messages.EraseBackground:
                return 1;

            case Messages.Size:
                // 窗口变大后内容可能不再需要滚动，必须重新夹紧偏移，
                // 否则会停在一片空白上，看起来像页面内容丢了。
                _scroll = Math.Max(0, _scroll);
                Invalidate();
                return 0;

            case Messages.GetMinMaxInfo:
                ApplyMinSize(lParam);
                return 0;

            case Messages.MouseMove:
                OnMouseMove(ToPoint(lParam));
                return 0;

            case Messages.MouseLeave:
                ClearHover();
                return 0;

            case Messages.MouseWheel:
                OnWheel(wParam);
                return 0;

            case Messages.LeftButtonDown:
                OnClick(ToPoint(lParam));
                return 0;


            case Messages.DpiChanged:
                Invalidate();
                return 0;

            case Messages.SettingChange:
                // 系统在明暗模式切换时广播这条消息。不响应它，
                // 用户切到暗色后设置中心还是刺眼的白。
                ApplySystemTheme();
                return 0;

            case Messages.Close:
                // 关闭只隐藏不销毁：设置中心会被反复打开，
                // 每次重建窗口既慢又会丢失当前所在页面和滚动位置。
                HideWindow();
                return 0;

            default:
                return null;
        }
    }

    // ------------------------------------------------------------------
    // 绘制
    // ------------------------------------------------------------------

    private void PaintFrame()
    {
        var client = ClientBounds;
        if (client.IsEmpty)
        {
            return;
        }

        var dpi = Dpi;
        var metrics = new SettingsMetrics().ScaleTo(dpi);
        var page = SettingsPages.Build(_page, _settings);

        // 先用一次性画布测量，再交给渲染器绘制。
        // 测量需要设备上下文来算文本尺寸，而此时还没进入 BeginPaint。
        using var probe = MeasureCanvas.Create(Handle);

        var viewportWidth = client.Width - metrics.NavWidth;
        var content = ContentLayout.Measure(probe.Canvas, page, viewportWidth, metrics, dpi);

        var layout = SettingsLayout.Compute(
            client.Width, client.Height, SettingsPages.Order, metrics, content.TotalHeight, _scroll);

        // 视口宽度会因滚动条出现而收窄，收窄后换行结果可能变化，需要按最终宽度重测一次。
        if (layout.ContentBounds.Width != viewportWidth)
        {
            content = ContentLayout.Measure(
                probe.Canvas, page, layout.ContentBounds.Width, metrics, dpi);

            layout = SettingsLayout.Compute(
                client.Width, client.Height, SettingsPages.Order, metrics, content.TotalHeight, _scroll);
        }

        _scroll = layout.ClampScroll(_scroll, content.TotalHeight);
        _content = content;
        _layout = layout;

        SettingsRenderer.Paint(Handle, new SettingsRenderState
        {
            Layout = layout,
            Content = content,
            Page = page,
            Theme = _theme,
            Metrics = metrics,
            Dpi = dpi,
            ScrollOffset = _scroll,
            HoveredNav = _hoveredNav,
            HoveredToggle = _hoveredToggle,
            HoveredCard = _hoveredCard,
            PointerPosition = _pointerInContent,
        });
    }

    private void ApplyMinSize(nint lParam)
    {
        var metrics = new SettingsMetrics().ScaleTo(Dpi);
        MinMaxInfo.SetMinTrackSize(lParam, metrics.MinWindowWidth, metrics.MinWindowHeight);
    }

    // ------------------------------------------------------------------
    // 交互
    // ------------------------------------------------------------------

    private void OnMouseMove(PixelPoint point)
    {
        if (!_mouseTracked)
        {
            _mouseTracked = TrackMouseLeave();
        }

        if (_layout is not { } layout || _content is not { } content)
        {
            return;
        }

        var nav = layout.HitTestNav(point);
        var contentPoint = ToContentPoint(point, layout);

        var toggle = layout.ContentBounds.Contains(point)
            ? content.HitTestToggle(contentPoint)
            : SettingId.None;

        var card = layout.ContentBounds.Contains(point)
            ? FindHoveredCard(content, contentPoint)
            : null;

        var pointer = layout.ContentBounds.Contains(point)
            ? contentPoint
            : new PixelPoint(-1, -1);

        if (nav != _hoveredNav || toggle != _hoveredToggle || card != _hoveredCard
            || pointer != _pointerInContent)
        {
            _hoveredNav = nav;
            _hoveredToggle = toggle;
            _hoveredCard = card;
            _pointerInContent = pointer;
            Invalidate();
        }
    }

    private void ClearHover()
    {
        _mouseTracked = false;

        if (_hoveredNav is not null || _hoveredToggle != SettingId.None || _hoveredCard is not null)
        {
            _hoveredNav = null;
            _hoveredToggle = SettingId.None;
            _hoveredCard = null;
            Invalidate();
        }
    }

    private void OnWheel(nint wParam)
    {
        if (_layout is not { } layout || _content is not { } content)
        {
            return;
        }

        // wParam 高 16 位是有符号的滚动量，每格 120。
        var delta = (short)((wParam >> 16) & 0xFFFF);
        var metrics = new SettingsMetrics().ScaleTo(Dpi);
        var step = delta / 120 * metrics.WheelStep;

        var next = layout.ClampScroll(_scroll - step, content.TotalHeight);

        if (next != _scroll)
        {
            _scroll = next;
            Invalidate();
        }
    }

    private void OnClick(PixelPoint point)
    {
        if (_layout is not { } layout || _content is not { } content)
        {
            return;
        }

        if (layout.HitTestNav(point) is { } page)
        {
            if (page != _page)
            {
                _page = page;

                // 换页把滚动归零。保留上一页的偏移会让新页面从中间开始显示。
                _scroll = 0;
                Invalidate();
            }

            return;
        }

        if (!layout.ContentBounds.Contains(point))
        {
            return;
        }

        var contentPoint = ToContentPoint(point, layout);

        var toggleId = content.HitTestToggle(contentPoint);
        if (toggleId != SettingId.None)
        {
            Apply(SettingsPages.Toggle(_settings, toggleId));
            return;
        }

        if (content.HitTestChoice(contentPoint) is { } choice)
        {
            Apply(SettingsPages.Choose(_settings, choice.Id, choice.Value));
            return;
        }

        if (content.HitTestList(contentPoint) is { } listAction)
        {
            HandleListAction(listAction.Action, listAction.Index);
        }
    }

    /// <summary>
    /// <summary>处理应用清单上的动作。</summary>
    private void HandleListAction(SettingAction action, int index)
    {
        switch (action)
        {
            case SettingAction.SelectApp:
                if (SettingsPages.SelectedAppIndex != index)
                {
                    SettingsPages.SelectedAppIndex = index;
                    Invalidate();
                }

                break;

            case SettingAction.AddApp:
                {
                    var picked = FileDialog.PickExecutable(Handle, "选择要加入列表的应用");

                    if (!string.IsNullOrEmpty(picked))
                    {
                        Apply(AppList.Add(_settings, picked));
                    }

                    break;
                }

            case SettingAction.RemoveApp:
                {
                    var apps = AppList.Apps(_settings);
                    var selected = SettingsPages.SelectedAppIndex;

                    if (selected < 0 || selected >= apps.Count)
                    {
                        return;
                    }

                    Apply(AppList.Remove(_settings, apps[selected]));

                    // 移除后选中项要收回到合法范围，否则会指向一个已经不存在的行。
                    SettingsPages.SelectedAppIndex = Math.Min(selected, apps.Count - 2);
                    break;
                }

            default:
                break;
        }
    }

    private void Apply(AppSettings updated)
    {
        if (updated == _settings)
        {
            return;
        }

        _settings = updated;
        Invalidate();

        // 立即生效并落盘，没有"确定/取消"。设置项都是即时可感知的，
        // 加一层确认反而让用户不确定改动到底生效了没有。
        _onChanged(new SettingsChanged(updated));
    }

    private PixelPoint ToContentPoint(PixelPoint p, SettingsLayout layout) =>
        new(p.X - layout.ContentBounds.Left, p.Y - layout.ContentBounds.Top + _scroll);

    private static (int Block, int Option)? FindHoveredCard(ContentLayout content, PixelPoint point)
    {
        for (var i = 0; i < content.Blocks.Count; i++)
        {
            var b = content.Blocks[i];

            if (b.Block is not ChoiceBlock)
            {
                continue;
            }

            for (var j = 0; j < b.Options.Count; j++)
            {
                if (b.Options[j].Contains(point))
                {
                    return (i, j);
                }
            }
        }

        return null;
    }

    private static PixelPoint ToPoint(nint lParam) =>
        new((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
}
