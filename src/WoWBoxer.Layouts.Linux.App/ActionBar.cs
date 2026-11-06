using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace WoWBoxer.Layouts.App;

/// <summary>
/// How much of Classic's main action bar a window shows. Every method takes the *effective* aspect
/// (game aspect / UI Scale, see <c>Core.UiScale</c>) (docs/wow-display-findings.md): the bar is 1024 UI units wide
/// and the UI 768 high, so it is complete at 4:3; down to about 1.26 only the gryphon ends are clipped.
/// </summary>
public static class ActionBar
{
    public static readonly Color Full = Color.FromRgb(0x8C, 0xCB, 0x7A), Gryphons = Color.FromRgb(0xE6, 0xB4, 0x55), Clipped = Color.FromRgb(0xE3, 0x7A, 0x7E);

    public static Color ColorFor(double gameAspect) => gameAspect >= 4.0 / 3 - 0.001 ? Full : gameAspect >= Core.UiScale.GryphonsOnly ? Gryphons : Clipped;

    public static IBrush Brush(double gameAspect) => new ImmutableSolidColorBrush(ColorFor(gameAspect));

    public static string Verdict(double gameAspect) => Core.UiScale.BarVerdict(gameAspect);

    public static string Short(double gameAspect) => gameAspect >= 4.0 / 3 - 0.001 ? "Full bar" : gameAspect >= Core.UiScale.GryphonsOnly ? "Gryphons clipped" : "Bar clipped";
}
