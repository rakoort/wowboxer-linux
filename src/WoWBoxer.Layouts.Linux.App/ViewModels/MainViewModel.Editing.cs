using System.IO;
using System.Windows.Input;
using WoWBoxer.Layouts.App.Infrastructure;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.App.ViewModels;

/// <summary>
/// Custom (the sandbox, was "Free for all") and Advanced editing on the "Your screens" map: multi-selection, match size,
/// align, distribute, fill row/column, Fit 4:3, numeric X/Y/W/H for the selection, and Redo. Every operation is one
/// undo step; the real windows follow when it is done (never on every mouse move).
/// </summary>
public sealed partial class MainViewModel
{
    private readonly CustomDraftStore _drafts = new(Path.Combine(AppSettings.Folder, "drafts"));
    private CustomDraft? _draft;
    private readonly Stack<UndoStep> _redo = new();
    private readonly SlotSelection _selection = new();

    // =====================================================================================
    // Custom
    // =====================================================================================

    /// <summary>"Saved automatically as …", or for an unsaved Custom draft what that means.</summary>
    private string CustomSavedText => Current is null ? ""
        : Current.Preset == PresetKind.Custom && _draft is { Unsaved: true }
            ? $"Custom draft of “{Current.Name}” – saved on every change. Save as… keeps it under its own name."
            : $"Saved automatically as “{Current.Name}”";

    private void LoadDraft()
    {
        _draft = Current is null ? null : _drafts.Load(Current.Name);
        OnPropertyChanged(nameof(SavedText));
    }

    private void SaveDraft(CustomDraft? draft)
    {
        _draft = draft;
        if (Current is not null) _drafts.Save(Current.Name, draft);
        OnPropertyChanged(nameof(SavedText));
        RefreshCards();   // the Custom card previews the draft
    }

    /// <summary>
    /// The user moved or resized windows (map, details, operations): a preset becomes Custom from exactly that
    /// arrangement; Custom keeps its draft up to date. Name or class edits never come here.
    /// </summary>
    private void UserGeometryEdit(LayoutProfile edited)
    {
        if (Current is null) return;
        var wasPreset = Current.Preset is not (PresetKind.Custom or null);
        var (profile, draft) = CustomLayouts.Edited(Current, edited, _draft);
        Update(profile);
        SaveDraft(draft);
        if (wasPreset) Status.Info("Switched to Custom – your arrangement is kept as a draft on every change. Ctrl+Z undoes it.");
    }

    /// <summary>The Custom card: keeps what is on the map (or brings this layout's last draft back). Never fixed positions.</summary>
    private void ChooseCustom()
    {
        if (Current is null || Current.Preset == PresetKind.Custom) return;
        ResetSwap(quiet: true);
        var restoring = _draft is not null && _draft.Custom.Slots.Count == Current.Slots.Count;
        var (profile, draft) = CustomLayouts.Choose(Current, _draft);
        Update(profile);
        SaveDraft(draft);
        Status.Info(restoring ? "Custom: your last custom arrangement is back." : "Custom: the arrangement on the map is now yours to change – nothing moved.");
    }

    /// <summary>
    /// Leaving Custom with unsaved changes asks once: Save as… / Keep as draft / Discard (Esc = keep as draft – nothing
    /// is ever lost). <paramref name="switchingLayout"/>: another saved layout was picked, not another card.
    /// </summary>
    private void LeaveCustom(bool switchingLayout)
    {
        if (Current is null || _draft is not { } draft || !CustomLayouts.AsksBeforeLeaving(Current, draft)) return;
        // rendering the website screenshots never asks (nobody is there to answer): the draft is kept
        var answer = Demo.Rendering ? 1 : _dialogs.Choose("Save your custom layout?",
            $"“{Current.Name}” has a custom arrangement that is not saved under its own name yet.",
            ["Save as…", "Keep as draft", "Discard"]);
        var choice = answer switch { 0 => LeaveChoice.SaveAs, 2 => LeaveChoice.Discard, _ => LeaveChoice.KeepDraft };
        if (choice == LeaveChoice.SaveAs)
        {
            var name = _dialogs.Prompt("Save as a new layout", "Name for your custom layout:", Current.Name + " custom");
            if (string.IsNullOrWhiteSpace(name)) choice = LeaveChoice.KeepDraft;   // cancelled: nothing is lost
            else
            {
                var copy = draft.Custom with { Name = _store.UniqueName(name.Trim()), Game = null };
                Profiles.Add(new ProfileItem(new StoredProfile(_store.Save(copy), copy)));
                Status.Ok($"Saved your custom layout as “{copy.Name}”.");
            }
        }
        var (keep, left) = CustomLayouts.Leave(draft, choice, switchingLayout);
        if (keep is not null) Update(keep, pushUndo: false);
        SaveDraft(left);
        if (choice == LeaveChoice.Discard) Status.Info("Custom arrangement discarded.");
        else if (choice == LeaveChoice.KeepDraft) Status.Info("Custom arrangement kept as a draft – pick Custom to get it back.");
    }

    // =====================================================================================
    // Advanced editing
    // =====================================================================================

    /// <summary>The Advanced editing toggle on the map header (toolbar above the map, multi-selection).</summary>
    public bool AdvancedEditing
    {
        get => _settings.AdvancedEditing;
        set
        {
            if (value == _settings.AdvancedEditing) return;
            SetSetting(() => _settings.AdvancedEditing = value);
            if (!value)
            {
                ClearFill();
                SetSelection(SelectedSlotIndex >= 0 ? [SelectedSlotIndex] : []);
            }
            OnPropertyChanged(nameof(SwapOnDrop));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Dropping a window onto another swaps them: always outside Advanced editing; a toolbar switch inside (off by default).</summary>
    public bool SwapOnDrop
    {
        get => !AdvancedEditing || _settings.SwapOnDropAdvanced;
        set
        {
            if (AdvancedEditing) SetSetting(() => _settings.SwapOnDropAdvanced = value);
        }
    }

    public bool SnapEnabled { get => _settings.SnapEnabled; set => SetSetting(() => _settings.SnapEnabled = value); }
    public bool ShowGrid { get => _settings.ShowGrid; set => SetSetting(() => _settings.ShowGrid = value); }
    public bool LockAspect { get => _settings.LockAspect; set => SetSetting(() => _settings.LockAspect = value); }

    /// <summary>Pixels between windows for Fill row / Fill column (0 = edge to edge).</summary>
    public int FillGap
    {
        get => _settings.FillGap;
        set
        {
            SetSetting(() => _settings.FillGap = Math.Clamp(value, 0, 200));
            RecomputeFill();
        }
    }

    /// <summary>The selected windows (row indices), the reference first. The map shows them; the toolbar works on them.</summary>
    public IReadOnlyList<int> SelectedIndices
    {
        get => _selection.Items.ToList();
        set => SetSelection(value ?? []);
    }

    public string SelectionText => _selection.Items.Count switch
    {
        0 => "Nothing selected – click a window, Ctrl+click for more, or drag a box around them.",
        1 => $"Window {_selection.Reference + 1} selected · align goes to its monitor",
        var n => $"{n} windows · reference: window {_selection.Reference + 1} (gold outline)",
    };

    /// <summary>The map changed the selection (click, Ctrl/Shift+click, rubber band, Ctrl+A, Esc).</summary>
    public void SetSelection(IReadOnlyList<int> indices)
    {
        var count = Current?.Slots.Count ?? 0;
        var clean = indices.Where(i => i >= 0 && i < count).ToList();
        if (clean.SequenceEqual(_selection.Items)) return;
        _selection.Set(clean);
        if (_selection.Reference >= 0 && _selection.Reference != SelectedSlotIndex) SelectedSlotIndex = _selection.Reference;
        RaiseSelection();
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedIndices));
        OnPropertyChanged(nameof(SelectionText));
        foreach (var n in new[] { nameof(SelX), nameof(SelY), nameof(SelW), nameof(SelH) }) OnPropertyChanged(n);
        if (_fillTarget != FillTarget.None) RecomputeFill();   // the preview follows the selection
        CommandManager.InvalidateRequerySuggested();
    }

    private bool CanMulti(int min) => AdvancedEditing && Current is not null && _selection.Items.Count >= min;

    public ICommand SelectAllCommand => new RelayCommand(() => { _selection.All(Current?.Slots.Count ?? 0); RaiseSelection(); }, () => AdvancedEditing);
    public ICommand ClearSelectionCommand => new RelayCommand(() => { _selection.Clear(); RaiseSelection(); }, () => AdvancedEditing && _selection.Items.Count > 0);
    public ICommand SameWidthCommand => new RelayCommand(() => Arrange("Same width", r => SlotArrange.MatchSize(r, true, false), sameFit: true), () => CanMulti(2));
    public ICommand SameHeightCommand => new RelayCommand(() => Arrange("Same height", r => SlotArrange.MatchSize(r, false, true), sameFit: true), () => CanMulti(2));
    public ICommand SameSizeCommand => new RelayCommand(() => Arrange("Same size", r => SlotArrange.MatchSize(r, true, true), sameFit: true), () => CanMulti(2));
    public ICommand AlignCommand => new RelayCommand(p =>
    {
        if (Enum.TryParse<AlignEdge>(p as string, out var edge)) Arrange($"Align {edge.ToString().ToLowerInvariant()}", r => SlotArrange.Align(r, edge, AreaOf(r[0])));
    }, _ => CanMulti(1));
    public ICommand DistributeHorizontallyCommand => new RelayCommand(() => Arrange("Distribute horizontally", r => SlotArrange.Distribute(r, true)), () => CanMulti(3));
    public ICommand DistributeVerticallyCommand => new RelayCommand(() => Arrange("Distribute vertically", r => SlotArrange.Distribute(r, false)), () => CanMulti(3));
    public ICommand FillRowCommand => new RelayCommand(() => Arrange("Fill row", r => SlotArrange.Fill(r, AreaOf(r[0]), row: true, FillGap), sameFit: true), () => CanMulti(1));
    public ICommand FillColumnCommand => new RelayCommand(() => Arrange("Fill column", r => SlotArrange.Fill(r, AreaOf(r[0]), row: false, FillGap), sameFit: true), () => CanMulti(1));
    public ICommand Fit43Command => new RelayCommand(() => Arrange("Fit 4:3", r => SlotArrange.FitAspect(r, 4.0 / 3, AreaOf)), () => CanMulti(1));
    public ICommand RedoCommand => new RelayCommand(Redo, () => _redo.Count > 0);

    /// <summary>The monitor's usable area (work area, or the whole screen with "Cover the taskbar area") under a rectangle.</summary>
    private PixelRect AreaOf(PixelRect r)
    {
        var screens = ChosenScreens.Count > 0 ? ChosenScreens : Monitors;
        return ProfileCapture.MonitorOf(r, screens).Area(Current?.Options.UseTaskbarArea ?? false);
    }

    /// <summary>The selected windows as drawn (before the aspect fit), the reference first.</summary>
    private List<PixelRect> SelectedRects()
    {
        if (Placed is not { } placed || Monitors.Count == 0) return [];
        return _selection.Items.Where(i => i < placed.Slots.Count).Select(i =>
        {
            var slot = placed.Slots[i];
            var monitor = Core.Monitors.Find(Monitors, slot.Monitor) ?? Core.Monitors.Primary(Monitors);
            return SlotEditing.Raw(slot, monitor, placed.Options);
        }).ToList();
    }

    /// <summary>
    /// Runs one operation on the selection: one undo step, the windows follow at once. <paramref name="sameFit"/>: the
    /// windows also take the reference's fit ("keep size" or shorten to the narrowest shape), so equal sizes really look
    /// equal on screen.
    /// </summary>
    private void Arrange(string what, Func<IReadOnlyList<PixelRect>, IReadOnlyList<PixelRect>> op, bool sameFit = false)
    {
        var rects = SelectedRects();
        if (rects.Count == 0) return;
        var result = op(rects);
        var fitChanges = sameFit && Placed is { } p && _selection.Items.Any(i => p.Slots[i].Fit != p.Slots[_selection.Reference].Fit || p.Slots[i].Anchor != p.Slots[_selection.Reference].Anchor);
        if (result.SequenceEqual(rects) && !fitChanges)
        {
            Status.Info($"{what}: already like that.");
            return;
        }
        ApplyRects(_selection.Items.Zip(result).ToList(), sameFit ? _selection.Reference : -1);
        Status.Ok($"{what}: {rects.Count} window{(rects.Count == 1 ? "" : "s")}. Ctrl+Z undoes it.");
    }

    /// <summary>New pixel places for some windows (row index → rectangle), stored as one edit.</summary>
    /// <param name="fitFrom">A window whose fit and anchor the others take (-1 = each keeps its own).</param>
    public void ApplyRects(IReadOnlyList<(int Index, PixelRect Rect)> changes, int fitFrom = -1)
    {
        if (Current is null || Placed is not { } placed) return;
        var screens = ChosenScreens.Count > 0 ? ChosenScreens : Monitors;
        var model = fitFrom >= 0 && fitFrom < placed.Slots.Count ? placed.Slots[fitFrom] : null;
        OnSlotsEdited(changes.Select(c =>
        {
            var slot = model is null ? placed.Slots[c.Index] : placed.Slots[c.Index] with { Fit = model.Fit, Anchor = model.Anchor };
            return (c.Index, SlotEditing.WithRect(slot, c.Rect, screens, placed.Options));
        }).ToList());
    }

    /// <summary>Several slots edited as the user sees them (a group drag, an operation): one undo step.</summary>
    public void OnSlotsEdited(IReadOnlyList<(int Index, LayoutSlot Slot)> edits)
    {
        if (Current is null || edits.Count == 0) return;
        var home = Current;
        foreach (var (i, slot) in edits)
            if (i >= 0 && i < home.Slots.Count) home = WindowSwap.Unapply(home, _swapPos, i, slot);
        if (CustomLayouts.SameGeometry(Current, home)) Update(home);
        else UserGeometryEdit(home);
        RaiseSlotProperties();
        RaiseSelection();
    }

    // ---------- numeric X / Y / W / H for the selection (blank where they differ) ----------

    private (int? X, int? Y, int? W, int? H) SelCommon => SlotArrange.Common(SelectedRects());

    public string SelX { get => SelCommon.X?.ToString() ?? ""; set => SetSel(value, (r, v) => r with { X = v }); }
    public string SelY { get => SelCommon.Y?.ToString() ?? ""; set => SetSel(value, (r, v) => r with { Y = v }); }
    public string SelW { get => SelCommon.W?.ToString() ?? ""; set => SetSel(value, (r, v) => r with { Width = Math.Max(v, SlotEditing.MinSlotSize) }); }
    public string SelH { get => SelCommon.H?.ToString() ?? ""; set => SetSel(value, (r, v) => r with { Height = Math.Max(v, SlotEditing.MinSlotSize) }); }

    private void SetSel(string text, Func<PixelRect, int, PixelRect> change)
    {
        if (!int.TryParse(text?.Trim(), out var v)) return;   // blank (values differ) or not a number: nothing changes
        var rects = SelectedRects();
        if (rects.Count == 0) return;
        ApplyRects(_selection.Items.Zip(rects.Select(r => change(r, v))).ToList());
    }

    // ---------- redo ----------

    private void Redo()
    {
        if (_redo.Count == 0 || Current is null) return;
        var step = _redo.Pop();
        _undo.Push(new UndoStep(Current, step.WindowSlots is null ? null : new Dictionary<nint, int>(_keeper.Slots), step.Accounts is null ? null : _settings.Accounts.ToList()));
        if (step.WindowSlots is { } map) _keeper.Restore(map);
        if (step.Accounts is { } accounts)
        {
            _settings.Accounts = accounts.ToList();
            _settings.Save();
        }
        Update(step.Profile, pushUndo: false);
        if (step.WindowSlots is not null)
        {
            RefreshWindows();
            RefreshParty();
            ApplyOnTop();
            ScheduleLive();
        }
        RaiseSlotProperties();
        CommandManager.InvalidateRequerySuggested();
    }
}
