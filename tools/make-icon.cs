#:package System.Drawing.Common@9.0.0

// 把 logo 转成多尺寸 ICO。
//
// 一次性的构建工具，不进产品：产品本身刻意不依赖 System.Drawing
// （它在 .NET 里已是独立 NuGet 包，且不利于裁剪）。
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var source = args.Length > 0 ? args[0] : @"D:\dev\workspace\TabNest\logo.jpg";
var target = args.Length > 1 ? args[1] : @"D:\dev\workspace\TabNest\src\TabNest.App\TabNest.ico";

// Windows 会按显示场景挑最合适的尺寸：托盘 16/20/24，任务栏 32/40，
// Alt+Tab 与文件资源管理器大图标 48/64，属性对话框与商店 256。
// 缺哪一档系统就得现场缩放，缩出来的图在小尺寸下会糊成一团。
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

using var original = new Bitmap(source);
Console.WriteLine($"源图 {original.Width}x{original.Height}");

var entries = new List<(int Size, byte[] Data, bool IsPng)>();

foreach (var size in sizes)
{
    using var scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb);

    using (var g = Graphics.FromImage(scaled))
    {
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);
        g.DrawImage(original, new Rectangle(0, 0, size, size));
    }

    // 256 用 PNG 压缩（否则光这一档就要 256KB）；其余用 DIB，
    // 兼容性最好 —— 部分较老的 shell 路径对小尺寸 PNG 图标支持不稳。
    if (size >= 256)
    {
        using var ms = new MemoryStream();
        scaled.Save(ms, ImageFormat.Png);
        entries.Add((size, ms.ToArray(), true));
    }
    else
    {
        entries.Add((size, ToDib(scaled), false));
    }
}

using (var fs = File.Create(target))
using (var w = new BinaryWriter(fs))
{
    w.Write((ushort)0);              // reserved
    w.Write((ushort)1);              // type: 1 = icon
    w.Write((ushort)entries.Count);

    var offset = 6 + (entries.Count * 16);

    foreach (var (size, data, _) in entries)
    {
        // 256 在 ICONDIRENTRY 里用 0 表示 —— 这个字段只有一个字节。
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);            // 调色板颜色数，32 位图为 0
        w.Write((byte)0);            // reserved
        w.Write((ushort)1);          // planes
        w.Write((ushort)32);         // 位深
        w.Write(data.Length);
        w.Write(offset);

        offset += data.Length;
    }

    foreach (var (_, data, _) in entries)
    {
        w.Write(data);
    }
}

var info = new FileInfo(target);
Console.WriteLine($"已写出 {target}");
Console.WriteLine($"{entries.Count} 档尺寸：{string.Join(", ", entries.Select(e => e.Size))}");
Console.WriteLine($"体积 {info.Length / 1024.0:F1} KB");

// 把 32bpp 位图打包成 ICO 里的 DIB 条目。
static byte[] ToDib(Bitmap bmp)
{
    var w = bmp.Width;
    var h = bmp.Height;

    using var ms = new MemoryStream();
    using var writer = new BinaryWriter(ms);

    // BITMAPINFOHEADER。高度写两倍：ICO 的 DIB 由 XOR 位图与 AND 掩码上下拼成，
    // 这个约定不写对的话图标会只显示上半截。
    writer.Write(40);                // biSize
    writer.Write(w);
    writer.Write(h * 2);
    writer.Write((ushort)1);         // biPlanes
    writer.Write((ushort)32);        // biBitCount
    writer.Write(0);                 // BI_RGB
    writer.Write(w * h * 4);         // biSizeImage
    writer.Write(0);                 // 分辨率
    writer.Write(0);
    writer.Write(0);                 // 调色板
    writer.Write(0);

    var data = bmp.LockBits(
        new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

    try
    {
        var row = new byte[w * 4];

        // DIB 是自下而上存储的。
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

    // AND 掩码。32 位图靠 alpha 通道决定透明，掩码全 0 即可，
    // 但**必须写出来**：省掉它的话某些 shell 路径会把图标读成花屏。
    var maskStride = ((w + 31) / 32) * 4;

    for (var y = 0; y < h; y++)
    {
        writer.Write(new byte[maskStride]);
    }

    return ms.ToArray();
}
