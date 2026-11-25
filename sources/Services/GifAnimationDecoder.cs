using System.Drawing;
using System.Drawing.Imaging;

namespace RAMshot.Services;

// Decodes all frames of a GIF so the preview can play it; callers own and dispose the returned bitmaps.
internal static class GifAnimationDecoder
{
    // A malformed file with a huge frame count must not turn the preview into an unbounded allocation.
    private const int MaxFrames = 2000;
    internal sealed record Animation(bool IsAnimated, List<Bitmap> Frames, int[] Delays);

    internal static Animation Decode(byte[] bytes, long maxDecodedBytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var gif = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        var dimension = FrameDimension.Time;
        int count = gif.GetFrameCount(dimension);
        if (count <= 1) return new Animation(false, new List<Bitmap>(), Array.Empty<int>());
        if (count > MaxFrames) return new Animation(true, new List<Bitmap>(), Array.Empty<int>());
        long frameBytes = (long)gif.Width * gif.Height * 4;
        if (frameBytes <= 0 || frameBytes * count > maxDecodedBytes)
            return new Animation(true, new List<Bitmap>(), Array.Empty<int>());
        int[] delays = ReadDelays(gif, count);
        var frames = new List<Bitmap>(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                gif.SelectActiveFrame(dimension, i);
                // GDI+ composites disposal and transparency into the selected frame; copy it to a detached bitmap.
                var frame = new Bitmap(gif.Width, gif.Height, PixelFormat.Format32bppArgb);
                try
                {
                    using (var graphics = Graphics.FromImage(frame)) graphics.DrawImageUnscaled(gif, 0, 0);
                    frames.Add(frame);
                }
                catch { frame.Dispose(); throw; }
            }
        }
        catch
        {
            foreach (var frame in frames) frame.Dispose();
            throw;
        }
        return new Animation(true, frames, delays);
    }

    private static int[] ReadDelays(Image gif, int count)
    {
        const int FrameDelayProperty = 0x5100; // Graphic control extension delays, in hundredths of a second per frame.
        var delays = new int[count];
        try
        {
            if (gif.GetPropertyItem(FrameDelayProperty)?.Value is { Length: >= 4 } value)
            {
                for (int i = 0; i < count; i++)
                {
                    int offset = i * 4;
                    if (offset + 4 > value.Length) break;
                    int hundredths = value[offset] | (value[offset + 1] << 8) | (value[offset + 2] << 16) | (value[offset + 3] << 24);
                    // Browsers treat 0 and 1 hundredths as 100 ms; the long cast keeps malformed values from overflowing.
                    delays[i] = hundredths <= 1 ? 100 : (int)Math.Min((long)hundredths * 10, 10_000);
                }
            }
        }
        catch (ArgumentException) { }
        for (int i = 0; i < count; i++) if (delays[i] <= 0) delays[i] = 100;
        return delays;
    }
}
