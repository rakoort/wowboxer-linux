using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using WoWBoxer.Layouts.App.ViewModels;

namespace WoWBoxer.Layouts.App;

/// <summary>First start: what the app does and never does (docs/plan.md §2), in the War Table style.</summary>
internal static class FirstRunWindow
{
    /// <summary>"Join the WoWBoxer Discord – tips, layouts and help" as a link (first-run screen, What's new). Never a pop-up.</summary>
    public static Button DiscordLink()
    {
        var link = Dialogs.Link("Join the WoWBoxer Discord – tips, layouts and help", Community.Links.Discord);
        link.Foreground = Dialogs.Resource<IBrush>("GoldHiBrush");
        return link;
    }

    public static void Show(Window? owner) => Dialogs.ShowModal(Create(owner), owner);

    /// <summary>The welcome window, not shown yet (the screenshots render it off-screen).</summary>
    public static Window Create(Window? owner)
    {
        IBrush? R(string key) => Dialogs.Resource<IBrush>(key);

        var go = new Button
        {
            Content = new TextBlock { Text = "GOT IT – LET'S SET UP" },
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 0),
        };
        if (Dialogs.Resource<ControlTheme>("EnterWorldButton") is { } enter) go.Theme = enter;
        var guide = Dialogs.Link("Read the multiboxing rules and the full guide", MainViewModel.WebsiteGuideUrl);

        TextBlock Line(string text, bool good) => new()
        {
            Text = (good ? "✓   " : "✗   ") + text,
            Foreground = good ? R("OkBrush") : R("NoBrush"),
            Margin = new Thickness(0, 4, 0, 4),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };
        TextBlock Head(string text) => Dialogs.Text(text, "H2", new Thickness(0, 0, 0, 6));

        var columns = new Grid { Margin = new Thickness(0, 22, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        columns.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(28)));
        columns.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var does = new StackPanel { Children = { Head("What it does"),
            Line("Starts Battle.net and places each WoW window in your layout as it opens.", true),
            Line("Moves, resizes and restyles windows – nothing else.", true),
            Line("Keeps your layouts and logs on your PC. No telemetry.", true) } };
        var never = new StackPanel { Children = { Head("What it never does"),
            Line("Never sends keys or mouse clicks to the game or broadcasts input.", false),
            Line("Never reads game memory, injects code or logs in for you.", false),
            Line("Never takes focus: you press Play and play every character yourself.", false) } };
        Grid.SetColumn(never, 2);
        columns.Children.Add(does);
        columns.Children.Add(never);

        var title = Dialogs.Text("Welcome, multiboxer", "H1", new Thickness(0, 12, 0, 0));
        title.FontSize = 30;
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var divider = new ContentControl { Margin = new Thickness(120, 20, 120, 0) };
        if (Dialogs.Resource<ControlTheme>("Divider") is { } line) divider.Theme = line;
        guide.FontSize = 13.5;
        guide.Margin = new Thickness(0, 18, 0, 0);
        var discord = DiscordLink();
        discord.FontSize = 13.5;
        discord.Margin = new Thickness(0, 6, 0, 0);
        var content = new StackPanel
        {
            Children =
            {
                new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://wowboxer/Assets/app.png"))), Width = 84, Height = 84, HorizontalAlignment = HorizontalAlignment.Center },
                title,
                new TextBlock { Text = "Your WoW windows, where you want them – one key press, one action, one character.", Foreground = R("MutedBrush"), FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) },
                divider,
                columns,
                guide,
                discord,
                go,
            },
        };
        var window = Dialogs.Chrome(owner, "Welcome to WoWBoxer", Dialogs.Ornate(content, new Thickness(40, 32, 40, 32)), "WarTableBrush", 14, 12, 60);
        window.Width = 820;
        window.SizeToContent = SizeToContent.Height;
        window.ShowInTaskbar = true;
        go.Click += (_, _) => window.Close();
        return window;
    }
}
