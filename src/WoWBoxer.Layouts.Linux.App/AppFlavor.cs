namespace WoWBoxer.Layouts.App;

/// <summary>
/// Normal app or "WoWBoxer Dev" – a local test build (<c>dotnet build -p:WbxDev=true</c>). The dev build keeps its own
/// settings, layouts and logs (~/.config/WoWBoxer/Dev, ~/.local/share/WoWBoxer/Dev) and shows a DEV badge in the title
/// bar. Unlike on Windows it shares the single-instance name with the normal app: both would drive the same KWin
/// script, so only one of them runs at a time.
/// </summary>
internal static class AppFlavor
{
#if WBX_DEV
    public static readonly bool IsDev = true;
#else
    public static readonly bool IsDev = false;
#endif

    /// <summary>The name shown in the title and the tray.</summary>
    public static string Name => IsDev ? "WoWBoxer Dev" : "WoWBoxer";

    /// <summary>The folder under ~/.config/WoWBoxer and ~/.local/share/WoWBoxer ("Layouts" is the normal app's, as upstream).</summary>
    public static string DataFolder => IsDev ? "Dev" : "Layouts";
}
