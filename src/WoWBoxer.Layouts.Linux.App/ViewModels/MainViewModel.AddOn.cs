using Avalonia.Threading;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.App.ViewModels;

/// <summary>
/// The WoWBoxer AddOn bridge (docs/classes-and-accounts.md; built in the Linux port). The AddOn writes the logged-in
/// character into its account's SavedVariables/WoWBoxer.lua on logout and /reload. A squad row whose WoW account is
/// chosen takes that character's name and class – through the normal slot edit, so Ctrl+Z undoes it – when the AddOn
/// saved it after the row's name or class was last set by hand. Which account plays in which window stays the user's
/// choice; nothing is read from the game itself.
/// </summary>
public sealed partial class MainViewModel
{
    private AddonBridgeWatcher? _addonWatcher;
    private string? _addonInstall;
    private bool _applyingAddOn;
    /// <summary>The AddOn's last character per WoW account (accounts the rows use), as read from the files.</summary>
    private readonly Dictionary<string, AddonCharacter?> _addonByAccount = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Watches the squad install's WoWBoxer.lua files; follows the squad to another install.</summary>
    private void WatchAddOn()
    {
        var install = Demo.On ? null : GameInstall.FolderOf(SquadExe);
        if (string.Equals(install, _addonInstall, StringComparison.Ordinal)) return;
        _addonWatcher?.Dispose();
        _addonWatcher = null;
        _addonInstall = install;
        _addonByAccount.Clear();
        if (install is null) return;
        _addonWatcher = new AddonBridgeWatcher(install);
        _addonWatcher.Changed += _ => Dispatcher.UIThread.Post(ApplyAddOn);
    }

    /// <summary>Reads the AddOn's character for every row's account and fills in the rows it is newer for.</summary>
    private void ApplyAddOn()
    {
        WatchAddOn();
        if (Current is null || _applyingAddOn) return;
        _applyingAddOn = true;
        try
        {
            _addonByAccount.Clear();
            for (var i = 0; i < Party.Count && i < Current.Slots.Count; i++)
            {
                var account = i < _settings.Accounts.Count ? _settings.Accounts[i] : "";
                if (account.Length == 0 || _addonInstall is null) continue;
                if (!_addonByAccount.TryGetValue(account, out var character))
                    _addonByAccount[account] = character = AddonBridge.Read(_addonInstall, account);
                if (character is null || character.SavedAt.ToUnixTimeSeconds() <= SlotEditedAt(i)) continue;
                var cls = WowClasses.Find(character.Class);
                var slot = Placed!.Slots[i];
                if (slot.Name == character.Character && (cls is null || slot.Class == cls.Id)) continue;
                EditSlotAt(i, s => s with { Name = character.Character, Class = cls?.Id ?? s.Class });
                Status.Info($"Window {i + 1}: {character.Character}{(cls is null ? "" : $" ({cls.Name})")} {PartyMember.AddOnText}.");
            }
        }
        finally
        {
            _applyingAddOn = false;
        }
        MarkAddOnRows();
    }

    /// <summary>The "from the WoWBoxer AddOn" note: on while a row still shows the AddOn's character.</summary>
    private void MarkAddOnRows()
    {
        var placed = Placed;
        for (var i = 0; i < Party.Count; i++)
        {
            var account = i < _settings.Accounts.Count ? _settings.Accounts[i] : "";
            var slot = placed?.Slots.ElementAtOrDefault(i);
            Party[i].FromAddOn = slot is not null && account.Length > 0 && _addonByAccount.GetValueOrDefault(account) is { } c
                && slot.Name == c.Character && (WowClasses.Find(c.Class) is not { } cls || slot.Class == cls.Id);
        }
    }

    /// <summary>When row <paramref name="index"/>'s name or class was last set by hand (unix seconds; 0 = never).</summary>
    private long SlotEditedAt(int index) => index < _settings.SlotEditedAt.Count ? _settings.SlotEditedAt[index] : 0;

    /// <summary>The user set row <paramref name="index"/>'s name or class: an older AddOn character no longer replaces it.</summary>
    private void MarkSlotEdited(int index)
    {
        if (index < 0 || _applyingAddOn) return;
        while (_settings.SlotEditedAt.Count <= index) _settings.SlotEditedAt.Add(0);
        _settings.SlotEditedAt[index] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _settings.Save();
    }

    /// <summary>Squad drag and drop: the hand-edit times move with the characters.</summary>
    private void MoveSlotEdited(int from, int to, int count)
    {
        while (_settings.SlotEditedAt.Count < count) _settings.SlotEditedAt.Add(0);
        var at = _settings.SlotEditedAt[from];
        _settings.SlotEditedAt.RemoveAt(from);
        _settings.SlotEditedAt.Insert(to, at);
    }
}
