using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RAMshot.Services;

// One process inside a selectable group; StartTicks guards against PID reuse.
public readonly record struct ProcessRef(uint Pid, long StartTicks);

// A selectable application: a windowed root process plus every descendant.
// Scanning a Target scans all of its Processes in order.
public sealed class Target
{
    public uint RootPid { get; init; }
    public string Name { get; init; } = "";
    public bool IsUwp { get; init; }
    public BitmapSource? Icon { get; init; }
    public List<ProcessRef> Processes { get; } = new();

    public string Label => $"{Name}  ·  {RootPid}  ·  {Processes.Count.Declension("process", "processes")}";

    public override string ToString() => Label;
}

// Builds the list of selectable applications: windowed processes plus their
// descendant trees, UWP apps resolved past ApplicationFrameHost, and an
// optional set of background (non-windowed) user processes.
internal static class AppCatalog
{
    private static readonly string[] SystemProcessNames =
    {
        "idle", "system", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "fontdrvhost", "dwm", "conhost", "runtimebroker", "dllhost", "sihost", "ctfmon",
        "textinputhost", "searchhost", "searchapp", "shellexperiencehost", "startmenuexperiencehost",
        "backgroundtaskhost", "audiodg", "spoolsv", "taskhost", "taskhostw", "applicationframehost"
    };

    private static readonly Dictionary<string, BitmapSource> iconCache = new(StringComparer.OrdinalIgnoreCase);

    internal static List<Target> Enumerate(bool includeBackground)
    {
        uint self = (uint)Environment.ProcessId;
        int currentSession = Process.GetCurrentProcess().SessionId;

        // 1. Snapshot every process: pid -> (name, parentPid).
        var snapshot = new Dictionary<uint, (string Name, uint Parent)>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap != InvalidHandle)
        {
            try
            {
                var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
                if (Process32First(snap, ref entry))
                {
                    do { snapshot[entry.ProcessId] = (entry.ExeFile, entry.ParentProcessId); }
                    while (Process32Next(snap, ref entry));
                }
            }
            finally { CloseHandle(snap); }
        }

        // Children map for BFS.
        var children = new Dictionary<uint, List<uint>>();
        foreach (var (pid, info) in snapshot)
        {
            if (!children.TryGetValue(info.Parent, out var list)) children[info.Parent] = list = new List<uint>();
            list.Add(pid);
        }

        // 2. Enumerate windows: collect windowed roots (best window per pid) and a
        //    "has a window" set used later for background detection.
        var windowRoots = new Dictionary<uint, IntPtr>();
        var bestArea = new Dictionary<uint, long>();
        var uwpRoots = new HashSet<uint>();
        var hasWindow = new HashSet<uint>();

        EnumWindows((hwnd, _) =>
        {
            if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0 || pid == self) return true;
            string cls = GetClassName(hwnd);
            if (IsIgnoredClass(cls)) return true;
            hasWindow.Add(pid);
            if (!IsWindowVisible(hwnd) || !IsMainWindow(hwnd)) return true;

            uint key;
            if (snapshot.TryGetValue(pid, out var info) && NameEquals(info.Name, "ApplicationFrameHost"))
            {
                uint real = ResolveUwpPid(hwnd);
                if (real == 0 || !snapshot.ContainsKey(real)) return true; // ghost frame or closed app
                key = real; uwpRoots.Add(real);
            }
            else
            {
                if (!snapshot.ContainsKey(pid)) return true; // stale window of a dead process
                key = pid;
            }
            long area = WindowArea(hwnd);
            if (!bestArea.TryGetValue(key, out long current) || area > current)
            {
                bestArea[key] = area;
                windowRoots[key] = hwnd;
            }
            return true;
        }, IntPtr.Zero);

        // 3. Only windowed processes with no windowed ancestor become top-level roots;
        //    a windowed child folds into its windowed ancestor's group.
        var topRoots = new List<uint>();
        foreach (uint pid in windowRoots.Keys)
        {
            uint cursor = pid;
            var chain = new HashSet<uint>();
            bool hasWindowedAncestor = false;
            while (snapshot.TryGetValue(cursor, out var info) && info.Parent != 0 && chain.Add(cursor))
            {
                cursor = info.Parent;
                if (windowRoots.ContainsKey(cursor)) { hasWindowedAncestor = true; break; }
            }
            if (!hasWindowedAncestor) topRoots.Add(pid);
        }

        var grouped = new HashSet<uint>();
        var targets = new List<Target>();
        foreach (uint pid in topRoots)
        {
            var target = BuildTarget(pid, snapshot, children, self, uwpRoots.Contains(pid));
            targets.Add(target);
            foreach (var p in target.Processes) grouped.Add(p.Pid);
        }

        // 4. Background apps: current-session processes with a (possibly hidden) top-level
        //    window that are not part of any windowed group. As with windowed roots, only a
        //    candidate with no candidate ancestor becomes a root; its descendants fold in.
        if (includeBackground)
        {
            var candidates = new List<uint>();
            foreach (var (pid, info) in snapshot)
            {
                if (pid == 0 || pid == self || grouped.Contains(pid) || !hasWindow.Contains(pid)) continue;
                if (IsSystemProcess(info.Name) || GetSessionId(pid) != currentSession) continue;
                candidates.Add(pid);
            }
            var candidateSet = new HashSet<uint>(candidates);
            foreach (uint pid in candidates)
            {
                if (grouped.Contains(pid)) continue; // absorbed as a descendant by an earlier target
                uint cursor = pid;
                var chain = new HashSet<uint>();
                bool hasCandidateAncestor = false;
                while (snapshot.TryGetValue(cursor, out var info) && info.Parent != 0 && chain.Add(cursor))
                {
                    cursor = info.Parent;
                    if (candidateSet.Contains(cursor)) { hasCandidateAncestor = true; break; }
                }
                if (hasCandidateAncestor) continue;
                var target = BuildTarget(pid, snapshot, children, self, isUwp: false);
                targets.Add(target);
                foreach (var p in target.Processes) grouped.Add(p.Pid);
            }
        }

        targets.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return targets;
    }

    // Re-resolves a group's descendants (and drops dead ones) so newly spawned
    // children — e.g. a fresh browser tab's renderer — are picked up on Resume.
    internal static void RefreshChildren(Target target)
    {
        var snapshot = new Dictionary<uint, (string Name, uint Parent)>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap == InvalidHandle) return;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snap, ref entry)) return;
            do { snapshot[entry.ProcessId] = (entry.ExeFile, entry.ParentProcessId); }
            while (Process32Next(snap, ref entry));
        }
        finally { CloseHandle(snap); }

        var children = new Dictionary<uint, List<uint>>();
        foreach (var (pid, info) in snapshot)
        {
            if (!children.TryGetValue(info.Parent, out var list)) children[info.Parent] = list = new List<uint>();
            list.Add(pid);
        }

        var members = Descendants(target.RootPid, children, (uint)Environment.ProcessId);
        var processes = new List<ProcessRef> { new(target.RootPid, GetStartTicks(target.RootPid)) };
        foreach (uint pid in members) processes.Add(new ProcessRef(pid, GetStartTicks(pid)));
        target.Processes.Clear();
        target.Processes.AddRange(processes);
    }

    // Maps a window handle to the pid that should be selected: the real UWP app
    // pid when the window is hosted by ApplicationFrameHost, otherwise the window's pid.
    internal static uint ResolveWindowPid(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0) return 0;
        using var process = TryGetProcess(pid);
        if (process is not null && process.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            uint real = ResolveUwpPid(hwnd);
            if (real != 0) return real;
        }
        return pid;
    }

    private static Target BuildTarget(uint rootPid, Dictionary<uint, (string Name, uint Parent)> snapshot,
        Dictionary<uint, List<uint>> children, uint self, bool isUwp)
    {
        string name = snapshot.TryGetValue(rootPid, out var rootInfo) ? StripExe(rootInfo.Name) : $"PID {rootPid}";
        var target = new Target { RootPid = rootPid, Name = name, IsUwp = isUwp, Icon = ExtractIcon(rootPid) };
        target.Processes.Add(new ProcessRef(rootPid, GetStartTicks(rootPid)));
        foreach (uint pid in Descendants(rootPid, children, self))
            target.Processes.Add(new ProcessRef(pid, GetStartTicks(pid)));
        return target;
    }

    private static IEnumerable<uint> Descendants(uint root, Dictionary<uint, List<uint>> children, uint self)
    {
        var queue = new Queue<uint>();
        var visited = new HashSet<uint>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            uint pid = queue.Dequeue();
            if (!children.TryGetValue(pid, out var list)) continue;
            foreach (uint child in list)
            {
                if (child == 0 || child == self || !visited.Add(child)) continue;
                yield return child;
                queue.Enqueue(child);
            }
        }
    }

    private static long GetStartTicks(uint pid)
    {
        try { using var process = Process.GetProcessById((int)pid); return process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { return 0; }
    }

    private static int GetSessionId(uint pid)
    {
        try { using var process = Process.GetProcessById((int)pid); return process.SessionId; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { return -1; }
    }

    private static Process? TryGetProcess(uint pid)
    {
        try { return Process.GetProcessById((int)pid); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    private static bool IsSystemProcess(string name)
    {
        foreach (string system in SystemProcessNames)
            if (NameEquals(name, system)) return true;
        return false;
    }

    // Toolhelp32 snapshot names carry the ".exe" extension; System.Diagnostics.Process
    // names do not. Match either form so both call sites agree.
    private static bool NameEquals(string actual, string expected) =>
        actual.Equals(expected, StringComparison.OrdinalIgnoreCase) || actual.Equals(expected + ".exe", StringComparison.OrdinalIgnoreCase);

    // Drop the ".exe" extension for the display name, matching the original ProcessName output.
    private static string StripExe(string fileName) =>
        fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;

    private static bool IsIgnoredClass(string cls) => cls is
        "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or "NotifyIconOverflowWindow"
        or "DV2ControlHost" or "#32768" or "ConsoleWindowClass" or "PseudoConsoleWindow" or "CASCADIA_HOSTING_WINDOW_CLASS";

    private static bool IsMainWindow(IntPtr hwnd)
    {
        long exStyle = GetWindowLongPtr(hwnd, -20 /* GWL_EXSTYLE */).ToInt64();
        if ((exStyle & 0x40000 /* WS_EX_APPWINDOW */) != 0) return true;
        if (GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero) return false;
        return (exStyle & 0x80 /* WS_EX_TOOLWINDOW */) == 0;
    }

    private static long WindowArea(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return 0;
        return (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassNameApi(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    private static uint ResolveUwpPid(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint host);
        uint found = 0;
        EnumChildWindows(hwnd, (child, _) =>
        {
            if (GetWindowThreadProcessId(child, out uint pid) == 0 || pid == 0 || pid == host || pid == (uint)Environment.ProcessId) return true;
            found = pid;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static BitmapSource? ExtractIcon(uint pid)
    {
        string? path = GetExecutablePath(pid);
        if (path is null) return null;
        if (iconCache.TryGetValue(path, out var cached)) return cached;

        IntPtr hIcon = IntPtr.Zero;
        try
        {
            var info = new ShellFileInfo();
            IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 /* SHGFI_ICON */ | 0x1 /* SHGFI_SMALLICON */);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            hIcon = info.hIcon;
            BitmapSource icon = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            icon.Freeze();
            iconCache[path] = icon;
            return icon;
        }
        finally { if (hIcon != IntPtr.Zero) DestroyIcon(hIcon); }
    }

    private static string? GetExecutablePath(uint pid)
    {
        IntPtr process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(512);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    private const int InvalidHandle = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string displayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string typeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder buffer, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassNameApi(IntPtr hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr hIcon);
}
