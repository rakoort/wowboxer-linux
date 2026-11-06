using System.IO.Pipes;
using Tmds.DBus.Protocol;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux.Agent;

namespace WoWBoxer.Layouts.Linux;

/// <summary>
/// Read-only picture of a window's client area (what the game draws), for checking layouts without asking the user.
/// Uses KWin's <c>org.kde.KWin.ScreenShot2.CaptureWindow</c>. KWin only answers programs whose desktop file lists
/// <c>X-KDE-DBUS-Restricted-Interfaces=org.kde.KWin.ScreenShot2</c>; anything else gets null (and
/// <see cref="LastError"/> says why).
/// </summary>
public static class WindowCapture
{
    public sealed record Snapshot(int Width, int Height, byte[] Bgra, string Method);

    /// <summary>Why the last capture failed (null after a success).</summary>
    public static string? LastError { get; private set; }

    private const uint FormatArgb32 = 5, FormatArgb32Premultiplied = 6, FormatRgb32 = 4,
        FormatRgbx8888 = 16, FormatRgba8888 = 17, FormatRgba8888Premultiplied = 18;

    public static Snapshot? Capture(nint h) => Task.Run(() => CaptureAsync(h)).GetAwaiter().GetResult();

    public static async Task<Snapshot?> CaptureAsync(nint h)
    {
        LastError = null;
        if (AgentLink.Connection is not { } c || AgentLink.State.IdOf(h) is not { } id)
        {
            LastError = "the window is not known to KWin";
            return null;
        }
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        Dictionary<string, VariantValue> results;
        var data = new MemoryStream();
        var reading = pipe.CopyToAsync(data);   // KWin writes the pixels after replying; read at once so the pipe never fills
        try
        {
            results = await Bus.CallAsync(c, "org.kde.KWin", "/org/kde/KWin/ScreenShot2", "org.kde.KWin.ScreenShot2", "CaptureWindow", "sa{sv}h",
                (ref MessageWriter w) =>
                {
                    w.WriteString(id);
                    var dict = w.WriteDictionaryStart();
                    w.WriteDictionaryEntryStart();
                    w.WriteString("include-decoration");
                    w.WriteVariantBool(false);
                    w.WriteDictionaryEntryStart();
                    w.WriteString("include-cursor");
                    w.WriteVariantBool(false);
                    w.WriteDictionaryEntryStart();
                    w.WriteString("native-resolution");
                    w.WriteVariantBool(true);
                    w.WriteDictionaryEnd(dict);
                    w.WriteHandle(pipe.ClientSafePipeHandle);
                },
                (m, _) => m.GetBodyReader().ReadDictionaryOfStringToVariantValue());
        }
        catch (DBusErrorReplyException e)
        {
            LastError = e.ErrorName.EndsWith("NoAuthorized", StringComparison.Ordinal)
                ? "KWin allows screenshots only for WoWBoxer's installed app (desktop file with X-KDE-DBUS-Restricted-Interfaces)"
                : $"KWin: {e.ErrorMessage}";
            return null;
        }
        finally
        {
            pipe.DisposeLocalCopyOfClientHandle();   // our copy of the write end: closed, so the read ends when KWin is done
        }
        if (await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(5))) != reading)
        {
            LastError = "KWin did not send the picture";
            return null;
        }
        uint width = Get(results, "width"), height = Get(results, "height"), stride = Get(results, "stride"), format = Get(results, "format");
        var pixels = data.ToArray();
        if (width == 0 || height == 0 || stride < width * 4 || pixels.Length < stride * height)
        {
            LastError = $"KWin sent {pixels.Length} bytes for {width}x{height}";
            return null;
        }
        if (ToBgra(pixels, (int)width, (int)height, (int)stride, format) is not { } bgra)
        {
            LastError = $"KWin sent an image format WoWBoxer does not read ({format})";
            return null;
        }
        return new Snapshot((int)width, (int)height, bgra, "KWin ScreenShot2");
    }

    public static void SavePng(Snapshot shot, string path)
    {
        using var file = File.Create(path);
        PngWriter.Write(file, shot.Width, shot.Height, shot.Bgra);
    }

    private static uint Get(Dictionary<string, VariantValue> results, string key) =>
        results.TryGetValue(key, out var v) && v.Type == VariantValueType.UInt32 ? v.GetUInt32() : 0;

    /// <summary>QImage rows (32-bit formats) as tightly packed BGRA. Null for formats we do not read.</summary>
    internal static byte[]? ToBgra(byte[] pixels, int width, int height, int stride, uint format)
    {
        var rgbaOrder = format is FormatRgbx8888 or FormatRgba8888 or FormatRgba8888Premultiplied;
        if (!rgbaOrder && format is not (FormatArgb32 or FormatArgb32Premultiplied or FormatRgb32)) return null;
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                int s = y * stride + x * 4, d = (y * width + x) * 4;
                // ARGB32 is 0xAARRGGBB in a little-endian uint: bytes B, G, R, A. RGBA8888 is bytes R, G, B, A.
                bgra[d] = pixels[s + (rgbaOrder ? 2 : 0)];
                bgra[d + 1] = pixels[s + 1];
                bgra[d + 2] = pixels[s + (rgbaOrder ? 0 : 2)];
                bgra[d + 3] = format is FormatRgb32 or FormatRgbx8888 ? (byte)255 : pixels[s + 3];
            }
        return bgra;
    }
}
