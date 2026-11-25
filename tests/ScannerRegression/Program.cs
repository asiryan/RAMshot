using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SkiaSharp;
using RAMshot.Services;

const int regionSize = 1024 * 1024;
int chunkSize = args.Length > 0 ? int.Parse(args[0]) : 4096;
var allocation = Native.VirtualAlloc(IntPtr.Zero, (nuint)regionSize, 0x3000, 4);
if (allocation == IntPtr.Zero) throw new Exception("VirtualAlloc failed");
try
{
    static byte[] ImageBytes(Color color, ImageFormat format)
    {
        using var image = new Bitmap(180, 160);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
        using var stream = new MemoryStream();
        image.Save(stream, format);
        return stream.ToArray();
    }
    using var webpBitmap = new SKBitmap(180, 160);
    webpBitmap.Erase(SKColors.Orange);
    using var webpImage = SKImage.FromBitmap(webpBitmap);
    using var webpData = webpImage.Encode(SKEncodedImageFormat.Webp, 100);
    byte[][] images = { ImageBytes(Color.Red, ImageFormat.Png), ImageBytes(Color.Blue, ImageFormat.Jpeg), ImageBytes(Color.Lime, ImageFormat.Gif), ImageBytes(Color.Purple, ImageFormat.Bmp), webpData.ToArray() };
    int[] offsets = { chunkSize == 8 ? 5 : 4093, 8190, 12284, 16383, 200699 };
    for (int i = 0; i < images.Length; i++) Marshal.Copy(images[i], 0, IntPtr.Add(allocation, offsets[i]), images[i].Length);
    var expected = images.Select(x => Convert.ToHexString(SHA256.HashData(x))).ToHashSet();
    var found = new HashSet<string>();
    using var control = new Control();
    var hwnd = control.Handle;
    long current = allocation.ToInt64();
    long end = current + regionSize;
    long totalRead = 0;
    for (int pass = 0; current < end && pass < 100 && found.Count < expected.Count; pass++)
    {
        var options = new ScanOptions(maxReadBytes: 256 * 1024, maxImageBytes: 128 * 1024, chunkSize: chunkSize, maxCandidates: 1, maxElapsed: TimeSpan.FromSeconds(2), minDimension: 128, maxDimension: 256, maxPixels: 65536);
        using var result = MemoryImageScanner.Scan(hwnd, current, default, (uint)Environment.ProcessId, options);
        totalRead += result.BytesRead;
        foreach (var candidate in result.Candidates)
            if (expected.Contains(candidate.Hash))
            {
                if (candidate.RegionKind != "Private") throw new Exception("Incorrect memory region kind");
                found.Add(candidate.Hash);
            }
        if (result.IsEnd || result.NextAddress <= current) break;
        current = result.NextAddress;
    }
    if (!found.SetEquals(expected)) throw new Exception($"Expected {expected.Count} distinct images, got {found.Count}");
    Console.WriteLine($"PASS 5 formats, signatures split across {chunkSize}-byte chunks, incremental resumption, bytes read {totalRead}");

    void CheckAnimation(byte[] encoded, string format, int x, int y, Func<Color, bool> checkPixel)
    {
        Marshal.Copy(encoded, 0, allocation, encoded.Length);
        var options = new ScanOptions(maxReadBytes: 256 * 1024, maxImageBytes: 128 * 1024,
            chunkSize: 4096, maxCandidates: 1, minDimension: 1, maxElapsed: TimeSpan.FromSeconds(2));
        using var result = MemoryImageScanner.Scan(hwnd, allocation.ToInt64(), default, (uint)Environment.ProcessId, options);
        var candidate = result.Candidates.Single();
        if (candidate.Format != format || candidate.Hash != Convert.ToHexString(SHA256.HashData(encoded)) ||
            !checkPixel(candidate.Image.GetPixel(x, y)))
            throw new Exception($"{format} animated first frame or transparency mismatch");
        Console.WriteLine($"PASS animated {format} first frame and transparency");
    }
    CheckAnimation(AnimatedGif(), "GIF", 0, 0, color => color.A == 0);
    // Synthetic two-frame 256x256 WebP; the first frame has alpha 128.
    var animatedWebp = Convert.FromBase64String("UklGRoYAAABXRUJQVlA4WAoAAAASAAAA/wAA/wAAQU5JTQYAAAAAAAAAAABBTk1GKgAAAAAAAAAAAP8AAP8AAGQAAAJWUDhMEQAAAC//wD8QB1CojhS5gIGI6H8AAEFOTUYoAAAAAAAAAAAA/wAA/wAAZAAAAlZQOEwPAAAAL//APwAHENH//gciov8BAA==");
    CheckAnimation(animatedWebp, "WebP", 128, 128, color => color.A == 128 && Math.Abs(color.R - 35) < 10);

    // A JPEG whose APP2 metadata pushes the SOF past the 64 KiB header must still be found.
    byte[] largeMetadataJpeg = JpegWithLargeMetadata();
    Marshal.Copy(largeMetadataJpeg, 0, allocation, largeMetadataJpeg.Length);
    var largeOptions = new ScanOptions(maxReadBytes: 256 * 1024, maxImageBytes: 128 * 1024,
        chunkSize: 65536, maxCandidates: 1, maxElapsed: TimeSpan.FromSeconds(3), minDimension: 128, maxDimension: 256, maxPixels: 65536);
    using (var result = MemoryImageScanner.Scan(hwnd, allocation.ToInt64(), default, (uint)Environment.ProcessId, largeOptions))
    {
        string expectedHash = Convert.ToHexString(SHA256.HashData(largeMetadataJpeg));
        if (!result.Candidates.Any(candidate => candidate.Hash == expectedHash))
            throw new Exception("JPEG with a large metadata segment was not found");
    }
    Console.WriteLine("PASS JPEG with >64 KiB metadata");
}
finally { Native.VirtualFree(allocation, 0, 0x8000); }

static byte[] JpegWithLargeMetadata()
{
    using var image = new Bitmap(180, 160);
    using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Blue);
    using var stream = new MemoryStream();
    image.Save(stream, ImageFormat.Jpeg);
    byte[] jpeg = stream.ToArray();
    const int payload = 65533; // largest payload that still fits the 16-bit segment length
    var result = new byte[2 + 4 + payload + jpeg.Length - 2];
    result[0] = 0xFF; result[1] = 0xD8;
    result[2] = 0xFF; result[3] = 0xE2;
    int length = payload + 2;
    result[4] = (byte)(length >> 8); result[5] = (byte)length;
    for (int i = 0; i < payload; i++) result[6 + i] = (byte)(i % 251);
    Buffer.BlockCopy(jpeg, 2, result, 6 + payload, jpeg.Length - 2);
    return result;
}

static byte[] AnimatedGif()
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(System.Text.Encoding.ASCII.GetBytes("GIF89a"));
    writer.Write((ushort)2); writer.Write((ushort)2);
    writer.Write(new byte[] { 0x81, 0, 0, 0, 0, 0, 255, 0, 0, 0, 0, 255, 0, 0, 0 });
    foreach (int color in new[] { 1, 2 })
    {
        writer.Write(new byte[] { 0x21, 0xf9, 4, 1, 10, 0, 0, 0, 0x2c });
        writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)2); writer.Write((ushort)2); writer.Write((byte)0);
        writer.Write((byte)2);
        int bits = 0, count = 0;
        var data = new List<byte>();
        foreach (int code in new[] { 4, 0, 4, color, 4, color, 4, color, 5 })
        {
            bits |= code << count; count += 3;
            while (count >= 8) { data.Add((byte)bits); bits >>= 8; count -= 8; }
        }
        if (count > 0) data.Add((byte)bits);
        writer.Write((byte)data.Count); writer.Write(data.ToArray()); writer.Write((byte)0);
    }
    writer.Write((byte)0x3b);
    return stream.ToArray();
}

internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool VirtualFree(IntPtr address, nuint size, uint type);
}
