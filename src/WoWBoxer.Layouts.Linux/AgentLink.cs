using System.Text;
using System.Text.Json;
using Tmds.DBus.Protocol;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux.Agent;

namespace WoWBoxer.Layouts.Linux;

/// <summary>How <see cref="AgentLink.StartAsync"/> went.</summary>
public enum AgentLinkStart
{
    /// <summary>This process owns the link: the agent is loaded and reports to us.</summary>
    Owner,
    /// <summary>Another WoWBoxer (app or wbx) owns the link; the arguments were handed to it when asked.</summary>
    OtherInstance,
    /// <summary>No session bus or no KWin (not a Plasma session). <see cref="AgentLink.Problem"/> says why.</summary>
    Unavailable,
}

/// <summary>
/// The link to the wowboxer-agent KWin script (docs/linux/port-plan.md, "The agent protocol"). The app owns the D-Bus
/// name <c>com.wowboxer.WoWBoxer</c> and exports <c>/Agent</c> (Pull, Report, Hotkey) and <c>/App</c> (Args); the agent
/// calls them. To make the agent act now, the link invokes the agent's shortcut <c>wowboxer-agent-pull</c>. The link
/// loads the agent into KWin when it is missing or another version, and unloads it again on <see cref="StopAsync"/>.
/// Events are raised on a D-Bus thread: marshal to the UI thread before touching UI state.
/// </summary>
public static class AgentLink
{
    public const string BusName = "com.wowboxer.WoWBoxer";
    public const string AgentPath = "/Agent";
    public const string AgentInterface = "com.wowboxer.Agent1";
    public const string AppPath = "/App";
    public const string AppInterface = "com.wowboxer.App1";
    /// <summary>The KWin script's plugin name.</summary>
    public const string ScriptName = "wowboxer-agent";
    public const string PullShortcut = "wowboxer-agent-pull";
    /// <summary>The agent's shortcut that shows the app window (default Meta+Alt+W).</summary>
    public const string ShowShortcut = "wowboxer-show";

    private const string KWin = "org.kde.KWin";
    private const string ScriptingInterface = "org.kde.kwin.Scripting";
    private const string KGlobalAccel = "org.kde.kglobalaccel";

    private static readonly Lock Gate = new();
    private static AgentState _state = new();
    private static DBusConnection? _connection;
    private static int _triggerRunning;
    private static int _triggerAgain;
    private static TaskCompletionSource _reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource _pulled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The agent's state as last reported, the action queue and the standing state.</summary>
    internal static AgentState State => _state;

    /// <summary>The D-Bus session connection (null before <see cref="StartAsync"/> or when unavailable).</summary>
    internal static DBusConnection? Connection => _connection;

    /// <summary>True while this process owns the link.</summary>
    public static bool IsOwner { get; private set; }

    /// <summary>Why the link does not work (no session bus, KWin refused the script …), or null.</summary>
    public static string? Problem { get; private set; }

    /// <summary>The agent version that reported last ("" until it did).</summary>
    public static string AgentVersion => _state.AgentVersion;

    /// <summary>The agent version that ships with this app (from its metadata.json), "" if the files are missing.</summary>
    public static string ExpectedAgentVersion => AgentFiles.Version();

    /// <summary>When the agent last reported (UTC), null if it never did.</summary>
    public static DateTime? LastReport => _state.ReportedAt;

    /// <summary>Raised after every report (windows, outputs or the active window may have changed).</summary>
    public static event Action? StateChanged;

    /// <summary>The user moved or resized a window by hand (KWin interactive move/resize finished): handle and new frame.</summary>
    public static event Action<nint, PixelRect>? HandEdited;

    /// <summary>The agent gave up restoring a window's keep-above (Wine kept clearing it).</summary>
    public static event Action<nint>? KeepAboveGaveUp;

    /// <summary>A second <c>wowboxer</c> start handed over its arguments.</summary>
    public static event Action<IReadOnlyList<string>>? ArgsReceived;

    /// <summary>The agent's <c>wowboxer-show</c> shortcut (default Meta+Alt+W) was pressed: show the app window.</summary>
    public static event Action? ShowRequested;

    /// <summary>
    /// Connects to the session bus and takes the name. If another WoWBoxer has it, hands <paramref name="args"/> to it
    /// (when not null) and returns <see cref="AgentLinkStart.OtherInstance"/>. Otherwise loads the agent if needed and
    /// waits up to 3 s for its first report.
    /// </summary>
    public static async Task<AgentLinkStart> StartAsync(IReadOnlyList<string>? args = null, CancellationToken cancel = default)
    {
        if (_connection is not null && IsOwner) return AgentLinkStart.Owner;
        DBusConnection connection;
        try
        {
            connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("DBUS_SESSION_BUS_ADDRESS is not set."));
            await connection.ConnectAsync();
        }
        catch (Exception e) when (e is DBusExceptionBase or InvalidOperationException or IOException or System.Net.Sockets.SocketException)
        {
            Problem = $"No D-Bus session bus: {e.Message}";
            return AgentLinkStart.Unavailable;
        }

        connection.AddMethodHandlers([new Handler(AgentPath, HandleAgentAsync), new Handler(AppPath, HandleAppAsync)]);
        bool owner;
        try
        {
            owner = await connection.TryRequestNameAsync(BusName, RequestNameOptions.None);
        }
        catch (DBusExceptionBase e)
        {
            connection.Dispose();
            Problem = $"Could not take the D-Bus name {BusName}: {e.Message}";
            return AgentLinkStart.Unavailable;
        }
        if (!owner)
        {
            try
            {
                if (args is not null)
                    await Bus.CallAsync(connection, BusName, AppPath, AppInterface, "Args", "as", (ref MessageWriter w) => w.WriteArray(args.ToArray()));
            }
            catch (DBusExceptionBase e)
            {
                Problem = $"The running WoWBoxer did not take the arguments: {e.Message}";
            }
            connection.Dispose();
            return AgentLinkStart.OtherInstance;
        }

        lock (Gate)
        {
            _connection = connection;
            _state = new AgentState();
            _reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pulled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            IsOwner = true;
        }
        Problem = null;
        try
        {
            await EnsureAgentAsync(cancel);
        }
        catch (DBusExceptionBase e)
        {
            Problem = $"KWin did not answer ({Bus.ErrorName(e)}): {e.Message}. WoWBoxer needs a KDE Plasma 6 session.";
            return AgentLinkStart.Owner;
        }
        await WaitForReportAsync(TimeSpan.FromSeconds(3), cancel);
        return AgentLinkStart.Owner;
    }

    /// <summary>Waits until the agent has reported at least once. False on timeout.</summary>
    public static async Task<bool> WaitForReportAsync(TimeSpan timeout, CancellationToken cancel = default)
    {
        if (_state.Report is not null) return true;
        Trigger();
        try
        {
            await _reported.Task.WaitAsync(timeout, cancel);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Waits until the agent has confirmed every queued action (or the timeout passed). False on timeout.</summary>
    public static async Task<bool> FlushAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (_connection is not null && _state.Pending.Count > 0)
        {
            if (DateTime.UtcNow >= until) return false;
            Trigger();
            await Task.Delay(50);
        }
        return true;
    }

    /// <summary>
    /// The app quits: unloads the agent (it stops keeping windows above, hover stops), removes its shortcuts with their
    /// keys from kglobalaccel and gives up the name. Windows stay where they are.
    /// </summary>
    public static async Task StopAsync()
    {
        var connection = _connection;
        if (connection is null) return;
        _state.Stop();
        try
        {
            await UnloadScriptAsync(ScriptName);
            // Unloading leaves the shortcuts (and the swap keys) in kglobalaccel's config: remove them too.
            foreach (var name in new[] { PullShortcut, ShowShortcut }.Concat(SwapActions.All.Select(GlobalHotkeys.ShortcutName)))
                await Bus.CallAsync(connection, KGlobalAccel, "/kglobalaccel", "org.kde.KGlobalAccel", "unregister", "ss",
                    (ref MessageWriter w) =>
                    {
                        w.WriteString("kwin");
                        w.WriteString(name);
                    }, Bus.ReadBool);
            await connection.ReleaseNameAsync(BusName);
        }
        catch (DBusExceptionBase)
        {
            // KWin or the bus went away first: nothing left to undo
        }
        lock (Gate)
        {
            _connection = null;
            IsOwner = false;
        }
        connection.Dispose();
    }

    /// <summary>
    /// Asks the agent to pull the desired state now (invokes its <c>wowboxer-agent-pull</c> shortcut). Calls made while
    /// one is under way are merged into one more pull afterwards.
    /// </summary>
    public static void Trigger()
    {
        if (_connection is null) return;
        Interlocked.Exchange(ref _triggerAgain, 1);
        if (Interlocked.CompareExchange(ref _triggerRunning, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref _triggerAgain, 0) == 1 && _connection is { } c)
                    await Bus.CallAsync(c, KGlobalAccel, "/component/kwin", "org.kde.kglobalaccel.Component", "invokeShortcut", "s",
                        (ref MessageWriter w) => w.WriteString(PullShortcut));
            }
            catch (DBusExceptionBase e)
            {
                Problem = $"Could not reach the KWin script ({e.Message}).";
            }
            finally
            {
                Interlocked.Exchange(ref _triggerRunning, 0);
                if (Volatile.Read(ref _triggerAgain) == 1) Trigger();
            }
        });
    }

    // ---------- the agent's KWin script ----------

    /// <summary>Loads the agent unless the loaded one answers with the expected version.</summary>
    private static async Task EnsureAgentAsync(CancellationToken cancel)
    {
        var expected = AgentFiles.Version();
        if (await IsScriptLoadedAsync(ScriptName))
        {
            Trigger();
            try
            {
                await _pulled.Task.WaitAsync(TimeSpan.FromSeconds(2.5), cancel);
            }
            catch (TimeoutException)
            {
            }
            if (_state.AgentVersion.Length > 0 && _state.AgentVersion == expected) return;
            await UnloadScriptAsync(ScriptName);
        }
        if (AgentFiles.MainScript() is not { } main)
        {
            Problem = $"The WoWBoxer KWin script is missing ({AgentFiles.Folder()}).";
            return;
        }
        var id = await Bus.CallAsync(_connection!, KWin, "/Scripting", ScriptingInterface, "loadScript", "ss",
            (ref MessageWriter w) =>
            {
                w.WriteString(main);
                w.WriteString(ScriptName);
            }, Bus.ReadInt32);
        if (id < 0)
        {
            Problem = "KWin refused to load the WoWBoxer KWin script.";
            return;
        }
        await Bus.CallAsync(_connection!, KWin, $"/Scripting/Script{id}", "org.kde.kwin.Script", "run", null, null);
    }

    internal static async Task<bool> IsScriptLoadedAsync(string name) =>
        _connection is { } c && await Bus.CallAsync(c, KWin, "/Scripting", ScriptingInterface, "isScriptLoaded", "s",
            (ref MessageWriter w) => w.WriteString(name), Bus.ReadBool);

    /// <summary>Unloads a script and waits (up to 2 s) until KWin has really let it go (it deletes scripts later).</summary>
    private static async Task UnloadScriptAsync(string name)
    {
        if (_connection is not { } c) return;
        if (!await Bus.CallAsync(c, KWin, "/Scripting", ScriptingInterface, "unloadScript", "s", (ref MessageWriter w) => w.WriteString(name), Bus.ReadBool))
            return;
        for (var i = 0; i < 20 && await IsScriptLoadedAsync(name); i++) await Task.Delay(100);
    }

    // ---------- what the agent calls ----------

    private static ValueTask HandleAgentAsync(MethodContext context)
    {
        var request = context.Request;
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([AgentXml], Array.Empty<string>());
            return ValueTask.CompletedTask;
        }
        if (request.InterfaceAsString != AgentInterface || request.SignatureAsString != "s")
        {
            context.ReplyUnknownMethodError();
            return ValueTask.CompletedTask;
        }
        var argument = request.GetBodyReader().ReadString();
        switch (request.MemberAsString)
        {
            case "Pull":
                var desired = _state.Desired(AgentJson.ParseHello(argument));
                _pulled.TrySetResult();
                ReplyString(context, AgentJson.Serialize(desired));
                break;
            case "Report":
                ReplyVoid(context);
                OnReport(argument);
                break;
            case "Hotkey":
                ReplyVoid(context);
                OnHotkey(argument);
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }
        return ValueTask.CompletedTask;
    }

    private static ValueTask HandleAppAsync(MethodContext context)
    {
        var request = context.Request;
        if (context.IsDBusIntrospectRequest)
            context.ReplyIntrospectXml([AppXml], Array.Empty<string>());
        else if (request.InterfaceAsString == AppInterface && request.MemberAsString == "Args" && request.SignatureAsString == "as")
        {
            var args = request.GetBodyReader().ReadArrayOfString();
            ReplyVoid(context);
            ArgsReceived?.Invoke(args);
        }
        else
            context.ReplyUnknownMethodError();
        return ValueTask.CompletedTask;
    }

    private static void OnReport(string json)
    {
        if (AgentJson.ParseReport(json) is not { } report) return;
        var (edits, gaveUp) = _state.Apply(report, DateTime.UtcNow);
        _reported.TrySetResult();
        // hover may switch between WoW windows only: keep the agent's list equal to the open game windows
        if (_state.SetWow(WindowFinder.FindGameWindows().Select(w => _state.IdOf(w.Handle)).OfType<string>())) Trigger();
        StateChanged?.Invoke();
        foreach (var (h, frame) in edits) HandEdited?.Invoke(h, frame);
        foreach (var h in gaveUp) KeepAboveGaveUp?.Invoke(h);
    }

    /// <summary>The agent sends the bare action name: <c>show</c>, <c>swap:next</c>, <c>swap:client:3</c> …</summary>
    private static void OnHotkey(string action)
    {
        if (action == "show") ShowRequested?.Invoke();
        else GlobalHotkeys.OnAgentHotkey(action);
    }

    private static void ReplyString(MethodContext context, string value)
    {
        if (context.NoReplyExpected) return;
        using var writer = context.CreateReplyWriter("s");
        writer.WriteString(value);
        context.Reply(writer.CreateMessage());
    }

    private static void ReplyVoid(MethodContext context)
    {
        if (context.NoReplyExpected) return;
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    private static readonly ReadOnlyMemory<byte> AgentXml = Encoding.UTF8.GetBytes($"""
        <interface name="{AgentInterface}">
          <method name="Pull"><arg name="hello" type="s" direction="in"/><arg name="desired" type="s" direction="out"/></method>
          <method name="Report"><arg name="state" type="s" direction="in"/></method>
          <method name="Hotkey"><arg name="action" type="s" direction="in"/></method>
        </interface>
        """);

    private static readonly ReadOnlyMemory<byte> AppXml = Encoding.UTF8.GetBytes($"""
        <interface name="{AppInterface}">
          <method name="Args"><arg name="args" type="as" direction="in"/></method>
        </interface>
        """);

    private sealed class Handler(string path, Func<MethodContext, ValueTask> handle) : IPathMethodHandler
    {
        public string Path => path;
        public bool HandlesChildPaths => false;
        public ValueTask HandleMethodAsync(MethodContext context) => handle(context);
    }
}

/// <summary>Where the agent's files are: <c>WBX_AGENT_DIR</c> (development), else next to the app.</summary>
internal static class AgentFiles
{
    public static string Folder() =>
        Environment.GetEnvironmentVariable("WBX_AGENT_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppContext.BaseDirectory, "kwin", AgentLink.ScriptName);

    public static string? MainScript()
    {
        var main = Path.Combine(Folder(), "contents", "code", "main.js");
        return File.Exists(main) ? main : null;
    }

    /// <summary>KPlugin.Version from metadata.json, "" if missing or unreadable.</summary>
    public static string Version()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder(), "metadata.json")));
            return doc.RootElement.TryGetProperty("KPlugin", out var plugin) && plugin.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return "";
        }
    }
}
