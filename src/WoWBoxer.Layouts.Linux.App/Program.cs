using Avalonia;
using Avalonia.Controls;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.App;

public static class Program
{
    /// <summary>
    /// One instance per desktop session: a second start hands its arguments to the running one over D-Bus (the
    /// <c>/App</c> Args call) and exits before any window opens. Demo and screenshot runs never touch the bus.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (!App.IsDemo(args))
        {
            App.Link = AgentLink.StartAsync(args).GetAwaiter().GetResult();
            if (App.Link == AgentLinkStart.OtherInstance) return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>Also used by Avalonia's designer.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
