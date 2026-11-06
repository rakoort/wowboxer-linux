using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using WoWBoxer.Layouts.App.ViewModels;

namespace WoWBoxer.Layouts.App.Views;

public partial class SetupView : UserControl
{
    /// <summary>How far the pointer moves before a press on ⋮⋮ becomes a drag (the usual system drag distance).</summary>
    private const double DragDistance = 4;

    private Point _dragStart;
    private PartyMember? _pressed;

    /// <summary>True while a squad row is being dragged (the window's live refresh pauses meanwhile).</summary>
    public static bool Dragging { get; private set; }

    public SetupView()
    {
        InitializeComponent();
        Canvas.SlotEdited += (_, e) => (DataContext as MainViewModel)?.OnSlotEdited(e.Index, e.Slot);
        Canvas.SlotsSwapped += (_, e) => (DataContext as MainViewModel)?.SwapSlots(e.A, e.B);
        Canvas.MainSlotClicked += (_, i) => (DataContext as MainViewModel)?.ToggleMainSlot(i);
        Canvas.SlotsEdited += (_, edits) => (DataContext as MainViewModel)?.ApplyRects(edits, -1);
        Canvas.AreaPicked += (_, area) => (DataContext as MainViewModel)?.OnAreaPicked(area);
        Canvas.FillCancelRequested += (_, _) => (DataContext as MainViewModel)?.CancelFillCommand.Execute(null);
        PartyList.AddHandler(GotFocusEvent, OnRowFocus, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The saved layout's "⋯" opens its menu on a normal click.</summary>
    private void OnMore(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.DataContext = DataContext;
            menu.Open(button);
        }
    }

    /// <summary>
    /// Typing in a row's name box (or tabbing into its details) selects that row and its window on the map – and keeps
    /// it selected when the focus moves on.
    /// </summary>
    private void OnRowFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is Visual element && element.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: PartyMember row }
            && DataContext is MainViewModel vm && vm.SelectedSlotIndex != row.Index)
            vm.SelectedSlotIndex = row.Index;
    }

    // ---------- drag squad rows by their handle to reorder (rows, map and real windows follow) ----------

    private void OnHandleDown(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
        _dragStart = e.GetPosition(this);
        _pressed = handle.DataContext as PartyMember;
        // The list would take the press to select the row, and then the handle never sees the drag move.
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnHandleMove(object? sender, PointerEventArgs e)
    {
        if (_pressed is null || DataContext is not MainViewModel vm) return;
        var d = e.GetPosition(this) - _dragStart;
        if (!Dragging && Math.Abs(d.X) < DragDistance && Math.Abs(d.Y) < DragDistance) return;
        Dragging = true;   // keep the list still while dragging
        var target = RowAt(e.GetPosition(PartyList));
        foreach (var p in vm.Party) p.IsDropTarget = p == target && target != _pressed;
    }

    private void OnHandleUp(object? sender, PointerReleasedEventArgs e)
    {
        var row = _pressed;
        var dropped = Dragging ? RowAt(e.GetPosition(PartyList)) : null;
        EndDrag();
        e.Pointer.Capture(null);
        if (row is null || DataContext is not MainViewModel vm) return;
        if (dropped is not null && dropped != row)
        {
            vm.MoveCharacter(row.Index, dropped.Index);
            vm.SelectedSlotIndex = dropped.Index;   // the moved character stays selected
        }
        else vm.SelectedSlotIndex = row.Index;   // a click on ⋮⋮ selects the row
    }

    private void OnHandleCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _pressed = null;
        Dragging = false;
        if (DataContext is MainViewModel vm) foreach (var p in vm.Party) p.IsDropTarget = false;
    }

    /// <summary>The squad row under <paramref name="point"/> (in the list's coordinates), if any.</summary>
    private PartyMember? RowAt(Point point) =>
        PartyList.InputHitTest(point) is Visual hit && hit.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: PartyMember row } ? row : null;
}
