using System.ComponentModel;
using System.Diagnostics;

namespace WoWBoxer.Layouts.App.Infrastructure;

/// <summary>Opens a web page or a folder with the desktop's default program (<c>xdg-open</c>; <c>open</c> on a Mac dev box).</summary>
public static class Shell
{
    /// <summary>Opens http(s) links only; anything else is ignored.</summary>
    public static void OpenUrl(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        Start(uri.AbsoluteUri);
    }

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Start(folder);
    }

    private static void Start(string target)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
        info.ArgumentList.Add(target);
        try
        {
            Process.Start(info)?.Dispose();
        }
        catch (Win32Exception)
        {
            // no opener installed: nothing to open it with
        }
    }
}
