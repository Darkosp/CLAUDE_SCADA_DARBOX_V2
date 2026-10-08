using System.Security.Cryptography;
using System.Text;
using ScadaDarbox.Gateway.Security;
using Xunit;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// The `script-src` a served page needs (ADR-0031 §8).
/// </summary>
/// <remarks>
/// **The expected hash is computed here, from the script's own text**, rather than copied from a run: a
/// test that asserts a literal it once printed would pass just as happily if the hashing started covering
/// the tags as well as the code, and the browser — not the test — would be the thing that noticed.
/// </remarks>
public sealed class ScriptHashTests
{
    /// <summary>The shape of the client's real inline script, trimmed to what the hashing cares about.</summary>
    private const string ThemeSnippet = """
        <script>
          (function () {
            var stored = window.localStorage.getItem('scada.theme');
            document.documentElement.setAttribute('data-theme', stored === 'dark' ? 'dark' : 'light');
          })();
        </script>
        """;

    [Fact]
    public void An_inline_script_is_hashed_by_its_own_text()
    {
        var hashes = ScriptHashes.InlineScriptHashes(ThemeSnippet);
        var hash = Assert.Single(hashes);

        // The element's text between the tags, and nothing else: that is what a CSP hashes -- with its
        // line endings as the HTML parser leaves them, which is LF. **This `Replace` is not tidying**:
        // without it the expected value depends on how git happened to check this source file out, which
        // is how the CRLF defect stayed invisible here while the browser refused the script.
        var code = ThemeSnippet["<script>".Length..^"</script>".Length].Replace("\r\n", "\n");
        var expected = $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code)))}'";

        Assert.Equal(expected, hash);

        // And not the whole element, which is the mistake that produces a header that looks right and a
        // script the browser refuses — a dark theme that silently stops applying.
        var wholeElement = $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(ThemeSnippet)))}'";
        Assert.NotEqual(wholeElement, hash);
    }

    [Fact]
    public void A_page_served_with_windows_line_endings_hashes_the_same_as_one_without()
    {
        // **The defect this file missed, and the reason it missed it.** A CSP hash covers the script
        // element's child text, which exists only after the HTML parser's input-stream preprocessing has
        // turned every CRLF and every lone CR into a single LF. The bytes on the wire still carry the CRs.
        //
        // So a page checked out on Windows is served with CRs the Gateway hashed and the browser did not,
        // the header names a hash nothing can match, and every inline script on the page is refused. Seen
        // in the browser on 2026-10-08: `data-theme` was never set, because the theme boot script was
        // blocked on every load.
        //
        // **Every other test here feeds in a literal and hashes the same literal back**, so both sides
        // carry whatever line endings the source file has and agree with each other while disagreeing with
        // the browser. This one cannot do that: it builds the two forms from each other, so passing
        // requires the normalization rather than a coincidence of encoding.
        const string WithLf = "<html>\n<script>\nvar a = 1;\nvar b = 2;\n</script>\n</html>";
        var withCrLf = WithLf.Replace("\n", "\r\n");
        var withLoneCr = WithLf.Replace("\n", "\r");

        var lf = Assert.Single(ScriptHashes.InlineScriptHashes(WithLf));

        Assert.Equal(lf, Assert.Single(ScriptHashes.InlineScriptHashes(withCrLf)));
        Assert.Equal(lf, Assert.Single(ScriptHashes.InlineScriptHashes(withLoneCr)));

        // And the fixtures really do differ, so the three assertions above are not comparing one string
        // with itself -- the shape that made the original defect invisible.
        Assert.NotEqual(WithLf, withCrLf);
        Assert.NotEqual(WithLf, withLoneCr);
    }

    [Fact]
    public void A_script_the_page_loads_from_a_file_needs_no_hash()
    {
        // **The content is the point of this fixture.** A `<script src="…">` with nothing inside it is
        // skipped by the "empty script" rule as well, so the first version of this test passed whether or
        // not the `src` rule existed — a test that cannot fail for the rule it names. A script that has both
        // a `src` and content is the only input where the two rules disagree, and the browser ignores the
        // content, so hashing it would add a directive that allows nothing.
        const string Page = """<html><script src="main-ABC123.js">var ignored = 1;</script></html>""";

        Assert.Empty(ScriptHashes.InlineScriptHashes(Page));

        // `'self'` is what loads it, and a hashed `script-src` still allows `'self'` — so the bundle is
        // not blocked by the very directive that was added to protect it.
        Assert.Equal("'self'", ScriptHashes.ScriptSourceFor(Page));
    }

    [Fact]
    public void A_page_with_no_inline_script_gets_the_bare_self()
    {
        // Also the shape of a developer running `ng serve`: the Gateway serves no client, so there is no
        // inline script of ours to allow, and a hash for a script that is not there would be a directive
        // nobody can satisfy and nothing needs.
        Assert.Equal("'self'", ScriptHashes.ScriptSourceFor(null));
        Assert.Equal("'self'", ScriptHashes.ScriptSourceFor(string.Empty));
        Assert.Equal("'self'", ScriptHashes.ScriptSourceFor("<html><body>a page</body></html>"));
    }

    [Fact]
    public void Every_inline_script_gets_its_own_hash_and_the_bundle_keeps_self()
    {
        const string Page = """
            <html>
              <script>var one = 1;</script>
              <script src="main.js"></script>
              <script>var two = 2;</script>
            </html>
            """;

        var hashes = ScriptHashes.InlineScriptHashes(Page);

        Assert.Equal(2, hashes.Count);
        Assert.Equal(hashes.Count, hashes.Distinct().Count());

        var source = ScriptHashes.ScriptSourceFor(Page);

        Assert.StartsWith("'self' 'sha256-", source);
        Assert.Contains("main.js", Page, StringComparison.Ordinal);

        // **Never `'unsafe-inline'`.** It is the one value that would make this whole file pointless while
        // looking like a policy, so it is asserted absent rather than left to a reviewer to notice.
        Assert.DoesNotContain("unsafe-inline", source);
    }

    [Fact]
    public void An_empty_inline_script_gets_no_hash_because_there_is_nothing_to_allow()
    {
        Assert.Empty(ScriptHashes.InlineScriptHashes("<html><script></script></html>"));
    }

    [Fact]
    public void Hashing_is_stable_across_calls_so_a_restart_does_not_invalidate_a_session()
    {
        // The same page must produce the same header every time: a hash that drifted would make the client
        // refuse its own inline script after a restart, which would look like a theme that forgets itself.
        Assert.Equal(
            ScriptHashes.ScriptSourceFor(ThemeSnippet),
            ScriptHashes.ScriptSourceFor(ThemeSnippet));
    }
}
