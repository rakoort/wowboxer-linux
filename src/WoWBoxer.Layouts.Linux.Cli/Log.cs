using System.Text;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.Cli;

/// <summary>Writes to the console (with colour) and to a local log file. Nothing leaves the PC.</summary>
internal sealed class Log : IDisposable, IStatusSink
{
    public void Report(StatusLevel level, string message) => Line(message, level switch
    {
        StatusLevel.Ok => ConsoleColor.Green,
        StatusLevel.Warn => ConsoleColor.Yellow,
        StatusLevel.Error => ConsoleColor.Red,
        StatusLevel.Detail => ConsoleColor.DarkGray,
        StatusLevel.Title => ConsoleColor.White,
        _ => ConsoleColor.Gray,
    });

    private readonly StreamWriter? _file;
    private readonly string _userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string? FilePath { get; }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WoWBoxer", "Layouts", "logs");

    public Log(string fileName)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            FilePath = Path.Combine(Folder, fileName);
            _file = new StreamWriter(FilePath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (IOException)
        {
            FilePath = null;   // logging is best effort; the console still works
        }
        catch (UnauthorizedAccessException)
        {
            FilePath = null;
        }
    }

    public void Line(string text = "", ConsoleColor color = ConsoleColor.Gray)
    {
        // Keep user names out of logs that people send us.
        text = text.Replace(_userProfile, "~", StringComparison.Ordinal);
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text.Length == 0 ? "" : "  " + text);
        Console.ForegroundColor = previous;
        _file?.WriteLine(text.Length == 0 ? "" : "  " + text);
    }

    public void Info(string text) => Line(text);
    public void Ok(string text) => Line(text, ConsoleColor.Green);
    public void Warn(string text) => Line(text, ConsoleColor.Yellow);
    public void Error(string text) => Line(text, ConsoleColor.Red);
    public void Title(string text) => Line(text, ConsoleColor.White);
    public void Detail(string text) => Line(text, ConsoleColor.DarkGray);
    public void Highlight(string text) => Line(text, ConsoleColor.Cyan);

    public void Dispose() => _file?.Dispose();
}
