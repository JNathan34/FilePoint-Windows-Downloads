using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FilePoint.Windows;

internal sealed class FolderIcons
{
    private readonly string assetPath = Path.Combine(AppContext.BaseDirectory, "Assets");
    private readonly Dictionary<string, int> codePoints;
    private readonly ConcurrentDictionary<string, byte[]> material = new();

    internal FolderIcons()
    {
        codePoints = File.ReadLines(Path.Combine(assetPath, "MaterialSymbolsOutlined.codepoints"))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => int.Parse(group.First()[1], NumberStyles.HexNumber));
    }

    internal static string FileName(IconChoice choice)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(choice.Material ?? choice.Url ?? ""));
        return $".filepoint-icon-{Convert.ToHexStringLower(hash.AsSpan(0, 8))}.ico";
    }

    internal static byte[] DesktopIni(IconChoice choice) =>
        [.. Encoding.Unicode.GetPreamble(),
         .. Encoding.Unicode.GetBytes($"[.ShellClassInfo]\r\nIconResource={FileName(choice)},0\r\nIconFile={FileName(choice)}\r\nIconIndex=0\r\n")];

    internal byte[] Load(IconChoice choice, FilePointApi api)
    {
        if (choice.Material is { } name && codePoints.TryGetValue(name, out int codePoint))
            return material.GetOrAdd(name, _ => RenderMaterial(codePoint));

        if (choice.Url is not { } url)
            throw new FileNotFoundException("FilePoint does not recognize this folder icon.");

        var image = api.GetIcon(url);
        if (image.AsSpan().StartsWith(new byte[] { 0, 0, 1, 0 }))
            return image;

        using var data = new MemoryStream(image);
        var frame = BitmapDecoder.Create(data, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if (frame.PixelWidth > 2048 || frame.PixelHeight > 2048)
            throw new InvalidDataException("The folder icon dimensions are too large.");
        return WriteIcon(size => Render(size, drawing =>
        {
            double scale = size / (double)Math.Max(frame.PixelWidth, frame.PixelHeight);
            double width = frame.PixelWidth * scale, height = frame.PixelHeight * scale;
            drawing.DrawImage(frame, new Rect((size - width) / 2, (size - height) / 2, width, height));
        }));
    }

    private byte[] RenderMaterial(int codePoint)
    {
        var typeface = new GlyphTypeface(new Uri(Path.Combine(assetPath, "MaterialSymbolsOutlined.ttf")));
        if (!typeface.CharacterToGlyphMap.TryGetValue(codePoint, out ushort glyph))
            throw new FileNotFoundException("The bundled font does not contain this symbol.");

        return WriteIcon(size => Render(size, drawing =>
        {
            var geometry = typeface.GetGlyphOutline(glyph, 100, 100);
            Rect bounds = geometry.Bounds;
            double scale = size * .73 / Math.Max(bounds.Width, bounds.Height);
            drawing.PushTransform(new TranslateTransform((size - bounds.Width * scale) / 2 - bounds.X * scale,
                (size - bounds.Height * scale) / 2 - bounds.Y * scale));
            drawing.PushTransform(new ScaleTransform(scale, scale));
            drawing.DrawGeometry(Brushes.DodgerBlue, null, geometry);
            drawing.Pop();
            drawing.Pop();
        }));
    }

    private static byte[] Render(int size, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            draw(drawing);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] WriteIcon(Func<int, byte[]> makeImage)
    {
        int[] sizes = [16, 32, 48, 256];
        byte[][] images = sizes.Select(makeImage).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);
        int offset = 6 + sizes.Length * 16;
        for (int index = 0; index < sizes.Length; index++)
        {
            writer.Write(unchecked((byte)sizes[index]));
            writer.Write(unchecked((byte)sizes[index]));
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[index].Length);
            writer.Write(offset);
            offset += images[index].Length;
        }
        foreach (var image in images)
            writer.Write(image);
        return stream.ToArray();
    }
}
