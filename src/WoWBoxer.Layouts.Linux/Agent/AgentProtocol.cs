using System.Text.Json;
using System.Text.Json.Serialization;
using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux.Agent;

// The JSON of the agent protocol, version 1 (docs/linux/port-plan.md, "The agent protocol").

/// <summary>A KWin rectangle. KWin uses fractional logical pixels; the port rounds them to whole pixels.</summary>
internal sealed record AgentRectF(double X, double Y, double Width, double Height)
{
    public PixelRect ToPixels() =>
        new((int)Math.Round(X), (int)Math.Round(Y), (int)Math.Round(Width), (int)Math.Round(Height));
}

/// <summary>A rectangle the app sends (whole logical pixels).</summary>
internal sealed record AgentRect(int X, int Y, int Width, int Height)
{
    public static AgentRect From(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);
}

/// <summary>The agent's argument to <c>Pull</c>.</summary>
internal sealed record AgentHello
{
    public string Agent { get; init; } = "";
    public string? Epoch { get; init; }
    public long Done { get; init; }
}

internal sealed record AgentOutput
{
    public string Name { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public double Scale { get; init; } = 1;
    public AgentRectF Geometry { get; init; } = new(0, 0, 0, 0);
    public AgentRectF WorkArea { get; init; } = new(0, 0, 0, 0);
}

internal sealed record AgentWindow
{
    public string Id { get; init; } = "";
    public int Pid { get; init; }
    public string Caption { get; init; } = "";
    public string ResourceClass { get; init; } = "";
    public string ResourceName { get; init; } = "";
    public AgentRectF Frame { get; init; } = new(0, 0, 0, 0);
    public AgentRectF Client { get; init; } = new(0, 0, 0, 0);
    public bool KeepAbove { get; init; }
    public bool NoBorder { get; init; }
    public bool Minimized { get; init; }
    public bool FullScreen { get; init; }
    public bool Moving { get; init; }
    public string? Output { get; init; }
    public int Stack { get; init; }
}

internal sealed record AgentHandEdit
{
    public string Id { get; init; } = "";
    public AgentRectF Frame { get; init; } = new(0, 0, 0, 0);
}

/// <summary>The agent's argument to <c>Report</c>.</summary>
internal sealed record AgentReport
{
    public int V { get; init; }
    public string Agent { get; init; } = "";
    public string? Epoch { get; init; }
    public long Done { get; init; }
    public List<AgentOutput> Outputs { get; init; } = [];
    public string? Active { get; init; }
    public List<AgentWindow> Windows { get; init; } = [];
    public List<AgentHandEdit> HandEdited { get; init; } = [];
    public List<string> GaveUp { get; init; } = [];
}

/// <summary>One numbered action: <c>place</c> (with <see cref="Rect"/>), <c>above</c> or <c>border</c> (with <see cref="On"/>).</summary>
internal sealed record AgentAction(long N, string Op, string Id)
{
    public AgentRect? Rect { get; init; }
    public bool? On { get; init; }
}

internal sealed record HoverDesired(bool Enabled, int DelayMs, bool BringToFront);

/// <summary>One of the app's own helper windows (Identify), found by the app's pid and its caption.</summary>
internal sealed record OwnWindow(string Caption, AgentRect Rect, bool Above, bool NoBorder, bool SkipTaskbar);

/// <summary>The reply to <c>Pull</c>.</summary>
internal sealed record DesiredState(
    int V,
    string Epoch,
    int OwnPid,
    IReadOnlyList<AgentAction> Actions,
    IReadOnlyList<string> KeepAbove,
    IReadOnlyList<string> Wow,
    HoverDesired Hover,
    IReadOnlyList<OwnWindow> Own);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(AgentHello))]
[JsonSerializable(typeof(AgentReport))]
[JsonSerializable(typeof(DesiredState))]
internal sealed partial class AgentJson : JsonSerializerContext
{
    public const int ProtocolVersion = 1;

    public static string Serialize(DesiredState desired) => JsonSerializer.Serialize(desired, Default.DesiredState);

    /// <summary>Null for text that is not a report (the caller logs and ignores it).</summary>
    public static AgentReport? ParseReport(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, Default.AgentReport);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static AgentHello ParseHello(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, Default.AgentHello) ?? new AgentHello();
        }
        catch (JsonException)
        {
            return new AgentHello();
        }
    }
}
