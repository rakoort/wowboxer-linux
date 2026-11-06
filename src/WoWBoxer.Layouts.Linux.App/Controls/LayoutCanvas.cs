using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using WoWBoxer.Layouts.Core;
using PixelRect = WoWBoxer.Layouts.Core.PixelRect;

namespace WoWBoxer.Layouts.App.Controls;

public sealed class SlotEditedEventArgs(int index, LayoutSlot slot) : EventArgs
{
    public int Index { get; } = index;
    public LayoutSlot Slot { get; } = slot;
}

public sealed class SlotsSwappedEventArgs(int a, int b) : EventArgs
{
    public int A { get; } = a;
    public int B { get; } = b;
}

/// <summary>
/// Draws the chosen monitors to scale, in their real arrangement and numbered 1, 2, 3 … – exactly what will happen on
/// screen, taskbar included – with the layout's windows on top. Windows glide to new positions when the layout, the
/// monitors or the client count change. In edit mode the user moves (drag, also onto another monitor), resizes
/// (handles), snaps (monitor and window edges; hold Alt to place freely) and nudges (arrow keys) windows.
/// <see cref="Compact"/> draws a small, non-interactive preview for the layout dropdown.
/// </summary>
public sealed class LayoutCanvas : Control
{
    public static readonly StyledProperty<LayoutProfile?> ProfileProperty =
        AvaloniaProperty.Register<LayoutCanvas, LayoutProfile?>(nameof(Profile));
    public static readonly StyledProperty<IReadOnlyList<MonitorInfo>?> MonitorsProperty =
        AvaloniaProperty.Register<LayoutCanvas, IReadOnlyList<MonitorInfo>?>(nameof(Monitors));
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<LayoutCanvas, int>(nameof(SelectedIndex), -1, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<LayoutCanvas, bool>(nameof(Compact));
    public static readonly StyledProperty<IReadOnlyList<MonitorInfo>?> ScreensProperty =
        AvaloniaProperty.Register<LayoutCanvas, IReadOnlyList<MonitorInfo>?>(nameof(Screens));
    public static readonly StyledProperty<IReadOnlyDictionary<int, ViewModels.LiveSlot>?> LiveSlotsProperty =
        AvaloniaProperty.Register<LayoutCanvas, IReadOnlyDictionary<int, ViewModels.LiveSlot>?>(nameof(LiveSlots));
    public static readonly StyledProperty<double> UiScaleProperty =
        AvaloniaProperty.Register<LayoutCanvas, double>(nameof(UiScale), 1.0);
    public static readonly StyledProperty<IReadOnlyList<ViewModels.PartyState>?> SlotStatesProperty =
        AvaloniaProperty.Register<LayoutCanvas, IReadOnlyList<ViewModels.PartyState>?>(nameof(SlotStates));
    public static readonly StyledProperty<IReadOnlyList<bool>?> MainSlotMarksProperty =
        AvaloniaProperty.Register<LayoutCanvas, IReadOnlyList<bool>?>(nameof(MainSlotMarks));
    public static readonly StyledProperty<bool> AdvancedEditingProperty =
        AvaloniaProperty.Register<LayoutCanvas, bool>(nameof(AdvancedEditing));
    public static readonly StyledProperty<bool> SwapOnDropProperty =
        AvaloniaProperty.Register<LayoutCanvas, bool>(nameof(SwapOnDrop), true);
    public static readonly StyledProperty<bool> SnapEnabledProperty =
        AvaloniaProperty.Register<LayoutCanvas, bool>(nameof(SnapEnabled), true);
    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<LayoutCanvas, bool>(nameof(ShowGrid));
    public static readonly StyledProperty<bool> LockAspectProperty =
        AvaloniaProperty.Register<LayoutCanvas, bool>(nameof(LockAspect));
    public static readonly StyledProperty<IReadOnlyList<int>?> SelectedIndicesProperty =
        AvaloniaProperty.Register<LayoutCanvas, IReadOnlyList<int>?>(nameof(SelectedIndices));
    public static readonly StyledProperty<IReadOnlyList<ViewModels.FillPreviewTile>?> PreviewTilesProperty =
        AvaloniaProperty.Register<LayoutCanvas, IReadOnlyList<ViewModels.FillPreviewTile>?>(nameof(PreviewTiles));
    public static readonly StyledProperty<PixelRect?> PreviewAreaProperty =
        AvaloniaProperty.Register<LayoutCanvas, PixelRect?>(nameof(PreviewArea));
    public static readonly StyledProperty<bool> PickingAreaProperty =
        AvaloniaProperty.Register<LayoutCanvas, bool>(nameof(PickingArea));
    public static readonly StyledProperty<int> CrownPulseProperty =
        AvaloniaProperty.Register<LayoutCanvas, int>(nameof(CrownPulse));
    public static readonly StyledProperty<bool> AnimateProperty =
        AvaloniaProperty.Register<LayoutCanvas, bool>(nameof(Animate), true);

    public LayoutProfile? Profile { get => GetValue(ProfileProperty); set => SetValue(ProfileProperty, value); }
    public IReadOnlyList<MonitorInfo>? Monitors { get => GetValue(MonitorsProperty); set => SetValue(MonitorsProperty, value); }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public bool Compact { get => GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
    public bool Animate { get => GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }
    /// <summary>The chosen monitors, in the layout's order (1 = the main's): exactly these are drawn and windows stay on them. Null: the monitors the slots use.</summary>
    public IReadOnlyList<MonitorInfo>? Screens { get => GetValue(ScreensProperty); set => SetValue(ScreensProperty, value); }
    /// <summary>Slot index → what the open WoW window in that slot really draws. Those slots show the measured action-bar state (solid dot).</summary>
    public IReadOnlyDictionary<int, ViewModels.LiveSlot>? LiveSlots { get => GetValue(LiveSlotsProperty); set => SetValue(LiveSlotsProperty, value); }
    /// <summary>WoW's UI Scale (1 = automatic), used for the planned state of slots without an open window (ring dot).</summary>
    public double UiScale { get => GetValue(UiScaleProperty); set => SetValue(UiScaleProperty, value); }

    /// <summary>Per slot: Awaiting (pulsing ring), Placing / Moved (amber), Connected (green). Null: no status drawn.</summary>
    public IReadOnlyList<ViewModels.PartyState>? SlotStates { get => GetValue(SlotStatesProperty); set => SetValue(SlotStatesProperty, value); }

    /// <summary>
    /// Window swap: per window, does it sit in a main slot? Each window then shows a crown chip ("MAIN SLOT" when it is
    /// one); clicking it raises <see cref="MainSlotClicked"/>. Null = no crowns (swap is off).
    /// </summary>
    public IReadOnlyList<bool>? MainSlotMarks { get => GetValue(MainSlotMarksProperty); set => SetValue(MainSlotMarksProperty, value); }

    /// <summary>Each change makes the crowns pulse for a moment: a swap hotkey was pressed but there is no main slot to swap into.</summary>
    public int CrownPulse { get => GetValue(CrownPulseProperty); set => SetValue(CrownPulseProperty, value); }

    /// <summary>Advanced editing: multi-selection (Ctrl/Shift+click, rubber band, Ctrl+A), group moves, grid, locked aspect.</summary>
    public bool AdvancedEditing { get => GetValue(AdvancedEditingProperty); set => SetValue(AdvancedEditingProperty, value); }
    /// <summary>Dropping a window onto another swaps them (always on outside Advanced editing).</summary>
    public bool SwapOnDrop { get => GetValue(SwapOnDropProperty); set => SetValue(SwapOnDropProperty, value); }
    public bool SnapEnabled { get => GetValue(SnapEnabledProperty); set => SetValue(SnapEnabledProperty, value); }
    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public bool LockAspect { get => GetValue(LockAspectProperty); set => SetValue(LockAspectProperty, value); }
    /// <summary>The selected windows in Advanced editing, the reference (gold outline) first.</summary>
    public IReadOnlyList<int>? SelectedIndices { get => GetValue(SelectedIndicesProperty); set => SetValue(SelectedIndicesProperty, value); }

    /// <summary>Fill preview: where each selected window would go (ghosts with their action-bar verdict).</summary>
    public IReadOnlyList<ViewModels.FillPreviewTile>? PreviewTiles { get => GetValue(PreviewTilesProperty); set => SetValue(PreviewTilesProperty, value); }
    /// <summary>Fill preview: the area that will be filled (highlighted).</summary>
    public PixelRect? PreviewArea { get => GetValue(PreviewAreaProperty); set => SetValue(PreviewAreaProperty, value); }
    /// <summary>"Fill area…": the next drag on the map draws the area (over windows too).</summary>
    public bool PickingArea { get => GetValue(PickingAreaProperty); set => SetValue(PickingAreaProperty, value); }

    /// <summary>"Fill area…": the area the user dragged, in pixels.</summary>
    public event EventHandler<PixelRect>? AreaPicked;

    /// <summary>Esc while a fill preview (or area picking) is showing.</summary>
    public event EventHandler? FillCancelRequested;

    /// <summary>Advanced editing: the selection changed (reference first).</summary>
    public event EventHandler<IReadOnlyList<int>>? SelectionChanged;

    /// <summary>Several windows got new places at once (a group drag or a nudge), in pixels.</summary>
    public event EventHandler<IReadOnlyList<(int Index, PixelRect Rect)>>? SlotsEdited;

    /// <summary>The crown chip of a window was clicked (window index).</summary>
    public event EventHandler<int>? MainSlotClicked;

    /// <summary>Raised when a drag or nudge has finished with the slot's new position.</summary>
    public event EventHandler<SlotEditedEventArgs>? SlotEdited;

    /// <summary>Raised when a window is dropped onto another one: swap their places.</summary>
    public event EventHandler<SlotsSwappedEventArgs>? SlotsSwapped;

    private const double HandleSize = 9, SnapScreenPixels = 10;
    private static readonly TimeSpan AnimationTime = TimeSpan.FromMilliseconds(320);

    // view transform: canvas = (pixel - origin) * scale + offset
    private double _scale = 1;
    private Point _offset;
    private int _originX, _originY;

    // drag state
    private int _dragIndex = -1, _hoverIndex = -1, _swapTarget = -1;
    private ResizeEdges _dragEdges;
    private PixelRect _dragStart, _dragRect;
    private Point _dragMouse;
    private IPointer? _captured;

    // glide animation: rectangles shown on screen move from where they were to where the profile says
    private Dictionary<int, Rect> _from = [], _shown = [];
    private DateTime _animStart;
    private bool _animating;

    // "Awaiting…" rings pulse: a light timer redraws only while a slot is waiting and the map is visible
    private readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromMilliseconds(66) };

    static LayoutCanvas()
    {
        AffectsRender<LayoutCanvas>(ProfileProperty, MonitorsProperty, SelectedIndexProperty, CompactProperty, ScreensProperty,
            LiveSlotsProperty, UiScaleProperty, SlotStatesProperty, MainSlotMarksProperty, AdvancedEditingProperty, ShowGridProperty,
            SelectedIndicesProperty, PreviewTilesProperty, PreviewAreaProperty, PickingAreaProperty);
    }

    public LayoutCanvas()
    {
        Focusable = true;
        FocusAdorner = null;
        ClipToBounds = true;
        UseLayoutRounding = false;   // smooth sub-pixel motion while gliding
        _pulse.Tick += (_, _) =>
        {
            // the timer runs while the map is in the window; a hidden map (other page) is not redrawn
            if (IsEffectivelyVisible) InvalidateVisual();
            if (_crownPulseUntil is { } until && DateTime.UtcNow > until) _crownPulseUntil = null;
            UpdatePulse();
        };
        Loaded += (_, _) => UpdatePulse();
        Unloaded += (_, _) => _pulse.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ProfileProperty) StartAnimation();
        else if (change.Property == SlotStatesProperty || change.Property == IsVisibleProperty) UpdatePulse();
        else if (change.Property == SelectedIndicesProperty) OnSelectedIndicesChanged();
        else if (change.Property == CrownPulseProperty) StartCrownPulse();
    }

    private void UpdatePulse()
    {
        var on = !Compact && IsVisible && IsLoaded && ((Animate && SlotStates?.Contains(ViewModels.PartyState.Awaiting) == true) || _crownPulseUntil is not null);
        if (on != _pulse.IsEnabled) _pulse.IsEnabled = on;
    }

    private LayoutOptions Options => Profile?.Options ?? new LayoutOptions();
    private double Padding => Compact ? 4 : 18;
    private double LabelSpace => Compact ? 0 : 22;

    private sealed record Item(int Index, LayoutSlot Slot, MonitorInfo Monitor, PixelRect Raw, PixelRect Fitted);

    private List<Item> Items()
    {
        if (Profile is null || Monitors is not { Count: > 0 } monitors) return [];
        var list = new List<Item>();
        for (var i = 0; i < Profile.Slots.Count; i++)
        {
            var slot = Profile.Slots[i];
            var monitor = Core.Monitors.Find(monitors, slot.Monitor) ?? Core.Monitors.Primary(monitors);
            var raw = i == _dragIndex ? _dragRect : _groupRects.TryGetValue(i, out var moved) ? moved : SlotEditing.Raw(slot, monitor, Options);
            list.Add(new Item(i, slot, monitor, raw, LayoutEngine.Fit(raw, slot.Fit ?? Options.Fit, slot.Anchor, Options.MinAspect, LayoutEngine.FrameSize(Options, monitor.Dpi))));
        }
        // overlays (always on top) are drawn last and hit first
        // overlays above the rest, and the window being edited above its neighbours
        return list.OrderBy(t => t.Slot.Z == ZOrder.Top ? 1 : 0).ThenBy(t => t.Index == SelectedIndex ? 1 : 0).ThenBy(t => t.Index).ToList();
    }

    /// <summary>The monitors to draw, in the layout's order: the chosen ones, or those the layout uses.</summary>
    private IReadOnlyList<MonitorInfo> Shown()
    {
        if (Screens is { Count: > 0 } chosen) return chosen;
        if (Monitors is not { Count: > 0 } all || Profile is null) return Monitors ?? [];
        return ScreenSet.Used(Profile, all);
    }

    private void UpdateTransform()
    {
        if (Shown() is not { Count: > 0 } monitors) return;
        int left = monitors.Min(m => m.Bounds.X), top = monitors.Min(m => m.Bounds.Y);
        int right = monitors.Max(m => m.Bounds.Right), bottom = monitors.Max(m => m.Bounds.Bottom);
        var w = Math.Max(Bounds.Width - 2 * Padding, 10);
        var h = Math.Max(Bounds.Height - 2 * Padding - LabelSpace, 10);
        _scale = Math.Min(w / (right - left), h / (bottom - top));
        _originX = left;
        _originY = top;
        _offset = new Point(Padding + (w - (right - left) * _scale) / 2, Padding + LabelSpace + (h - (bottom - top) * _scale) / 2);
    }

    private Rect ToCanvas(PixelRect r) =>
        new((r.X - _originX) * _scale + _offset.X, (r.Y - _originY) * _scale + _offset.Y, r.Width * _scale, r.Height * _scale);

    // ---------- animation ----------

    private void StartAnimation()
    {
        if (!Animate || _dragIndex >= 0 || _shown.Count == 0 || !IsLoaded)
        {
            _from.Clear();
            return;
        }
        _from = new Dictionary<int, Rect>(_shown);
        _animStart = DateTime.UtcNow;
        if (!_animating)
        {
            _animating = true;
            RequestFrame();
        }
    }

    /// <summary>Asks for the next frame (Avalonia's counterpart of WPF's CompositionTarget.Rendering).</summary>
    private void RequestFrame()
    {
        if (TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(OnFrame);
        else EndAnimation();
    }

    private void OnFrame(TimeSpan _)
    {
        InvalidateVisual();
        if (DateTime.UtcNow - _animStart < AnimationTime) RequestFrame();
        else EndAnimation();
    }

    private void EndAnimation()
    {
        _animating = false;
        _from.Clear();
        InvalidateVisual();
    }

    /// <summary>Where a window is drawn this frame (gliding from its previous place, ease-out cubic).</summary>
    private Rect Shown(int index, Rect target)
    {
        if (!_animating || !_from.TryGetValue(index, out var from)) return target;
        var t = Math.Clamp((DateTime.UtcNow - _animStart).TotalMilliseconds / AnimationTime.TotalMilliseconds, 0, 1);
        var k = 1 - Math.Pow(1 - t, 3);
        return new Rect(from.X + (target.X - from.X) * k, from.Y + (target.Y - from.Y) * k,
            Math.Max(0, from.Width + (target.Width - from.Width) * k), Math.Max(0, from.Height + (target.Height - from.Height) * k));
    }

    // ---------- drawing ----------

    private static ImmutableSolidColorBrush Brush(byte a, Color c) => new(Color.FromArgb(a, c.R, c.G, c.B));
    private static ImmutableSolidColorBrush Solid(Color c) => new(c);
    private static readonly Color Gold = Color.FromRgb(0xC9, 0xA2, 0x4A), GoldHi = Color.FromRgb(0xE8, 0xC6, 0x6A), Parchment = Color.FromRgb(0xE9, 0xDF, 0xC4);
    private static readonly Color NumberGold = Color.FromRgb(0xFF, 0xD1, 0x00);
    // same faces as the theme's DisplayFont (one file, so the regular weight is the Bold face) and BodyFont
    private static readonly FontFamily DisplayFont = new("avares://wowboxer/Assets/Fonts/Cinzel-Bold.ttf#Cinzel");
    private static readonly FontFamily BodyFont = new("fonts:Inter#Inter");

    /// <summary>WPF's LinearGradientBrush(start, end, angle): from the top-left corner along the angle (degrees, clockwise).</summary>
    private static ImmutableLinearGradientBrush Linear(Color start, Color end, double angle)
    {
        var a = angle * Math.PI / 180;
        return new ImmutableLinearGradientBrush([new ImmutableGradientStop(0, start), new ImmutableGradientStop(1, end)],
            startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(Math.Cos(a), Math.Sin(a), RelativeUnit.Relative));
    }

    private static readonly IBrush BezelFill = Solid(Color.FromRgb(0x07, 0x09, 0x0E)), TaskbarFill = Solid(Color.FromRgb(0x1A, 0x1F, 0x2B));
    private static readonly IPen BezelPen = new Pen(Brush(0x70, Gold), 1);
    private static readonly IBrush ScreenFill = Linear(Color.FromRgb(0x10, 0x15, 0x20), Color.FromRgb(0x08, 0x0B, 0x11), 90);
    private static readonly IBrush ScreenEdge = Solid(Color.FromRgb(0x3A, 0x45, 0x60));
    private static readonly IBrush MainBody = new ImmutableRadialGradientBrush(
        [new ImmutableGradientStop(0, Color.FromRgb(0x26, 0x33, 0x4D)), new ImmutableGradientStop(1, Color.FromRgb(0x0E, 0x13, 0x1D))],
        center: new RelativePoint(0.5, 0.4, RelativeUnit.Relative), gradientOrigin: new RelativePoint(0.5, 0.4, RelativeUnit.Relative),
        radiusX: new RelativeScalar(0.8, RelativeUnit.Relative), radiusY: new RelativeScalar(0.9, RelativeUnit.Relative));
    private static readonly IBrush AltBody = Linear(Color.FromRgb(0x22, 0x2A, 0x3E), Color.FromRgb(0x12, 0x17, 0x22), 70);
    private static readonly IBrush MedallionFill = new ImmutableRadialGradientBrush(
        [new ImmutableGradientStop(0, Color.FromRgb(0x23, 0x2B, 0x3E)), new ImmutableGradientStop(1, Color.FromRgb(0x0B, 0x0E, 0x14))],
        radiusX: new RelativeScalar(0.5, RelativeUnit.Relative), radiusY: new RelativeScalar(0.5, RelativeUnit.Relative));
    private static readonly IBrush MedallionRim = Linear(Color.FromRgb(0xF7, 0xE3, 0xA6), Color.FromRgb(0x8C, 0x6B, 0x26), 90);
    private static readonly IBrush Shadow = new ImmutableSolidColorBrush(Color.FromArgb(0x90, 0, 0, 0));

    public override void Render(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));   // whole area hit-testable
        if (Monitors is not { Count: > 0 } monitors)
        {
            if (!Compact) Draw(dc, Text("No monitors found.", 14, Brushes.Gray), new Point(Padding, Padding));
            return;
        }
        UpdateTransform();
        var radius = Compact ? 2.5 : 6;

        var screens = Shown();
        for (var number = 1; number <= screens.Count; number++)
        {
            var m = screens[number - 1];
            var bounds = ToCanvas(m.Bounds);
            // bezel + screen
            if (!Compact)
                dc.DrawRectangle(BezelFill, BezelPen, bounds.Inflate(5), radius + 3, radius + 3);
            dc.DrawRectangle(ScreenFill, new Pen(ScreenEdge, Compact ? 0.8 : 1), bounds, radius, radius);

            // the taskbar band (bounds minus work area), drawn as a taskbar so nobody wonders about the strip
            foreach (var band in TaskbarBands(m))
            {
                var b = ToCanvas(band);
                dc.DrawRectangle(TaskbarFill, null, b);
                if (!Compact && b.Height >= 10 && b.Width > 140)
                {
                    var dots = Math.Min(7, (int)(b.Width / 28));
                    var cx = b.X + b.Width / 2 - dots * 9;
                    var dotBrush = Brush(0x55, Parchment);
                    for (var i = 0; i < dots; i++)
                        dc.DrawRectangle(dotBrush, null, new Rect(cx + i * 18, b.Y + b.Height / 2 - 3, 9, 6), 1.5, 1.5);
                    var label = Text(Options.UseTaskbarArea ? "Taskbar (windows go under it)" : "Taskbar", 10, Brush(0x99, Parchment));
                    if (label.Width < b.Width / 2 - 70) Draw(dc, label, new Point(b.X + 8, b.Y + (b.Height - label.Height) / 2));
                }
            }

            if (!Compact)
            {
                var title = Text($"{number}{(m.IsPrimary ? " · PRIMARY" : "")}  ·  {m.Bounds.Width}×{m.Bounds.Height}  ·  {m.ScalePercent} %", 11, Brush(0xD0, Gold), display: true);
                if (title.Width > bounds.Width) title = Text($"{number}  ·  {m.Bounds.Width}×{m.Bounds.Height}", 11, Brush(0xD0, Gold), display: true);
                Draw(dc, title, new Point(bounds.X, bounds.Y - title.Height - 8));
            }
        }

        if (!Compact && AdvancedEditing && ShowGrid)
        {
            var gridPen = new Pen(Brush(0x2C, Parchment), 1);
            foreach (var (x0, y0, x1, y1) in GridLines())
                dc.DrawLine(gridPen, ToCanvasPoint(x0, y0), ToCanvasPoint(x1, y1));
        }
        var items = Items();
        if (!Compact)
            foreach (var m in screens.Where(m => items.All(i => i.Monitor.Index != m.Index)))
            {
                var b = ToCanvas(m.WorkArea);
                var hint = Text("Free – drag a window over", 12, Brush(0x90, Parchment));
                hint.MaxTextWidth = Math.Max(40, b.Width - 40);
                hint.TextAlignment = TextAlignment.Center;
                Draw(dc, hint, new Point(b.X + 20, b.Y + (b.Height - hint.Height) / 2));
            }
        var shown = new Dictionary<int, Rect>();
        foreach (var item in items)
        {
            var color = WowClasses.ColorFor(item.Slot.Class);
            var selected = !Compact && (AdvancedEditing ? _selection.Contains(item.Index) : item.Index == SelectedIndex);
            var reference = selected && AdvancedEditing && item.Index == _selection.Reference && _selection.Items.Count > 1;
            var hovered = item.Index == _hoverIndex && !Compact && _dragIndex < 0;
            var target = ToCanvas(item.Fitted);
            var r = Shown(item.Index, target);
            shown[item.Index] = r;
            var overlay = item.Slot.Z == ZOrder.Top;

            if (item.Fitted != item.Raw && !Compact && !_animating)
                dc.DrawRectangle(null, new Pen(Brush(0x70, color), 1, DashStyle.Dash), ToCanvas(item.Raw));

            // shadow under floating / selected windows
            if (overlay || selected)
                dc.DrawRectangle(Shadow, null, r.Translate(new Vector(0, Compact ? 1.5 : 4)), 4, 4);
            // body: panel gradient (main gets a radial "game" glow), class-colour top edge, gold frame when selected
            var body = item.Slot.Role == SlotRole.Main ? MainBody : AltBody;
            IPen frame = reference ? new Pen(Solid(GoldHi), 3)
                : selected && AdvancedEditing && _selection.Items.Count > 1 ? new Pen(Brush(0xD0, Parchment), 1.6, DashStyle.Dash)
                : selected ? new Pen(Solid(GoldHi), 2) : new Pen(hovered ? Brush(0xB0, Gold) : Solid(Color.FromRgb(0x2E, 0x38, 0x50)), hovered ? 1.5 : 1);
            if (selected)
                dc.DrawRectangle(null, new Pen(Brush(0x40, GoldHi), 5), r.Inflate(3), 6, 6);
            dc.DrawRectangle(body, frame, r, Compact ? 1.5 : 3, Compact ? 1.5 : 3);
            var edge = Math.Max(Compact ? 2 : 3, Math.Min(5, r.Height * 0.03));
            if (r.Width > 2 && r.Height > edge)
                dc.DrawRectangle(Solid(color), null, new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), edge));

            if (Compact)
            {
                if (r.Height > 14 && r.Width > 10)
                {
                    var n = Text($"{item.Slot.N}", Math.Clamp(r.Height * 0.3, 8, 30), Solid(NumberGold), display: true);
                    Draw(dc, n, new Point(r.X + (r.Width - n.Width) / 2, r.Y + (r.Height - n.Height) / 2 + edge / 2));
                }
                continue;
            }

            // gold number medallion, class/character name, size, action-bar dot (app mockup v0.2)
            var (gw, gh) = LayoutEngine.GameArea(item.Fitted, LayoutEngine.FrameSize(Options, item.Monitor.Dpi));
            // what the action bar sees: measured on the open window, or planned (game area / UI Scale)
            var measured = LiveSlots is { } bars && bars.TryGetValue(item.Index, out var bar) ? bar : null;
            var aspect = measured?.Effective ?? (gh == 0 ? 0 : (double)gw / gh / Math.Max(0.1, UiScale));
            var radiusM = Math.Clamp(Math.Min(r.Width, r.Height) * 0.16, 9, 26);
            var lines = new List<FormattedText>();
            var label = item.Slot.Name ?? WowClasses.Find(item.Slot.Class)?.Name;
            if (r.Height > 70 && label is not null) lines.Add(Text(label, 12.5, Solid(color), display: true));
            if (r.Height > 56 && r.Width > 80) lines.Add(Text($"{item.Fitted.Width} × {item.Fitted.Height}", 11, Brush(0xC8, Parchment)));
            var showBar = r.Height > 84 && r.Width > 110;
            var barBrush = Solid(ActionBar.ColorFor(aspect));
            var barText = showBar ? Text(ActionBar.Short(aspect), 10.5, barBrush) : null;
            var total = radiusM * 2 + 6 + lines.Sum(l => l.Height) + (barText?.Height ?? 0);
            var y = r.Y + edge / 2 + (r.Height - total) / 2;
            var mc = new Point(r.X + r.Width / 2, y + radiusM);
            dc.DrawEllipse(MedallionFill, new Pen(MedallionRim, Math.Max(1.5, radiusM * 0.12)), mc, radiusM, radiusM);
            DrawStatus(dc, item.Index, mc, radiusM, r, edge);
            var num = Text($"{item.Slot.N}", radiusM * 1.15, Solid(NumberGold), display: true);
            Draw(dc, num, new Point(mc.X - num.Width / 2, mc.Y - num.Height / 2), shadow: true);
            y += radiusM * 2 + 6;
            foreach (var line in lines)
            {
                Draw(dc, line, new Point(r.X + (r.Width - line.Width) / 2, y), shadow: true);
                y += line.Height;
            }
            if (barText is not null)
            {
                var dot = 7.0;
                var x0 = r.X + (r.Width - barText.Width - dot - 5) / 2;
                var centre = new Point(x0 + dot / 2, y + barText.Height / 2 + 0.5);
                if (measured is not null) dc.DrawEllipse(barBrush, null, centre, dot / 2, dot / 2);   // measured on the real window
                else dc.DrawEllipse(null, new Pen(barBrush, 1.4), centre, dot / 2 - 0.7, dot / 2 - 0.7);   // planned
                Draw(dc, barText, new Point(x0 + dot + 5, y), shadow: true);
            }
            // pin: the slot is "always on top"; red when the real window differs (the guard puts it right in a moment)
            var realTop = measured?.Topmost;
            if ((overlay || realTop == true) && r.Width > 50)
            {
                var pinBrush = Solid(realTop is null || realTop == overlay ? GoldHi : Color.FromRgb(0xE3, 0x7A, 0x7E));
                DrawPin(dc, new Point(r.X + 9, r.Y + edge + 9), pinBrush);
                var pinLabel = realTop is null || realTop == overlay ? "ON TOP" : overlay ? "NOT ON TOP YET" : "ON TOP (NOT WANTED)";
                Draw(dc, Text(pinLabel, 9, pinBrush, display: true), new Point(r.X + 17, r.Y + edge + 3));
            }

            if (MainSlotMarks is { } marks && item.Index < marks.Count && CrownChip(r) is { } chip)
            {
                if (!marks[item.Index]) DrawCrownGlow(dc, chip);   // "click one of these"
                DrawCrown(dc, chip, marks[item.Index], item.Index == _hoverIndex && _hoverCrown);
            }

            if (item.Index == _swapTarget)
            {
                var t = r.Inflate(2);
                dc.DrawRectangle(Brush(0x30, GoldHi), new Pen(Solid(GoldHi), 2.5, DashStyle.Dash), t, 4, 4);
                var swap = Text("SWAP", 13, Solid(GoldHi), display: true);
                Draw(dc, swap, new Point(t.X + (t.Width - swap.Width) / 2, t.Y + 8), shadow: true);
            }

            if (reference && r.Width > 60)
                Draw(dc, Text("REFERENCE", 9, Solid(GoldHi), display: true), new Point(r.X + 8, r.Bottom - 16), shadow: true);

            if (selected && !_animating && (!AdvancedEditing || _selection.Items.Count <= 1))
            {
                var handleFill = Solid(GoldHi);
                var handlePen = new Pen(Brushes.Black, 1);
                foreach (var (handle, _) in Handles(ToCanvas(item.Raw)))
                    dc.DrawRectangle(handleFill, handlePen, handle);
            }
        }
        _shown = shown;
        if (!Compact && PreviewArea is { } fillArea)
        {
            var a = ToCanvas(fillArea);
            dc.DrawRectangle(Brush(0x26, GoldHi), new Pen(Solid(GoldHi), 2, DashStyle.Dash), a);
            Draw(dc, Text("FILL AREA", 10, Solid(GoldHi), display: true), new Point(a.X + 6, a.Y + 4), shadow: true);
        }
        if (!Compact && PreviewTiles is { } ghosts)
            foreach (var g in ghosts)
            {
                var gr = ToCanvas(g.Rect).Deflate(2);
                if (gr.Width <= 0 || gr.Height <= 0) continue;
                dc.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x15, 0x22)), new Pen(Brush(0xE0, Parchment), 1.4, DashStyle.Dot), gr, 3, 3);
                var num = Text($"{g.Index + 1}", Math.Clamp(gr.Height * 0.22, 10, 26), Solid(NumberGold), display: true);
                Draw(dc, num, new Point(gr.X + (gr.Width - num.Width) / 2, gr.Y + gr.Height / 2 - num.Height), shadow: true);
                if (gr.Width > 70 && gr.Height > 40)
                {
                    var verdict = Text(g.Verdict, 10.5, Solid(g.Color));
                    Draw(dc, verdict, new Point(gr.X + (gr.Width - verdict.Width) / 2, gr.Y + gr.Height / 2 + 2), shadow: true);
                }
            }
        if (_band is { } rubber)
            dc.DrawRectangle(Brush(0x22, GoldHi), new Pen(Solid(GoldHi), 1.2, DashStyle.Dash), ToCanvas(rubber));
    }

    private Point ToCanvasPoint(int x, int y) => new((x - _originX) * _scale + _offset.X, (y - _originY) * _scale + _offset.Y);

    // ---------- window swap: main slot crowns ----------

    private bool _hoverCrown;
    private DateTime? _crownPulseUntil;
    private const double CrownPulseSeconds = 2.4;

    private void StartCrownPulse()
    {
        if (Compact || MainSlotMarks is null) return;
        _crownPulseUntil = DateTime.UtcNow.AddSeconds(CrownPulseSeconds);
        UpdatePulse();
        InvalidateVisual();
    }

    /// <summary>The glow around a crown chip while <see cref="CrownPulse"/> asks for attention (steady when animations are off).</summary>
    private void DrawCrownGlow(DrawingContext dc, Rect chip)
    {
        if (_crownPulseUntil is not { } until) return;
        var left = (until - DateTime.UtcNow).TotalSeconds;
        if (left <= 0) return;
        var wave = Animate ? 0.5 + 0.5 * Math.Sin((CrownPulseSeconds - left) * Math.PI * 2 / 0.8 - Math.PI / 2) : 1;
        dc.DrawRectangle(Brush((byte)(0x20 + 0x50 * wave), GoldHi), new Pen(Brush((byte)(0x80 + 0x7F * wave), GoldHi), 2), chip.Inflate(3 + 3 * wave), 5, 5);
    }

    /// <summary>Where a window's crown chip goes: bottom left, inside the window (null if the window is drawn too small).</summary>
    /// <remarks>Small windows (overlay followers) get the crown alone, without the label.</remarks>
    private static Rect? CrownChip(Rect r) =>
        r.Width < 34 || r.Height < 30 ? null
        : r.Width < 150 || r.Height < 110 ? new Rect(r.X + 4, r.Bottom - 20, 22, 16)
        : new Rect(r.X + 6, r.Bottom - 24, Math.Min(96, r.Width - 12), 18);

    /// <summary>The window whose crown chip is under the pointer, or null.</summary>
    private int? CrownAt(Point p)
    {
        if (MainSlotMarks is null || Compact) return null;
        foreach (var item in Enumerable.Reverse(Items()))
        {
            var r = _shown.TryGetValue(item.Index, out var shown) ? shown : ToCanvas(item.Fitted);
            if (CrownChip(r) is { } chip && chip.Contains(p)) return item.Index;
            if (r.Contains(p)) return null;   // covered by this window
        }
        return null;
    }

    private static void DrawCrown(DrawingContext dc, Rect chip, bool main, bool hover)
    {
        var fill = main ? Brush(0xE0, GoldHi) : Brush(hover ? (byte)0x50 : (byte)0x28, Gold);
        var ink = main ? Solid(Color.FromRgb(0x14, 0x10, 0x06)) : Brush(hover ? (byte)0xF0 : (byte)0xA0, GoldHi);
        dc.DrawRectangle(fill, new Pen(Brush(main ? (byte)0xFF : (byte)0x90, GoldHi), 1), chip, 3, 3);
        // a small three-point crown
        double x = chip.X + 5, y = chip.Y + (chip.Height - 10) / 2, w = 12, h = 10;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x, y + h), true);
            c.LineTo(new Point(x, y + 2));
            c.LineTo(new Point(x + w * 0.28, y + h * 0.55));
            c.LineTo(new Point(x + w / 2, y));
            c.LineTo(new Point(x + w * 0.72, y + h * 0.55));
            c.LineTo(new Point(x + w, y + 2));
            c.LineTo(new Point(x + w, y + h));
            c.EndFigure(true);
        }
        dc.DrawGeometry(ink, null, g);
        if (chip.Width < 40) return;
        var label = Text(main ? "MAIN SLOT" : "MAIN SLOT?", 9, ink, display: true);
        if (label.Width < chip.Width - 22) Draw(dc, label, new Point(x + w + 5, chip.Y + (chip.Height - label.Height) / 2));
    }

    /// <summary>The parts of the monitor outside its work area (where the taskbar is).</summary>
    private static IEnumerable<PixelRect> TaskbarBands(MonitorInfo m)
    {
        PixelRect b = m.Bounds, w = m.WorkArea;
        if (w.Bottom < b.Bottom) yield return new PixelRect(b.X, w.Bottom, b.Width, b.Bottom - w.Bottom);
        if (w.Y > b.Y) yield return new PixelRect(b.X, b.Y, b.Width, w.Y - b.Y);
        if (w.X > b.X) yield return new PixelRect(b.X, b.Y, w.X - b.X, b.Height);
        if (w.Right < b.Right) yield return new PixelRect(w.Right, b.Y, b.Right - w.Right, b.Height);
    }

    private static IEnumerable<(Rect Handle, ResizeEdges Edges)> Handles(Rect r)
    {
        var s = HandleSize;
        double l = r.Left - s / 2, t = r.Top - s / 2, cx = r.Left + r.Width / 2 - s / 2, cy = r.Top + r.Height / 2 - s / 2, rr = r.Right - s / 2, b = r.Bottom - s / 2;
        yield return (new Rect(l, t, s, s), ResizeEdges.Left | ResizeEdges.Top);
        yield return (new Rect(rr, t, s, s), ResizeEdges.Right | ResizeEdges.Top);
        yield return (new Rect(l, b, s, s), ResizeEdges.Left | ResizeEdges.Bottom);
        yield return (new Rect(rr, b, s, s), ResizeEdges.Right | ResizeEdges.Bottom);
        yield return (new Rect(cx, t, s, s), ResizeEdges.Top);
        yield return (new Rect(cx, b, s, s), ResizeEdges.Bottom);
        yield return (new Rect(l, cy, s, s), ResizeEdges.Left);
        yield return (new Rect(rr, cy, s, s), ResizeEdges.Right);
    }

    // ---------- editing ----------

    private (int Index, ResizeEdges Edges) HitTest(Point p)
    {
        var items = Items();
        // resize handles: the selected window – in Advanced editing only while it is the one selected window
        var handlesOn = !AdvancedEditing || _selection.Items.Count <= 1;
        var sel = handlesOn ? items.FirstOrDefault(i => i.Index == SelectedIndex) : null;
        if (sel is not null)
            foreach (var (handle, edges) in Handles(ToCanvas(sel.Raw)))
                if (handle.Inflate(4).Contains(p)) return (sel.Index, edges);
        foreach (var item in Enumerable.Reverse(items))
            if (ToCanvas(item.Fitted).Contains(p) || ToCanvas(item.Raw).Contains(p)) return (item.Index, ResizeEdges.All);
        return (-1, ResizeEdges.None);
    }

    private static bool AddModifier(KeyModifiers m) => m.HasFlag(KeyModifiers.Control) || m.HasFlag(KeyModifiers.Shift);

    /// <summary>Snapping: always (Alt turns it off) outside Advanced editing; inside, the toolbar's Snap switch (Alt inverts it).</summary>
    private bool SnapNow(KeyModifiers m) => (AdvancedEditing ? SnapEnabled : true) != m.HasFlag(KeyModifiers.Alt);

    private void CaptureMouse(IPointer pointer)
    {
        _captured = pointer;
        pointer.Capture(this);
    }

    /// <summary>Releases the pointer; <see cref="OnPointerCaptureLost"/> then cancels whatever is still in progress.</summary>
    private void ReleaseMouseCapture()
    {
        var pointer = _captured;
        _captured = null;
        if (pointer?.Captured == this) pointer.Capture(null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Compact) return;
        var point = e.GetCurrentPoint(this);
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        Focus();
        var p = point.Position;
        if (CrownAt(p) is { } crown)
        {
            MainSlotClicked?.Invoke(this, crown);
            e.Handled = true;
            return;
        }
        if (AdvancedEditing && PickingArea)
        {
            _bandStart = p;
            _bandAdd = false;
            _bandForArea = true;
            _band = null;
            CaptureMouse(e.Pointer);
            e.Handled = true;
            return;
        }
        var (index, edges) = HitTest(p);
        if (AdvancedEditing)
        {
            if (index < 0)
            {
                // rubber band on the empty map (Ctrl/Shift adds to the selection)
                _bandStart = p;
                _bandAdd = AddModifier(e.KeyModifiers);
                _band = null;
                if (!_bandAdd) SetSelection([]);
                CaptureMouse(e.Pointer);
                e.Handled = true;
                return;
            }
            if (AddModifier(e.KeyModifiers))
            {
                _selection.Toggle(index);
                RaiseSelectionChanged();
                e.Handled = true;
                return;
            }
            _selection.Click(index, keepGroupIfSelected: edges == ResizeEdges.All);
            RaiseSelectionChanged();
        }
        else SelectedIndex = index;
        if (index < 0 || Profile is null) return;
        var item = Items().First(i => i.Index == index);
        _dragIndex = index;
        _dragEdges = edges;
        _dragStart = _dragRect = item.Raw;
        _dragMouse = p;
        _groupStart.Clear();
        _groupRects.Clear();
        if (AdvancedEditing && edges == ResizeEdges.All && _selection.Items.Count > 1)
            foreach (var other in Items().Where(i => _selection.Contains(i.Index) && i.Index != index))
                _groupStart[other.Index] = other.Raw;
        CaptureMouse(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Compact) return;
        var point = e.GetCurrentPoint(this);
        var p = point.Position;
        if (_bandStart is { } start && point.Properties.IsLeftButtonPressed)
        {
            var a = FromCanvas(start);
            var b = FromCanvas(p);
            _band = SlotArrange.Band(a.X, a.Y, b.X, b.Y);
            InvalidateVisual();
            return;
        }
        if (_dragIndex < 0)
        {
            var (index, edges) = HitTest(p);
            var overCrown = CrownAt(p) is not null;
            if (index != _hoverIndex || overCrown != _hoverCrown)
            {
                _hoverIndex = index;
                _hoverCrown = overCrown;
                InvalidateVisual();
            }
            if (overCrown)
            {
                Cursor = CursorOf(StandardCursorType.Hand);
                return;
            }
            Cursor = CursorOf(edges switch
            {
                ResizeEdges.All => StandardCursorType.SizeAll,
                ResizeEdges.Left or ResizeEdges.Right => StandardCursorType.SizeWestEast,
                ResizeEdges.Top or ResizeEdges.Bottom => StandardCursorType.SizeNorthSouth,
                ResizeEdges.Left | ResizeEdges.Top or ResizeEdges.Right | ResizeEdges.Bottom => StandardCursorType.TopLeftCorner,
                _ when AdvancedEditing && PickingArea => StandardCursorType.Cross,
                ResizeEdges.None => AdvancedEditing ? StandardCursorType.Cross : StandardCursorType.Arrow,
                _ => StandardCursorType.TopRightCorner,
            });
            return;
        }
        var dx = (int)Math.Round((p.X - _dragMouse.X) / _scale);
        var dy = (int)Math.Round((p.Y - _dragMouse.Y) / _scale);
        var locked = AdvancedEditing && LockAspect && _dragEdges != ResizeEdges.All;
        var rect = locked ? SlotArrange.ResizeLocked(_dragStart, _dragEdges, dx, dy) : SlotEditing.Drag(_dragStart, _dragEdges, dx, dy);
        if (SnapNow(e.KeyModifiers) && !locked)
        {
            var moving = _groupStart.Keys.Append(_dragIndex).ToHashSet();
            var others = Items().Where(i => !moving.Contains(i.Index)).Select(i => i.Raw);
            var (xs, ys) = SlotEditing.SnapLines(others, Shown(), Options.UseTaskbarArea);
            if (AdvancedEditing && ShowGrid) AddGridLines(xs, ys);
            rect = SlotEditing.Snap(rect, _dragEdges, xs, ys, (int)Math.Ceiling(SnapScreenPixels / _scale));
        }
        // a moved window lands on the chosen monitor that holds its centre, inside it (across monitor borders too);
        // a moved group keeps its shape (each window is kept inside its monitor when it is stored)
        if (_dragEdges == ResizeEdges.All && _groupStart.Count == 0 && Shown() is { Count: > 0 } screens)
            rect = SlotEditing.ClampInto(rect, ProfileCapture.MonitorOf(rect, screens).Area(Options.UseTaskbarArea));
        _dragRect = rect;
        int mx = rect.X - _dragStart.X, my = rect.Y - _dragStart.Y;
        foreach (var (i, r) in _groupStart) _groupRects[i] = r with { X = r.X + mx, Y = r.Y + my };
        _swapTarget = _dragEdges == ResizeEdges.All && _groupStart.Count == 0 && SwapOnDrop ? SwapTargetAt(p) : -1;
        InvalidateVisual();
    }

    private static readonly Dictionary<StandardCursorType, Cursor> Cursors = [];

    /// <summary>One shared cursor per kind (created on first use, on the UI thread).</summary>
    private static Cursor CursorOf(StandardCursorType type)
    {
        if (!Cursors.TryGetValue(type, out var cursor)) Cursors[type] = cursor = new Cursor(type);
        return cursor;
    }

    /// <summary>Another window under the pointer while moving one: dropping there swaps the two.</summary>
    private int SwapTargetAt(Point p)
    {
        foreach (var item in Enumerable.Reverse(Items()))
        {
            if (item.Index == _dragIndex) continue;
            var r = ToCanvas(item.Fitted);
            // only the middle half counts, so snapping against a neighbour's edge never turns into a swap
            if (r.Deflate(new Thickness(r.Width / 4, r.Height / 4)).Contains(p)) return item.Index;
        }
        return -1;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverIndex < 0) return;
        _hoverIndex = -1;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (_bandStart is not null)
        {
            var band = _band;
            _bandStart = null;
            _band = null;
            ReleaseMouseCapture();
            if (_bandForArea)
            {
                _bandForArea = false;
                if (band is { } picked) AreaPicked?.Invoke(this, picked);
                InvalidateVisual();
                return;
            }
            if (band is { Width: > 2, Height: > 2 } b)
            {
                var items = Items().OrderBy(i => i.Index).ToList();
                var hit = SlotArrange.InBand(items.Select(i => i.Raw).ToList(), b).Select(k => items[k].Index);
                _selection.Band(hit, _bandAdd);
                RaiseSelectionChanged();
            }
            InvalidateVisual();
            return;
        }
        if (_dragIndex < 0) return;
        var index = _dragIndex;
        var rect = _dragRect;
        var changed = rect != _dragStart;
        var target = _swapTarget;
        var group = _groupRects.ToDictionary();
        _dragIndex = -1;
        _swapTarget = -1;
        _groupStart.Clear();
        _groupRects.Clear();
        ReleaseMouseCapture();
        if (target >= 0) SlotsSwapped?.Invoke(this, new SlotsSwappedEventArgs(index, target));
        else if (changed && group.Count > 0)
        {
            // the whole selection moved: one edit, the real windows follow now
            _from.Clear();
            group[index] = rect;
            SlotsEdited?.Invoke(this, group.Select(kv => (kv.Key, kv.Value)).ToList());
        }
        else if (changed) Commit(index, rect);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _captured = null;
        if (_bandStart is not null)
        {
            _bandStart = null;
            _band = null;
            _bandForArea = false;
            InvalidateVisual();
        }
        if (_dragIndex < 0) return;
        _dragIndex = -1;   // e.g. Escape or focus loss: cancel the drag
        _swapTarget = -1;
        _groupStart.Clear();
        _groupRects.Clear();
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Compact || Profile is null || Monitors is null) return;
        var mods = e.KeyModifiers;
        if (e.Key == Key.Escape && _dragIndex < 0 && _bandStart is null && (PreviewTiles is not null || PickingArea))
        {
            FillCancelRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && (_dragIndex >= 0 || _bandStart is not null))
        {
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (AdvancedEditing)
        {
            if (e.Key == Key.A && mods == KeyModifiers.Control)
            {
                _selection.All(Profile.Slots.Count);
                RaiseSelectionChanged();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                SetSelection([]);
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.Tab && Profile.Slots.Count > 0)
        {
            var back = mods.HasFlag(KeyModifiers.Shift);
            var next = ((SelectedIndex < 0 ? (back ? 0 : -1) : SelectedIndex) + (back ? -1 : 1) + Profile.Slots.Count) % Profile.Slots.Count;
            if (AdvancedEditing) SetSelection([next]);
            else SelectedIndex = next;
            e.Handled = true;
            return;
        }
        int dx = 0, dy = 0;
        switch (e.Key)
        {
            case Key.Left: dx = -1; break;
            case Key.Right: dx = 1; break;
            case Key.Up: dy = -1; break;
            case Key.Down: dy = 1; break;
            default: return;
        }
        var step = mods.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        var resize = mods.HasFlag(KeyModifiers.Control);
        if (AdvancedEditing && _selection.Items.Count > 1 && !resize)
        {
            // nudge the whole selection: 1 px, Shift = 10 px
            _from.Clear();
            SlotsEdited?.Invoke(this, Items().Where(i => _selection.Contains(i.Index))
                .Select(i => (i.Index, SlotEditing.Drag(i.Raw, ResizeEdges.All, dx * step, dy * step))).ToList());
            e.Handled = true;
            return;
        }
        if (SelectedIndex < 0 || SelectedIndex >= Profile.Slots.Count) return;
        var item = Items().First(i => i.Index == SelectedIndex);
        var rect = resize
            ? SlotEditing.Drag(item.Raw, ResizeEdges.Right | ResizeEdges.Bottom, dx * step, dy * step)   // Ctrl+arrows: resize
            : SlotEditing.Drag(item.Raw, ResizeEdges.All, dx * step, dy * step);
        Commit(SelectedIndex, rect);
        e.Handled = true;
    }

    private void Commit(int index, PixelRect rect)
    {
        if (Profile is null || Monitors is null) return;
        _from.Clear();   // the user put it there – no glide
        var screens = Shown();
        SlotEdited?.Invoke(this, new SlotEditedEventArgs(index, SlotEditing.WithRect(Profile.Slots[index], rect, screens.Count > 0 ? screens : Monitors, Options)));
    }

    // ---------- Advanced editing: selection, rubber band, grid ----------

    private readonly SlotSelection _selection = new();
    private readonly Dictionary<int, PixelRect> _groupStart = [], _groupRects = [];
    private Point? _bandStart;
    private PixelRect? _band;
    private bool _bandAdd;
    private bool _syncingSelection;
    private bool _bandForArea;

    private void SetSelection(IReadOnlyList<int> indices)
    {
        _selection.Set(indices);
        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        _syncingSelection = true;
        try
        {
            SelectedIndices = _selection.Items.ToList();
            if (_selection.Reference >= 0) SelectedIndex = _selection.Reference;
        }
        finally
        {
            _syncingSelection = false;
        }
        SelectionChanged?.Invoke(this, _selection.Items.ToList());
        InvalidateVisual();
    }

    private void OnSelectedIndicesChanged()
    {
        if (_syncingSelection) return;
        _selection.Set(SelectedIndices ?? []);
        InvalidateVisual();
    }

    /// <summary>Canvas point → pixel (virtual-screen coordinates).</summary>
    private (int X, int Y) FromCanvas(Point p) =>
        ((int)Math.Round((p.X - _offset.X) / _scale + _originX), (int)Math.Round((p.Y - _offset.Y) / _scale + _originY));

    /// <summary>The grid: every monitor's usable area in twelfths (halves, thirds, quarters and sixths all fall on it).</summary>
    private IEnumerable<(int X0, int Y0, int X1, int Y1)> GridLines()
    {
        foreach (var m in Shown())
        {
            var a = m.Area(Options.UseTaskbarArea);
            for (var k = 1; k < GridSteps; k++)
            {
                var x = a.X + (int)Math.Round((double)a.Width * k / GridSteps);
                var y = a.Y + (int)Math.Round((double)a.Height * k / GridSteps);
                yield return (x, a.Y, x, a.Bottom);
                yield return (a.X, y, a.Right, y);
            }
        }
    }

    private const int GridSteps = 12;

    private void AddGridLines(List<int> xs, List<int> ys)
    {
        foreach (var (x0, y0, x1, y1) in GridLines())
        {
            if (x0 == x1) xs.Add(x0);
            else ys.Add(y0);
        }
    }

    // ---------- text ----------

    private static readonly Typeface DisplayFace = new(DisplayFont, FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface BodyFace = new(BodyFont, FontStyle.Normal, FontWeight.SemiBold);

    private static FormattedText Text(string text, double size, IBrush brush, bool display = false) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, display ? DisplayFace : BodyFace, size, brush);

    private static readonly Color Ok = Color.FromRgb(0x8C, 0xCB, 0x7A), Amber = Color.FromRgb(0xE6, 0xB4, 0x55);

    /// <summary>The slot's status: a ring round the number (pulsing while awaiting its window) and a label in the corner.</summary>
    private void DrawStatus(DrawingContext dc, int index, Point centre, double radius, Rect r, double edge)
    {
        if (SlotStates is not { } states || index >= states.Count) return;
        var state = states[index];
        var (color, text) = state switch
        {
            ViewModels.PartyState.Connected => (Ok, "CONNECTED"),
            ViewModels.PartyState.Placing => (Amber, "PLACING…"),
            ViewModels.PartyState.Moved => (Amber, "MOVED"),
            _ => (GoldHi, "AWAITING…"),
        };
        var alpha = state != ViewModels.PartyState.Awaiting ? 1.0
            : !_pulse.IsEnabled ? 0.55
            : 0.25 + 0.6 * (0.5 + 0.5 * Math.Sin(DateTime.UtcNow.TimeOfDay.TotalSeconds * Math.PI * 2 / 1.6));
        var ring = Brush((byte)(alpha * 255), color);
        dc.DrawEllipse(null, new Pen(ring, state == ViewModels.PartyState.Awaiting ? 2 : 1.6), centre, radius + 4, radius + 4);
        if (r.Width < 110 || r.Height < 50) return;
        var label = Text(text, 9, Solid(color), display: true);
        var x = r.Right - label.Width - 9;
        dc.DrawEllipse(ring, null, new Point(x - 7, r.Y + edge + 3 + label.Height / 2), 3, 3);
        Draw(dc, label, new Point(x, r.Y + edge + 3));
    }

    /// <summary>A small push-pin: round head with a needle, pointing down-left.</summary>
    private static void DrawPin(DrawingContext dc, Point head, IBrush brush)
    {
        dc.DrawLine(new Pen(brush, 1.6, lineCap: PenLineCap.Round), new Point(head.X - 1, head.Y + 1), new Point(head.X - 5, head.Y + 5));
        dc.DrawEllipse(brush, new Pen(new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), 0.8), new Point(head.X + 1, head.Y - 1), 3.2, 3.2);
    }

    private static void Draw(DrawingContext dc, FormattedText text, Point at, bool shadow = false)
    {
        if (shadow && text.BuildGeometry(new Point(at.X, at.Y + 1)) is { } dark)
            dc.DrawGeometry(Brushes.Black, null, dark);
        dc.DrawText(text, at);
    }
}
