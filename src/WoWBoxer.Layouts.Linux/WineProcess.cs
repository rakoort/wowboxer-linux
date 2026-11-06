using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux;

/// <summary>
/// A process seen through <c>/proc</c>. Wine keeps the Windows path of the exe in <c>argv[0]</c>
/// (<c>C:\Program Files (x86)\World of Warcraft\_forever_\Wow.exe</c>) and its prefix in <c>WINEPREFIX</c>, so the
/// Linux path of the exe is <c>&lt;prefix&gt;/drive_c/…</c>.
/// </summary>
/// <param name="WindowsPath">argv[0] as Wine shows it ("" for a Linux program).</param>
/// <param name="Prefix">The Wine prefix (the folder holding drive_c), "" if unknown.</param>
/// <param name="ExePath">The Linux path of the exe.</param>
/// <param name="Name">The exe name without ".exe" (like Windows' process name), e.g. "Wow".</param>
public sealed record WineProcess(int Pid, string WindowsPath, string Prefix, string ExePath, string Name)
{
    /// <summary>
    /// Reads one process. <paramref name="cmdline"/> and <paramref name="environ"/> are the raw <c>/proc</c> files
    /// (NUL-separated); <paramref name="cwd"/> resolves a relative argv[0]; <paramref name="exe"/> is the
    /// <c>/proc/&lt;pid&gt;/exe</c> link (used for Linux programs). Null for an empty command line (kernel thread, gone).
    /// </summary>
    public static WineProcess? Parse(int pid, string cmdline, string environ, string cwd, string exe)
    {
        var argv0 = cmdline.Split('\0')[0];
        if (argv0.Length == 0) return null;
        var prefix = PrefixFrom(environ);
        var windows = LooksLikeWindowsPath(argv0) || argv0.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        if (!windows) return new WineProcess(pid, "", prefix, exe.Length > 0 ? exe : argv0, Path.GetFileName(exe.Length > 0 ? exe : argv0));
        var linux = ToLinuxPath(argv0, prefix, cwd);
        var file = argv0[(argv0.LastIndexOfAny(['\\', '/']) + 1)..];
        var name = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
        return new WineProcess(pid, argv0, prefix, linux, name);
    }

    /// <summary>WINEPREFIX from a raw environ (NUL-separated), without a trailing slash; "" if not set.</summary>
    public static string PrefixFrom(string environ)
    {
        foreach (var entry in environ.Split('\0'))
            if (entry.StartsWith("WINEPREFIX=", StringComparison.Ordinal))
                return entry["WINEPREFIX=".Length..].TrimEnd('/');
        return "";
    }

    /// <summary>"X:\…" or "\\…" (a Windows path as Wine shows it in argv[0]).</summary>
    public static bool LooksLikeWindowsPath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/' || path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// A Windows path in a Wine prefix as a Linux path: <c>C:\a\b.exe</c> → <c>&lt;prefix&gt;/drive_c/a/b.exe</c>, other
    /// drives through <c>dosdevices/&lt;x&gt;:</c>. A relative path is taken from <paramref name="cwd"/> (Wine's working
    /// folder is a Linux path). Without a prefix the default <c>~/.wine</c> is assumed, as Wine does.
    /// </summary>
    public static string ToLinuxPath(string windowsPath, string prefix, string cwd)
    {
        var rest = windowsPath.Replace('\\', '/');
        if (!LooksLikeWindowsPath(windowsPath))
            return Path.GetFullPath(Path.Combine(cwd.Length > 0 ? cwd : "/", rest));
        if (prefix.Length == 0) prefix = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wine");
        var drive = char.ToLowerInvariant(rest[0]);
        var root = drive == 'c' ? Path.Combine(prefix, "drive_c") : Path.Combine(prefix, "dosdevices", $"{drive}:");
        return root + rest[2..];
    }

    /// <summary>The prefix of a Linux path inside one (the part before <c>/drive_c/</c>), or null.</summary>
    public static string? PrefixOf(string linuxPath)
    {
        var i = linuxPath.IndexOf("/drive_c/", StringComparison.Ordinal);
        return i > 0 ? linuxPath[..i] : null;
    }

    /// <summary>
    /// A WoW game client: a <c>Wow*.exe</c> name (not the voice proxy, error reporter …) or a game exe inside a
    /// "World of Warcraft" folder – the upstream rules, on the Windows path.
    /// </summary>
    public bool IsWow => WindowsPath.Length > 0 && (GameDetection.IsWowProcessName(Name) || GameDetection.IsWowPath(WindowsPath.Replace('/', '\\')));

    public WowGame Game => GameDetection.GameOf(ExePath);

    /// <summary>
    /// The start time from <c>/proc/&lt;pid&gt;/stat</c> (field 22, clock ticks after boot) and the boot time
    /// (<c>btime</c> in <c>/proc/stat</c>). Linux counts USER_HZ = 100 ticks per second on every architecture we run on.
    /// </summary>
    public static DateTime? StartTimeFrom(string stat, long bootTimeUnix)
    {
        var close = stat.LastIndexOf(')');   // the name in (…) may contain spaces and ')'
        if (close < 0) return null;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // fields[0] is field 3 (state), so field 22 (starttime) is fields[19]
        if (fields.Length < 20 || !long.TryParse(fields[19], out var ticks)) return null;
        return DateTime.UnixEpoch.AddSeconds(bootTimeUnix).AddTicks(ticks * (TimeSpan.TicksPerSecond / 100));
    }

    /// <summary>The <c>btime</c> line of <c>/proc/stat</c> (boot time, Unix seconds); 0 if missing.</summary>
    public static long BootTimeFrom(string procStat)
    {
        foreach (var line in procStat.Split('\n'))
            if (line.StartsWith("btime ", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6).Trim(), out var t)) return t;
        return 0;
    }
}
