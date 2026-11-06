using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using WoWBoxer.Layouts.App.Infrastructure;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.App.ViewModels;

/// <summary>A window in the fill preview: where it would go and what its action bar would show there.</summary>
public sealed record FillPreviewTile(int Index, PixelRect Rect, string Verdict, Color Color);

public enum FillTarget { None, EmptySpace, Span, Area }

/// <summary>
/// Advanced editing – fill a custom area with the selection: the largest free space ("Fill empty space"), the selection's
/// own span, or a rectangle drawn on the map or typed in px or %. Row / Column / Grid (automatic or set rows × columns),
/// gap, order by position or by client number. Every fill is a live preview first (the area highlighted, each window's
/// place and action-bar verdict); Apply makes it one undo step, Cancel (or Esc) leaves everything as it was.
/// </summary>
public sealed partial class MainViewModel
{
    private FillTarget _fillTarget;
    private PixelRect? _fillArea, _previewArea;
    private IReadOnlyList<FillPreviewTile>? _previewTiles;
    private bool _pickingArea;

    public IReadOnlyList<TileArrangement> FillArrangements { get; } = [TileArrangement.Row, TileArrangement.Column, TileArrangement.Grid];

    public TileArrangement FillArrangement
    {
        get => _settings.FillArrangement;
        set
        {
            SetSetting(() => _settings.FillArrangement = value);
            OnPropertyChanged(nameof(IsGridFill));
            RecomputeFill();
        }
    }

    public bool IsGridFill => FillArrangement == TileArrangement.Grid;

    /// <summary>Grid rows / columns ("" = automatic).</summary>
    public string FillRows { get => _settings.FillRows > 0 ? _settings.FillRows.ToString(CultureInfo.InvariantCulture) : ""; set => SetGrid(value, rows: true); }
    public string FillColumns { get => _settings.FillColumns > 0 ? _settings.FillColumns.ToString(CultureInfo.InvariantCulture) : ""; set => SetGrid(value, rows: false); }

    private void SetGrid(string text, bool rows)
    {
        var v = int.TryParse(text?.Trim(), out var n) ? Math.Clamp(n, 0, Presets.MaxClients) : 0;
        SetSetting(() => { if (rows) _settings.FillRows = v; else _settings.FillColumns = v; }, rows ? nameof(FillRows) : nameof(FillColumns));
        RecomputeFill();
    }

    /// <summary>Order by client number instead of the current left-to-right / top-to-bottom order.</summary>
    public bool FillByNumber
    {
        get => _settings.FillByNumber;
        set
        {
            SetSetting(() => _settings.FillByNumber = value);
            RecomputeFill();
        }
    }

    private TileOptions FillOptions => new(FillArrangement, _settings.FillRows, _settings.FillColumns, FillGap, FillByNumber);

    // ---------- the preview (shown on the map) ----------

    public IReadOnlyList<FillPreviewTile>? PreviewTiles => _previewTiles;
    public PixelRect? PreviewArea => _previewArea;
    public bool HasFillPreview => _previewTiles is not null;

    /// <summary>The preview bar under the toolbar: while a preview shows or the map waits for an area.</summary>
    public bool ShowFillBar => HasFillPreview || PickingArea;

    /// <summary>The map is waiting for the user to drag the area for "Fill area…".</summary>
    public bool PickingArea
    {
        get => _pickingArea;
        private set
        {
            if (!Set(ref _pickingArea, value)) return;
            OnPropertyChanged(nameof(FillPreviewText));
            OnPropertyChanged(nameof(ShowFillBar));
        }
    }

    public string FillPreviewText
    {
        get
        {
            if (PickingArea) return "Fill area: drag a rectangle on the map (over windows too), or type it below. Esc cancels.";
            if (_previewTiles is null || _previewArea is not { } a) return "";
            var (rows, cols) = AreaFill.GridFor(_previewTiles.Count, a, FillOptions);
            var full = _previewTiles.Count(t => t.Verdict == "Full bar");
            var what = _fillTarget switch { FillTarget.EmptySpace => "empty space", FillTarget.Span => "selection span", _ => "area" };
            return $"Preview – {what} {a.Width}×{a.Height}: {_previewTiles.Count} window{(_previewTiles.Count == 1 ? "" : "s")} in {rows}×{cols}, "
                + $"whole action bar in {full} of {_previewTiles.Count}. Apply or Cancel (Esc).";
        }
    }

    public ICommand FillEmptySpaceCommand => new RelayCommand(() => StartFill(FillTarget.EmptySpace), () => CanMulti(1));
    public ICommand FillSpanCommand => new RelayCommand(() => StartFill(FillTarget.Span), () => CanMulti(2));
    public ICommand FillAreaCommand => new RelayCommand(() =>
    {
        ClearFill();
        PickingArea = true;
        _fillTarget = FillTarget.Area;
        RaiseFill();
    }, () => CanMulti(1));
    public ICommand ApplyFillCommand => new RelayCommand(ApplyFill, () => HasFillPreview);
    public ICommand CancelFillCommand => new RelayCommand(() =>
    {
        if (!HasFillPreview && !PickingArea) return;
        ClearFill();
        Status.Info("Fill cancelled – nothing changed.");
    });

    private void StartFill(FillTarget target)
    {
        PickingArea = false;
        _fillTarget = target;
        _fillArea = null;
        RecomputeFill();
        if (_previewTiles is null)
            Status.Warn(target == FillTarget.EmptySpace ? "Fill empty space: there is no free space left on these monitors." : "Nothing to fill.");
    }

    /// <summary>The map: the user dragged the area for "Fill area…".</summary>
    public void OnAreaPicked(PixelRect area)
    {
        if (!PickingArea) return;
        PickingArea = false;
        if (area.Width < 40 || area.Height < 40)
        {
            Status.Info("Fill area: drag a bigger rectangle.");
            PickingArea = true;
            return;
        }
        _fillArea = area;
        _fillTarget = FillTarget.Area;
        RecomputeFill();
    }

    /// <summary>Works the preview out again (another arrangement, gap, order, area or selection).</summary>
    private void RecomputeFill()
    {
        if (_fillTarget == FillTarget.None || PickingArea && _fillArea is null)
        {
            RaiseFill();
            return;
        }
        var rects = SelectedRects();
        _previewArea = rects.Count == 0 ? null : _fillTarget switch
        {
            FillTarget.EmptySpace => EmptySpace(),
            FillTarget.Span => rects.Count >= 2 ? AreaFill.Span(rects) : null,
            _ => _fillArea,
        };
        if (_previewArea is not { } area)
        {
            _previewTiles = null;
            RaiseFill();
            return;
        }
        var tiles = AreaFill.Tile(rects, area, FillOptions);
        _previewTiles = _selection.Items.Zip(tiles).Select(p => Tile(p.First, p.Second)).ToList();
        RaiseFill();
    }

    /// <summary>The free space (not under an unselected window) the selection is mostly in, on the chosen monitors.</summary>
    private PixelRect? EmptySpace()
    {
        if (Placed is not { } placed) return null;
        var screens = ChosenScreens.Count > 0 ? ChosenScreens : Monitors;
        var others = Enumerable.Range(0, placed.Slots.Count).Where(i => !_selection.Contains(i)).Select(RawOf).ToList();
        var areas = screens.Select(m => (m.Area(placed.Options.UseTaskbarArea), (IReadOnlyList<PixelRect>)others)).ToList();
        return AreaFill.EmptySpaceFor(SelectedRects(), areas);
    }

    private PixelRect RawOf(int i)
    {
        var slot = Placed!.Slots[i];
        var monitor = Core.Monitors.Find(Monitors, slot.Monitor) ?? Core.Monitors.Primary(Monitors);
        return SlotEditing.Raw(slot, monitor, Placed.Options);
    }

    /// <summary>One preview window, with the action-bar verdict it would get there (the reference's fit, today's UI Scale).</summary>
    private FillPreviewTile Tile(int index, PixelRect rect)
    {
        var placed = Placed!;
        var model = placed.Slots[_selection.Reference];
        var screens = ChosenScreens.Count > 0 ? ChosenScreens : Monitors;
        var slot = SlotEditing.WithRect(placed.Slots[index] with { Fit = model.Fit, Anchor = model.Anchor }, rect, screens, placed.Options);
        var monitor = Core.Monitors.Find(Monitors, slot.Monitor) ?? Core.Monitors.Primary(Monitors);
        var frame = LayoutEngine.FrameSize(placed.Options, monitor.Dpi);
        var fitted = LayoutEngine.Fit(SlotEditing.Raw(slot, monitor, placed.Options), slot.Fit ?? placed.Options.Fit, slot.Anchor, placed.Options.MinAspect, frame);
        var (w, h) = LayoutEngine.GameArea(fitted, frame);
        var aspect = h == 0 ? 0 : UiScale.EffectiveAspect((double)w / h, UiScaleNow);
        return new FillPreviewTile(index, rect, ActionBar.Short(aspect), ActionBar.ColorFor(aspect));
    }

    private void ApplyFill()
    {
        if (_previewTiles is not { } tiles || _previewArea is not { } area) return;
        var what = _fillTarget switch { FillTarget.EmptySpace => "Fill empty space", FillTarget.Span => "Fill selection span", _ => "Fill area" };
        var reference = _selection.Reference;
        ClearFill();
        ApplyRects(tiles.Select(t => (t.Index, t.Rect)).ToList(), reference);
        Status.Ok($"{what}: {tiles.Count} window{(tiles.Count == 1 ? "" : "s")} in {area.Width}×{area.Height}. Ctrl+Z undoes it.");
    }

    private void ClearFill()
    {
        _fillTarget = FillTarget.None;
        _fillArea = null;
        _previewArea = null;
        _previewTiles = null;
        PickingArea = false;
        RaiseFill();
    }

    private void RaiseFill()
    {
        OnPropertyChanged(nameof(PreviewTiles));
        OnPropertyChanged(nameof(PreviewArea));
        OnPropertyChanged(nameof(HasFillPreview));
        OnPropertyChanged(nameof(ShowFillBar));
        OnPropertyChanged(nameof(FillPreviewText));
        foreach (var n in new[] { nameof(AreaX), nameof(AreaY), nameof(AreaW), nameof(AreaH) }) OnPropertyChanged(n);
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------- typing the area (px, or % of the monitor's usable area) ----------

    public bool AreaInPercent
    {
        get => _settings.FillAreaPercent;
        set
        {
            SetSetting(() => _settings.FillAreaPercent = value);
            foreach (var n in new[] { nameof(AreaX), nameof(AreaY), nameof(AreaW), nameof(AreaH) }) OnPropertyChanged(n);
        }
    }

    private static double Pct(int value, int total) => total == 0 ? 0 : value * 100.0 / total;

    /// <summary>The monitor area typed percentages refer to: the one under the area, or the reference window's.</summary>
    private PixelRect PercentBase()
    {
        var at = _fillArea ?? _previewArea ?? (SelectedRects() is { Count: > 0 } r ? r[0] : (PixelRect?)null);
        return at is { } a ? AreaOf(a) : Monitors.Count > 0 ? Core.Monitors.Primary(Monitors).WorkArea : default;
    }

    private string AreaValue(Func<PixelRect, int> px, Func<PixelRect, PixelRect, double> pct)
    {
        var a = _fillArea ?? (_fillTarget == FillTarget.Area ? null : _previewArea);
        if (a is not { } r) return "";
        return AreaInPercent ? Math.Round(pct(r, PercentBase()), 1).ToString(CultureInfo.InvariantCulture) : px(r).ToString(CultureInfo.InvariantCulture);
    }

    public string AreaX { get => AreaValue(r => r.X, (r, b) => Pct(r.X - b.X, b.Width)); set => TypeArea(value, 0); }
    public string AreaY { get => AreaValue(r => r.Y, (r, b) => Pct(r.Y - b.Y, b.Height)); set => TypeArea(value, 1); }
    public string AreaW { get => AreaValue(r => r.Width, (r, b) => Pct(r.Width, b.Width)); set => TypeArea(value, 2); }
    public string AreaH { get => AreaValue(r => r.Height, (r, b) => Pct(r.Height, b.Height)); set => TypeArea(value, 3); }

    /// <summary>A typed value for the area: starts "Fill area" from the current preview area (or the reference's monitor).</summary>
    private void TypeArea(string text, int field)
    {
        if (!double.TryParse(text?.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
        var b = PercentBase();
        var start = _fillArea ?? _previewArea ?? b;
        double x, y, w, h;
        if (AreaInPercent)
        {
            (x, y, w, h) = (Pct(start.X - b.X, b.Width), Pct(start.Y - b.Y, b.Height), Pct(start.Width, b.Width), Pct(start.Height, b.Height));
            switch (field) { case 0: x = v; break; case 1: y = v; break; case 2: w = v; break; default: h = v; break; }
            _fillArea = AreaFill.FromPercent(x, y, w, h, b);
        }
        else
        {
            var px = (int)Math.Round(v);
            _fillArea = field switch
            {
                0 => start with { X = px },
                1 => start with { Y = px },
                2 => start with { Width = Math.Max(40, px) },
                _ => start with { Height = Math.Max(40, px) },
            };
        }
        PickingArea = false;
        _fillTarget = FillTarget.Area;
        RecomputeFill();
    }
}
