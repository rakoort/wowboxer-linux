using Avalonia.Threading;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.App;

/// <summary>
/// Owns the window-swap hotkeys: KWin global shortcuts that WoWBoxer's KWin script registers, given exactly the keys
/// passed in (all of them are cleared again when swap is turned off or the app quits). KWin takes such a key press for
/// itself, so the game never receives it; the script reports it and <paramref name="onAction"/> runs on the UI thread.
/// Nothing else is watched or sent.
/// </summary>
internal sealed class SwapHotkeyHost : IDisposable
{
    private readonly Action<string> _onAction;

    public SwapHotkeyHost(Action<string> onAction)
    {
        _onAction = onAction;
        GlobalHotkeys.Pressed += OnPressed;
    }

    /// <summary>The actions whose hotkey is set now.</summary>
    public IReadOnlyCollection<string> Active => GlobalHotkeys.Active;

    /// <summary>
    /// Replaces the hotkeys. Returns the ones KDE refused, with the reason (another shortcut uses it, …).
    /// </summary>
    public Task<IReadOnlyDictionary<string, string>> ApplyAsync(IReadOnlyDictionary<string, Hotkey> keys) => GlobalHotkeys.ApplyAsync(keys);

    public Task ClearAsync() => GlobalHotkeys.ClearAsync();

    private void OnPressed(string action) => Dispatcher.UIThread.Post(() => _onAction(action));

    /// <summary>Stops listening. The keys themselves are cleared by <see cref="ClearAsync"/> or when the app unloads its KWin script.</summary>
    public void Dispose() => GlobalHotkeys.Pressed -= OnPressed;
}
