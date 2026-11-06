using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;
using WoWBoxer.Layouts.App.Infrastructure;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.App.ViewModels;

public enum AppTab { Setup, Guide, Settings, Help, About }

/// <summary>A saved layout in the "Saved layouts" list.</summary>
public sealed class ProfileItem(StoredProfile stored) : ObservableObject
{
    public string Path { get; set; } = stored.Path;
    public LayoutProfile Profile { get; set; } = stored.Profile;
    public string Name => Profile.Name;
    public string Summary => $"{Profile.Clients} clients · {(Profile.Preset is { } p ? Presets.DisplayName(p) : "custom")}"
        + (Profile.Screens is { Count: > 1 } s ? $" · {s.Count} monitors" : "");

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Summary));
    }

    public override string ToString() => Name;
}

/// <summary>One of the layout cards on the Setup tab, with a live mini preview for the current clients and monitors.</summary>
public sealed class LayoutCard(PresetKind kind, string title, string blurb) : ObservableObject
{
    private LayoutProfile? _preview;
    private bool _isSelected;
    private IReadOnlyList<MonitorInfo> _monitors = [], _screens = [];

    public PresetKind Kind { get; } = kind;
    public string Title { get; } = title;
    public string Blurb { get; } = blurb;
    /// <summary>A click on the card: use this layout.</summary>
    public System.Windows.Input.ICommand? Apply { get; init; }
    public LayoutProfile? Preview { get => _preview; set => Set(ref _preview, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    /// <summary>For the preview: every monitor, and the chosen ones it is drawn on.</summary>
    public IReadOnlyList<MonitorInfo> Monitors { get => _monitors; set => Set(ref _monitors, value); }
    public IReadOnlyList<MonitorInfo> Screens { get => _screens; set => Set(ref _screens, value); }

    public override string ToString() => Title;
}

/// <summary>A monitor choice: "2 monitors" with which ones ("Primary + right · 2560×1440 + 1920×1080").</summary>
public sealed record MonitorChoice(IReadOnlyList<MonitorInfo> Screens, string Title, string Detail)
{
    public override string ToString() => Title;
}

/// <summary>A slot's window: none yet (Awaiting), being placed (first 30 s), in its slot (Connected), or moved out of it.</summary>
public enum PartyState { Awaiting, Placing, Connected, Moved }

/// <summary>
/// One slot of the squad ("Your squad" panel on Setup): the character (name, class – set by hand, never guessed), the WoW account
/// it plays on (picked from the WTF folders), the window currently in that slot and its status.
/// </summary>
public sealed class PartyMember : ObservableObject
{
    private PartyState _state;
    private string _account = "";
    private string _name = "";
    private WowClass _class = WowClasses.Automatic;
    private string _window = "No window yet";
    private bool _dropTarget;

    public int Number { get; init; }
    /// <summary>Name for UI Automation and screen readers: "Window 2".</summary>
    public string AutomationName => $"Window {Number}";
    public int Index => Number - 1;
    public string Role { get; init; } = "";
    /// <summary>"Party 1" / "Party 2" when there are more than 5 clients (rows are grouped by it); "" otherwise.</summary>
    public string PartyName { get; init; } = "";
    /// <summary>The group header drawn above this row: <see cref="PartyName"/> on the first row of each party, else "" (Avalonia's list has no grouping).</summary>
    public string PartyHeader { get; init; } = "";
    /// <summary>1, 2 … within its party (0 for a main).</summary>
    public int FollowerNumber { get; init; }
    public IReadOnlyList<WowClass> Classes { get; } = [WowClasses.Automatic, .. WowClasses.All];
    public IReadOnlyList<string> Accounts { get; init; } = [];
    /// <summary>Last characters per WoW account (from WTF folder times): a hint, never a fact.</summary>
    public IReadOnlyDictionary<string, WtfCharacter?> LastPlayed { get; init; } = new Dictionary<string, WtfCharacter?>();
    public Action<int, string>? NameChanged { get; init; }
    public Action<int, WowClass>? ClassChanged { get; init; }
    public Action<int, string>? AccountChanged { get; init; }
    public ICommand? Identify { get; init; }
    /// <summary>Opens or closes this row's details (class, WoW account, always on top, size).</summary>
    public ICommand? ToggleDetails { get; init; }

    private bool _expanded;

    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
    public ICommand UseHint => new RelayCommand(() => { if (Hint is { } h) Name = h.Name; }, () => Hint is not null && Hint.Name != Name);

    /// <summary>Character name typed by the user ("" = not set).</summary>
    public string Name
    {
        get => _name;
        set
        {
            value = value.Trim();
            if (!Set(ref _name, value)) return;
            OnPropertyChanged(nameof(Title));
            NameChanged?.Invoke(Index, value);
        }
    }

    public WowClass Class
    {
        get => _class;
        set
        {
            value ??= WowClasses.Automatic;
            if (!Set(ref _class, value)) return;
            OnPropertyChanged(nameof(ClassBrush));
            OnPropertyChanged(nameof(Title));
            ClassChanged?.Invoke(Index, value);
        }
    }

    public IBrush ClassBrush => _class.Brush;
    public string Title => _name.Length > 0 ? _name : Role == "Main" ? "Main" : $"Follower {FollowerNumber}";

    /// <summary>The WoW account folder this slot plays on (local setting, never in shared layouts).</summary>
    public string Account
    {
        get => _account;
        set
        {
            value ??= "";
            if (!Set(ref _account, value)) return;
            OnPropertyChanged(nameof(Hint));
            OnPropertyChanged(nameof(HintText));
            AccountChanged?.Invoke(Index, value);
        }
    }

    public WtfCharacter? Hint => _account.Length > 0 && LastPlayed.TryGetValue(_account, out var c) ? c : null;
    public string HintText => Hint is { } h ? $"Last played here: {h.Display}" : _account.Length > 0 ? "No characters found for this account" : "Pick the WoW account for hints";

    private bool _fromAddOn;

    /// <summary>Name and class came from the WoWBoxer AddOn (this account's SavedVariables) and are still as it wrote them.</summary>
    public bool FromAddOn { get => _fromAddOn; set => Set(ref _fromAddOn, value); }

    public const string AddOnText = "from the WoWBoxer AddOn";

    public string Window { get => _window; set => Set(ref _window, value); }

    private bool _onTop;
    private bool? _topNow;
    private bool _inMainSlot;
    private string _swapSlot = "";
    private string _combatRole = "";

    /// <summary>Window swap: this client sits in a main slot now (it was swapped there).</summary>
    public bool InMainSlot { get => _inMainSlot; set => Set(ref _inMainSlot, value); }

    /// <summary>Window swap: "in slot 1" while the client is out of its home slot, else "".</summary>
    public string SwapSlot { get => _swapSlot; set => Set(ref _swapSlot, value); }

    /// <summary>Choices for the optional role: "" (none), Tank, Healer, Damage.</summary>
    public IReadOnlyList<string> CombatRoles { get; } = ["", "Tank", "Healer", "Damage"];
    public Action<int, string>? CombatRoleChanged { get; init; }

    /// <summary>Optional role in the group (Tank / Healer / Damage), used for ready-made window-swap orders.</summary>
    public string CombatRole
    {
        get => _combatRole;
        set
        {
            if (Set(ref _combatRole, value ?? "")) CombatRoleChanged?.Invoke(Index, _combatRole);
        }
    }

    /// <summary>Sets the role without reporting it back (loading the row).</summary>
    public void LoadCombatRole(string role) => Set(ref _combatRole, role, nameof(CombatRole));

    /// <summary>The slot wants "always on top".</summary>
    public bool OnTop
    {
        get => _onTop;
        set
        {
            if (Set(ref _onTop, value)) RaisePin();
        }
    }

    /// <summary>Whether KWin really has this slot's window on top (null = no window open).</summary>
    public bool? TopNow
    {
        get => _topNow;
        set
        {
            if (Set(ref _topNow, value)) RaisePin();
        }
    }

    /// <summary>Pin shown on the row: the slot is "on top", or its window is (when it should not be).</summary>
    public bool ShowPin => _onTop || _topNow == true;

    /// <summary>Gold = on top as wanted; red = differs from the layout (the agent puts it right within a moment).</summary>
    public IBrush? PinBrush => Avalonia.Application.Current?.FindResource(_topNow is null || _topNow == _onTop ? "GoldHiBrush" : "NoBrush") as IBrush;

    public string PinText => !_onTop ? "Not on top, but KWin has it on top" : _topNow switch
    {
        true => "Always on top – the window is on top",
        false => "Always on top – the window is not on top yet",
        _ => "Always on top (no window open)",
    };

    private void RaisePin()
    {
        OnPropertyChanged(nameof(ShowPin));
        OnPropertyChanged(nameof(PinBrush));
        OnPropertyChanged(nameof(PinText));
    }

    /// <summary>True while a row is dragged over this one (drop highlight).</summary>
    public bool IsDropTarget { get => _dropTarget; set => Set(ref _dropTarget, value); }

    public PartyState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StateTip));
        }
    }

    public string StateText => State switch
    {
        PartyState.Connected => "Connected",
        PartyState.Placing => "Placing…",
        PartyState.Moved => "Moved",
        _ => "Awaiting…",
    };

    public string StateTip => State switch
    {
        PartyState.Connected => "The window is open and in its slot",
        PartyState.Placing => "A new window: it is put into its slot while WoW loads",
        PartyState.Moved => "The window left its slot – Re-arrange puts it back",
        _ => "No WoW window in this slot yet – it is placed as soon as one opens",
    };

    /// <summary>Sets values from the layout without calling back (no save loop).</summary>
    public void Load(string name, WowClass cls, string account)
    {
        _name = name;
        _class = cls;
        _account = account;
        foreach (var p in new[] { nameof(Name), nameof(Class), nameof(ClassBrush), nameof(Title), nameof(Account), nameof(Hint), nameof(HintText) }) OnPropertyChanged(p);
    }
}

/// <summary>An open WoW window of the squad and the slot it sits in (1-based; slots may have no window).</summary>
public sealed class GameWindowItem(WindowInfo window) : ObservableObject
{
    private int _slot;
    public WindowInfo Window { get; } = window;
    public int Slot { get => _slot; set => Set(ref _slot, value); }
    public string Process => $"{Window.ProcessName}.exe";
    public string Detail => $"pid {Window.Pid} · {WindowFinder.VisibleRectOf(Window.Handle)}";
}

public sealed record AspectChoice(double Value, string Label)
{
    public override string ToString() => Label;
}
