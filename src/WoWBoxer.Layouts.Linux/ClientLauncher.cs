using System.Diagnostics;
using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux;

/// <summary>
/// Starts WoW clients at their own login screen with <c>umu-run</c> in the game's Wine prefix (no Battle.net auto-login:
/// see docs/launch-research.md). The user logs in to each. Clients start one after the other (a few seconds apart), so
/// the first one has set up the Proton container the others join. Only clients this launcher started can be closed by it.
/// </summary>
public sealed class ClientLauncher
{
    /// <summary>Time between two client starts.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly HashSet<int> _started = [];         // WoW client pids that appeared after our umu-run starts
    private readonly HashSet<int> _seen = [];            // WoW pids that were there before (never ours)
    private readonly List<Process> _launchers = [];      // umu-run processes
    private string _prefix = "";
    private int _expected;

    public IReadOnlyCollection<int> Started
    {
        get
        {
            lock (_gate)
            {
                Discover();
                _started.RemoveWhere(id => ProcessInfo.Inspect(id) is not { IsWow: true });
                return _started.ToList();
            }
        }
    }

    /// <summary>Starts <paramref name="count"/> clients (the first now, the others <see cref="Gap"/> apart) and returns how many are on their way.</summary>
    public int Start(string exe, int count, IStatusSink sink)
    {
        if (Sandbox.Active)
        {
            sink.Info("Demo mode: no WoW client is opened.");
            return 0;
        }
        if (!File.Exists(exe))
        {
            sink.Warn($"Game not found at {exe}. Set the game exe in Settings.");
            return 0;
        }
        if (count < 1) return 0;
        lock (_gate)
        {
            Discover();
            _seen.UnionWith(ProcessInfo.WowClients().Select(p => p.Pid).Where(p => !_started.Contains(p)));
            _prefix = WineProcess.PrefixOf(exe) is { } prefix ? ProcessInfo.RealPath(prefix) : "";
        }
        if (!Launch(exe, sink)) return 0;
        if (count > 1)
        {
            var context = SynchronizationContext.Current;
            _ = Task.Run(async () =>
            {
                for (var i = 1; i < count; i++)
                {
                    await Task.Delay(Gap);
                    var ok = true;
                    if (context is null) ok = Launch(exe, sink);
                    else context.Send(_ => ok = Launch(exe, sink), null);
                    if (!ok) break;
                }
            });
        }
        sink.Ok($"Opening {count} WoW login screen(s), one after the other. Log in to each one yourself – the windows are placed as they appear.");
        return count;
    }

    private bool Launch(string exe, IStatusSink sink)
    {
        try
        {
            var p = WinePrefixes.Start(exe);
            lock (_gate)
            {
                _launchers.Add(p);
                _expected++;
            }
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            sink.Warn("umu-run was not found. Install umu-launcher (it starts Windows games with Proton).");
        }
        catch (InvalidOperationException e)
        {
            sink.Warn($"Could not start {Path.GetFileName(exe)}: {e.Message}");
        }
        return false;
    }

    /// <summary>WoW clients in our prefix that were not there before count as ours, up to the number we started.</summary>
    private void Discover()
    {
        if (_expected == 0) return;
        foreach (var p in ProcessInfo.WowClients())
        {
            if (_started.Count >= _expected) break;
            if (_seen.Contains(p.Pid) || _started.Contains(p.Pid) || p.Prefix != _prefix) continue;
            _started.Add(p.Pid);
        }
    }

    /// <summary>Closes only the clients this launcher started (never the user's own).</summary>
    public int CloseMine(IStatusSink sink)
    {
        var closed = 0;
        foreach (var id in Started)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                p.Kill();
                closed++;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
        lock (_gate)
        {
            _seen.UnionWith(_started);   // a pid we closed never counts again
            _started.Clear();
            foreach (var launcher in _launchers) launcher.Dispose();
            _launchers.Clear();
            _expected = 0;
        }
        sink.Info(closed == 0 ? "No clients started by this app are running." : $"Closed {closed} client(s) this app had started.");
        return closed;
    }
}

/// <summary>
/// Where the WoW client lives: the setting, <c>WBX_GAME_EXE</c>, a running client, then the known Wine prefixes
/// (faugus-launcher's, <c>~/Games/*</c>, <c>~/Faugus/*</c>, <c>~/.wine</c>).
/// </summary>
public static class GameInstall
{
    private const string WowFolder = "drive_c/Program Files (x86)/World of Warcraft";

    /// <summary>The client exe of <paramref name="game"/>. A configured exe of another game is ignored.</summary>
    public static string? FindExe(string? configured, WowGame game = WowGame.ClassicEra)
    {
        if (Sandbox.Active) return Sandbox.GameExe;
        foreach (var candidate in new[] { configured, Environment.GetEnvironmentVariable("WBX_GAME_EXE") })
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate) && GameDetection.GameOf(candidate) is var g && (g == game || g == WowGame.Unknown))
                return candidate;

        foreach (var w in WindowFinder.FindGameWindows(game))
            if (w.ExePath is { Length: > 0 } running && File.Exists(running)) return running;
        foreach (var p in ProcessInfo.WowClients())
            if (p.Game == game && File.Exists(p.ExePath)) return p.ExePath;

        if (GameDetection.InstallOf(game) is not { } install) return null;
        var (folder, exe) = install;
        foreach (var prefix in WinePrefixes.Known())
        {
            var dir = Path.Combine(prefix, WowFolder, folder);
            if (!Directory.Exists(dir)) continue;
            var path = Path.Combine(dir, exe);
            if (File.Exists(path)) return path;
            // Wine folders are case-sensitive on Linux: accept any Wow*.exe game client there
            var other = Directory.EnumerateFiles(dir, "*.exe").FirstOrDefault(f => GameDetection.IsWowProcessName(Path.GetFileNameWithoutExtension(f)));
            if (other is not null) return other;
        }
        return null;
    }

    /// <summary>The client folder (holds WTF) for an exe.</summary>
    public static string? FolderOf(string? exe) => exe is null ? null : Path.GetDirectoryName(exe);

    /// <summary>Accounts and characters from that client's WTF folder (read-only hints).</summary>
    public static IReadOnlyList<WtfAccount> Accounts(string? exe) => Sandbox.Active ? [] : FolderOf(exe) is { } folder ? WtfInfo.Read(folder) : [];
}
