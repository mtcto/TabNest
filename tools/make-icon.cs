#:package System.Drawing.Common@9.0.0
#:property ManagePackageVersionsCentrally=false
#:property TreatWarningsAsErrors=false

// 按尺寸矢量绘制应用图标，再打成多档 ICO。
//
// 一次性的构建工具，不进产品：产品本身刻意不依赖 System.Drawing。
// 托盘 16/20/24 按整像素格子画，不从大图缩小 —— 插画缩到 16px 会糊成一团。
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var repo = FindRepoRoot();
var target = args.Length > 0 ? args[0] : Path.Combine(repo, "src", "TabNest.App", "TabNest.ico");
var assets = Path.Combine(repo, "assets");

// Windows 会按显示场景挑最合适的尺寸：托盘 16/20/24，任务栏 32/40，
// Alt+Tab 与文件资源管理器大图标 48/64，属性对话框与商店 256。
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

Directory.CreateDirectory(assets);
WritePng(Path.Combine(assets, "logo.png"), 1024, polished: true);
WritePng(Path.Combine(assets, "logo-tray.png"), 256, polished: false);
WritePng(Path.Combine(assets, "TabNest-icon.png"), 1024, polished: true);

var entries = new List<(int Size, byte[] Data)>();

foreach (var size in sizes)
{
    using var bmp = Render(size, polished: size >= 48);
    if (size >= 256)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        entries.Add((size, ms.ToArray()));
    }
    else
    {
        entries.Add((size, ToDib(bmp)));
    }
}

using (var fs = File.Create(target))
using (var w = new BinaryWriter(fs))
{
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)entries.Count);

    var offset = 6 + (entries.Count * 16);

    foreach (var (size, data) in entries)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(data.Length);
        w.Write(offset);
        offset += data.Length;
    }

    foreach (var (_, data) in entries)
    {
        w.Write(data);
    }
}

var info = new FileInfo(target);
Console.WriteLine($"已写出 {target}");
Console.WriteLine($"{entries.Count} 档尺寸：{string.Join(", ", entries.Select(e => e.Size))}");
Console.WriteLine($"体积 {info.Length / 1024.0:F1} KB");

static void WritePng(string path, int size, bool polished)
{
    using var bmp = Render(size, polished);
    bmp.Save(path, ImageFormat.Png);
    Console.WriteLine($"源图 {size}x{size}  ← {path}");
}

static Bitmap Render(int size, bool polished)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.Clear(Color.Transparent);
    g.PixelOffsetMode = PixelOffsetMode.Half;
    g.CompositingQuality = CompositingQuality.HighQuality;

    if (size <= 24)
    {
        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        DrawTray(g, size);
    }
    else
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        DrawLarge(g, size, polished);
    }

    return bmp;
}

// 托盘档：整像素。橙底 + 加粗白 T。和托盘里绿色 N 同一套认法：色块 + 字母。
static void DrawTray(Graphics g, int size)
{
    var plate = Color.FromArgb(255, 234, 88, 12);
    using var mark = new SolidBrush(Color.White);

    int r = size <= 16 ? 3 : 4;
    FillRound(g, new Rectangle(0, 0, size, size), r, plate);

    int barX, barY, barW, barH, stemX, stemY, stemW, stemH;
    if (size <= 16)
    {
        barX = 3; barY = 3; barW = 10; barH = 4;
        stemX = 6; stemY = 6; stemW = 4; stemH = 8;
    }
    else if (size <= 20)
    {
        barX = 3; barY = 4; barW = 14; barH = 5;
        stemX = 8; stemY = 7; stemW = 5; stemH = 10;
    }
    else
    {
        barX = 4; barY = 5; barW = 16; barH = 6;
        stemX = 9; stemY = 9; stemW = 6; stemH = 11;
    }

    g.FillRectangle(mark, barX, barY, barW, barH);
    g.FillRectangle(mark, stemX, stemY, stemW, stemH);
}

// 32px 及以上：同一枚圆角粗 T，顶杠做成标签那样的胶囊横条。
static void DrawLarge(Graphics g, int size, bool polished)
{
    var plateTop = Color.FromArgb(255, 255, 122, 26);
    var plateBot = Color.FromArgb(255, 196, 70, 8);
    var mark = Color.White;

    float s = size;
    float plateR = s * 0.22f;
    var plate = new RectangleF(0, 0, s, s);

    if (polished)
    {
        using var shadow = Rounded(new RectangleF(s * 0.04f, s * 0.06f, s * 0.92f, s * 0.90f), plateR);
        using var sb = new SolidBrush(Color.FromArgb(40, 0, 0, 0));
        g.FillPath(sb, shadow);
    }

    using (var path = Rounded(plate, plateR))
    using (var brush = new LinearGradientBrush(plate, plateTop, plateBot, 90f))
    {
        g.FillPath(brush, path);
    }

    float barX = s * 0.17f;
    float barY = s * 0.20f;
    float barW = s * 0.66f;
    float barH = s * 0.22f;
    FillRound(g, new RectangleF(barX, barY, barW, barH), barH / 2f, mark);

    float stemW = s * 0.22f;
    float stemX = (s - stemW) / 2f;
    float stemY = barY + barH * 0.45f;
    float stemH = s * 0.52f;
    FillRound(g, new RectangleF(stemX, stemY, stemW, stemH), s * 0.07f, mark);
}

static void FillRound(Graphics g, RectangleF rect, float radius, Color color)
{
    using var path = Rounded(rect, radius);
    using var brush = new SolidBrush(color);
    g.FillPath(brush, path);
}

static GraphicsPath Rounded(RectangleF r, float radius)
{
    var path = new GraphicsPath();
    var d = Math.Max(0.1f, Math.Min(radius * 2f, Math.Min(r.Width, r.Height)));
    path.AddArc(r.X, r.Y, d, d, 180, 90);
    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}

static byte[] ToDib(Bitmap bmp)
{
    var w = bmp.Width;
    var h = bmp.Height;

    using var ms = new MemoryStream();
    using var writer = new BinaryWriter(ms);

    writer.Write(40);
    writer.Write(w);
    writer.Write(h * 2);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(0);
    writer.Write(w * h * 4);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);

    var data = bmp.LockBits(
        new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

    try
    {
        var row = new byte[w * 4];
        for (var y = h - 1; y >= 0; y--)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                data.Scan0 + (y * data.Stride), row, 0, row.Length);
            writer.Write(row);
        }
    }
    finally
    {
        bmp.UnlockBits(data);
    }

    var maskStride = ((w + 31) / 32) * 4;
    for (var y = 0; y < h; y++)
    {
        writer.Write(new byte[maskStride]);
    }

    return ms.ToArray();
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "TabNest.slnx")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return Directory.GetCurrentDirectory();
}
