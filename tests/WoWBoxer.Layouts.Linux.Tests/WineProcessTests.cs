using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux.Tests;

public class WineProcessTests
{
    // As /proc shows a Forever client started by faugus-launcher through umu-run (prefix path made up).
    private const string Prefix = "/games/battlenet";
    private const string WowCmdline = "C:\\Program Files (x86)\\World of Warcraft\\_forever_\\Wow.exe\0-launcherlogin\0-uid\0wowf\0";
    private const string Environ = "UMU_ID=umu-default\0WINEPREFIX=/games/battlenet/pfx/\0PROTONPATH=/opt/proton/GE-Proton\0GAMEID=umu-default\0";
    private const string Cwd = "/games/battlenet/drive_c/Program Files (x86)/World of Warcraft/_forever_";
    private const string Preloader = "/opt/proton/GE-Proton/files/lib/wine/i386-unix/wine64-preloader";

    [Fact]
    public void Wow_client_from_proc()
    {
        var p = WineProcess.Parse(4242, WowCmdline, Environ, Cwd, Preloader)!;
        Assert.True(p.IsWow);
        Assert.Equal("Wow", p.Name);
        Assert.Equal(@"C:\Program Files (x86)\World of Warcraft\_forever_\Wow.exe", p.WindowsPath);
        Assert.Equal(Prefix + "/pfx", p.Prefix);
        Assert.Equal(Prefix + "/pfx/drive_c/Program Files (x86)/World of Warcraft/_forever_/Wow.exe", p.ExePath);
        Assert.Equal(WowGame.Forever, p.Game);
    }

    [Fact]
    public void Voice_proxy_with_a_relative_path_is_not_a_client()
    {
        var p = WineProcess.Parse(4243, "./Utils/WowVoiceProxy.exe\0", Environ, Cwd, Preloader)!;
        Assert.False(p.IsWow);
        Assert.Equal("WowVoiceProxy", p.Name);
        Assert.Equal(Cwd + "/Utils/WowVoiceProxy.exe", p.ExePath);
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\World of Warcraft\_classic_era_\WowClassic.exe", true)]
    [InlineData(@"C:\Program Files (x86)\World of Warcraft\_forever_\SomeNewName.exe", true)]
    [InlineData(@"C:\Program Files (x86)\World of Warcraft\World of Warcraft Launcher.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Battle.net\Battle.net.exe", false)]
    [InlineData(@"C:\Program Files (x86)\World of Warcraft\_retail_\WowErrorReporter.exe", false)]
    [InlineData(@"C:\windows\system32\explorer.exe", false)]
    public void Which_wine_processes_are_wow_clients(string argv0, bool wow)
    {
        var p = WineProcess.Parse(1, argv0 + "\0", Environ, Cwd, Preloader)!;
        Assert.Equal(wow, p.IsWow);
    }

    [Fact]
    public void Linux_programs_are_never_wow()
    {
        var p = WineProcess.Parse(7, "/usr/bin/konsole\0--new-tab\0", "LANG=C.UTF-8\0", "/tmp", "/usr/bin/konsole")!;
        Assert.False(p.IsWow);
        Assert.Equal("", p.WindowsPath);
        Assert.Equal("/usr/bin/konsole", p.ExePath);
        Assert.Equal("konsole", p.Name);
    }

    [Fact]
    public void Empty_cmdline_is_no_process()
    {
        Assert.Null(WineProcess.Parse(2, "", "", "", ""));
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\World of Warcraft\_forever_\Wow.exe", "/p", "/p/drive_c/Program Files (x86)/World of Warcraft/_forever_/Wow.exe")]
    [InlineData(@"c:\games\Wow.exe", "/p", "/p/drive_c/games/Wow.exe")]
    [InlineData(@"D:\World of Warcraft\_classic_era_\WowClassic.exe", "/p", "/p/dosdevices/d:/World of Warcraft/_classic_era_/WowClassic.exe")]
    public void Windows_paths_map_into_the_prefix(string windows, string prefix, string linux)
    {
        Assert.Equal(linux, WineProcess.ToLinuxPath(windows, prefix, "/"));
    }

    [Fact]
    public void Prefix_from_environ_and_from_a_path()
    {
        Assert.Equal("/games/battlenet/pfx", WineProcess.PrefixFrom(Environ));
        Assert.Equal("", WineProcess.PrefixFrom("HOME=/x\0"));
        Assert.Equal("/games/battlenet", WineProcess.PrefixOf("/games/battlenet/drive_c/Program Files (x86)/World of Warcraft/_forever_/Wow.exe"));
        Assert.Null(WineProcess.PrefixOf("/usr/bin/konsole"));
    }

    [Fact]
    public void Start_time_from_stat_even_with_spaces_in_the_name()
    {
        // field 22 (starttime) = 86100547 ticks = 861005.47 s after boot
        const string stat = "3890197 (Wow (main).exe) R 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 86100547 23 24";
        var boot = WineProcess.BootTimeFrom("cpu  1 2 3\nbtime 1793000000\nprocesses 5\n");
        Assert.Equal(1793000000, boot);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1793000000 + 861005.47), WineProcess.StartTimeFrom(stat, boot));
        Assert.Null(WineProcess.StartTimeFrom("garbage", boot));
    }

    [Fact]
    public void Wow_path_rule_works_on_linux_paths()
    {
        Assert.True(WindowFinder.IsWowPath("/games/battlenet/drive_c/Program Files (x86)/World of Warcraft/_forever_/Wow.exe"));
        Assert.False(WindowFinder.IsWowPath("/games/battlenet/drive_c/Program Files (x86)/World of Warcraft/World of Warcraft Launcher.exe"));
        Assert.Equal(WowGame.Forever, GameDetection.GameOf(WindowFinder.FakeExe));
    }
}
