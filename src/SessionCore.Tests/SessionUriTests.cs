using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The link parser is the entire security boundary of the <c>skysession://</c> feature: a
/// page anyone browses can navigate to one of these, and Windows hands the text straight to
/// the registered handler. So most of what is locked here is what must be refused.
///
/// The bar for an accept is that the request comes back typed — a verb, an id, a folder —
/// and never as a string anything downstream will concatenate into a command line.
/// </summary>
public class SessionUriTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "sky-uri-tests");
    private static readonly string[] Roots = [Root];

    private static SessionUriRequest Parse(string url, params string[] roots) =>
        SessionUri.Parse(url, roots.Length > 0 ? roots : Roots);

    private const string Id = "b9e83ad3-8742-4f86-b5e3-40e844f24da1";

    // --- what a link may do -------------------------------------------------

    [Fact]
    public void Resume_carries_the_id()
    {
        var request = Parse($"skysession://resume/{Id}");

        Assert.True(request.Ok);
        Assert.Equal(SessionUriVerb.Resume, request.Verb);
        Assert.Equal(Id, request.Id);
    }

    [Fact]
    public void Done_carries_the_id()
    {
        var request = Parse($"skysession://done/{Id}");

        Assert.True(request.Ok);
        Assert.Equal(SessionUriVerb.Done, request.Verb);
        Assert.Equal(Id, request.Id);
    }

    [Fact]
    public void A_prefix_is_an_id_too()
    {
        // The CLI resolves any unique prefix and a brief may well emit a short one.
        var request = Parse("skysession://resume/b9e83ad3");

        Assert.True(request.Ok);
        Assert.Equal("b9e83ad3", request.Id);
    }

    [Fact]
    public void The_scheme_and_the_verb_are_case_insensitive()
    {
        // Whoever hands the URL over may have upper-cased any of it; only the id is data.
        var request = Parse($"SkySession://RESUME/{Id.ToUpperInvariant()}");

        Assert.True(request.Ok);
        Assert.Equal(SessionUriVerb.Resume, request.Verb);
        Assert.Equal(Id, request.Id);
    }

    // --- what a link may not do ---------------------------------------------

    [Theory]
    [InlineData("fork")]
    [InlineData("restart")]
    [InlineData("trust")]
    [InlineData("close")]
    public void The_verbs_a_link_is_not_allowed_are_named_as_such(string verb)
    {
        // These are exactly the ones a bad link would want, so the refusal says they were
        // withheld rather than that they were not understood.
        var request = Parse($"skysession://{verb}/{Id}");

        Assert.False(request.Ok);
        Assert.Contains("deliberately not", request.Refusal);
    }

    [Fact]
    public void An_unknown_verb_is_refused()
    {
        var request = Parse($"skysession://delete/{Id}");

        Assert.False(request.Ok);
        Assert.Contains("Unknown verb", request.Refusal);
    }

    [Fact]
    public void Another_scheme_is_refused()
    {
        Assert.False(Parse($"vscode://resume/{Id}").Ok);
        Assert.False(Parse("https://example.com/resume").Ok);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url at all")]
    public void Nonsense_is_refused_rather_than_thrown(string url)
    {
        var request = Parse(url);

        Assert.False(request.Ok);
        Assert.NotNull(request.Refusal);
    }

    // --- the id allowlist ---------------------------------------------------

    [Theory]
    [InlineData("b9e83ad3\" & calc.exe")]
    [InlineData("b9e83ad3 | whoami")]
    [InlineData("b9e83ad3; shutdown")]
    [InlineData("b9e83ad3`whoami`")]
    [InlineData("b9e83ad3$(whoami)")]
    [InlineData("b9e83ad3 --force")]
    [InlineData("../../etc/passwd")]
    [InlineData("zzzzzzzz")]
    public void An_id_is_hex_and_dashes_or_it_is_not_an_id(string id)
    {
        // An allowlist, not a list of characters someone thought to forbid. Every one of
        // these is refused for the same reason: it is not what a session id looks like.
        var request = Parse($"skysession://resume/{Uri.EscapeDataString(id)}");

        Assert.False(request.Ok);
        Assert.Contains("Not a session id", request.Refusal);
    }

    [Fact]
    public void An_escaped_quote_does_not_survive_unescaping_into_an_id()
    {
        // %22 is a quote once Uri has decoded the path. The check runs after that decode,
        // which is the only order that is worth anything.
        var request = Parse("skysession://resume/b9e83ad3%22");

        Assert.False(request.Ok);
        Assert.Contains("Not a session id", request.Refusal);
    }

    [Fact]
    public void A_newline_cannot_be_smuggled_into_an_id()
    {
        var request = Parse("skysession://resume/b9e83ad3%0Aresume%20whatever");

        Assert.False(request.Ok);
    }

    [Fact]
    public void An_id_too_short_to_mean_one_session_is_refused()
    {
        // The CLI would happily resolve "b9" today. A link is clicked by someone who cannot
        // see what it matched, and what is unique this week may not be next.
        var request = Parse("skysession://resume/b9");

        Assert.False(request.Ok);
        Assert.Contains("too short", request.Refusal);
    }

    [Fact]
    public void A_verb_with_no_id_is_refused()
    {
        Assert.False(Parse("skysession://resume").Ok);
        Assert.False(Parse("skysession://resume/").Ok);
    }

    // --- the folder allowlist -----------------------------------------------

    [Fact]
    public void New_accepts_a_folder_under_a_root()
    {
        var folder = Path.Combine(Root, "demo");
        Directory.CreateDirectory(folder);

        var request = Parse($"skysession://new?in={Uri.EscapeDataString(folder)}");

        Assert.True(request.Ok, request.Refusal);
        Assert.Equal(SessionUriVerb.New, request.Verb);
        Assert.Equal(folder, request.Folder);
    }

    [Fact]
    public void A_sibling_that_merely_starts_with_the_root_is_not_under_it()
    {
        // The reason the root check appends a separator to both ends. C:\CodeEvil starts
        // with C:\Code as a string and is a different folder entirely.
        Directory.CreateDirectory(Root + "Evil");

        var request = Parse($"skysession://new?in={Uri.EscapeDataString(Root + "Evil")}");

        Assert.False(request.Ok);
        Assert.Contains("not under", request.Refusal);
    }

    [Fact]
    public void A_folder_outside_every_root_is_refused()
    {
        var request = Parse(@"skysession://new?in=C:\Windows\System32");

        Assert.False(request.Ok);
        Assert.Contains("not under", request.Refusal);
    }

    [Fact]
    public void Traversal_out_of_a_root_is_refused_after_it_is_resolved()
    {
        // The string starts inside the root; the path does not. The check runs on the
        // resolved path, which is why this is refused rather than accepted.
        var escape = Path.Combine(Root, "..", "..", "Windows");

        var request = Parse($"skysession://new?in={Uri.EscapeDataString(escape)}");

        Assert.False(request.Ok);
    }

    [Theory]
    [InlineData(@"\\server\share\repo")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData("//server/share/repo")]
    public void A_path_that_is_not_a_plain_local_folder_is_refused(string path)
    {
        // Refused before resolution: a UNC share reaches another machine, and the device
        // namespace sidesteps the very normalisation the root check depends on.
        var request = Parse($"skysession://new?in={Uri.EscapeDataString(path)}");

        Assert.False(request.Ok);
    }

    [Fact]
    public void A_root_that_exists_but_a_folder_that_does_not_is_refused()
    {
        var missing = Path.Combine(Root, "no-such-repo-here");

        var request = Parse($"skysession://new?in={Uri.EscapeDataString(missing)}");

        Assert.False(request.Ok);
        Assert.Contains("No such folder", request.Refusal);
    }

    [Fact]
    public void With_no_roots_configured_new_is_refused_outright()
    {
        var folder = Path.Combine(Root, "demo");
        Directory.CreateDirectory(folder);

        var request = SessionUri.Parse($"skysession://new?in={Uri.EscapeDataString(folder)}", []);

        Assert.False(request.Ok);
    }

    [Fact]
    public void New_without_a_folder_is_refused()
    {
        Assert.False(Parse("skysession://new").Ok);
        Assert.False(Parse("skysession://new?in=").Ok);
    }

    [Fact]
    public void A_link_cannot_carry_a_prompt()
    {
        // Rule 5. A link that opens a session and also sends it a prompt is remote code
        // execution with an extra step — refused loudly rather than quietly ignored.
        var folder = Path.Combine(Root, "demo");
        Directory.CreateDirectory(folder);

        var request = Parse(
            $"skysession://new?in={Uri.EscapeDataString(folder)}&prompt={Uri.EscapeDataString("rm -rf /")}");

        Assert.False(request.Ok);
        Assert.Contains("cannot carry a prompt", request.Refusal);
    }

    [Fact]
    public void A_repeated_folder_cannot_mean_two_things()
    {
        // ?in=<allowed>&in=<forbidden> is the classic walk-past: one parser reads the first
        // value and checks it, another reads the last and acts on it. Here the last value
        // is the only one there is, so the check and the launch cannot disagree.
        var folder = Path.Combine(Root, "demo");
        Directory.CreateDirectory(folder);

        var request = Parse(
            $"skysession://new?in={Uri.EscapeDataString(folder)}&in={Uri.EscapeDataString(@"C:\Windows")}");

        Assert.False(request.Ok);
    }

    // --- refusals are showable ----------------------------------------------

    [Fact]
    public void A_refusal_quotes_what_it_refused_without_letting_it_run_long()
    {
        var request = Parse("skysession://resume/" + new string('z', 500));

        Assert.False(request.Ok);
        Assert.True(request.Refusal!.Length < 120, $"refusal was {request.Refusal.Length} chars");
    }

    [Fact]
    public void Under_is_true_of_a_root_and_itself()
    {
        Assert.True(SessionUri.Under(@"C:\Code", @"C:\Code"));
        Assert.True(SessionUri.Under(@"C:\Code\repo", @"C:\Code"));
        Assert.True(SessionUri.Under(@"C:\Code\repo", @"C:\Code\"));
        Assert.False(SessionUri.Under(@"C:\CodeEvil", @"C:\Code"));
        Assert.False(SessionUri.Under(@"C:\Other", @"C:\Code"));
    }
}
