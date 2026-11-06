namespace WoWBoxer.Layouts.Linux;

/// <summary>What the WoWBoxer AddOn last saved about the character an account played (docs/classes-and-accounts.md).</summary>
/// <param name="Class">WoW's English class file token, e.g. "ROGUE".</param>
public sealed record AddonCharacter(string Character, string Realm, string Class, int Level, DateTimeOffset LoggedInAt, DateTimeOffset SavedAt);

/// <summary>
/// Reads the WoWBoxer AddOn's bridge: <c>WoWBoxerDB.bridge</c> in
/// <c>&lt;install&gt;/WTF/Account/&lt;ACCOUNT&gt;/SavedVariables/WoWBoxer.lua</c>, written by WoW on logout and /reload.
/// Read-only, never runs Lua. The account → window link stays the user's choice.
/// </summary>
public static class AddonBridge
{
    public const string FileName = "WoWBoxer.lua";

    public static string PathFor(string installFolder, string account) =>
        Path.Combine(installFolder, "WTF", "Account", account, "SavedVariables", FileName);

    /// <summary>The bridge of one account, or null (no file, no bridge yet, or a file WoW is still writing).</summary>
    public static AddonCharacter? Read(string installFolder, string account)
    {
        if (Sandbox.Active) return null;
        try
        {
            var path = PathFor(installFolder, account);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The bridge from the text of WoWBoxer.lua, or null if it has none (or the text is not a SavedVariables file).</summary>
    public static AddonCharacter? Parse(string lua)
    {
        IReadOnlyDictionary<string, object?> vars;
        try
        {
            vars = LuaReader.ReadAssignments(lua);
        }
        catch (FormatException)
        {
            return null;
        }
        if (vars.GetValueOrDefault("WoWBoxerDB") is not LuaTable db || db.Table("bridge") is not { } bridge) return null;
        if (bridge.String("character") is not { Length: > 0 } character) return null;
        return new AddonCharacter(character, bridge.String("realm") ?? "", bridge.String("class") ?? "", (int)(bridge.Number("level") ?? 0),
            Time(bridge.Number("loggedInAt")), Time(bridge.Number("savedAt")));
    }

    private static DateTimeOffset Time(double? unixSeconds) =>
        unixSeconds is { } s && s > 0 && s < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds((long)s) : DateTimeOffset.MinValue;
}

/// <summary>
/// Watches an install's <c>WTF/Account/*/SavedVariables/WoWBoxer.lua</c> files and raises <see cref="Changed"/> with the
/// account after WoW wrote one (debounced: WoW writes the file in several steps). Raised on a thread-pool thread.
/// </summary>
public sealed class AddonBridgeWatcher : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    private readonly FileSystemWatcher? _watcher;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Timer> _pending = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>The account whose WoWBoxer.lua changed.</summary>
    public event Action<string>? Changed;

    public AddonBridgeWatcher(string installFolder)
    {
        if (Sandbox.Active) return;
        // WTF/Account may not exist yet on a fresh install: watch the install folder then.
        var accounts = Path.Combine(installFolder, "WTF", "Account");
        var root = Directory.Exists(accounts) ? accounts : installFolder;
        if (!Directory.Exists(root)) return;
        _watcher = new FileSystemWatcher(root, AddonBridge.FileName)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += OnEvent;
        _watcher.Created += OnEvent;
        _watcher.Renamed += OnEvent;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (!string.Equals(Path.GetFileName(e.FullPath), AddonBridge.FileName, StringComparison.OrdinalIgnoreCase)) return;
        var savedVariables = Path.GetDirectoryName(e.FullPath);
        if (!string.Equals(Path.GetFileName(savedVariables), "SavedVariables", StringComparison.OrdinalIgnoreCase)) return;
        var account = Path.GetFileName(Path.GetDirectoryName(savedVariables));
        if (string.IsNullOrEmpty(account)) return;
        lock (_gate)
        {
            if (_disposed) return;
            if (_pending.TryGetValue(account, out var timer))
                timer.Change(Settle, Timeout.InfiniteTimeSpan);
            else
                _pending[account] = new Timer(_ => Fire(account), null, Settle, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire(string account)
    {
        lock (_gate)
        {
            if (_disposed || !_pending.Remove(account, out var timer)) return;
            timer.Dispose();
        }
        Changed?.Invoke(account);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var t in _pending.Values) t.Dispose();
            _pending.Clear();
        }
        _watcher?.Dispose();
    }
}
