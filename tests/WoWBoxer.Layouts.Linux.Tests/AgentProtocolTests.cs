using System.Text.Json;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux.Agent;

namespace WoWBoxer.Layouts.Linux.Tests;

public class AgentProtocolTests
{
    private const string Report = """
        {
          "v": 1, "agent": "1.0.0", "epoch": "e1", "done": 2,
          "outputs": [
            {"name": "DP-1", "manufacturer": "Maker", "model": "Wide 34", "scale": 1.5,
             "geometry": {"x": 0, "y": 0, "width": 2293.3333, "height": 960},
             "workArea": {"x": 0, "y": 0, "width": 2293.3333, "height": 916.6667}},
            {"name": "HDMI-A-1", "manufacturer": "Maker", "model": "Side 24", "scale": 1,
             "geometry": {"x": 2293.3333, "y": 0, "width": 1920, "height": 1080},
             "workArea": {"x": 2293.3333, "y": 0, "width": 1920, "height": 1080}}
          ],
          "active": "{11111111-2222-3333-4444-555555555555}",
          "windows": [
            {"id": "{11111111-2222-3333-4444-555555555555}", "pid": 4242, "caption": "World of Warcraft",
             "resourceClass": "wow.exe", "resourceName": "wow.exe",
             "frame": {"x": 10.4, "y": 20.6, "width": 1280, "height": 829.5},
             "client": {"x": 10.4, "y": 50.6, "width": 1280, "height": 800},
             "keepAbove": true, "noBorder": false, "minimized": false, "fullScreen": false, "moving": false,
             "output": "DP-1", "stack": 3, "somethingNew": 7}
          ],
          "handEdited": [{"id": "{11111111-2222-3333-4444-555555555555}", "frame": {"x": 1, "y": 2, "width": 300, "height": 200}}],
          "gaveUp": []
        }
        """;

    [Fact]
    public void Report_is_read_with_fractional_rects_rounded()
    {
        var report = AgentJson.ParseReport(Report)!;
        Assert.Equal("1.0.0", report.Agent);
        Assert.Equal(2, report.Done);
        var w = Assert.Single(report.Windows);
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", w.Id);
        Assert.Equal(4242, w.Pid);
        Assert.Equal("wow.exe", w.ResourceClass);
        Assert.True(w.KeepAbove);
        Assert.Equal(new PixelRect(10, 21, 1280, 830), w.Frame.ToPixels());
        Assert.Equal(new PixelRect(10, 51, 1280, 800), w.Client.ToPixels());
        Assert.Equal(new PixelRect(1, 2, 300, 200), Assert.Single(report.HandEdited).Frame.ToPixels());
    }

    [Fact]
    public void Text_that_is_not_a_report_gives_null()
    {
        Assert.Null(AgentJson.ParseReport("not json"));
        Assert.Null(AgentJson.ParseReport("[1, 2]"));
    }

    [Fact]
    public void Outputs_become_monitors_first_is_primary()
    {
        var monitors = MonitorReader.FromOutputs(AgentJson.ParseReport(Report)!.Outputs);
        Assert.Equal(2, monitors.Count);
        Assert.Equal(new MonitorInfo(1, "DP-1", true, new PixelRect(0, 0, 2293, 960), new PixelRect(0, 0, 2293, 917), 144) { DeviceId = "DP-1:Wide 34" }, monitors[0]);
        Assert.False(monitors[1].IsPrimary);
        Assert.Equal(2, monitors[1].Index);
        Assert.Equal(96, monitors[1].Dpi);
        Assert.Equal(150, monitors[0].ScalePercent);
    }

    [Fact]
    public void Desired_state_has_the_protocol_shape()
    {
        var state = new AgentState("e1", ownPid: 4242);
        state.Enqueue("place", "{a}", new PixelRect(0, 0, 1688, 1392));
        state.Enqueue("above", "{a}", on: true);
        state.Enqueue("border", "{a}", on: false);
        state.SetKeepAbove("{a}", true);
        state.SetWow(["{a}", "{b}"]);
        state.SetHover(new HoverSettings(true, 30, true));
        state.SetOwn("WoWBoxer Identify 1", new PixelRect(10, 10, 300, 200));

        var json = AgentJson.Serialize(state.Desired(new AgentHello { Agent = "1.0.0", Epoch = "", Done = 0 }));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("e1", root.GetProperty("epoch").GetString());
        Assert.Equal(4242, root.GetProperty("ownPid").GetInt32());

        var actions = root.GetProperty("actions").EnumerateArray().ToList();
        Assert.Equal([1L, 2L, 3L], actions.Select(a => a.GetProperty("n").GetInt64()));
        Assert.Equal(["place", "above", "border"], actions.Select(a => a.GetProperty("op").GetString()));
        var rect = actions[0].GetProperty("rect");
        Assert.Equal(1688, rect.GetProperty("width").GetInt32());
        Assert.Equal(1392, rect.GetProperty("height").GetInt32());
        Assert.False(actions[0].TryGetProperty("on", out _));
        Assert.True(actions[1].GetProperty("on").GetBoolean());
        Assert.False(actions[1].TryGetProperty("rect", out _));
        Assert.False(actions[2].GetProperty("on").GetBoolean());

        Assert.Equal(["{a}"], root.GetProperty("keepAbove").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["{a}", "{b}"], root.GetProperty("wow").EnumerateArray().Select(e => e.GetString()));
        var hover = root.GetProperty("hover");
        Assert.True(hover.GetProperty("enabled").GetBoolean());
        Assert.Equal(30, hover.GetProperty("delayMs").GetInt32());
        Assert.True(hover.GetProperty("bringToFront").GetBoolean());
        var own = Assert.Single(root.GetProperty("own").EnumerateArray().ToList());
        Assert.Equal("WoWBoxer Identify 1", own.GetProperty("caption").GetString());
        Assert.Equal(300, own.GetProperty("rect").GetProperty("width").GetInt32());
        Assert.True(own.GetProperty("above").GetBoolean());
        Assert.True(own.GetProperty("noBorder").GetBoolean());
        Assert.True(own.GetProperty("skipTaskbar").GetBoolean());
    }

    [Fact]
    public void Hello_is_read_and_bad_hello_counts_as_new_agent()
    {
        var hello = AgentJson.ParseHello("""{"agent":"1.0.0","epoch":"e1","done":9}""");
        Assert.Equal(("1.0.0", "e1", 9L), (hello.Agent, hello.Epoch, hello.Done));
        Assert.Equal(0, AgentJson.ParseHello("garbage").Done);
    }
}

public class AgentStateTests
{
    [Fact]
    public void Actions_are_numbered_in_order_and_dropped_once_done()
    {
        var state = new AgentState("e1");
        var first = state.Enqueue("place", "{a}", new PixelRect(0, 0, 100, 100));
        var second = state.Enqueue("place", "{b}", new PixelRect(100, 0, 100, 100));
        var third = state.Enqueue("above", "{a}", on: true);
        Assert.Equal([1L, 2L, 3L], new[] { first, second, third });

        var desired = state.Desired(new AgentHello { Agent = "1.0.0", Epoch = "e1", Done = 2 });
        Assert.Equal([3L], desired.Actions.Select(a => a.N));
        Assert.Equal(4, state.Enqueue("border", "{a}", on: false));
    }

    [Fact]
    public void An_agent_of_another_epoch_gets_every_pending_action_again()
    {
        var state = new AgentState("e2");
        state.Enqueue("place", "{a}", new PixelRect(0, 0, 100, 100));
        state.Enqueue("place", "{b}", new PixelRect(0, 0, 100, 100));
        // "done 5" belongs to the previous app start: it must not drop our actions 1 and 2
        var desired = state.Desired(new AgentHello { Agent = "1.0.0", Epoch = "e1", Done = 5 });
        Assert.Equal([1L, 2L], desired.Actions.Select(a => a.N));
        Assert.Equal("e2", desired.Epoch);
    }

    [Fact]
    public void A_report_confirms_actions_too()
    {
        var state = new AgentState("e1");
        state.Enqueue("place", "{a}", new PixelRect(0, 0, 100, 100));
        state.Enqueue("place", "{b}", new PixelRect(0, 0, 100, 100));
        state.Apply(new AgentReport { Agent = "1.0.0", Epoch = "e1", Done = 1 }, DateTime.UtcNow);
        Assert.Equal([2L], state.Pending.Select(a => a.N));
        Assert.Equal("1.0.0", state.AgentVersion);
    }

    [Fact]
    public void A_newer_action_of_the_same_kind_replaces_the_pending_one()
    {
        var state = new AgentState("e1");
        state.Enqueue("place", "{a}", new PixelRect(0, 0, 100, 100));
        state.Enqueue("above", "{a}", on: true);
        state.Enqueue("place", "{a}", new PixelRect(5, 5, 100, 100));
        var pending = state.Pending;
        Assert.Equal([2L, 3L], pending.Select(a => a.N));
        Assert.Equal(new AgentRect(5, 5, 100, 100), pending[1].Rect);
    }

    [Fact]
    public void Stopped_state_asks_the_agent_for_nothing()
    {
        var state = new AgentState("e1");
        state.Enqueue("place", "{a}", new PixelRect(0, 0, 100, 100));
        state.SetKeepAbove("{a}", true);
        state.SetWow(["{a}"]);
        state.SetHover(new HoverSettings(true, 30, true));
        state.SetOwn("WoWBoxer Identify 1", new PixelRect(0, 0, 10, 10));
        state.Stop();
        var desired = state.Desired(new AgentHello { Agent = "1.0.0", Epoch = "e1", Done = 0 });
        Assert.Empty(desired.Actions);
        Assert.Empty(desired.KeepAbove);
        Assert.Empty(desired.Wow);
        Assert.Empty(desired.Own);
        Assert.False(desired.Hover.Enabled);
    }

    [Fact]
    public void Each_epoch_is_new()
    {
        Assert.NotEqual(new AgentState().Epoch, new AgentState().Epoch);
    }

    [Fact]
    public void Handles_are_stable_per_window_id_and_never_reused()
    {
        var state = new AgentState("e1");
        var a = state.HandleOf("{a}");
        var b = state.HandleOf("{b}");
        Assert.NotEqual(a, b);
        Assert.NotEqual(0, a);
        Assert.Equal(a, state.HandleOf("{a}"));
        Assert.Equal("{b}", state.IdOf(b));
        Assert.Null(state.IdOf(12345));

        var window = new AgentWindow { Id = "{a}", Pid = 1, Frame = new AgentRectF(0, 0, 400, 300) };
        state.Apply(new AgentReport { Windows = [window] }, DateTime.UtcNow);
        Assert.NotNull(state.WindowOf(a));
        Assert.Null(state.WindowOf(b));

        // the window closes and opens again (same KWin id): same handle; a new id never gets an old handle
        state.Apply(new AgentReport(), DateTime.UtcNow);
        Assert.Null(state.WindowOf(a));
        state.Apply(new AgentReport { Windows = [window] }, DateTime.UtcNow);
        Assert.Equal(a, state.HandleOf("{a}"));
        var c = state.HandleOf("{c}");
        Assert.NotEqual(a, c);
        Assert.NotEqual(b, c);
    }

    [Fact]
    public void Hand_edits_and_give_ups_come_back_as_handles()
    {
        var state = new AgentState("e1");
        var (edits, gaveUp) = state.Apply(new AgentReport
        {
            HandEdited = [new AgentHandEdit { Id = "{a}", Frame = new AgentRectF(1.6, 2, 300, 200) }],
            GaveUp = ["{b}"],
        }, DateTime.UtcNow);
        var edit = Assert.Single(edits);
        Assert.Equal(state.HandleOf("{a}"), edit.Handle);
        Assert.Equal(new PixelRect(2, 2, 300, 200), edit.Frame);
        Assert.Equal([state.HandleOf("{b}")], gaveUp);
    }

    [Fact]
    public void Standing_state_reports_whether_it_changed()
    {
        var state = new AgentState("e1");
        Assert.True(state.SetKeepAbove("{a}", true));
        Assert.False(state.SetKeepAbove("{a}", true));
        Assert.True(state.IsKeptAbove("{a}"));
        Assert.True(state.SetWow(["{a}"]));
        Assert.False(state.SetWow(["{a}"]));
        Assert.True(state.SetHover(new HoverSettings(true, 50, false)));
        Assert.False(state.SetHover(new HoverSettings(true, 50, false)));
        Assert.Equal(new HoverSettings(true, 50, false), state.Hover);
        Assert.True(state.SetOwn("x", new PixelRect(0, 0, 1, 1)));
        Assert.False(state.SetOwn("x", new PixelRect(0, 0, 1, 1)));
        Assert.True(state.SetOwn("x", null));
    }
}
