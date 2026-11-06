using Tmds.DBus.Protocol;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux.Agent;

namespace WoWBoxer.Layouts.Linux;

/// <summary>
/// Window swap's global hotkeys (Settings › Advanced, off by default) – the Linux replacement for RegisterHotKey and
/// WM_HOTKEY. The agent registers KWin global shortcuts without keys (<c>wowboxer-swap-next</c>, <c>-previous</c>,
/// <c>-client-1</c> … <c>-10</c>, <c>-home</c>); <see cref="ApplyAsync"/> gives them the user's keys through
/// kglobalaccel. KWin takes such a key press for itself, so the game never sees it; the agent then calls
/// <c>Hotkey</c> and <see cref="Pressed"/> fires. Nothing else is watched or sent.
/// </summary>
public static class GlobalHotkeys
{
    private const string KGlobalAccel = "org.kde.kglobalaccel";
    private const string Component = "kwin";
    private static readonly Lock Gate = new();
    private static readonly HashSet<string> Set = [];

    /// <summary>A swap hotkey was pressed: the <see cref="SwapActions"/> action ("next", "client:3" …). Raised on a D-Bus thread.</summary>
    public static event Action<string>? Pressed;

    /// <summary>The actions whose keys are set now.</summary>
    public static IReadOnlyCollection<string> Active
    {
        get
        {
            lock (Gate) return Set.ToList();
        }
    }

    /// <summary>The KWin shortcut name of a swap action ("next" → "wowboxer-swap-next", "client:3" → "wowboxer-swap-client-3").</summary>
    public static string ShortcutName(string action) => "wowboxer-swap-" + action.Replace(':', '-');

    /// <summary>
    /// Replaces the keys of every swap shortcut. Returns the actions that could not get their key, with the reason
    /// (another shortcut uses it …). Actions missing from <paramref name="keys"/> get no key.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ApplyAsync(IReadOnlyDictionary<string, Hotkey> keys)
    {
        var failed = new Dictionary<string, string>();
        if (Sandbox.Active) return failed;
        if (AgentLink.Connection is not { } c)
        {
            foreach (var action in keys.Keys) failed[action] = "WoWBoxer's KWin script is not running";
            return failed;
        }
        await ClearAsync();   // our old keys must not count as "taken"
        var layouts = QtKeys.UserLayouts();
        foreach (var action in SwapActions.All)
        {
            if (!keys.TryGetValue(action, out var key)) continue;
            var combos = QtKeys.CombinationsOf(key, layouts);
            if (combos.Count == 0)
            {
                failed[action] = "KDE has no code for this key";
                continue;
            }
            var taken = false;
            foreach (var combo in combos)
                taken |= !await IsAvailableAsync(c, combo);
            if (taken)
            {
                failed[action] = "used by another shortcut in KDE";
                continue;
            }
            await SetKeysAsync(c, action, combos);
            lock (Gate) Set.Add(action);
        }
        return failed;
    }

    /// <summary>
    /// Removes the keys of every swap shortcut (swap hotkeys off, or the app quits) – also keys left over from a run
    /// that ended without cleaning up, since kglobalaccel keeps keys across sessions.
    /// </summary>
    public static async Task ClearAsync()
    {
        if (Sandbox.Active || AgentLink.Connection is not { } c) return;
        lock (Gate) Set.Clear();
        foreach (var action in SwapActions.All) await SetKeysAsync(c, action, []);
    }

    /// <summary>The window that has the keyboard now (read only; used to pick "the active window's party").</summary>
    public static nint ForegroundWindow()
    {
        if (Sandbox.Active) return 0;
        return AgentLink.State.Report?.Active is { Length: > 0 } id ? AgentLink.State.HandleOf(id) : 0;
    }

    /// <summary>The agent's hotkey name ("swap:next", "swap:client:3" …) → <see cref="Pressed"/> with the swap action.</summary>
    internal static void OnAgentHotkey(string agentAction)
    {
        if (!agentAction.StartsWith("swap:", StringComparison.Ordinal)) return;
        var action = agentAction[5..];
        if (SwapActions.All.Contains(action)) Pressed?.Invoke(action);
    }

    private static Task<bool> IsAvailableAsync(DBusConnection c, int combo) =>
        Bus.CallAsync(c, KGlobalAccel, "/kglobalaccel", "org.kde.KGlobalAccel", "globalShortcutAvailable", "(ai)s",
            (ref MessageWriter w) =>
            {
                w.WriteStructureStart();
                w.WriteArray(new[] { combo, 0, 0, 0 });
                w.WriteString(Component);
            }, Bus.ReadBool);

    /// <summary>kglobalaccel <c>setForeignShortcutKeys(as actionId, a(ai) keys)</c>: one key sequence per combination.</summary>
    private static Task SetKeysAsync(DBusConnection c, string action, IReadOnlyList<int> combos) =>
        Bus.CallAsync(c, KGlobalAccel, "/kglobalaccel", "org.kde.KGlobalAccel", "setForeignShortcutKeys", "asa(ai)",
            (ref MessageWriter w) =>
            {
                var name = ShortcutName(action);
                w.WriteArray(new[] { Component, name, "KWin", name });
                var array = w.WriteArrayStart(DBusType.Struct);
                foreach (var combo in combos)
                {
                    w.WriteStructureStart();
                    w.WriteArray(new[] { combo, 0, 0, 0 });
                }
                w.WriteArrayEnd(array);
            });
}
