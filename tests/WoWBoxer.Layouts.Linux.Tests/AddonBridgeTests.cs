namespace WoWBoxer.Layouts.Linux.Tests;

public class AddonBridgeTests
{
    // Shaped like a real WoWBoxer.lua (WoW writes CRLF, an empty first line, list items without keys); names made up.
    private static readonly string Sample = string.Join("\r\n",
        "",
        "WoWBoxerDB = {",
        "\t[\"squads\"] = {",
        "\t\t{",
        "\t\t\t[\"notes\"] = \"Say \\\"hi\\\" first\\n\\tthen go\",",
        "\t\t\t[\"members\"] = {",
        "\t\t\t\t{",
        "\t\t\t\t\t[\"leader\"] = true,",
        "\t\t\t\t\t[\"level\"] = 10,",
        "\t\t\t\t\t[\"cls\"] = \"shaman\",",
        "\t\t\t\t\t[\"pins\"] = {",
        "\t\t\t\t\t},",
        "\t\t\t\t\t[\"name\"] = \"Testchar\",",
        "\t\t\t\t},",
        "\t\t\t\t{",
        "\t\t\t\t\t[\"leader\"] = false,",
        "\t\t\t\t\t[\"level\"] = 11,",
        "\t\t\t\t},",
        "\t\t\t},",
        "\t\t\t[\"ratio\"] = -1.5e2,",
        "\t\t\t[\"mask\"] = 0x1F,",
        "\t\t\t[\"half\"] = .5,",
        "\t\t},",
        "\t},",
        "\t[\"log\"] = {",
        "\t\t\"first\", -- [1]",
        "\t\t\"sec\\226\\128\\148ond\",",
        "\t\t42,",
        "\t},",
        "\t[1] = \"one\",",
        "\tplain = 'single \\'quoted\\'',",
        "\t[\"bridge\"] = {",
        "\t\t[\"loggedInAt\"] = 1793947071,",
        "\t\t[\"character\"] = \"Testchar\",",
        "\t\t[\"version\"] = 1,",
        "\t\t[\"savedAt\"] = 1793948525,",
        "\t\t[\"class\"] = \"SHAMAN\",",
        "\t\t[\"level\"] = 16,",
        "\t\t[\"realm\"] = \"Test Realm\",",
        "\t},",
        "}",
        "WoWBoxerCharDB = nil",
        "");

    [Fact]
    public void Bridge_is_read_from_a_realistic_file()
    {
        var c = AddonBridge.Parse(Sample)!;
        Assert.Equal("Testchar", c.Character);
        Assert.Equal("Test Realm", c.Realm);
        Assert.Equal("SHAMAN", c.Class);
        Assert.Equal(16, c.Level);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793947071), c.LoggedInAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793948525), c.SavedAt);
    }

    [Fact]
    public void Lua_values_keys_and_lists()
    {
        var vars = LuaReader.ReadAssignments(Sample);
        Assert.Null(vars["WoWBoxerCharDB"]);
        var db = Assert.IsType<LuaTable>(vars["WoWBoxerDB"]);
        Assert.Equal("one", db.Entries[1.0]);
        Assert.Equal("single 'quoted'", db.String("plain"));

        var squad = Assert.IsType<LuaTable>(Assert.Single(db.Table("squads")!.Items));
        Assert.Equal("Say \"hi\" first\n\tthen go", squad.String("notes"));
        Assert.Equal(-150.0, squad.Number("ratio"));
        Assert.Equal(31.0, squad.Number("mask"));
        Assert.Equal(0.5, squad.Number("half"));
        var members = squad.Table("members")!.Items.Cast<LuaTable>().ToList();
        Assert.Equal(2, members.Count);
        Assert.Equal(true, members[0]["leader"]);
        Assert.Equal(false, members[1]["leader"]);
        Assert.Equal(10.0, members[0].Number("level"));
        Assert.Empty(members[0].Table("pins")!.Entries);

        // decimal byte escapes are bytes of UTF-8 text
        Assert.Equal(["first", "sec\u2014ond", 42.0], db.Table("log")!.Items);
    }

    [Theory]
    [InlineData("WoWBoxerDB = {\n}\n")]
    [InlineData("WoWBoxerDB = {\n [\"bridge\"] = {\n  [\"realm\"] = \"R\",\n },\n}\n")]
    [InlineData("SomethingElse = {\n [\"bridge\"] = { [\"character\"] = \"X\" },\n}\n")]
    [InlineData("WoWBoxerDB = {\n [\"bridge\"] = {\n")]
    [InlineData("WoWBoxerDB = { [\"a\"] = function() end }")]
    [InlineData("")]
    public void No_bridge_gives_null(string lua)
    {
        Assert.Null(AddonBridge.Parse(lua));
    }

    [Fact]
    public void Bad_lua_names_the_line()
    {
        var e = Assert.Throws<FormatException>(() => LuaReader.ReadAssignments("A = {\r\n [\"x\"] = 1,\r\n [\"y\"] = @\r\n}"));
        Assert.Contains("line 3", e.Message);
    }

    [Fact]
    public void Long_strings_comments_and_other_escapes()
    {
        var vars = LuaReader.ReadAssignments("--[[ block\ncomment ]]\nA = [==[\nline ]] one\r\nline two]==]\nB = \"\\x41\\u{20AC}\\z\n   end\\\\\"\nC = { 1; 2, }\n");
        Assert.Equal("line ]] one\nline two", vars["A"]);
        Assert.Equal("A€end\\", vars["B"]);
        Assert.Equal([1.0, 2.0], Assert.IsType<LuaTable>(vars["C"]).Items);
    }

    [Fact]
    public void Read_finds_the_account_file_and_misses_cleanly()
    {
        var install = Path.Combine(Path.GetTempPath(), "wbx-bridge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = AddonBridge.PathFor(install, "ACCOUNT1");
            Assert.EndsWith(Path.Combine("WTF", "Account", "ACCOUNT1", "SavedVariables", "WoWBoxer.lua"), file);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, Sample);
            Assert.Equal("Testchar", AddonBridge.Read(install, "ACCOUNT1")?.Character);
            Assert.Null(AddonBridge.Read(install, "ACCOUNT2"));
        }
        finally
        {
            Directory.Delete(install, recursive: true);
        }
    }
}
