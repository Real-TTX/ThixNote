using SkiaSharp;
using Svg.Skia;

// Renders the logo SVGs into PNG, ICO and ICNS files. Each target size uses the SVG with the matching detail level.
// Usage: dotnet run --project tools/IconGen -- <repo root>
var root = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
var logoDirectory = Path.Combine(root, "assets", "logo");
var outputDirectory = Path.Combine(root, "assets", "icons");
Directory.CreateDirectory(outputDirectory);

var appIcon = new IconSet("thixnote", size => size <= 24 ? "thixnote-small.svg" : size <= 64 ? "thixnote-medium.svg" : "thixnote-full.svg");
var documentIcon = new IconSet("thixnote-document", size => size <= 32 ? "thixnote-document-small.svg" : "thixnote-document-full.svg");

foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256, 512, 1024 })
{
    File.WriteAllBytes(Path.Combine(outputDirectory, $"thixnote-{size}.png"), RenderPng(appIcon, size));
}

foreach (var icon in new[] { appIcon, documentIcon })
{
    File.WriteAllBytes(Path.Combine(outputDirectory, icon.Name + ".ico"), BuildIco(icon));
    File.WriteAllBytes(Path.Combine(outputDirectory, icon.Name + ".icns"), BuildIcns(icon));
}

Console.WriteLine($"Icons written to {outputDirectory}");

SKBitmap Render(IconSet icon, int size)
{
    var svgPath = Path.Combine(logoDirectory, icon.SvgForSize(size));
    using var svg = new SKSvg();
    svg.Load(svgPath);
    var picture = svg.Picture ?? throw new InvalidOperationException($"Cannot load {svgPath}");
    var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul));
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / picture.CullRect.Width, size / picture.CullRect.Height);
    canvas.DrawPicture(picture);
    canvas.Flush();
    return bitmap;
}

byte[] RenderPng(IconSet icon, int size)
{
    using var bitmap = Render(icon, size);
    using var image = SKImage.FromBitmap(bitmap);
    return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
}

// Classic 32-bit BMP entries up to 128 px (understood by every Windows component), PNG for 256 px.
byte[] BuildIco(IconSet icon)
{
    int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    var images = sizes.Select(size => size == 256 ? RenderPng(icon, size) : RenderIcoBitmap(icon, size)).ToList();

    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)sizes.Length);
    var offset = 6 + 16 * sizes.Length;
    for (var index = 0; index < sizes.Length; index++)
    {
        var dimension = (byte)(sizes[index] >= 256 ? 0 : sizes[index]);
        writer.Write(dimension);
        writer.Write(dimension);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(images[index].Length);
        writer.Write(offset);
        offset += images[index].Length;
    }

    images.ForEach(writer.Write);
    writer.Flush();
    return stream.ToArray();
}

byte[] RenderIcoBitmap(IconSet icon, int size)
{
    using var bitmap = Render(icon, size);
    var maskStride = (size + 31) / 32 * 4;
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);

    // BITMAPINFOHEADER; the height counts XOR and AND mask together.
    writer.Write(40);
    writer.Write(size);
    writer.Write(size * 2);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(0);
    writer.Write(size * size * 4 + maskStride * size);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);

    // Pixels bottom-up in BGRA.
    for (var y = size - 1; y >= 0; y--)
    {
        for (var x = 0; x < size; x++)
        {
            var color = bitmap.GetPixel(x, y);
            writer.Write(color.Blue);
            writer.Write(color.Green);
            writer.Write(color.Red);
            writer.Write(color.Alpha);
        }
    }

    // AND mask: all zero, transparency comes from the alpha channel.
    writer.Write(new byte[maskStride * size]);
    writer.Flush();
    return stream.ToArray();
}

byte[] BuildIcns(IconSet icon)
{
    (string Type, int Size)[] chunks = [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024)];
    var chunkData = chunks.Select(chunk => (chunk.Type, Data: RenderPng(icon, chunk.Size))).ToList();
    using var stream = new MemoryStream();
    stream.Write("icns"u8);
    stream.Write(ToBigEndian(8 + chunkData.Sum(chunk => 8 + chunk.Data.Length)));
    foreach (var (type, data) in chunkData)
    {
        stream.Write(System.Text.Encoding.ASCII.GetBytes(type));
        stream.Write(ToBigEndian(8 + data.Length));
        stream.Write(data);
    }

    return stream.ToArray();
}

static byte[] ToBigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

internal sealed record IconSet(string Name, Func<int, string> SvgForSize);
