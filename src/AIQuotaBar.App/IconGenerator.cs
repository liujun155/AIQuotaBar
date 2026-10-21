using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIQuotaBar.App;

public static class IconGenerator
{
    public static void GenerateIcons(string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        // 1. 生成 512x512 高清原图
        var baseBitmap = RenderIconBitmap(512);
        var png512Path = Path.Combine(outputDir, "app.png");
        SaveBitmapToPng(baseBitmap, png512Path);

        // 2. 生成多分辨率位图用于组装 Windows .ico 容器
        int[] sizes = { 256, 128, 64, 48, 32, 24, 16 };
        var pngDataList = new List<(int Size, byte[] PngBytes)>();

        foreach (var size in sizes)
        {
            var bmp = RenderIconBitmap(size);
            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(ms);
            pngDataList.Add((size, ms.ToArray()));
        }

        // 3. 打包生成符合 Windows 规范的 .ico 文件
        var icoPath = Path.Combine(outputDir, "app.ico");
        using (var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write))
        {
            WriteIcoFile(fs, pngDataList);
        }

        Console.WriteLine($"[IconGenerator] Generated {icoPath} and {png512Path}");
    }

    private static RenderTargetBitmap RenderIconBitmap(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double scale = size / 512.0;

            // 1. 外层圆角底座 (Squircle Container)
            var containerRect = new Rect(32 * scale, 32 * scale, 448 * scale, 448 * scale);
            double cornerRadius = 100 * scale;

            // 底座暗夜科技渐变
            var bgBrush = new LinearGradientBrush(
                Color.FromRgb(0x13, 0x1B, 0x2E), // 绀青深空
                Color.FromRgb(0x07, 0x0A, 0x12), // 极夜黑
                new Point(0, 0),
                new Point(1, 1));

            // 外微光边框 (Fluent Border Glow)
            var borderPen = new Pen(
                new LinearGradientBrush(
                    Color.FromArgb(0x80, 0x38, 0xBD, 0xF8), // 亮天蓝微光
                    Color.FromArgb(0x33, 0x10, 0xB9, 0x81), // 翡翠微光
                    new Point(0, 0),
                    new Point(1, 1)),
                1.5 * scale);

            dc.DrawRoundedRectangle(bgBrush, borderPen, containerRect, cornerRadius, cornerRadius);

            // 2. 内部顶部微弱天光高光反射
            var innerGlowRect = new Rect(36 * scale, 36 * scale, 440 * scale, 220 * scale);
            var innerGlowBrush = new LinearGradientBrush(
                Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
                new Point(0.5, 0),
                new Point(0.5, 1));
            dc.DrawRoundedRectangle(innerGlowBrush, null, innerGlowRect, cornerRadius - 4 * scale, cornerRadius - 4 * scale);

            // 3. 左侧：AI 灵动四芒星光 (AI Core Sparkle)
            Point starCenter = new Point(148 * scale, 256 * scale);
            double starR = 54 * scale;
            double starInner = 14 * scale;

            // 四芒星柔和外发光晕
            var haloBrush = new RadialGradientBrush(
                Color.FromArgb(0x40, 0x34, 0xD3, 0x99),
                Color.FromArgb(0x00, 0x06, 0xB6, 0xD4));
            dc.DrawEllipse(haloBrush, null, starCenter, 80 * scale, 80 * scale);

            // 绘制主四芒星几何路径
            var starGeometry = new StreamGeometry();
            using (var ctx = starGeometry.Open())
            {
                ctx.BeginFigure(new Point(starCenter.X, starCenter.Y - starR), true, true);
                ctx.QuadraticBezierTo(new Point(starCenter.X + starInner, starCenter.Y - starInner), new Point(starCenter.X + starR, starCenter.Y), true, false);
                ctx.QuadraticBezierTo(new Point(starCenter.X + starInner, starCenter.Y + starInner), new Point(starCenter.X, starCenter.Y + starR), true, false);
                ctx.QuadraticBezierTo(new Point(starCenter.X - starInner, starCenter.Y + starInner), new Point(starCenter.X - starR, starCenter.Y), true, false);
                ctx.QuadraticBezierTo(new Point(starCenter.X - starInner, starCenter.Y - starInner), new Point(starCenter.X, starCenter.Y - starR), true, false);
            }
            starGeometry.Freeze();

            var starBrush = new LinearGradientBrush(
                Color.FromRgb(0xFF, 0xFF, 0xFF),
                Color.FromRgb(0x34, 0xD3, 0x99),
                new Point(0.3, 0),
                new Point(0.7, 1));
            dc.DrawGeometry(starBrush, null, starGeometry);

            // 右上方伴随微光次星 (Secondary Little Sparkle)
            Point subStarCenter = new Point(208 * scale, 180 * scale);
            double subR = 16 * scale;
            double subInner = 4 * scale;
            var subStarGeom = new StreamGeometry();
            using (var ctx = subStarGeom.Open())
            {
                ctx.BeginFigure(new Point(subStarCenter.X, subStarCenter.Y - subR), true, true);
                ctx.QuadraticBezierTo(new Point(subStarCenter.X + subInner, subStarCenter.Y - subInner), new Point(subStarCenter.X + subR, subStarCenter.Y), true, false);
                ctx.QuadraticBezierTo(new Point(subStarCenter.X + subInner, subStarCenter.Y + subInner), new Point(subStarCenter.X, subStarCenter.Y + subR), true, false);
                ctx.QuadraticBezierTo(new Point(subStarCenter.X - subInner, subStarCenter.Y + subInner), new Point(subStarCenter.X - subR, subStarCenter.Y), true, false);
                ctx.QuadraticBezierTo(new Point(subStarCenter.X - subInner, subStarCenter.Y - subInner), new Point(subStarCenter.X, subStarCenter.Y - subR), true, false);
            }
            subStarGeom.Freeze();
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0xDD, 0x38, 0xBD, 0xF8)), null, subStarGeom);

            // 4. 右侧：双行配额能量胶囊条 (Quota Dual Bars)
            double trackX = 230 * scale;
            double trackW = 200 * scale;
            double trackH = 36 * scale;
            double trackR = 18 * scale;

            var trackBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x1E, 0x29, 0x3B));
            var trackBorderPen = new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)), 1 * scale);

            // 上行胶囊 (5h 短期配额)
            double row1Y = 206 * scale;
            dc.DrawRoundedRectangle(trackBrush, trackBorderPen, new Rect(trackX, row1Y, trackW, trackH), trackR, trackR);

            // 上行动态进度 (78% 充沛绿到青蓝渐变)
            double fill1W = trackW * 0.78;
            var fill1Brush = new LinearGradientBrush(
                Color.FromRgb(0x10, 0xB9, 0x81), // Emerald
                Color.FromRgb(0x06, 0xB6, 0xD4), // Cyan
                new Point(0, 0.5),
                new Point(1, 0.5));
            dc.DrawRoundedRectangle(fill1Brush, null, new Rect(trackX + 2 * scale, row1Y + 2 * scale, fill1W - 4 * scale, trackH - 4 * scale), trackR - 2 * scale, trackR - 2 * scale);

            // 上行高光晶体层
            var sheen1Brush = new LinearGradientBrush(
                Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
                new Point(0.5, 0),
                new Point(0.5, 1));
            dc.DrawRoundedRectangle(sheen1Brush, null, new Rect(trackX + 4 * scale, row1Y + 3 * scale, fill1W - 8 * scale, (trackH - 6 * scale) * 0.45), (trackR - 3 * scale), (trackR - 3 * scale));

            // 下行胶囊 (7d 周度配额)
            double row2Y = 270 * scale;
            dc.DrawRoundedRectangle(trackBrush, trackBorderPen, new Rect(trackX, row2Y, trackW, trackH), trackR, trackR);

            // 下行动态进度 (92% 天空青到紫罗兰渐变)
            double fill2W = trackW * 0.92;
            var fill2Brush = new LinearGradientBrush(
                Color.FromRgb(0x38, 0xBD, 0xF8), // Sky Blue
                Color.FromRgb(0x63, 0x66, 0xF1), // Indigo
                new Point(0, 0.5),
                new Point(1, 0.5));
            dc.DrawRoundedRectangle(fill2Brush, null, new Rect(trackX + 2 * scale, row2Y + 2 * scale, fill2W - 4 * scale, trackH - 4 * scale), trackR - 2 * scale, trackR - 2 * scale);

            // 下行高光晶体层
            dc.DrawRoundedRectangle(sheen1Brush, null, new Rect(trackX + 4 * scale, row2Y + 3 * scale, fill2W - 8 * scale, (trackH - 6 * scale) * 0.45), (trackR - 3 * scale), (trackR - 3 * scale));
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    private static void SaveBitmapToPng(RenderTargetBitmap bitmap, string filePath)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }

    private static void WriteIcoFile(Stream stream, IReadOnlyList<(int Size, byte[] PngBytes)> images)
    {
        using var bw = new BinaryWriter(stream);

        // ICO Header
        bw.Write((ushort)0); // Reserved
        bw.Write((ushort)1); // Type: 1 = Icon
        bw.Write((ushort)images.Count); // Image count

        int offset = 6 + images.Count * 16;

        // ICO Directory Entries
        foreach (var img in images)
        {
            byte w = img.Size >= 256 ? (byte)0 : (byte)img.Size;
            byte h = img.Size >= 256 ? (byte)0 : (byte)img.Size;

            bw.Write(w);              // Width
            bw.Write(h);              // Height
            bw.Write((byte)0);        // Color count (0 = >=8bpp)
            bw.Write((byte)0);        // Reserved
            bw.Write((ushort)1);      // Color planes
            bw.Write((ushort)32);     // Bits per pixel
            bw.Write((uint)img.PngBytes.Length); // Image size in bytes
            bw.Write((uint)offset);   // Offset of image data

            offset += img.PngBytes.Length;
        }

        // Image PNG payloads
        foreach (var img in images)
        {
            bw.Write(img.PngBytes);
        }
    }
}
