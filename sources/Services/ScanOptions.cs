namespace RAMshot.Services;

// Immutable limits for one memory-scan batch. Each scan snapshots these values.
internal sealed class ScanOptions
{
    internal static ScanOptions Default { get; } = new();
    internal long MaxReadBytes { get; }
    internal int MaxImageBytes { get; }
    internal int ChunkSize { get; }
    internal int MaxCandidates { get; }
    internal int MaxSignatures { get; }
    internal long MaxDecodedBytes { get; }
    internal TimeSpan MaxElapsed { get; }
    internal int MinDimension { get; }
    internal int MaxDimension { get; }
    internal long MaxPixels { get; }
    internal bool IncludeMappedRegions { get; }
    internal bool IncludeImageRegions { get; }

    internal ScanOptions(long maxReadBytes = 512L * 1024 * 1024,
        int maxImageBytes = 16 * 1024 * 1024, int chunkSize = 256 * 1024,
        int maxCandidates = 12, int maxSignatures = 4096,
        long maxDecodedBytes = 100L * 1024 * 1024, TimeSpan? maxElapsed = null,
        int minDimension = 128, int maxDimension = 4096, long maxPixels = 16_000_000,
        bool includeMappedRegions = false, bool includeImageRegions = false)
    {
        TimeSpan elapsed = maxElapsed ?? TimeSpan.FromSeconds(2);
        if (maxReadBytes is < 65536 or > 2L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxReadBytes));
        if (maxImageBytes is < 33 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxImageBytes));
        if (chunkSize is < 8 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (maxReadBytes < (long)maxImageBytes + chunkSize + 65536)
            throw new ArgumentException("The read limit must leave room for one image, a chunk and its header.", nameof(maxReadBytes));
        if (maxCandidates is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        if (maxSignatures is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(maxSignatures));
        if (maxDecodedBytes is < 4 or > 512L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxDecodedBytes));
        if (elapsed < TimeSpan.FromMilliseconds(10) || elapsed > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(maxElapsed));
        if (minDimension < 1 || minDimension > maxDimension) throw new ArgumentOutOfRangeException(nameof(minDimension));
        if (maxDimension is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(maxDimension));
        if (maxPixels is < 1 or > 100_000_000) throw new ArgumentOutOfRangeException(nameof(maxPixels));
        MaxReadBytes = maxReadBytes;
        MaxImageBytes = maxImageBytes;
        ChunkSize = chunkSize;
        MaxCandidates = maxCandidates;
        MaxSignatures = maxSignatures;
        MaxDecodedBytes = maxDecodedBytes;
        MaxElapsed = elapsed;
        MinDimension = minDimension;
        MaxDimension = maxDimension;
        MaxPixels = maxPixels;
        IncludeMappedRegions = includeMappedRegions;
        IncludeImageRegions = includeImageRegions;
    }
}
