using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace RAMshot.Services;

// Decodes WebP frames for the preview; callers own and dispose the returned bitmaps.
internal static class WebpAnimationDecoder
{
    // A malformed file with a huge frame count must not turn the preview into an unbounded allocation.
    private const int MaxFrames = 2000;

    internal sealed record Animation(bool IsAnimated, List<Bitmap> Frames, int[] Delays, bool Limited);

    internal static Animation Decode(byte[] bytes, long maxDecodedBytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidOperationException("WebP decoding failed.");
        if (codec.EncodedFormat != SKEncodedImageFormat.Webp)
            throw new InvalidOperationException("The data is not a WebP image.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        int count = codec.FrameCount;
        if (count <= 1)
            return new Animation(false, new List<Bitmap> { DecodeStill(codec, info) }, Array.Empty<int>(), false);
        long frameBytes = (long)info.Width * info.Height * 4;
        if (count > MaxFrames || frameBytes <= 0 || frameBytes * count > maxDecodedBytes)
            return new Animation(true, new List<Bitmap> { DecodeFrame(codec, info, 0) }, Array.Empty<int>(), true);
        int[] delays = ReadDelays(codec, count);
        var frames = new List<Bitmap>(count);
        try
        {
            // Frames are decoded into the same buffer: Skia needs the previous frame when blending is enabled.
            using var canvas = new SKBitmap(info);
            canvas.Erase(SKColors.Transparent);
            for (int i = 0; i < count; i++)
            {
                var options = i == 0 ? new SKCodecOptions(0) : new SKCodecOptions(i, i - 1);
                var result = codec.GetPixels(info, canvas.GetPixels(), canvas.RowBytes, options);
                if (result != SKCodecResult.Success)
                    throw new InvalidOperationException($"WebP frame {i} decoding failed: {result}.");
                frames.Add(ToBitmap(canvas));
            }
        }
        catch
        {
            foreach (var frame in frames) frame.Dispose();
            throw;
        }
        return new Animation(true, frames, delays, false);
    }

    internal static Bitmap? DecodeFirstFrame(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Webp) return null;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        return codec.FrameCount <= 1 ? DecodeStill(codec, info) : DecodeFrame(codec, info, 0);
    }

    private static Bitmap DecodeStill(SKCodec codec, SKImageInfo info)
    {
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
            throw new InvalidOperationException($"WebP decoding failed: {result}.");
        return ToBitmap(bitmap);
    }

    private static Bitmap DecodeFrame(SKCodec codec, SKImageInfo info, int frame)
    {
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(SKColors.Transparent);
        var result = codec.GetPixels(info, bitmap.GetPixels(), bitmap.RowBytes, new SKCodecOptions(frame));
        if (result != SKCodecResult.Success)
            throw new InvalidOperationException($"WebP frame {frame} decoding failed: {result}.");
        return ToBitmap(bitmap);
    }

    private static int[] ReadDelays(SKCodec codec, int count)
    {
        var delays = new int[count];
        var frameInfo = codec.FrameInfo;
        for (int i = 0; i < count; i++)
        {
            int duration = i < frameInfo.Length ? frameInfo[i].Duration : 0;
            delays[i] = duration <= 0 ? 100 : Math.Min(duration, 10_000);
        }
        return delays;
    }

    private static Bitmap ToBitmap(SKBitmap source)
    {
        var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        try
        {
            var bounds = new Rectangle(0, 0, source.Width, source.Height);
            var target = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = source.Width * 4;
                var row = new byte[rowBytes];
                for (int y = 0; y < source.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(source.GetPixels(), checked(y * source.RowBytes)), row, 0, rowBytes);
                    Marshal.Copy(row, 0, IntPtr.Add(target.Scan0, checked(y * target.Stride)), rowBytes);
                }
            }
            finally { bitmap.UnlockBits(target); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
}
