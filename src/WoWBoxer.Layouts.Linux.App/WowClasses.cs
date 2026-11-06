using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace WoWBoxer.Layouts.App;

/// <summary>A WoW class for colour accents (official class colours, as on the website). Names only, no Blizzard art.</summary>
public sealed record WowClass(string Id, string Name, Color Color)
{
    public IImmutableSolidColorBrush Brush { get; } = new ImmutableSolidColorBrush(Color);

    public override string ToString() => Name;
}

public static class WowClasses
{
    /// <summary>"No class set": shown as unknown in a neutral colour – the app never guesses a class.</summary>
    public static readonly WowClass Automatic = new("", "Unknown", Color.FromRgb(0x7A, 0x84, 0x99));

    public static readonly IReadOnlyList<WowClass> All =
    [
        new("warrior", "Warrior", Color.FromRgb(0xC6, 0x9B, 0x6D)),
        new("paladin", "Paladin", Color.FromRgb(0xF4, 0x8C, 0xBA)),
        new("hunter", "Hunter", Color.FromRgb(0xAA, 0xD3, 0x72)),
        new("rogue", "Rogue", Color.FromRgb(0xFF, 0xF4, 0x68)),
        new("priest", "Priest", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        new("shaman", "Shaman", Color.FromRgb(0x00, 0x70, 0xDD)),
        new("mage", "Mage", Color.FromRgb(0x3F, 0xC7, 0xEB)),
        new("warlock", "Warlock", Color.FromRgb(0x87, 0x88, 0xEE)),
        new("druid", "Druid", Color.FromRgb(0xFF, 0x7C, 0x0A)),
    ];

    public static WowClass? Find(string? id) => All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The slot's class colour, or the neutral "unknown" colour when no class is set.</summary>
    public static Color ColorFor(string? id) => (Find(id) ?? Automatic).Color;
}
