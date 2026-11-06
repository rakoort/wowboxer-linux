using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WoWBoxer.Layouts.Linux;

/// <summary>
/// Retitles a game's XWayland window with <c>xprop</c> (on PATH; <c>DISPLAY</c>, else ":0"). KWin offers no way to
/// set another program's caption, and Wine windows are X11 windows, so this is the Linux counterpart of WM_SETTEXT.
/// Sets nothing but the two title properties.
/// </summary>
internal static partial class Xprop
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    public static bool SetTitle(int pid, string currentCaption, string title)
    {
        if (FindWindow(pid, currentCaption) is not { } xid) return false;
        return Run(["-id", xid, "-f", "_NET_WM_NAME", "8u", "-set", "_NET_WM_NAME", title], out _)
            && Run(["-id", xid, "-f", "WM_NAME", "8u", "-set", "WM_NAME", title], out _);
    }

    /// <summary>The X window id of a client of <paramref name="pid"/>; the one titled <paramref name="caption"/> if there are several.</summary>
    private static string? FindWindow(int pid, string caption)
    {
        if (!Run(["-root", "_NET_CLIENT_LIST"], out var list)) return null;
        string? first = null;
        foreach (Match m in WindowId().Matches(list))
        {
            var xid = m.Value;
            if (!Run(["-id", xid, "_NET_WM_PID", "_NET_WM_NAME"], out var props)) continue;
            var pidMatch = PidLine().Match(props);
            if (!pidMatch.Success || int.Parse(pidMatch.Groups[1].Value, CultureInfo.InvariantCulture) != pid) continue;
            if (props.Contains($"= \"{caption}\"", StringComparison.Ordinal)) return xid;
            first ??= xid;
        }
        return first;
    }

    private static bool Run(IReadOnlyList<string> args, out string output)
    {
        output = "";
        var start = new ProcessStartInfo("xprop") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-display");
        start.ArgumentList.Add(Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } d ? d : ":0");
        foreach (var a in args) start.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(start);
            if (p is null) return false;
            var read = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(Timeout))
            {
                p.Kill();
                return false;
            }
            output = read.Result;
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;   // xprop not installed
        }
    }

    [GeneratedRegex(@"0x[0-9a-fA-F]+")]
    private static partial Regex WindowId();

    [GeneratedRegex(@"_NET_WM_PID\(CARDINAL\) = (\d+)")]
    private static partial Regex PidLine();
}
