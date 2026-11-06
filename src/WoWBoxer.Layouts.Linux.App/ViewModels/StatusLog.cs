using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.App.ViewModels;

public sealed record StatusLine(DateTime Time, StatusLevel Level, string Message)
{
    private static readonly IBrush WarnBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xF5, 0xC8, 0x6A)),
        ErrorBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A)),
        InfoBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xC8, 0xCE, 0xDA));

    public string TimeText => Time.ToString("HH:mm:ss");

    public IBrush Brush => Level switch
    {
        StatusLevel.Ok => Brushes.LightGreen,
        StatusLevel.Warn => WarnBrush,
        StatusLevel.Error => ErrorBrush,
        StatusLevel.Detail => Brushes.Gray,
        StatusLevel.Title => Brushes.White,
        _ => InfoBrush,
    };
}

/// <summary>Status lines for the UI (thread-safe) and a local log file. Nothing leaves the PC.</summary>
public sealed class StatusLog : IStatusSink
{
    private const int MaxLines = 400;
    private readonly string _userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly object _fileLock = new();

    public ObservableCollection<StatusLine> Lines { get; } = [];

    public static string LogFolder => Demo.On ? Path.Combine(Demo.Root, "logs") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WoWBoxer", AppFlavor.DataFolder, "logs");
    public static string LogFile => Path.Combine(LogFolder, "app.log");

    public void Report(StatusLevel level, string message)
    {
        if (_userProfile.Length > 1) message = message.Replace(_userProfile, "~", StringComparison.Ordinal);
        var line = new StatusLine(Demo.Now(), level, message);
        WriteFile(line);
        if (Avalonia.Application.Current is null) return;
        var dispatcher = Dispatcher.UIThread;
        if (dispatcher.CheckAccess()) Add(line);
        else dispatcher.Post(() => Add(line));
    }

    private void Add(StatusLine line)
    {
        Lines.Add(line);
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
    }

    private void WriteFile(StatusLine line)
    {
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(LogFolder);
                File.AppendAllText(LogFile, $"{line.Time:yyyy-MM-dd HH:mm:ss} {line.Level,-6} {line.Message}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the log is best effort
        }
    }
}
