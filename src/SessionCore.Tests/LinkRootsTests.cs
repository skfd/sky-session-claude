using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// What a link may open a session in. The failure worth guarding is the quiet one: a
/// settings file that cannot be read must never fall back to the default, because the
/// default is wider than whatever someone had narrowed it to.
/// </summary>
public class LinkRootsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sky-linkroots-" + Guid.NewGuid().ToString("N")[..8]);

    private string Write(string json)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void No_file_means_the_default_root()
    {
        var roots = LinkRoots.Load(Path.Combine(_dir, "not-there.json"));

        Assert.Null(roots.Warning);
        Assert.Equal([LinkRoots.DefaultRoot()], roots.Roots);
    }

    [Fact]
    public void A_configured_root_replaces_the_default_rather_than_joining_it()
    {
        var roots = LinkRoots.Load(Write("""{ "linkRoots": ["C:\\Work"] }"""));

        Assert.Null(roots.Warning);
        Assert.Equal([@"C:\Work"], roots.Roots);
    }

    [Fact]
    public void Several_roots_are_kept_in_order()
    {
        var roots = LinkRoots.Load(Write("""{ "linkRoots": ["C:\\Work", "D:\\Repos"] }"""));

        Assert.Equal([@"C:\Work", @"D:\Repos"], roots.Roots);
    }

    [Fact]
    public void A_tilde_is_expanded_because_that_is_what_someone_will_type()
    {
        var roots = LinkRoots.Load(Write("""{ "linkRoots": ["~/Code"] }"""));

        Assert.Equal(
            [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Code")],
            roots.Roots);
    }

    [Fact]
    public void A_settings_file_about_something_else_still_means_the_default()
    {
        // Not a broken file. A file with other settings in it and no opinion about links.
        var roots = LinkRoots.Load(Write("""{ "theme": "dark" }"""));

        Assert.Null(roots.Warning);
        Assert.Equal([LinkRoots.DefaultRoot()], roots.Roots);
    }

    [Fact]
    public void An_empty_list_turns_new_off_and_is_not_a_mistake()
    {
        var roots = LinkRoots.Load(Write("""{ "linkRoots": [] }"""));

        Assert.Null(roots.Warning);
        Assert.Empty(roots.Roots);
    }

    [Fact]
    public void Broken_json_allows_nothing_and_says_so()
    {
        // The whole reason this class exists rather than a try/catch returning the default:
        // falling back here would widen the allowlist at the moment someone narrowed it.
        var roots = LinkRoots.Load(Write("{ not json at all"));

        Assert.NotNull(roots.Warning);
        Assert.Empty(roots.Roots);
    }

    [Fact]
    public void A_list_of_nothing_usable_allows_nothing_and_says_so()
    {
        var roots = LinkRoots.Load(Write("""{ "linkRoots": ["   ", ""] }"""));

        Assert.NotNull(roots.Warning);
        Assert.Empty(roots.Roots);
    }

    [Fact]
    public void One_bad_entry_costs_only_itself()
    {
        var roots = LinkRoots.Load(Write("""{ "linkRoots": ["C:\\Work", "   "] }"""));

        Assert.Null(roots.Warning);
        Assert.Equal([@"C:\Work"], roots.Roots);
    }

    [Fact]
    public void Roots_reach_the_parser_as_the_allowlist()
    {
        // The two halves together: what LinkRoots loads is exactly what SessionUri enforces.
        Directory.CreateDirectory(Path.Combine(_dir, "repo"));
        var settings = Write($$"""{ "linkRoots": [{{System.Text.Json.JsonSerializer.Serialize(_dir)}}] }""");
        var roots = LinkRoots.Load(settings);

        var inside = SessionUri.Parse(
            $"skysession://new?in={Uri.EscapeDataString(Path.Combine(_dir, "repo"))}", roots.Roots);
        var outside = SessionUri.Parse(@"skysession://new?in=C:\Windows", roots.Roots);

        Assert.True(inside.Ok, inside.Refusal);
        Assert.False(outside.Ok);
    }
}
