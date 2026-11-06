using System.Collections.ObjectModel;
using System.Windows.Input;
using WoWBoxer.Layouts.App.Infrastructure;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.App.ViewModels;

/// <summary>
/// Window swap (Settings › Advanced, off by default; Guide "Swap windows (advanced)"). Hotkeys move a client into the
/// main slot and the client that sat there into the chosen client's old slot. Only windows are moved – with the same
/// placement code as always (never activating, never sending input); hover to activate or a click decides which
/// window gets the keys. The layout file is never changed by a swap: <see cref="Placed"/> is the layout as placed now.
/// </summary>
public sealed partial class MainViewModel
{
    private int[] _swapPos = [];
    private string? _swapLayout;
    private int _swapCursor = -1;
    private SwapHotkeyHost? _hotkeys;
    private IReadOnlyDictionary<string, string> _hotkeyFailures = new Dictionary<string, string>();
    /// <summary>Hotkey edits not applied yet (null = none). Only <see cref="ApplyHotkeyChangesCommand"/> makes them work.</summary>
    private Dictionary<string, string>? _pendingKeys;

    /// <summary>The layout as the windows are placed now: <see cref="Current"/> with any swaps applied.</summary>
    public LayoutProfile? Placed => Current is null ? null
        : WindowSwap.Apply(Current, _swapPos.Length == Current.Slots.Count ? _swapPos : WindowSwap.Home(Current.Slots.Count));

    /// <summary>True while at least one client is out of its home slot.</summary>
    public bool IsSwapped => Current is not null && _swapPos.Length == Current.Slots.Count && !WindowSwap.IsHome(_swapPos);

    // ---------- settings (Settings › Advanced) ----------

    public bool SwapHotkeysEnabled
    {
        get => _settings.SwapHotkeys;
        set
        {
            if (value == _settings.SwapHotkeys) return;
            SetSetting(() => _settings.SwapHotkeys = value);
            if (!value)
            {
                ResetSwap(quiet: false);   // off = every window back home
                _pendingKeys = null;       // and unapplied hotkey edits are dropped
            }
            ApplyHotkeys();
            Status.Info(value
                ? "Window swap hotkeys are on. They only move windows – nothing is sent to the game."
                : "Window swap hotkeys are off.");
            RaiseSwap();
        }
    }

    public IReadOnlyList<ScopeChoice> SwapScopeChoices { get; } =
    [
        new(SwapCycleScope.Party1, "Party 1"),
        new(SwapCycleScope.ActiveParty, "The party of the active window"),
        new(SwapCycleScope.All, "All clients (into party 1's main slot)"),
    ];

    public ScopeChoice SwapScope
    {
        get => SwapScopeChoices.FirstOrDefault(c => c.Scope == _settings.SwapScope) ?? SwapScopeChoices[0];
        set
        {
            if (value is null) return;
            _swapCursor = -1;
            SetSetting(() => _settings.SwapScope = value.Scope);
        }
    }

    public bool SwapRemember
    {
        get => _settings.SwapRemember;
        set => SetSetting(() => _settings.SwapRemember = value);
    }

    /// <summary>One row per swap action in the hotkey editor.</summary>
    public ObservableCollection<HotkeyRow> HotkeyRows { get; } = [];

    /// <summary>"13 hotkeys active", "2 could not be registered: …" or "" when off.</summary>
    public string SwapStatusText
    {
        get
        {
            if (!_settings.SwapHotkeys) return "";
            if (Demo.On) return "Demo: hotkeys are not registered (nothing on this PC is changed).";
            var active = _hotkeys?.Active.Count ?? 0;
            var text = $"{active} hotkey{(active == 1 ? "" : "s")} active";
            if (_hotkeyFailures.Count > 0) text += $" · {_hotkeyFailures.Count} not available (see the list)";
            return text;
        }
    }

    /// <summary>Shown next to the map while swap is on but a party has no main slot yet.</summary>
    public string MainSlotHint => !_settings.SwapHotkeys || Current is null || !WindowSwap.NeedsChoice(Current) ? ""
        : Current.Slots.Count > Presets.PartySize
            ? "Window swap: click the crown on one window per party to make it that party's main slot."
            : "Window swap: click the crown on a window to make it the main slot – the hotkeys work once there is one.";

    /// <summary>Per client (row index): does it sit in a main slot now? Null when swap is off (no crowns on the map).</summary>
    public IReadOnlyList<bool>? MainSlotMarks
    {
        get
        {
            if (!_settings.SwapHotkeys || Current is null) return null;
            var mains = WindowSwap.MainSlots(Current);
            var pos = _swapPos.Length == Current.Slots.Count ? _swapPos : WindowSwap.Home(Current.Slots.Count);
            return pos.Select(slot => mains.Contains(slot)).ToList();
        }
    }

    // ---------- cycle order ----------

    /// <summary>Ready-made next/previous orders for this layout ("All in number order", "Leader ↔ healer" …).</summary>
    public IReadOnlyList<WindowSwap.OrderPreset> SwapOrderPresets => Current is null ? [] : WindowSwap.OrderPresets(Current);

    public ICommand UseSwapOrderCommand => new RelayCommand(o =>
    {
        if (o is not WindowSwap.OrderPreset p) return;
        SetSwapOrder(p.Name == "All in number order" ? null : p.Clients.ToList());
    });

    /// <summary>The custom order as chips ("1", "3" …); empty = number order.</summary>
    public IReadOnlyList<OrderChip> SwapOrderChips =>
        (_settings.SwapOrder ?? []).Select((n, i) => new OrderChip(i, n, ChipLabel(n))).ToList();

    /// <summary>Clients that can be added to the custom order.</summary>
    public IReadOnlyList<OrderChip> SwapOrderSource =>
        Current is null ? [] : Current.Slots.Select((s, i) => new OrderChip(-1, s.N, ChipLabel(s.N))).ToList();

    public string SwapOrderText => _settings.SwapOrder is { Count: >= 2 } o
        ? "Order: " + string.Join(" → ", o) + " → (again)"
        : "Order: number order (1 → 2 → 3 …), closed windows skipped.";

    public ICommand AddToSwapOrderCommand => new RelayCommand(o =>
    {
        if (o is not OrderChip c) return;
        SetSwapOrder([.. _settings.SwapOrder ?? [], c.Number]);
    });

    public ICommand RemoveFromSwapOrderCommand => new RelayCommand(o =>
    {
        if (o is not OrderChip c || _settings.SwapOrder is not { } order || c.Position < 0 || c.Position >= order.Count) return;
        var list = order.ToList();
        list.RemoveAt(c.Position);
        SetSwapOrder(list);
    });

    /// <summary>Drag a chip in the order to another place (from → to positions).</summary>
    public void MoveSwapOrderChip(int from, int to)
    {
        if (_settings.SwapOrder is not { } o || from < 0 || from >= o.Count || to < 0 || to >= o.Count || from == to) return;
        var list = o.ToList();
        var n = list[from];
        list.RemoveAt(from);
        list.Insert(to, n);
        SetSwapOrder(list);
    }

    public ICommand ClearSwapOrderCommand => new RelayCommand(() => SetSwapOrder(null));

    private void SetSwapOrder(List<int>? order)
    {
        _settings.SwapOrder = order is { Count: > 0 } ? order : null;
        _settings.Save();
        _swapCursor = -1;
        OnPropertyChanged(nameof(SwapOrderChips));
        OnPropertyChanged(nameof(SwapOrderText));
    }

    private string ChipLabel(int n)
    {
        var slot = Current?.Slots.FirstOrDefault(s => s.N == n);
        var name = slot?.Name ?? WowClasses.Find(slot?.Class)?.Name;
        return name is null ? $"{n}" : $"{n} · {name}";
    }

    // ---------- running a swap ----------

    /// <summary>A hotkey (or the tray, or "--swap" from a script) asked for <paramref name="action"/>.</summary>
    public void RunSwapAction(string action)
    {
        if (!_settings.SwapHotkeys || Current is null) return;
        if (action == SwapActions.Home)
        {
            ResetSwap(quiet: false);
            return;
        }
        if (_paused)
        {
            Status.Warn("Window swap: press Start first – while stopped, WoWBoxer leaves every window alone.");
            return;
        }
        var clients = Current.Slots.Count;
        var mains = WindowSwap.MainSlots(Current);
        if (SwapActions.ClientNumber(action) is { } n)
        {
            if (n > clients)
            {
                Status.Info($"Window swap: this layout has no client {n}.");
                return;
            }
            var main = WindowSwap.MainSlotFor(mains, n - 1, clients);
            if (main < 0)
            {
                NoMainSlot(MainSlotHint);
                return;
            }
            if (WindowAt(n - 1) is null)
            {
                Status.Info($"Window swap: {ClientLabel(n - 1)}'s window is not open – nothing to move.");
                return;
            }
            _swapCursor = -1;
            SwapInto(n - 1, main);
            return;
        }

        var forward = action == SwapActions.Next;
        var scope = _settings.SwapScope;
        var party = scope == SwapCycleScope.ActiveParty ? ActivePartyIndex() : 0;
        var mainSlot = scope == SwapCycleScope.All ? mains[0] : party < mains.Length ? mains[party] : -1;
        if (mainSlot < 0)
        {
            NoMainSlot(MainSlotHint is { Length: > 0 } hint ? hint : "Window swap: this party has no main slot – click the crown on one of its windows.");
            return;
        }
        var custom = WindowSwap.CustomOrder(_settings.SwapOrder, clients);
        var order = custom is not null && party == 0 ? custom : WindowSwap.NumberOrder(clients, scope, party);
        var (client, cursor) = WindowSwap.Step(_swapPos, mainSlot, order, c => WindowAt(c) is not null, _swapCursor, forward);
        if (client < 0)
        {
            Status.Info("Window swap: no other open window in the cycle.");
            return;
        }
        _swapCursor = cursor;
        SwapInto(client, mainSlot);
    }

    private bool _noMainLogged;
    private int _crownPulse;

    /// <summary>Goes up by one each time a swap hotkey finds no main slot: the map pulses the crowns (LayoutCanvas.CrownPulse).</summary>
    public int CrownPulse { get => _crownPulse; private set => Set(ref _crownPulse, value); }

    /// <summary>
    /// A swap hotkey was pressed but there is no main slot: a short notice in the status bar and pulsing crowns on the
    /// map – no pop-up, no focus change. Written to the log once, not on every press.
    /// </summary>
    private void NoMainSlot(string message)
    {
        if (!_noMainLogged)
        {
            _noMainLogged = true;
            Status.Warn(message);
        }
        else ShowNotice(StatusLevel.Warn, message);
        CrownPulse++;
    }

    /// <summary>A status bar line that is not written to the log (repeats of a message that is logged once).</summary>
    private void ShowNotice(StatusLevel level, string message) => LastStatus = new StatusLine(Demo.Now(), level, message);

    private void SwapInto(int client, int mainSlot)
    {
        var other = WindowSwap.ClientIn(_swapPos, mainSlot);
        if (other == client)
        {
            Status.Info($"Window swap: {ClientLabel(client)} is already in the main slot.");
            return;
        }
        var from = _swapPos[client];
        SetSwap(WindowSwap.ToMain(_swapPos, client, mainSlot));
        Status.Ok($"Swap: {ClientLabel(client)} → main slot, {ClientLabel(other)} → slot {from + 1}.");
    }

    /// <summary>Every client back in its home slot ("Back to home layout", Re-arrange, a new layout).</summary>
    public void ResetSwap(bool quiet)
    {
        _swapCursor = -1;
        if (!IsSwapped) return;
        SetSwap(WindowSwap.Home(_swapPos.Length));
        if (!quiet) Status.Ok("Back to the home layout: every client is in its own slot again.");
    }

    private void SetSwap(int[] pos)
    {
        _swapPos = pos;
        if (Current is not null)
        {
            _settings.SwapPositions[Current.Name] = pos;
            _settings.Save();
        }
        RaiseSwap();
        MoveWindowsNow();
    }

    /// <summary>Puts the open windows where <see cref="Placed"/> says, at once (no waiting for the next tick), and fixes "on top".</summary>
    private void MoveWindowsNow()
    {
        if (_paused || Plan() is not { } plan) return;
        foreach (var (window, slot) in Placements())
            if (slot < plan.Slots.Count) plan.Place(window.Handle, slot);
        _keeper.Settle();   // and keep them there for the next seconds, like any placement
        ApplyOnTop();
    }

    /// <summary>The party (0/1) of the client whose window has the keyboard now; party 1 if it is not a squad window.</summary>
    private int ActivePartyIndex()
    {
        if (Current is null) return 0;
        var fg = GlobalHotkeys.ForegroundWindow();
        var item = Windows.FirstOrDefault(w => w.Window.Handle == fg);
        return item is null ? 0 : WindowSwap.PartyIndexOf(item.Slot - 1, Current.Slots.Count);
    }

    private string ClientLabel(int client)
    {
        var slot = Current?.Slots.ElementAtOrDefault(client);
        return slot?.Name is { Length: > 0 } name ? $"{client + 1} ({name})" : $"client {client + 1}";
    }

    // ---------- main slot choice (the crowns on the map) ----------

    /// <summary>The crown on window <paramref name="client"/> (as placed) was clicked: its slot becomes (or stops being) the main slot.</summary>
    public void ToggleMainSlot(int client)
    {
        if (Current is null || client < 0 || client >= Current.Slots.Count) return;
        var slot = _swapPos.Length == Current.Slots.Count ? _swapPos[client] : client;
        var mains = WindowSwap.ToggleMain(Current, slot);
        Update(Current with { SwapMainSlots = mains });
        Status.Info(mains.Contains(slot + 1) ? $"Slot {slot + 1} is the main slot now." : $"Slot {slot + 1} is no longer a main slot.");
        RaiseSwap();
    }

    // ---------- keeping things in step ----------

    /// <summary>The layout changed: keep a fitting placement (the remembered one for this layout, or home).</summary>
    private void SyncSwapToLayout()
    {
        if (Current is null)
        {
            _swapPos = [];
            _swapLayout = null;
        }
        else if (_swapLayout != Current.Name || _swapPos.Length != Current.Slots.Count)
        {
            _swapLayout = Current.Name;
            // the saved placement is used at start (so swapped windows are recognised where they sit) or when "remember"
            // is on; picking another layout later starts it at home
            var saved = _firstSync || _settings.SwapRemember ? _settings.SwapPositions.GetValueOrDefault(Current.Name) : null;
            _swapPos = WindowSwap.Valid(saved, Current.Slots.Count);
            _swapCursor = -1;
        }
        RaiseSwap();
    }

    /// <summary>
    /// After the first look at the open windows: they were recognised by where they sit (the saved placement), so the
    /// right characters got the right rows. Unless "remember" is on, the clients now go back to their home slots.
    /// </summary>
    private void AfterFirstSwapSync()
    {
        if (IsSwapped && (!_settings.SwapRemember || !_settings.SwapHotkeys)) ResetSwap(quiet: true);
    }

    private void RaiseSwap()
    {
        if (Current is null || !_settings.SwapHotkeys || !WindowSwap.NeedsChoice(Current)) _noMainLogged = false;   // log it again next time it happens
        OnPropertyChanged(nameof(Placed));
        OnPropertyChanged(nameof(IsSwapped));
        OnPropertyChanged(nameof(MainSlotMarks));
        OnPropertyChanged(nameof(MainSlotHint));
        OnPropertyChanged(nameof(SwapStatusText));
        OnPropertyChanged(nameof(SwapOrderPresets));
        OnPropertyChanged(nameof(SwapOrderSource));
        OnPropertyChanged(nameof(SwapOrderChips));
        UpdateSwapBadges();
    }

    /// <summary>"In main slot" on the squad rows, and each row's "on top" as placed now.</summary>
    private void UpdateSwapBadges()
    {
        var marks = MainSlotMarks;
        var placed = Placed;
        for (var i = 0; i < Party.Count; i++)
        {
            Party[i].InMainSlot = marks is not null && i < marks.Count && marks[i];   // always, while swap is on
            Party[i].SwapSlot = IsSwapped && i < _swapPos.Length && _swapPos[i] != i ? $"in slot {_swapPos[i] + 1}" : "";
            if (placed is not null && i < placed.Slots.Count) Party[i].OnTop = placed.Slots[i].Z == ZOrder.Top;
        }
    }

    // ---------- hotkeys ----------

    /// <summary>Sets the hotkeys from the settings (or none when swap is off) and refreshes the editor rows.</summary>
    public void ApplyHotkeys() => QueueHotkeys(ApplyHotkeysAsync);

    private Task _hotkeyWork = Task.CompletedTask;

    /// <summary>One shortcut change at a time, in order (a pause for editing must not land after the resume).</summary>
    private void QueueHotkeys(Func<Task> work) =>
        _hotkeyWork = _hotkeyWork.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None,
            SynchronizationContext.Current is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();

    /// <summary>KDE's shortcut service answers over D-Bus: the rows and the status follow once it has.</summary>
    private async Task ApplyHotkeysAsync()
    {
        try
        {
            if (Demo.On) _hotkeyFailures = new Dictionary<string, string>();
            else if (_settings.SwapHotkeys)
            {
                _hotkeys ??= new SwapHotkeyHost(RunSwapAction);
                _hotkeyFailures = await _hotkeys.ApplyAsync(EffectiveHotkeys());
                foreach (var (action, why) in _hotkeyFailures) Status.Warn($"Swap hotkey for \"{SwapActions.Describe(action)}\" is not available: {why}.");
            }
            else
            {
                if (_hotkeys is not null) await _hotkeys.ClearAsync();
                _hotkeyFailures = new Dictionary<string, string>();
            }
        }
        catch (Exception e) when (e is Tmds.DBus.Protocol.DBusExceptionBase or InvalidOperationException)
        {
            Status.Warn($"Swap hotkeys: KDE's shortcut service did not answer ({e.Message}).");
        }
        RefreshHotkeyRows();
        OnPropertyChanged(nameof(SwapStatusText));
    }

    private IReadOnlyDictionary<string, Hotkey> EffectiveHotkeys()
    {
        var keys = Hotkeys.Effective(_settings.SwapKeys).ToDictionary();
        foreach (var dup in Hotkeys.Duplicates(keys).Keys) keys.Remove(dup);   // a combination works for one action only
        return keys;
    }

    private void RefreshHotkeyRows()
    {
        var saved = Hotkeys.Effective(_settings.SwapKeys);
        var keys = Hotkeys.Effective(_pendingKeys ?? _settings.SwapKeys);
        var dups = Hotkeys.Duplicates(keys);
        if (HotkeyRows.Count != SwapActions.All.Count)
        {
            HotkeyRows.Clear();
            foreach (var action in SwapActions.All) HotkeyRows.Add(new HotkeyRow(action, SetHotkey));
        }
        foreach (var row in HotkeyRows)
        {
            var key = keys.GetValueOrDefault(row.Action);
            var notes = new List<string>();
            var changed = saved.GetValueOrDefault(row.Action) != key;
            if (changed) notes.Add($"Not applied yet (in use now: {saved.GetValueOrDefault(row.Action)?.ToString() ?? "none"}).");
            if (dups.TryGetValue(row.Action, out var first)) notes.Add($"Already used for \"{SwapActions.Describe(first)}\" – not active.");
            if (!changed && _hotkeyFailures.TryGetValue(row.Action, out var why)) notes.Add($"Not available: {why}.");
            if (key is not null) notes.AddRange(Hotkeys.Warnings(key));
            row.Update(key?.ToString() ?? "", string.Join(" ", notes), Hotkeys.Defaults.TryGetValue(row.Action, out var d) && d == key);
        }
        OnPropertyChanged(nameof(HasPendingHotkeys));
    }

    /// <summary>Hotkey edits waiting for Apply.</summary>
    public bool HasPendingHotkeys => _pendingKeys is not null
        && !Hotkeys.Effective(_pendingKeys).OrderBy(kv => kv.Key).SequenceEqual(Hotkeys.Effective(_settings.SwapKeys).OrderBy(kv => kv.Key));

    /// <summary>"Apply hotkeys": the edited hotkeys are saved and registered; until then the old ones keep working.</summary>
    public ICommand ApplyHotkeyChangesCommand => new RelayCommand(() =>
    {
        if (_pendingKeys is null) return;
        _settings.SwapKeys = _pendingKeys;
        _pendingKeys = null;
        _settings.Save();
        ApplyHotkeys();
        Status.Ok("Swap hotkeys applied: " + SwapStatusText + ".");
    });

    /// <summary>"Discard": back to the hotkeys in use.</summary>
    public ICommand DiscardHotkeyChangesCommand => new RelayCommand(() =>
    {
        _pendingKeys = null;
        RefreshHotkeyRows();
        Status.Info("Swap hotkey changes discarded.");
    });

    /// <summary>The editor changed a hotkey ("" = cleared, null = back to the default). Not in use until Apply.</summary>
    private void SetHotkey(string action, string? text)
    {
        _pendingKeys ??= new Dictionary<string, string>(_settings.SwapKeys);
        if (text is null) _pendingKeys.Remove(action);
        else _pendingKeys[action] = text;
        RefreshHotkeyRows();
    }

    public ICommand ResetAllHotkeysCommand => new RelayCommand(() =>
    {
        _pendingKeys = [];
        RefreshHotkeyRows();
    });

    /// <summary>A hotkey box is waiting for keys: let go of the hotkeys meanwhile, so pressing one reaches the box.</summary>
    public void PauseHotkeysForEditing() => QueueHotkeys(() => _hotkeys?.ClearAsync() ?? Task.CompletedTask);

    /// <summary>The hotkey box is done: register the hotkeys again.</summary>
    public void ResumeHotkeys() => ApplyHotkeys();

    /// <summary>Quitting: stop listening (the keys are cleared when the app unloads its KWin script).</summary>
    public void DisposeHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
    }
}

public sealed record ScopeChoice(SwapCycleScope Scope, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A client in the custom next/previous order (<see cref="Position"/> -1 = in the list to add from).</summary>
public sealed record OrderChip(int Position, int Number, string Label);

/// <summary>One action in the hotkey editor: its hotkey text, notes (conflicts, warnings) and Clear / Default.</summary>
public sealed class HotkeyRow(string action, Action<string, string?> set) : ObservableObject
{
    private string _text = "", _notes = "";
    private bool _isDefault;

    public string Action => action;
    public string Title => SwapActions.Describe(action);
    public string Text { get => _text; private set => Set(ref _text, value); }
    public string Notes { get => _notes; private set => Set(ref _notes, value); }
    public bool HasNotes => _notes.Length > 0;
    public bool IsDefault { get => _isDefault; private set => Set(ref _isDefault, value); }

    public ICommand ClearCommand => new RelayCommand(() => set(action, ""));
    public ICommand DefaultCommand => new RelayCommand(() => set(action, null));

    /// <summary>The capture box got a combination ("Ctrl+Alt+Shift+F5").</summary>
    public void Capture(string text) => set(action, text);

    public void Update(string text, string notes, bool isDefault)
    {
        Text = text;
        Notes = notes;
        IsDefault = isDefault;
        OnPropertyChanged(nameof(HasNotes));
    }
}
