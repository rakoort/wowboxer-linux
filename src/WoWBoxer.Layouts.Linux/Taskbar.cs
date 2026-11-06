namespace WoWBoxer.Layouts.Linux;

/// <summary>Plasma's panels. KWin's work area already leaves visible panels out, so there is no auto-hide case to detect.</summary>
public static class Taskbar
{
    public static bool AutoHides() => false;
}
