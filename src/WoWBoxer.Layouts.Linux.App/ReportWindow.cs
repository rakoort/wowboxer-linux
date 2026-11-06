using System.IO;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.App;

/// <summary>
/// "Report a problem": the report the app built (personal names hidden), a "What happened?" box, and Post in Discord –
/// which copies the message to the clipboard, saves the full report as a file and opens the help channel's invite. The
/// user sees and may edit everything first; nothing is ever posted or uploaded by the app.
/// </summary>
internal static class ReportWindow
{
    /// <param name="report">The short report (already redacted); editable in the window.</param>
    /// <param name="saveFull">Saves the full report (also redacted) and returns its path.</param>
    /// <param name="openUrl">Opens the help invite in the browser.</param>
    public static void Show(Window owner, string report, Func<string, string> saveFull, string helpUrl, Action<string> openUrl)
    {
        IBrush? R(string key) => Dialogs.Resource<IBrush>(key);

        var what = new TextBox { MinHeight = 54, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12), MaxLength = 600 };
        AutomationProperties.SetAutomationId(what, "ReportWhat");
        AutomationProperties.SetName(what, "What happened?");
        var text = new TextBox
        {
            Text = report,
            AcceptsReturn = true,
            FontFamily = Dialogs.Resource<FontFamily>("MonoFont") ?? FontFamily.Default,
            FontSize = 11.5,
            Height = 300,
            Margin = new Thickness(0, 4, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(text, "ReportText");
        AutomationProperties.SetName(text, "Report");

        var doneText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = R("OkBrush") };
        var done = new StackPanel { Margin = new Thickness(0, 12, 0, 0), IsVisible = false, Children = { doneText } };
        AutomationProperties.SetAutomationId(done, "ReportDone");
        var post = new Button { Content = new TextBlock { Text = "POST IN DISCORD" }, IsDefault = true, Padding = new Thickness(20, 7, 20, 7), Margin = new Thickness(0, 0, 10, 0) };
        if (Dialogs.Resource<ControlTheme>("EnterWorldButton") is { } enter) post.Theme = enter;
        AutomationProperties.SetAutomationId(post, "ReportPost");
        AutomationProperties.SetName(post, "Post in Discord");
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 90, VerticalAlignment = VerticalAlignment.Center };

        var body = new DockPanel { Width = 640 };
        var intro = Dialogs.Text("Ask in the WoWBoxer Discord (#setup-help). Below is what the app sees, with your user name, home folder, WoW account and character names hidden. Read it and change anything you like – nothing leaves your PC until you paste it yourself.",
            "Body", new Thickness(0, 6, 0, 12));
        var top = new StackPanel
        {
            Children =
            {
                Dialogs.Text("Report a problem", "H2", default),
                intro,
                new TextBlock { Text = "What happened?", Foreground = R("ParchmentBrush"), FontSize = 13 },
                what,
                new TextBlock { Text = "The report", Foreground = R("ParchmentBrush"), FontSize = 13 },
            },
        };
        var bottom = new StackPanel
        {
            Children =
            {
                done,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { post, close } },
            },
        };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(top);
        body.Children.Add(bottom);
        body.Children.Add(text);

        var visibleOwner = owner.IsVisible ? owner : null;
        var window = Dialogs.Frame(visibleOwner, "Report a problem", body);
        var workArea = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
        if (workArea is not null) window.MaxHeight = workArea.WorkingArea.Height / owner.DesktopScaling - 20;
        AutomationProperties.SetAutomationId(window, "ReportWindow");

        close.Click += (_, _) => window.Close();
        post.Click += async (_, _) =>
        {
            var message = ProblemReport.ForDiscord(what.Text ?? "", text.Text ?? "");
            string? file = null;
            try
            {
                file = saveFull(text.Text ?? "");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the clipboard message still works without the file
            }
            if (window.Clipboard is not { } clipboard)
            {
                doneText.Text = "The clipboard is busy – press Post in Discord again.";
                done.IsVisible = true;
                return;
            }
            await clipboard.SetTextAsync(message);
            openUrl(helpUrl);
            doneText.Text = "Copied. Paste with Ctrl+V in #setup-help and press Enter.";
            while (done.Children.Count > 1) done.Children.RemoveAt(1);
            if (file is not null)
            {
                done.Children.Add(new TextBlock { Text = "If someone asks for more: drag the full report into Discord.", Foreground = R("MutedBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
                var link = new Button { Content = "Show the full report file", Foreground = R("GoldHiBrush"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
                if (Dialogs.Resource<ControlTheme>("Hyperlink") is { } hyperlink) link.Theme = hyperlink;
                link.Click += (_, _) => Infrastructure.Shell.OpenFolder(Path.GetDirectoryName(file)!);
                done.Children.Add(link);
            }
            done.IsVisible = true;
        };
        window.Opened += (_, _) => what.Focus();
        Dialogs.ShowModal(window, visibleOwner);
    }
}
