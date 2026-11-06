using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace WoWBoxer.Layouts.App;

/// <summary>
/// <c>app:SliderCommit.Command</c>: runs a command when a slider is let go (mouse up after a drag or click, or a key),
/// so a setting is written once on release instead of on every step of the drag. A value set without the mouse
/// (screen readers, UI Automation) is committed once it has rested for a moment.
/// </summary>
public static class SliderCommit
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Slider, ICommand?>("Command", typeof(SliderCommit));

    public static ICommand? GetCommand(Slider slider) => slider.GetValue(CommandProperty);
    public static void SetCommand(Slider slider, ICommand? value) => slider.SetValue(CommandProperty, value);

    static SliderCommit() => CommandProperty.Changed.AddClassHandler<Slider>(OnCommandChanged);

    private static void OnCommandChanged(Slider slider, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is not null) return;
        var pressed = false;
        // handledEventsToo: the thumb and the track mark the pointer events as handled.
        slider.AddHandler(InputElement.PointerPressedEvent, (_, _) => pressed = true, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        slider.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
        {
            pressed = false;
            Run(slider);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        slider.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => pressed = false, RoutingStrategies.Bubble, handledEventsToo: true);
        slider.KeyUp += (_, _) => Run(slider);
        var rest = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        rest.Tick += (_, _) =>
        {
            rest.Stop();
            Run(slider);
        };
        slider.AddHandler(RangeBase.ValueChangedEvent, (_, _) =>
        {
            if (pressed) return;   // a drag commits on mouse up
            rest.Stop();
            rest.Start();
        });
    }

    private static void Run(Slider slider)
    {
        if (GetCommand(slider) is { } command && command.CanExecute(null)) command.Execute(null);
    }
}
