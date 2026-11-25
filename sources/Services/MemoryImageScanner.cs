using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace RAMshot.Services;

// Searches only the foreground process's readable memory for complete encoded images.
// These are resources, not necessarily the pixels currently displayed on screen.
internal static class MemoryImageScanner
{
    // Mapped views and image (module) sections are searched only when enabled in the scan settings.
    private const uint MemMapped = 0x40000, MemImage = 0x1000000;
    private const uint Commit = 0x1000, Guard = 0x100, NoAccess = 0x01;
    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly SearchValues<byte> SignatureStarts = SearchValues.Create(new byte[] { 137, 255, 71, 66, 82 });
    private enum ImageKind { Png, Jpeg, Gif, Bmp, Webp }

    internal sealed class Candidate : IDisposable
    {
        internal Bitmap Image { get; }
        internal string Format { get; }
        internal string Hash { get; }
        internal string RegionKind { get; }
        // Original encoded bytes are kept for every format so saves are byte-for-byte exact.
        internal byte[] EncodedBytes { get; }
        internal Candidate(Bitmap image, string format, string hash, string regionKind, byte[] encodedBytes) { Image = image; Format = format; Hash = hash; RegionKind = regionKind; EncodedBytes = encodedBytes; }
        public void Dispose() => Image.Dispose();
    }

    internal sealed class ScanResult : IDisposable
    {
        internal List<Candidate> Candidates { get; } = new();
        internal long BytesRead { get; set; }
        internal int RegionsRead { get; set; }
        internal long DecodedBytes { get; set; }
        internal string StopReason { get; set; } = "Completed";
        internal long NextAddress { get; set; }
        internal bool IsEnd { get; set; }
        public void Dispose() { foreach (var candidate in Candidates) candidate.Dispose(); Candidates.Clear(); }
    }

    internal static ScanResult Scan(IntPtr window, long startAddress = 0, CancellationToken cancellationToken = default, uint expectedProcessId = 0, ScanOptions? options = null)
    {
        options ??= ScanOptions.Default;
        uint excludedRegionTypes = (options.IncludeMappedRegions ? 0u : MemMapped) | (options.IncludeImageRegions ? 0u : MemImage);
        if (startAddress < 0) throw new ArgumentOutOfRangeException(nameof(startAddress));
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("Memory scan requires a 64-bit process.");
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out uint pid) == 0 || pid == 0)
            throw new InvalidOperationException("The target window is unavailable.");
        if (expectedProcessId != 0 && pid != expectedProcessId)
            throw new InvalidOperationException("The target window now belongs to a different process.");
        IntPtr process = OpenProcess(0x0410, false, pid); // PROCESS_QUERY_INFORMATION | PROCESS_VM_READ
        if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read target process memory.");
        var result = new ScanResult();
        try
        {
            var clock = Stopwatch.StartNew();
            var seen = new HashSet<long>();
            var imageHashes = new HashSet<string>(StringComparer.Ordinal);
            bool memoryLimit = false;
            bool readBudgetReached = false;
            long address = startAddress;
            long nextAddress = address;
            const int overlap = 17; // Retain the complete longest signature across chunk boundaries.
            var chunk = new byte[options.ChunkSize + overlap];
            var tail = new byte[overlap];
            while (address >= 0 && result.BytesRead < options.MaxReadBytes && clock.Elapsed < options.MaxElapsed && result.Candidates.Count < options.MaxCandidates && seen.Count < options.MaxSignatures && !cancellationToken.IsCancellationRequested)
            {
                if (VirtualQueryEx(process, new IntPtr(address), out var region, (nuint)Marshal.SizeOf<MemoryInfo>()) == 0) { result.IsEnd = true; break; }
                long start = region.BaseAddress.ToInt64();
                long length = (long)region.RegionSize;
                if (length <= 0 || start < 0 || start > long.MaxValue - length) { result.IsEnd = true; break; }
                long end = start + length;
                if (region.State == Commit && (region.Protect & (Guard | NoAccess)) == 0 && IsReadable(region.Protect) && (region.Type & excludedRegionTypes) == 0)
                {
                    result.RegionsRead++;
                    int previous = 0;
                    long position = Math.Max(address, start);
                    while (position < end && result.BytesRead < options.MaxReadBytes && clock.Elapsed < options.MaxElapsed && result.Candidates.Count < options.MaxCandidates && seen.Count < options.MaxSignatures && !cancellationToken.IsCancellationRequested)
                    {
                        int count = (int)Math.Min(Math.Min(options.ChunkSize, end - position), options.MaxReadBytes - result.BytesRead);
                        if (count <= 0) break;
                        ReadProcessMemory(process, new IntPtr(position), chunk, (nuint)count, out nuint read);
                        int actual = (int)Math.Min((ulong)read, (ulong)count);
                        result.BytesRead += actual;
                        if (actual == 0)
                        {
                            // A region can change between VirtualQueryEx and ReadProcessMemory.
                            // Skip only the current page so a transient failure cannot hide an entire chunk.
                            previous = 0;
                            int pageRemaining = Environment.SystemPageSize - (int)(position % Environment.SystemPageSize);
                            position += Math.Min(count, pageRemaining);
                            nextAddress = position;
                            continue;
                        }
                        // ReadProcessMemory fills the start of its output buffer; shift to preserve overlap.
                        if (previous > 0)
                        {
                            Buffer.BlockCopy(chunk, 0, chunk, previous, actual);
                            Buffer.BlockCopy(tail, 0, chunk, 0, previous);
                        }
                        int available = previous + actual;
                        long baseAddress = position - previous;
                        int i = 0;
                        for (; i + 18 <= available && result.Candidates.Count < options.MaxCandidates && clock.Elapsed < options.MaxElapsed && seen.Count < options.MaxSignatures && result.BytesRead < options.MaxReadBytes && !cancellationToken.IsCancellationRequested; i++)
                        {
                            int skip = chunk.AsSpan(i, available - i - overlap).IndexOfAny(SignatureStarts);
                            if (skip < 0) { i = available - overlap; break; }
                            i += skip;
                            bool png = chunk.AsSpan(i, 8).SequenceEqual(PngSignature);
                            bool jpeg = chunk[i] == 0xFF && chunk[i + 1] == 0xD8 && chunk[i + 2] == 0xFF;
                            bool gif = chunk.AsSpan(i, 6).SequenceEqual("GIF87a"u8) || chunk.AsSpan(i, 6).SequenceEqual("GIF89a"u8);
                            bool bmp = chunk[i] == (byte)'B' && chunk[i + 1] == (byte)'M' && chunk[i + 6] == 0 && chunk[i + 7] == 0 && chunk[i + 8] == 0 && chunk[i + 9] == 0;
                            bool webp = chunk.AsSpan(i, 4).SequenceEqual("RIFF"u8) && chunk.AsSpan(i + 8, 4).SequenceEqual("WEBP"u8);
                            if (!png && !jpeg && !gif && !bmp && !webp) continue;
                            ImageKind kind = png ? ImageKind.Png : jpeg ? ImageKind.Jpeg : gif ? ImageKind.Gif : bmp ? ImageKind.Bmp : ImageKind.Webp;
                            long candidateAddress = baseAddress + i;
                            if (seen.Contains(candidateAddress)) continue;
                            long headerLimit = Math.Min(Math.Min(jpeg ? 65536 : 64, options.MaxImageBytes), end - candidateAddress);
                            if (options.MaxReadBytes - result.BytesRead < headerLimit) { readBudgetReached = true; break; }
                            int headerSize = (int)headerLimit;
                            int minimumHeader = kind switch { ImageKind.Png => 33, ImageKind.Jpeg => 4, ImageKind.Gif => 13, ImageKind.Bmp => 26, _ => 12 };
                            if (headerSize < minimumHeader) continue;
                            var header = new byte[headerSize];
                            ReadProcessMemory(process, new IntPtr(candidateAddress), header, (nuint)headerSize, out nuint headerRead);
                            int headerBytes = (int)Math.Min((ulong)headerRead, (ulong)headerSize);
                            result.BytesRead += headerBytes;
                            int candidateLimit = (int)Math.Min(options.MaxImageBytes, end - candidateAddress);
                            // A JPEG with large metadata segments can keep its SOF far past the small header.
                            // Such a candidate is allowed through and the retry loop below reads further.
                            if (kind != ImageKind.Webp)
                            {
                                bool dimensionsKnown = TryGetDimensions(header.AsSpan(0, headerBytes), kind, options, out int candidateWidth, out int candidateHeight, out bool truncated);
                                bool canGrow = kind == ImageKind.Jpeg && truncated && headerBytes == headerSize && candidateLimit > headerBytes;
                                if (!dimensionsKnown && !canGrow) continue;
                                if (dimensionsKnown && (candidateWidth < options.MinDimension || candidateHeight < options.MinDimension)) continue;
                            }
                            if (kind == ImageKind.Webp && (headerBytes < 12 || LittleEndian(header.AsSpan(4, 4)) < 4 || (long)LittleEndian(header.AsSpan(4, 4)) + 8 > options.MaxImageBytes)) continue;
                            seen.Add(candidateAddress);
                            if (kind == ImageKind.Bmp)
                                candidateLimit = (int)Math.Min(candidateLimit, LittleEndian(header.AsSpan(2, 4)));
                            else if (kind == ImageKind.Webp)
                                candidateLimit = (int)Math.Min(candidateLimit, (long)LittleEndian(header.AsSpan(4, 4)) + 8);
                            int bytes = headerBytes;
                            var data = header;
                            bool found = TryFindImage(data.AsSpan(0, bytes), kind, out int imageLength, out int width, out int height, options);
                            while (!found && bytes < candidateLimit && !cancellationToken.IsCancellationRequested && clock.Elapsed < options.MaxElapsed)
                            {
                                int requested = Math.Min(Math.Max(options.ChunkSize, bytes), candidateLimit - bytes);
                                requested = (int)Math.Min(requested, options.MaxReadBytes - result.BytesRead);
                                if (requested <= 0) { readBudgetReached = true; break; }
                                var next = new byte[requested];
                                ReadProcessMemory(process, new IntPtr(candidateAddress + bytes), next, (nuint)requested, out nuint candidateRead);
                                int actualRead = (int)Math.Min((ulong)candidateRead, (ulong)requested);
                                result.BytesRead += actualRead;
                                if (actualRead == 0) break;
                                if (bytes + actualRead > data.Length)
                                    Array.Resize(ref data, Math.Min(candidateLimit, Math.Max(bytes + actualRead, data.Length * 2)));
                                Buffer.BlockCopy(next, 0, data, bytes, actualRead);
                                bytes += actualRead;
                                found = TryFindImage(data.AsSpan(0, bytes), kind, out imageLength, out width, out height, options);
                            }
                            // Retry a candidate next batch if its remaining bytes could not be examined.
                            if (!found && (readBudgetReached || clock.Elapsed >= options.MaxElapsed || cancellationToken.IsCancellationRequested)) break;
                            if (found && width >= options.MinDimension && height >= options.MinDimension)
                            {
                                // Decoding and hashing can be expensive; do not start them when the batch is already done.
                                if (cancellationToken.IsCancellationRequested || clock.Elapsed >= options.MaxElapsed) break;
                                try
                                {
                                    string hash = Convert.ToHexString(SHA256.HashData(data.AsSpan(0, imageLength)));
                                    if (imageHashes.Contains(hash)) continue;
                                    long decodedBytes = (long)width * height * 4;
                                    // An image larger than the whole budget cannot fit in any batch.
                                    if (decodedBytes > options.MaxDecodedBytes) continue;
                                    if (result.DecodedBytes + decodedBytes > options.MaxDecodedBytes)
                                    {
                                        memoryLimit = true;
                                        break;
                                    }
                                    Bitmap? bitmap;
                                    if (kind == ImageKind.Webp)
                                    {
                                        // WebpDecoder returns an independent Bitmap, so transfer its ownership.
                                        bitmap = WebpDecoder.Decode(data.AsSpan(0, imageLength), width, height, options);
                                    }
                                    else
                                    {
                                        using var stream = new MemoryStream(data, 0, imageLength, writable: false);
                                        using var decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
                                        bitmap = new Bitmap(decoded); // Detach from the backing stream.
                                    }
                                    if (bitmap is null) continue;
                                    try
                                    {
                                        if (bitmap.Width == width && bitmap.Height == height)
                                        {
                                            string regionKind = region.Type switch 
                                            { 
                                                0x20000 => "Private",
                                                0x40000 => "Mapped",
                                                0x1000000 => "Image module",
                                                _ => "Unknown"
                                            };
                                            string format = kind switch { ImageKind.Png => "PNG", ImageKind.Jpeg => "JPEG", ImageKind.Gif => "GIF", ImageKind.Bmp => "BMP", _ => "WebP" };
                                            // Keep the complete encoded file (all frames, metadata and trailing data of the format) for an exact save.
                                            byte[] encodedBytes = data.AsSpan(0, imageLength).ToArray();
                                            result.Candidates.Add(new Candidate(bitmap, format, hash, regionKind, encodedBytes));
                                            bitmap = null; // Candidate owns the image now.
                                            result.DecodedBytes += decodedBytes;
                                            imageHashes.Add(hash);
                                        }
                                    }
                                    finally { bitmap?.Dispose(); }
                                }
                                catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException) { }
                            }
                        }
                        // Preserve the first unexamined byte when the batch stops mid-chunk.
                        nextAddress = Math.Max(address, baseAddress + i);
                        if (i + 18 <= available) break;
                        previous = Math.Min(overlap, available);
                        Buffer.BlockCopy(chunk, available - previous, tail, 0, previous);
                        position += actual;
                        nextAddress = position < end ? Math.Max(address, position - previous) : end;
                    }
                    if (position < end) break;
                }
                address = end;
                nextAddress = end;
            }
            result.NextAddress = result.IsEnd ? 0 : nextAddress;
            result.StopReason = result.Candidates.Count >= options.MaxCandidates ? "Candidate limit reached" :
                memoryLimit ? "Image memory limit reached" :
                seen.Count >= options.MaxSignatures ? "Signature limit reached" :
                readBudgetReached || result.BytesRead >= options.MaxReadBytes ? "Read limit reached" : cancellationToken.IsCancellationRequested ? "Cancelled" : clock.Elapsed >= options.MaxElapsed ? "Time limit reached" : "Completed";
            return result;
        }
        catch { result.Dispose(); throw; }
        finally { CloseHandle(process); }
    }

    private static bool IsReadable(uint protection) => (protection & 0xFF) is 0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80;
    private static bool ValidDimensions(int width, int height, ScanOptions options) => width > 0 && height > 0 && width <= options.MaxDimension && height <= options.MaxDimension && (long)width * height <= options.MaxPixels;
    private static uint BigEndian(ReadOnlySpan<byte> b) => ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    private static uint LittleEndian(ReadOnlySpan<byte> b) => (uint)b[0] | ((uint)b[1] << 8) | ((uint)b[2] << 16) | ((uint)b[3] << 24);

    private static bool TryGetDimensions(ReadOnlySpan<byte> bytes, ImageKind kind, ScanOptions options, out int width, out int height, out bool truncated)
    {
        if (kind is ImageKind.Png or ImageKind.Jpeg)
            return TryGetDimensions(bytes, kind == ImageKind.Png, options, out width, out height, out truncated);
        width = height = 0;
        truncated = false;
        if (kind == ImageKind.Gif)
        {
            if (bytes.Length < 13 || !(bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))) return false;
            width = bytes[6] | (bytes[7] << 8);
            height = bytes[8] | (bytes[9] << 8);
            return ValidDimensions(width, height, options);
        }
        if (kind == ImageKind.Bmp)
        {
            if (bytes.Length < 26 || bytes[0] != 'B' || bytes[1] != 'M') return false;
            uint fileSize = LittleEndian(bytes.Slice(2, 4)), offset = LittleEndian(bytes.Slice(10, 4)), dib = LittleEndian(bytes.Slice(14, 4));
            if (fileSize < 26 || fileSize > options.MaxImageBytes || offset >= fileSize || dib < 12 || (long)dib + 14 > offset) return false;
            if (dib == 12)
            {
                width = bytes[18] | (bytes[19] << 8); height = bytes[20] | (bytes[21] << 8);
            }
            else if (dib >= 40)
            {
                int w = unchecked((int)LittleEndian(bytes.Slice(18, 4)));
                int h = unchecked((int)LittleEndian(bytes.Slice(22, 4)));
                if (w <= 0 || h == int.MinValue) return false;
                width = w; height = Math.Abs(h);
            }
            else return false;
            return ValidDimensions(width, height, options);
        }
        return false;
    }

    private static bool TryFindImage(ReadOnlySpan<byte> bytes, ImageKind kind, out int length, out int width, out int height, ScanOptions options)
    {
        if (kind is ImageKind.Png or ImageKind.Jpeg)
            return TryFindImage(bytes, kind == ImageKind.Png, out length, out width, out height, options);
        length = width = height = 0;
        if (kind == ImageKind.Bmp)
        {
            if (!TryGetDimensions(bytes, kind, options, out width, out height, out _)) return false;
            uint size = LittleEndian(bytes.Slice(2, 4));
            if (size > bytes.Length) return false;
            length = (int)size;
            return true;
        }
        if (kind == ImageKind.Gif)
        {
            if (!TryGetDimensions(bytes, kind, options, out width, out height, out _)) return false;
            int pos = 13;
            if ((bytes[10] & 0x80) != 0) pos += 3 * (1 << ((bytes[10] & 7) + 1));
            bool hasImage = false;
            while (pos < bytes.Length)
            {
                byte marker = bytes[pos++];
                if (marker == 0x3B) { if (!hasImage) return false; length = pos; return true; }
                if (marker == 0x21)
                {
                    if (pos >= bytes.Length) return false;
                    pos++; // extension label; extension data is a chain of sub-blocks.
                }
                else if (marker == 0x2C)
                {
                    if (bytes.Length - pos < 9) return false;
                    byte packed = bytes[pos + 8]; pos += 9;
                    if ((packed & 0x80) != 0) pos += 3 * (1 << ((packed & 7) + 1));
                    if (pos >= bytes.Length) return false;
                    pos++; // LZW minimum code size.
                    hasImage = true;
                }
                else return false;
                while (true)
                {
                    if (pos >= bytes.Length) return false;
                    int block = bytes[pos++];
                    if (block == 0) break;
                    if (block > bytes.Length - pos) return false;
                    pos += block;
                }
            }
            return false;
        }
        if (kind == ImageKind.Webp)
        {
            if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return false;
            long size = (long)LittleEndian(bytes.Slice(4, 4)) + 8;
            if (size < 20 || size > options.MaxImageBytes || size > bytes.Length) return false;
            int end = (int)size, pos = 12;
            bool hasPixels = false;
            while (pos <= end - 8)
            {
                ReadOnlySpan<byte> name = bytes.Slice(pos, 4);
                uint chunkSize = LittleEndian(bytes.Slice(pos + 4, 4));
                long next = (long)pos + 8 + chunkSize + (chunkSize & 1);
                if (next > end) return false;
                var payload = bytes.Slice(pos + 8, (int)chunkSize);
                if (name.SequenceEqual("VP8X"u8) && payload.Length >= 10)
                {
                    width = 1 + payload[4] + (payload[5] << 8) + (payload[6] << 16);
                    height = 1 + payload[7] + (payload[8] << 8) + (payload[9] << 16);
                }
                else if (name.SequenceEqual("VP8L"u8) && payload.Length >= 5 && payload[0] == 0x2f)
                {
                    uint bits = LittleEndian(payload.Slice(1, 4));
                    if (width == 0) width = (int)(bits & 0x3fff) + 1;
                    if (height == 0) height = (int)((bits >> 14) & 0x3fff) + 1;
                    hasPixels = true;
                }
                else if (name.SequenceEqual("VP8 "u8) && payload.Length >= 10 && payload.Slice(3, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
                {
                    if (width == 0) width = (payload[6] | (payload[7] << 8)) & 0x3fff;
                    if (height == 0) height = (payload[8] | (payload[9] << 8)) & 0x3fff;
                    hasPixels = true;
                }
                else if (name.SequenceEqual("ANMF"u8)) hasPixels = true;
                pos = (int)next;
            }
            if (pos != end || !hasPixels || !ValidDimensions(width, height, options)) return false;
            length = end;
            return true;
        }
        return false;
    }

    private static bool TryGetDimensions(ReadOnlySpan<byte> bytes, bool png, ScanOptions options, out int width, out int height, out bool truncated)
    {
        width = height = 0;
        truncated = false;
        if (png)
        {
            if (bytes.Length < 33 || !bytes[..8].SequenceEqual(PngSignature) || BigEndian(bytes.Slice(8, 4)) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
            uint w = BigEndian(bytes.Slice(16, 4)), h = BigEndian(bytes.Slice(20, 4));
            if (w > options.MaxDimension || h > options.MaxDimension || !ValidDimensions((int)w, (int)h, options)) return false;
            width = (int)w; height = (int)h; return true;
        }
        if (bytes.Length < 2 || bytes[0] != 0xff || bytes[1] != 0xd8) return false;
        if (bytes.Length < 4) { truncated = true; return false; }
        int p = 2;
        while (p + 4 <= bytes.Length)
        {
            if (bytes[p++] != 0xff) return false;
            while (p < bytes.Length && bytes[p] == 0xff) p++;
            if (p >= bytes.Length) { truncated = true; return false; }
            int marker = bytes[p++];
            if (marker is 0xda or 0xd8 or 0xd9 or 0x00) return false;
            if (p + 2 > bytes.Length) { truncated = true; return false; }
            int size = (bytes[p] << 8) | bytes[p + 1];
            if (size < 2) return false;
            if (size > bytes.Length - p) { truncated = true; return false; }
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (size < 7) return false;
                height = (bytes[p + 3] << 8) | bytes[p + 4];
                width = (bytes[p + 5] << 8) | bytes[p + 6];
                return ValidDimensions(width, height, options);
            }
            p += size;
        }
        truncated = true;
        return false;
    }

    internal static bool TryFindImage(ReadOnlySpan<byte> bytes, bool png, out int length, out int width, out int height, ScanOptions? options = null)
    {
        options ??= ScanOptions.Default;
        length = width = height = 0;
        if (png)
        {
            if (bytes.Length < 33 || !bytes[..8].SequenceEqual(PngSignature) || BigEndian(bytes.Slice(8, 4)) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
            uint w = BigEndian(bytes.Slice(16, 4)), h = BigEndian(bytes.Slice(20, 4));
            if (w > options.MaxDimension || h > options.MaxDimension || !ValidDimensions((int)w, (int)h, options)) return false;
            width = (int)w; height = (int)h;
            int pos = 8;
            while (pos <= bytes.Length - 12)
            {
                uint size = BigEndian(bytes.Slice(pos, 4));
                if (size > bytes.Length - pos - 12) return false;
                bool end = bytes.Slice(pos + 4, 4).SequenceEqual("IEND"u8);
                pos += 12 + (int)size;
                if (end) { if (size != 0) return false; length = pos; return true; }
            }
            return false;
        }
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) return false;
        int p = 2;
        while (p + 4 <= bytes.Length)
        {
            if (bytes[p++] != 0xff) return false;
            while (p < bytes.Length && bytes[p] == 0xff) p++;
            if (p >= bytes.Length) return false;
            int marker = bytes[p++];
            if (marker == 0xda) break;
            if (marker is 0xd8 or 0xd9 or 0x00 || p + 2 > bytes.Length) return false;
            int size = (bytes[p] << 8) | bytes[p + 1];
            if (size < 2 || size > bytes.Length - p) return false;
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (size < 7) return false;
                height = (bytes[p + 3] << 8) | bytes[p + 4];
                width = (bytes[p + 5] << 8) | bytes[p + 6];
                if (!ValidDimensions(width, height, options)) return false;
            }
            p += size;
        }
        if (!ValidDimensions(width, height, options)) return false;
        for (int i = p; i + 1 < bytes.Length; i++)
            if (bytes[i] == 0xff && bytes[i + 1] == 0xd9) { length = i + 2; return true; }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId; public nuint RegionSize; public uint State, Protect, Type; }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint VirtualQueryEx(IntPtr process, IntPtr address, out MemoryInfo info, nuint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint bytesRead);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
