namespace WoWBoxer.Layouts.Linux;

public enum StatusLevel { Info, Ok, Warn, Error, Detail, Title }

/// <summary>Where the squad engine reports what it does (console for wbx, status list for the app).</summary>
public interface IStatusSink
{
    void Report(StatusLevel level, string message);
}

public static class StatusSinkExtensions
{
    public static void Info(this IStatusSink sink, string message) => sink.Report(StatusLevel.Info, message);
    public static void Ok(this IStatusSink sink, string message) => sink.Report(StatusLevel.Ok, message);
    public static void Warn(this IStatusSink sink, string message) => sink.Report(StatusLevel.Warn, message);
    public static void Detail(this IStatusSink sink, string message) => sink.Report(StatusLevel.Detail, message);
    public static void Title(this IStatusSink sink, string message) => sink.Report(StatusLevel.Title, message);
}
