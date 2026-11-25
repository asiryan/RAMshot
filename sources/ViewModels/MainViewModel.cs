using Microsoft.Win32;
using RAMshot.Services;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RAMshot.ViewModels;

internal sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SettingsStore store;
    private readonly Func<Target, long, CancellationToken, ScanOptions, MemoryImageScanner.ScanResult> scanTarget;
    private AppSettings settings;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<string> hashes = new(StringComparer.Ordinal);
    private CancellationTokenSource? cancellation;
    private int generation;
    private long bytes;
    private long requiredMemoryBytes;
    private bool paused, stoppedForLimit, disposed, refreshing;
    private enum StopReason { None, Manual, Quota, End, Memory, Error }
    private StopReason stopReason;
    private long scanCursor;
    private int portionAdded;
    private int portionQuota;
    private Target? selectedTarget;
    private ImageEntry? selectedImage;
    private BitmapSource? selectedPreview;
    private DispatcherTimer? animationTimer;
    private List<BitmapSource> animationFrames = new();
    private int[] animationDelays = Array.Empty<int>();
    private int animationFrame;
    private int previewVersion;
    private bool suppressPreviewRestart;
    private string status = "Select a process to start scanning", settingsStatus = "", selectedLabel = "No image selected", copyLabel = "Copy", saveLabel = "Save", applyLabel = "Apply settings", selectedFormatLabel = "";
    private Visibility settingsVisibility = Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SettingsChanged;
    public ObservableCollection<Target> Targets { get; } = new();
    public ObservableCollection<ImageEntry> Images { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SelectImageCommand { get; }
    public ICommand ApplySettingsCommand { get; }
    public ICommand ToggleSettingsCommand { get; }
    public string ReadMiB { get; set; }
    public string ImageMiB { get; set; }
    public string BatchSeconds { get; set; }
    public string MaxCandidates { get; set; }
    public string MaxSignatures { get; set; }
    public string MaxDecodedMiB { get; set; }
    public string MinDimension { get; set; }
    public string MaxDimension { get; set; }
    public string MaxImages { get; set; }
    public string GalleryMiB { get; set; }
    public string IntervalMs { get; set; }
    public bool HotkeyEnabled { get; set; }
    public bool IncludeMappedRegions { get; set; }
    public bool IncludeImageRegions { get; set; }
    public bool IsHotkeyEnabled => settings.HotkeyEnabled;
    public string HotkeyHelp => settings.HotkeyEnabled ? "Images stay in memory · Ctrl + Shift + F12" : "Images stay in memory · Hotkey disabled";
    public string Status { get => status; private set { status = value; Changed(); } }
    public string SettingsStatus { get => settingsStatus; private set { settingsStatus = value; Changed(); } }
    public string SelectedLabel { get => selectedLabel; private set { selectedLabel = value; Changed(); } }
    public string ImageCountLabel => $"{Images.Count} images";
    public string PauseLabel => paused ? "Resume" : "Pause";
    public string CopyLabel { get => copyLabel; private set { copyLabel = value; Changed(); } }
    public string SaveLabel { get => saveLabel; private set { saveLabel = value; Changed(); } }
    public string ApplyLabel { get => applyLabel; private set { applyLabel = value; Changed(); } }
    public bool ShowEmptyHint => selectedTarget is null && Images.Count == 0;
    public BitmapSource? SelectedPreview { get => selectedPreview; private set { if (ReferenceEquals(selectedPreview, value)) return; selectedPreview = value; Changed(); } }
    public string SelectedRegionLabel => selectedImage is null ? "" : $"First found in: {selectedImage.RegionKind} region";
    public string SelectedFormatLabel { get => selectedFormatLabel; private set { if (selectedFormatLabel == value) return; selectedFormatLabel = value; Changed(); } }
    public Visibility PreviewVisibility => settingsVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SettingsVisibility
    {
        get => settingsVisibility;
        set
        {
            if (settingsVisibility == value) return;
            settingsVisibility = value;
            Changed();
            Changed(nameof(PreviewVisibility));
            // Stop decoding and timer work while the preview is hidden behind the settings panel; resume when it returns.
            if (value == Visibility.Visible) StopAnimation();
            else if (!suppressPreviewRestart && selectedImage is not null) BeginPreview(selectedImage);
        }
    }
    public Target? SelectedTarget { get => selectedTarget; set { if (ReferenceEquals(selectedTarget, value)) return; selectedTarget = value; Changed(); Changed(nameof(ShowEmptyHint)); if (!refreshing) { paused = false; stoppedForLimit = false; Changed(nameof(PauseLabel)); Restart(true); } } }

    public MainViewModel(IntPtr initialWindow) : this(initialWindow, new SettingsStore(), ScanTarget) { }

    internal MainViewModel(IntPtr initialWindow, SettingsStore store, Func<Target, long, CancellationToken, ScanOptions, MemoryImageScanner.ScanResult> scanTarget)
    {
        this.store = store;
        this.scanTarget = scanTarget;
        settings = store.Load();
        portionQuota = settings.MaxImages;
        ReadMiB = settings.MaxReadMiB.ToString(); ImageMiB = settings.MaxImageMiB.ToString(); BatchSeconds = settings.BatchSeconds.ToString();
        MaxCandidates = settings.MaxCandidates.ToString(); MaxSignatures = settings.MaxSignatures.ToString(); MaxDecodedMiB = settings.MaxDecodedMiB.ToString();
        MinDimension = settings.MinDimension.ToString(); MaxDimension = settings.MaxDimension.ToString(); MaxImages = settings.MaxImages.ToString(); GalleryMiB = settings.GalleryMiB.ToString(); IntervalMs = settings.ScanIntervalMs.ToString(); HotkeyEnabled = settings.HotkeyEnabled;
        IncludeMappedRegions = settings.IncludeMappedRegions; IncludeImageRegions = settings.IncludeImageRegions;
        RefreshCommand = new RelayCommand(_ => RefreshTargets(SelectedTarget?.Handle ?? IntPtr.Zero));
        PauseCommand = new RelayCommand(_ => TogglePause());
        ClearCommand = new RelayCommand(_ => { if (stoppedForLimit) { paused = false; stoppedForLimit = false; Changed(nameof(PauseLabel)); } Clear(); if (!paused) Restart(false); });
        CopyCommand = new RelayCommand(_ => CopySelected());
        SaveCommand = new RelayCommand(_ => SaveSelected());
        SelectImageCommand = new RelayCommand(SelectImage);
        ApplySettingsCommand = new RelayCommand(_ => ApplySettings());
        ToggleSettingsCommand = new RelayCommand(_ => { SettingsVisibility = SettingsVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; ResetApplyLabel(); });
        RefreshTargets(initialWindow);
    }

    public void SelectWindow(IntPtr hwnd) { if (hwnd != IntPtr.Zero && SelectedTarget is null) RefreshTargets(hwnd); }
    private void RefreshTargets(IntPtr preferred)
    {
        Target? previous = SelectedTarget;
        GetWindowThreadProcessId(preferred, out uint wanted);
        if (wanted == 0 && previous is not null) wanted = previous.Pid;
        var targets = new List<Target>(); var seen = new HashSet<uint>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == Environment.ProcessId || !seen.Add(pid)) return true;
            try { using var process = Process.GetProcessById((int)pid); targets.Add(new Target(hwnd, pid, process.ProcessName, process.StartTime.ToUniversalTime().Ticks)); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { }
            return true;
        }, IntPtr.Zero);
        targets.Sort((a,b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name,b.Name));
        refreshing = true;
        Targets.Clear(); foreach (var target in targets) Targets.Add(target);
        SelectedTarget = Targets.FirstOrDefault(target => target.Pid == wanted);
        refreshing = false;
        bool same = previous is not null && SelectedTarget is not null && previous.Pid == SelectedTarget.Pid && previous.StartTicks == SelectedTarget.StartTicks;
        if (!same) { paused = false; stoppedForLimit = false; Changed(nameof(PauseLabel)); Restart(true); }
        else if (previous!.Handle != SelectedTarget!.Handle && !paused) Restart(false);
    }
    private void SelectImage(object? value)
    {
        var previous = selectedImage;
        if (selectedImage is not null) selectedImage.IsSelected = false;
        selectedImage = value as ImageEntry;
        if (selectedImage is not null) selectedImage.IsSelected = true;
        if (!ReferenceEquals(previous, selectedImage)) { CopyLabel = "Copy"; SaveLabel = "Save"; }
        if (selectedImage is not null)
        {
            // Collapsing the settings panel must not restart the preview for the previously selected image.
            suppressPreviewRestart = true;
            SettingsVisibility = Visibility.Collapsed;
            suppressPreviewRestart = false;
            ApplyLabel = "Apply settings";
        }
        SelectedLabel = selectedImage is null ? "No image selected" : selectedImage.Label;
        BeginPreview(selectedImage);
        Changed(nameof(SelectedRegionLabel));
    }
    // Shows the first frame immediately and starts the animation once the frames are decoded in the background.
    private void BeginPreview(ImageEntry? image)
    {
        StopAnimation();
        SelectedPreview = image?.Image;
        SelectedFormatLabel = image is null ? "" : image.Format is "GIF" or "WebP" ? $"{image.Format} · First frame shown" : image.Format;
        if (image is { Format: "GIF", EncodedBytes: { Length: > 0 } bytes })
        {
            int version = previewVersion;
            _ = LoadAnimationAsync(image, bytes, version);
        }
    }
    private void StopAnimation()
    {
        previewVersion++;
        animationTimer?.Stop();
        animationTimer = null;
        animationFrames.Clear();
        animationDelays = Array.Empty<int>();
        animationFrame = 0;
    }
    private async Task LoadAnimationAsync(ImageEntry image, byte[] bytes, int version)
    {
        GifPreview? preview = null;
        // A broken animation must never affect the gallery: the already shown first frame stays.
        try
        {
            preview = await Task.Run<GifPreview>(() =>
            {
                var animation = GifAnimationDecoder.Decode(bytes, MaxPreviewAnimationBytes);
                try
                {
                    return new GifPreview(animation.IsAnimated, animation.Frames.Select(ConvertImage).ToList(), animation.Delays);
                }
                finally
                {
                    foreach (var frame in animation.Frames) frame.Dispose();
                }
            });
        }
        catch (Exception) { }
        // The selection, the settings panel or the gallery may have changed while decoding.
        if (version != previewVersion || !ReferenceEquals(selectedImage, image) || preview is null) return;
        if (!preview.IsAnimated) { SelectedFormatLabel = "GIF · static"; return; }
        if (preview.Frames.Count == 0) { SelectedFormatLabel = "GIF · animation (preview limited)"; return; }
        animationFrames = preview.Frames;
        animationDelays = preview.Delays;
        animationFrame = 0;
        SelectedPreview = animationFrames[0];
        SelectedFormatLabel = $"GIF · animation, {animationFrames.Count} frames";
        animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(animationDelays[0]) };
        animationTimer.Tick += AdvanceAnimation;
        animationTimer.Start();
    }
    private void AdvanceAnimation(object? sender, EventArgs e)
    {
        if (animationFrames.Count == 0) { StopAnimation(); return; }
        animationFrame = (animationFrame + 1) % animationFrames.Count;
        SelectedPreview = animationFrames[animationFrame];
        if (animationTimer is not null) animationTimer.Interval = TimeSpan.FromMilliseconds(animationDelays[animationFrame]);
    }
    private void TogglePause()
    {
        if (!paused)
        {
            paused = true; stopReason = StopReason.Manual; stoppedForLimit = false;
            Changed(nameof(PauseLabel)); Cancel(); Status = "Paused"; return;
        }
        long memoryAvailable = (long)settings.GalleryMiB * 1048576 - bytes;
        if (stopReason == StopReason.Memory && (memoryAvailable <= 0 || memoryAvailable < requiredMemoryBytes))
        {
            Status = "Gallery memory limit reached. Increase the limit or clear images";
            return;
        }
        if (stopReason is StopReason.Quota or StopReason.End)
        {
            portionAdded = 0; portionQuota = settings.MaxImages;
            Changed(nameof(ImageCountLabel));
        }
        paused = false; stoppedForLimit = false; stopReason = StopReason.None;
        Changed(nameof(PauseLabel)); Restart(false);
    }
    private void Cancel() { generation++; cancellation?.Cancel(); cancellation = null; }
    private void Clear() { StopAnimation(); Images.Clear(); hashes.Clear(); bytes = 0; requiredMemoryBytes = 0; scanCursor = 0; portionAdded = 0; portionQuota = settings.MaxImages; stopReason = StopReason.None; selectedImage = null; SelectedLabel = "No image selected"; CopyLabel = "Copy"; SaveLabel = "Save"; SelectedPreview = null; SelectedFormatLabel = ""; Changed(nameof(SelectedRegionLabel)); Changed(nameof(ImageCountLabel)); Changed(nameof(ShowEmptyHint)); }
    private void Restart(bool clear)
    {
        Cancel();
        if (clear) { Clear(); if (stoppedForLimit) { paused = false; stoppedForLimit = false; Changed(nameof(PauseLabel)); } }
        if (disposed || paused) return;
        if (SelectedTarget is null) { Status = "Select a process to start scanning"; return; }
        var cts = new CancellationTokenSource(); cancellation = cts;
        _ = ScanLoop(SelectedTarget, generation, cts);
    }
    private async Task ScanLoop(Target target, int version, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            while (!token.IsCancellationRequested && !disposed && version == generation)
            {
                Status = $"Scanning {target.Name} · {Images.Count} images";
                if (!await WaitForGateAsync(token)) return;
                AppSettings active;
                long batchStart;
                ScanBatch? batch;
                try
                {
                    if (token.IsCancellationRequested || disposed || version != generation) return;
                    // Snapshot the UI-owned gallery state before crossing into the worker.
                    active = settings.Clone();
                    batchStart = scanCursor;
                    var knownHashes = new HashSet<string>(hashes, StringComparer.Ordinal);
                    long remainingBytes = (long)active.GalleryMiB * 1048576 - bytes;
                    int remainingSlots = portionQuota - portionAdded;
                    var sourceOptions = active.ToScanOptions();
                    var scanOptions = new ScanOptions(sourceOptions.MaxReadBytes, sourceOptions.MaxImageBytes,
                        sourceOptions.ChunkSize, Math.Min(sourceOptions.MaxCandidates, remainingSlots), sourceOptions.MaxSignatures,
                        sourceOptions.MaxDecodedBytes, sourceOptions.MaxElapsed, sourceOptions.MinDimension, sourceOptions.MaxDimension, sourceOptions.MaxPixels,
                        sourceOptions.IncludeMappedRegions, sourceOptions.IncludeImageRegions);
                    batch = await Task.Run<ScanBatch?>(() =>
                    {
                        if (token.IsCancellationRequested) return null;
                        using var scan = scanTarget(target, batchStart, token, scanOptions);
                        var converted = new List<ConvertedImage>();
                        bool limitReached = false;
                        long requiredBytes = 0;
                        long galleryLimit = (long)active.GalleryMiB * 1048576;
                        foreach (var candidate in scan.Candidates)
                        {
                            if (token.IsCancellationRequested) return null;
                            if (!knownHashes.Add(candidate.Hash)) continue;
                            // The gallery holds the decoded first frame plus the original GIF bytes needed for a lossless save.
                            long size = (long)candidate.Image.Width * candidate.Image.Height * 4 + (candidate.EncodedBytes?.Length ?? 0);
                            // A single image that is larger than the entire gallery can never fit.
                            if (size > galleryLimit) continue;
                            if (remainingSlots <= 0 || size > remainingBytes)
                            {
                                limitReached = true;
                                requiredBytes = size;
                                break;
                            }
                            // Freeze the WPF image on the worker before handing it to the UI.
                            BitmapSource image = ConvertImage(candidate.Image);
                            converted.Add(new ConvertedImage(image, candidate.Hash, candidate.Image.Width, candidate.Image.Height, size, candidate.RegionKind, candidate.Format, candidate.EncodedBytes));
                            remainingSlots--;
                            remainingBytes -= size;
                        }
                        return new ScanBatch(converted, scan.BytesRead, scan.NextAddress, scan.IsEnd, limitReached, requiredBytes);
                    });
                }
                finally { gate.Release(); }
                if (batch is null || disposed || token.IsCancellationRequested || version != generation) return;
                foreach (var candidate in batch.Images)
                {
                    hashes.Add(candidate.Hash); bytes += candidate.Bytes;
                    portionAdded++;
                    Images.Add(new ImageEntry(candidate.Image, candidate.Width, candidate.Height, candidate.RegionKind, candidate.Format, candidate.Hash, candidate.EncodedBytes)); Changed(nameof(ImageCountLabel)); Changed(nameof(ShowEmptyHint));
                }
                if (batch.LimitReached)
                { scanCursor = batchStart; requiredMemoryBytes = batch.RequiredBytes; paused = stoppedForLimit = true; stopReason = StopReason.Memory; Changed(nameof(PauseLabel)); Status = "Gallery memory limit reached. Increase the limit or clear images"; return; }
                scanCursor = batch.IsEnd ? 0 : batch.NextAddress;
                if (portionAdded >= portionQuota)
                { paused = stoppedForLimit = true; stopReason = StopReason.Quota; Changed(nameof(PauseLabel)); Status = $"Found {portionAdded} images. Press Resume for the next batch"; return; }
                if (batch.IsEnd)
                { paused = true; stoppedForLimit = false; stopReason = StopReason.End; Changed(nameof(PauseLabel)); Status = "Scan complete. Press Resume to search again"; return; }
                Status = $"{target.Name} · {Images.Count} images · {batch.BytesRead / 1048576} MiB scanned";
                await WaitForDelayAsync(active.ScanIntervalMs, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed && version == generation) { paused = true; stopReason = StopReason.Error; Changed(nameof(PauseLabel)); Status = $"Scan stopped (error 0x{ex.HResult:X8}). Check access or select another process"; } }
        finally { if (ReferenceEquals(cancellation, cts)) cancellation = null; cts.Dispose(); }
    }
    // Observe cancellation by task status rather than awaiting a canceled task:
    // routine pause/switch operations then do not raise first-chance exceptions.
    private async Task<bool> WaitForGateAsync(CancellationToken token)
    {
        // SemaphoreSlim's cancellable asynchronous wait throws internally on cancellation.
        // A nonblocking probe avoids that first-chance exception and leaves no abandoned waiter.
        while (!token.IsCancellationRequested)
        {
            if (gate.Wait(0)) return true;
            await WaitForDelayAsync(30, token);
        }
        return false;
    }

    private static async Task WaitForDelayAsync(int milliseconds, CancellationToken token)
    {
        Task delay = Task.Delay(milliseconds, token);
        await delay.ContinueWith(task => { if (!task.IsCanceled) task.GetAwaiter().GetResult(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private static MemoryImageScanner.ScanResult ScanTarget(Target target, long cursor, CancellationToken token, ScanOptions options)
    {
        if (GetWindowThreadProcessId(target.Handle, out uint pid) == 0 || pid != target.Pid) throw new InvalidOperationException("Target process changed.");
        using var process = Process.GetProcessById((int)target.Pid);
        if (process.StartTime.ToUniversalTime().Ticks != target.StartTicks) throw new InvalidOperationException("Target process changed.");
        return MemoryImageScanner.Scan(target.Handle, cursor, token, target.Pid, options);
    }
    private void CopySelected() { if (selectedImage is null) { Status = "Select an image first"; return; } try { Clipboard.SetImage(selectedImage.Image); CopyLabel = "Copied"; } catch (Exception ex) { Status = $"Clipboard unavailable (error 0x{ex.HResult:X8})"; } }
    private void SaveSelected()
    {
        if (selectedImage is null) { Status = "Select an image first"; return; }
        var (name, extension) = selectedImage.Format switch
        {
            "JPEG" => ("JPEG image", "jpg"),
            "GIF" => ("GIF image", "gif"),
            "BMP" => ("BMP image", "bmp"),
            "WebP" => ("WebP image", "webp"),
            _ => ("PNG image", "png")
        };
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save image",
                Filter = $"{name} (*.{extension})|*.{extension}",
                DefaultExt = extension,
                FileName = SaveFileName(selectedImage, extension),
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog() != true) return;
            SaveImage(selectedImage, dialog.FileName);
            SaveLabel = "Saved";
        }
        catch (Exception ex) { Status = $"Unable to save image (error 0x{ex.HResult:X8})"; }
    }
    // The name is the content hash, so identical bytes always produce the same file name.
    private static string SaveFileName(ImageEntry image, string extension)
    {
        string hash = image.Hash.Length >= 16 ? image.Hash[..16] : image.Hash;
        if (hash.Length == 0) hash = "unknown";
        return $"{hash.ToLowerInvariant()}.{extension}";
    }
    private static void SaveImage(ImageEntry image, string path)
    {
        // GIF is written exactly as found: re-encoding keeps only one frame and loses delays, palettes and metadata.
        if (image.Format == "GIF" && image.EncodedBytes is { Length: > 0 } bytes) { File.WriteAllBytes(path, bytes); return; }
        if (image.Format == "WebP") { SaveWebp(image.Image, path); return; }
        BitmapEncoder encoder = image.Format switch
        {
            "JPEG" => new JpegBitmapEncoder { QualityLevel = 100 },
            "GIF" => new GifBitmapEncoder(),
            "BMP" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };
        encoder.Frames.Add(BitmapFrame.Create(image.Image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    // WebP has no WPF encoder, so it is written through SkiaSharp like decoding is.
    private static void SaveWebp(BitmapSource source, string path)
    {
        int width = source.PixelWidth, height = source.PixelHeight, stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        source.CopyPixels(pixels, stride, 0);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 100) ?? throw new InvalidOperationException("WebP encoding failed.");
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }
    private static BitmapSource ConvertImage(System.Drawing.Bitmap bitmap)
    {
        int stride = checked(bitmap.Width * 4);
        var pixels = new byte[checked(stride * bitmap.Height)];
        var bounds = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, checked(y * data.Stride)), pixels, y * stride, stride);
        }
        finally { bitmap.UnlockBits(data); }
        var image = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }
    // Animation preview frames are capped so a pathological GIF cannot exhaust memory; the first frame is still shown.
    private const long MaxPreviewAnimationBytes = 64L * 1024 * 1024;
    private sealed record GifPreview(bool IsAnimated, List<BitmapSource> Frames, int[] Delays);

    private void ApplySettings()
    {
        if (!int.TryParse(ReadMiB,out int read) || !int.TryParse(ImageMiB,out int image) || !int.TryParse(BatchSeconds,out int seconds) || !int.TryParse(MaxCandidates,out int candidates) || !int.TryParse(MaxSignatures,out int signatures) || !int.TryParse(MaxDecodedMiB,out int decoded) || !int.TryParse(MinDimension,out int min) || !int.TryParse(MaxDimension,out int max) || !int.TryParse(MaxImages,out int count) || !int.TryParse(GalleryMiB,out int gallery) || !int.TryParse(IntervalMs,out int interval)) { SettingsStatus = "Enter whole numbers for all limits"; return; }
        var updated = new AppSettings { MaxReadMiB=read, MaxImageMiB=image, BatchSeconds=seconds, MaxCandidates=candidates, MaxSignatures=signatures, MaxDecodedMiB=decoded, MinDimension=min, MaxDimension=max, MaxImages=count, GalleryMiB=gallery, ScanIntervalMs=interval, HotkeyEnabled=HotkeyEnabled, IncludeMappedRegions=IncludeMappedRegions, IncludeImageRegions=IncludeImageRegions };
        if (updated.Validate() is string error) { SettingsStatus = error; return; }
        try { store.Save(updated); settings = updated; ApplyLabel = "Applied"; SettingsStatus = "Settings saved. Changes apply to the next scan batch"; Changed(nameof(HotkeyHelp)); SettingsChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { SettingsStatus = $"Unable to save settings (error 0x{ex.HResult:X8})"; }
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    // Any edit in the settings form invalidates the "Applied" confirmation on the Apply button.
    internal void ResetApplyLabel() { if (applyLabel != "Apply settings") ApplyLabel = "Apply settings"; }
    public void Dispose() { disposed = true; Cancel(); Clear(); }
    private sealed record ConvertedImage(BitmapSource Image, string Hash, int Width, int Height, long Bytes, string RegionKind, string Format, byte[]? EncodedBytes);
    private sealed record ScanBatch(List<ConvertedImage> Images, long BytesRead, long NextAddress, bool IsEnd, bool LimitReached, long RequiredBytes);
    public sealed record Target(IntPtr Handle, uint Pid, string Name, long StartTicks)
    {
        public string Label => $"{Name}  ·  {Pid}";
        public override string ToString() => Label;
    }
    public sealed class ImageEntry : INotifyPropertyChanged
    {
        private bool isSelected;
        public ImageEntry(BitmapSource image, int width, int height) : this(image, width, height, "Unknown") { }
        public ImageEntry(BitmapSource image, int width, int height, string regionKind) : this(image, width, height, regionKind, "Unknown format") { }
        public ImageEntry(BitmapSource image, int width, int height, string regionKind, string format) : this(image, width, height, regionKind, format, "") { }
        public ImageEntry(BitmapSource image, int width, int height, string regionKind, string format, string hash, byte[]? encodedBytes = null)
        { Image = image; Width = width; Height = height; RegionKind = regionKind; Format = format; Hash = hash; EncodedBytes = encodedBytes; }
        public BitmapSource Image { get; }
        public int Width { get; }
        public int Height { get; }
        public string RegionKind { get; }
        public string Format { get; }
        public string Hash { get; }
        // GIF only: the complete encoded file, used for byte-exact saving and animation preview.
        public byte[]? EncodedBytes { get; }
        public string Label => $"{Width} × {Height}";
        public string CardLabel => $"{Label}  ·  {RegionKind}";
        public bool IsSelected
        {
            get => isSelected;
            set { if (isSelected == value) return; isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private sealed class RelayCommand(Action<object?> action) : ICommand { public event EventHandler? CanExecuteChanged { add { } remove { } } public bool CanExecute(object? p) => true; public void Execute(object? p) => action(p); }
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
