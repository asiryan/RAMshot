using RAMshot.Services;

namespace RAMshot;

// Mutable draft for the settings UI; only validated values are persisted or used for scans.
internal sealed class AppSettings
{
    public int MaxReadMiB { get; set; } = 512;
    public int MaxImageMiB { get; set; } = 16;
    public int BatchSeconds { get; set; } = 2;
    public int MinDimension { get; set; } = 128;
    public int MaxDimension { get; set; } = 4096;
    public int MaxImages { get; set; } = 80;
    public int GalleryMiB { get; set; } = 200;
    public int MaxCandidates { get; set; } = 12;
    public int MaxSignatures { get; set; } = 4096;
    public int MaxDecodedMiB { get; set; } = 100;
    public int ScanIntervalMs { get; set; } = 1000;
    public bool HotkeyEnabled { get; set; } = true;
    public bool IncludeMappedRegions { get; set; }
    public bool IncludeImageRegions { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public string? Validate()
    {
        if (MaxReadMiB is < 16 or > 2048) return "Scan size must be between 16 and 2048 MiB";
        if (MaxImageMiB is < 1 or > 64) return "Encoded image size must be between 1 and 64 MiB";
        if (MaxImageMiB >= MaxReadMiB) return "Scan size must exceed the encoded image limit";
        if (BatchSeconds is < 1 or > 30) return "Batch time must be between 1 and 30 seconds";
        if (MinDimension is < 1 or > 4096) return "Minimum dimension must be between 1 and 4096 pixels";
        if (MaxDimension is < 128 or > 4096) return "Maximum dimension must be between 128 and 4096 pixels";
        if (MinDimension > MaxDimension) return "Minimum dimension cannot exceed the maximum";
        if (MaxImages is < 1 or > 500) return "Image count must be between 1 and 500";
        if (GalleryMiB is < 16 or > 1024) return "Gallery memory must be between 16 and 1024 MiB";
        if (MaxCandidates is < 1 or > 256) return "Candidates per batch must be between 1 and 256";
        if (MaxSignatures is < 1 or > 65536) return "Signatures per batch must be between 1 and 65536";
        if (MaxDecodedMiB is < 4 or > 512) return "Decoded memory per batch must be between 4 and 512 MiB";
        if (ScanIntervalMs is < 250 or > 60000) return "Scan interval must be between 250 and 60000 milliseconds";
        return null;
    }

    public ScanOptions ToScanOptions()
    {
        string? error = Validate();
        if (error is not null) throw new ArgumentException(error);
        return new ScanOptions(maxReadBytes: (long)MaxReadMiB * 1024 * 1024,
            maxImageBytes: checked(MaxImageMiB * 1024 * 1024),
            maxCandidates: MaxCandidates, maxSignatures: MaxSignatures,
            maxDecodedBytes: (long)MaxDecodedMiB * 1024 * 1024,
            maxElapsed: TimeSpan.FromSeconds(BatchSeconds),
            minDimension: MinDimension, maxDimension: MaxDimension,
            maxPixels: (long)MaxDimension * MaxDimension,
            includeMappedRegions: IncludeMappedRegions, includeImageRegions: IncludeImageRegions);
    }
}
