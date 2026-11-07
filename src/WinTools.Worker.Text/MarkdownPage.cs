using System.Net;
using System.Text;
using Markdig;
using Markdig.Renderers;
using WinTools.Core.Capabilities;

namespace WinTools.Worker.Text;

/// <summary>
/// Markdown to a standalone HTML page with Markdig (BSD-2-Clause, ADR-0033), made safe to open:
/// <list type="bullet">
/// <item>raw HTML in the Markdown is never passed through: Markdig's DisableHtml makes it text, so a &lt;script&gt; in the
/// file shows as "&lt;script&gt;";</item>
/// <item>only the extensions that add no markup of the author's choosing (tables, task lists, footnotes, strikethrough,
/// automatic links and heading ids). Generic attributes, which would let "{onclick=...}" through, are not used;</item>
/// <item>every link and image address goes through <see cref="SafeUrl"/>: http, https, mailto, in-page and relative
/// addresses only. "javascript:", "data:", "file:" and any other scheme become "#";</item>
/// <item>the page carries a Content-Security-Policy that allows no script, no frame, no form and no style from outside,
/// so even a mistake above could not run anything. Images from the web still load when the user opens the page, as the
/// Markdown asked.</item>
/// </list>
/// Right-to-left text: each paragraph, heading, list item and cell takes its direction from its own first letter
/// (unicode-bidi: plaintext), so an Arabic paragraph reads right to left next to an English one.
/// </summary>
internal static class MarkdownPage
{
    /// <summary>The most characters converted (the file limit is in bytes; this bounds the decoded text too).</summary>
    public const int MaxChars = 20_000_000;

    /// <summary>The page's policy: nothing runs, nothing is framed or posted; images and inline styles only.</summary>
    public const string ContentSecurityPolicy = DocumentCapabilities.HtmlPolicy;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseGridTables()
        .UseTaskLists()
        .UseFootnotes()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseAutoIdentifiers()
        .Build();

    public static string Render(string markdown, string title)
    {
        var body = new StringWriter();
        var renderer = new HtmlRenderer(body) { LinkRewriter = SafeUrl };
        Pipeline.Setup(renderer);
        renderer.Render(Markdown.Parse(markdown, Pipeline));
        body.Flush();
        return $"""
            <!DOCTYPE html>
            <html dir="auto">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{ContentSecurityPolicy}">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="generator" content="WinTools">
            <title>{WebUtility.HtmlEncode(title)}</title>
            <style>
            body {"{"} font-family: "Segoe UI", Tahoma, Arial, sans-serif; line-height: 1.6; max-width: 46em; margin: 2em auto; padding: 0 1em; color: #1f1f1f; {"}"}
            p, li, h1, h2, h3, h4, h5, h6, td, th, blockquote, dt, dd {"{"} unicode-bidi: plaintext; text-align: start; {"}"}
            pre, code {"{"} font-family: Consolas, "Courier New", monospace; direction: ltr; unicode-bidi: embed; {"}"}
            pre {"{"} background: #f4f4f4; padding: .75em; overflow-x: auto; {"}"}
            table {"{"} border-collapse: collapse; {"}"}
            th, td {"{"} border: 1px solid #ccc; padding: .3em .6em; {"}"}
            img {"{"} max-width: 100%; {"}"}
            blockquote {"{"} margin-inline-start: 0; padding-inline-start: 1em; border-inline-start: 3px solid #ccc; color: #555; {"}"}
            </style>
            </head>
            <body>
            {body}
            </body>
            </html>

            """.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>A link or image address the page may hold: http(s), mailto, in-page or relative; anything else is "#".</summary>
    public static string SafeUrl(string url)
    {
        var trimmed = url.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#') return trimmed;
        // Browsers ignore tabs, line breaks and leading control characters inside a scheme ("java\tscript:"), so the
        // scheme is read the way they read it.
        var compact = new string([.. trimmed.Where(c => c > ' ' && c != '\u007F')]);
        var colon = compact.IndexOf(':', StringComparison.Ordinal);
        var slash = compact.IndexOfAny(['/', '?', '#']);
        if (colon < 0 || (slash >= 0 && slash < colon)) return compact.StartsWith("//", StringComparison.Ordinal) ? "https:" + compact : trimmed; // relative
        var scheme = compact[..colon].ToLowerInvariant();
        return scheme is "http" or "https" or "mailto" ? trimmed : "#";
    }

    /// <summary>
    /// A last check of the page written: no script element, no inline event handler, no "javascript:" address anywhere,
    /// and the policy in place. Markdown's own text never reaches the page unescaped (a "&lt;" in the page always opens a
    /// tag), so any of these means a fault, and the job fails rather than keep the page.
    /// </summary>
    public static bool IsInert(string html)
    {
        if (!html.Contains(ContentSecurityPolicy, StringComparison.Ordinal)) return false;
        var i = 0;
        while ((i = html.IndexOf('<', i)) >= 0)
        {
            var end = TagEnd(html, i);
            if (end < 0) return false;
            var tag = html[(i + 1)..end];
            i = end + 1;
            if (tag.StartsWith('/') || tag.StartsWith('!')) continue; // a closing tag or the doctype carries no attribute
            var name = new string([.. tag.TakeWhile(char.IsAsciiLetterOrDigit)]).ToLowerInvariant();
            if (name is "script" or "iframe" or "frame" or "object" or "embed" or "form" or "base" or "link" or "svg" or "math" or "style" && !(name == "style" && i < html.IndexOf("</head>", StringComparison.Ordinal))) return false;
            foreach (var (attribute, value) in Attributes(tag[name.Length..]))
            {
                if (attribute.StartsWith("on", StringComparison.Ordinal) || attribute is "style" or "srcdoc" or "formaction") return false;
                if (attribute is "href" or "src" or "action" or "xlink:href" && SafeUrl(WebUtility.HtmlDecode(value)) == "#" && value != "#") return false;
            }
        }
        return true;
    }

    /// <summary>The '>' that closes the tag opened at <paramref name="start"/>, skipping quoted attribute values.</summary>
    private static int TagEnd(string html, int start)
    {
        char quote = '\0';
        for (var i = start + 1; i < html.Length; i++)
        {
            var c = html[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '>') return i;
        }
        return -1;
    }

    /// <summary>The attributes of a tag's inside (after its name): lower-case names and their raw values.</summary>
    private static IEnumerable<(string Name, string Value)> Attributes(string inside)
    {
        var i = 0;
        while (i < inside.Length)
        {
            while (i < inside.Length && (char.IsWhiteSpace(inside[i]) || inside[i] == '/')) i++;
            var start = i;
            while (i < inside.Length && !char.IsWhiteSpace(inside[i]) && inside[i] is not ('=' or '/')) i++;
            if (i == start) { i++; continue; }
            var name = inside[start..i].ToLowerInvariant();
            while (i < inside.Length && char.IsWhiteSpace(inside[i])) i++;
            var value = "";
            if (i < inside.Length && inside[i] == '=')
            {
                i++;
                while (i < inside.Length && char.IsWhiteSpace(inside[i])) i++;
                if (i < inside.Length && inside[i] is '"' or '\'')
                {
                    var q = inside[i];
                    var close = inside.IndexOf(q, i + 1);
                    if (close < 0) close = inside.Length;
                    value = inside[(i + 1)..close];
                    i = close + 1;
                }
                else
                {
                    var s = i;
                    while (i < inside.Length && !char.IsWhiteSpace(inside[i])) i++;
                    value = inside[s..i];
                }
            }
            yield return (name, value);
        }
    }
}
