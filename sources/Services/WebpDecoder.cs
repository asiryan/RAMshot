using SkiaSharp;
using System.Drawing;
using System.Drawing.Imaging;

namespace RAMshot.Services;

// Decodes the first WebP frame in memory; callers must validate container length.
internal static class WebpDecoder
{
    internal static Bitmap? Decode(ReadOnlySpan<byte> data, int expectedWidth, int expectedHeight, ScanOptions options)
    {
        long pixelCount = (long)expectedWidth * expectedHeight;
        if (data.Length < 20 || data.Length > options.MaxImageBytes || expectedWidth <= 0 || expectedHeight <= 0 ||
            expectedWidth > options.MaxDimension || expectedHeight > options.MaxDimension ||
            pixelCount > options.MaxPixels || pixelCount > options.MaxDecodedBytes / 4)
            return null;

        using var input = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(input);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Webp ||
            codec.Info.Width != expectedWidth || codec.Info.Height != expectedHeight)
            return null;

        var result = new Bitmap(expectedWidth, expectedHeight, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = result.LockBits(new Rectangle(0, 0, expectedWidth, expectedHeight),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            bool decoded;
            try
            {
                if (pixels.Stride < checked(expectedWidth * 4))
                    throw new InvalidOperationException("Unsupported bitmap stride.");
                var info = new SKImageInfo(expectedWidth, expectedHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                decoded = codec.GetPixels(info, pixels.Scan0, pixels.Stride, new SKCodecOptions(0)) == SKCodecResult.Success;
            }
            finally { result.UnlockBits(pixels); }
            if (!decoded) { result.Dispose(); return null; }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
