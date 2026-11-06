using Tmds.DBus.Protocol;

namespace WoWBoxer.Layouts.Linux.Agent;

/// <summary>Small helpers for calling D-Bus methods with Tmds.DBus.Protocol.</summary>
internal static class Bus
{
    public delegate void WriteArgs(ref MessageWriter writer);

    public static Task<T> CallAsync<T>(DBusConnection connection, string destination, string path, string iface, string member,
        string? signature, WriteArgs? write, MessageValueReader<T> read) =>
        connection.CallMethodAsync(Build(connection, destination, path, iface, member, signature, write), read, null);

    public static Task CallAsync(DBusConnection connection, string destination, string path, string iface, string member,
        string? signature, WriteArgs? write) =>
        connection.CallMethodAsync(Build(connection, destination, path, iface, member, signature, write));

    private static MessageBuffer Build(DBusConnection connection, string destination, string path, string iface, string member,
        string? signature, WriteArgs? write)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, iface, member, signature, MessageFlags.None);
            write?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    public static string ReadString(Message message, object? _) => message.GetBodyReader().ReadString();

    public static bool ReadBool(Message message, object? _) => message.GetBodyReader().ReadBool();

    public static int ReadInt32(Message message, object? _) => message.GetBodyReader().ReadInt32();

    /// <summary>The D-Bus error name of a failed call ("" for other failures).</summary>
    public static string ErrorName(Exception e) => e is DBusErrorReplyException reply ? reply.ErrorName : "";
}
