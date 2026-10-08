using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ScadaDarbox.Gateway.Security;

/// <summary>
/// The `script-src` a served page needs, and the hashes of its inline scripts (ADR-0031 §8).
/// </summary>
/// <remarks>
/// **Public because the test project calls it directly.** The alternative — proving it through a running
/// Gateway with a built client in its web root — is a test that stops covering the hashing the day nobody
/// builds the client, and a hash that is wrong in a way the test cannot see is a dark theme that silently
/// stops applying, which is the defect ADR-0027's inline script exists to prevent.
/// </remarks>
public static partial class ScriptHashes
{
    /// <summary>
    /// The value `script-src` should carry for this page: `'self'`, plus one hash per inline script.
    /// </summary>
    /// <remarks>
    /// With no page at all — a developer running `ng serve`, where the Gateway serves no client — this is
    /// the bare `'self'`, because there is no inline script of ours to allow. A hash for a script that is
    /// not there would be a directive nobody can satisfy and nothing needs.
    /// </remarks>
    public static string ScriptSourceFor(string? html)
    {
        var hashes = InlineScriptHashes(html);

        return hashes.Count == 0 ? "'self'" : $"'self' {string.Join(' ', hashes)}";
    }

    /// <summary>
    /// The SHA-256 of each inline script's own text, in the form a CSP expects.
    /// </summary>
    /// <remarks>
    /// **The hash covers the element's text and nothing else** — not the tags, not the attributes: that is
    /// what the specification hashes, and getting it wrong produces a header that looks right and a script
    /// the browser refuses. A `<script src="…">` needs none (a hashed `script-src` still allows `'self'`,
    /// which is what loads it), and neither does an empty `<script></script>`, which has nothing in it to
    /// allow.
    /// </remarks>
    public static IReadOnlyList<string> InlineScriptHashes(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        var hashes = new List<string>();

        foreach (Match element in ScriptElement().Matches(html))
        {
            if (SourceAttribute().IsMatch(element.Groups["attributes"].Value))
            {
                continue;
            }

            var code = NormalizeNewlines(element.Groups["code"].Value);

            if (code.Length == 0)
            {
                continue;
            }

            hashes.Add($"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code)))}'");
        }

        return hashes;
    }

    /// <summary>
    /// The script's text as the browser will see it: every line ending an LF.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **This is the difference between a correct header and a blocked script, and it is invisible from
    /// the code.** A CSP hash is taken over the script element's *child text*, and by then the HTML
    /// parser has already been through its input-stream preprocessing, which replaces every CRLF pair
    /// and every lone CR with a single LF. The bytes on the wire still carry the CRs; the text the
    /// browser hashes does not.
    /// </para>
    /// <para>
    /// So a page checked out on Windows — where git writes CRLF, and every checkout of this repository
    /// on this machine does — is served with CRs that the Gateway hashed and the browser did not. The
    /// header names a hash nothing can ever match, and **every inline script on the page is refused**.
    /// </para>
    /// <para>
    /// **Found in the browser on 2026-10-08, not by a test, and the test could not have found it**: it
    /// builds its expected hash from the same literal it feeds in, so both sides carried the same line
    /// endings and agreed with each other while disagreeing with the browser. The symptom was the theme
    /// boot script being blocked on every load — `data-theme` never set, which is exactly the
    /// "dark theme that silently stops applying" this class's own remarks warn about, arriving by a
    /// route nobody had thought of.
    /// </para>
    /// </remarks>
    private static string NormalizeNewlines(string code) =>
        code.Contains('\r') ? code.Replace("\r\n", "\n").Replace('\r', '\n') : code;

    [GeneratedRegex(
        "<script(?<attributes>[^>]*)>(?<code>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptElement();

    [GeneratedRegex(@"\ssrc\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex SourceAttribute();
}
