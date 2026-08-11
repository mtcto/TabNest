namespace TabNest.Core.Models;

/// <summary>
/// 物理像素矩形。
///
/// 领域层刻意不使用 System.Drawing.Rectangle：那会把 Core 绑到 Windows，
/// 破坏"零平台依赖、可在任意环境单测"的约束。
///
/// 单位一律是**物理像素**，不是 DIP。跨显示器 DPI 不同时，DIP 无法唯一定位一个矩形，
/// 而窗口恢复必须精确到物理像素，否则在 125%/150% 缩放下会错位。
/// </summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public static PixelRect Empty => default;

    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelPoint Center => new(Left + (Width / 2), Top + (Height / 2));

    public static PixelRect FromSize(int left, int top, int width, int height) =>
        new(left, top, left + width, top + height);

    public bool Contains(PixelPoint point) =>
        point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;

    public bool IntersectsWith(PixelRect other) =>
        other.Left < Right && Left < other.Right && other.Top < Bottom && Top < other.Bottom;

    /// <summary>两个矩形的重叠面积。用于判断窗口主要落在哪个显示器上。</summary>
    public long IntersectionArea(PixelRect other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
        return w <= 0 || h <= 0 ? 0 : (long)w * h;
    }

    public PixelRect Inflate(int dx, int dy) =>
        new(Left - dx, Top - dy, Right + dx, Bottom + dy);

    public PixelRect OffsetBy(int dx, int dy) =>
        new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

/// <summary>物理像素坐标点。</summary>
public readonly record struct PixelPoint(int X, int Y)
{
    public override string ToString() => $"({X},{Y})";
}
