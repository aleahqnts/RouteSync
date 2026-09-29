using System.Text.RegularExpressions;

namespace RouteSyncWeb.Tests;

/// <summary>The views against the content security policy.</summary>
/// <remarks>
/// The policy runs no inline script that lacks the request's nonce. A handler written as
/// onclick="…", or a script block without the nonce, still compiles and renders, and then
/// does nothing in the browser, so nothing short of clicking every button would notice.
/// These read the view files instead.
/// </remarks>
public class ContentSecurityPolicyTests
{
    // An on* attribute, or the same written into a C# string ("onclick=f()"). Lower case
    // only, so a C# variable such as onIncidents is not taken for one.
    private static readonly Regex Handler =
        new(@"(?<![\w.@$-])on[a-z]+\s*=\s*(?:[""']|[A-Za-z_$][\w$.]*\()");

    private static readonly Regex ScriptTag = new(@"<script\b[^>]*>", RegexOptions.IgnoreCase);

    private static readonly string[] ExecutableTypes =
        { "", "text/javascript", "application/javascript", "module", "importmap" };

    [Fact]
    public void No_view_has_an_inline_event_handler()
    {
        var found = Views().SelectMany(v => Matches(v, Handler)).ToList();

        Assert.True(found.Count == 0,
            "The content security policy refuses inline event handlers. Name the handler in " +
            "data-on and register it with rsOn (wwwroot/js/data-on.js):\n" + string.Join("\n", found));
    }

    [Fact]
    public void Every_inline_script_carries_the_nonce()
    {
        var found = Views()
            .SelectMany(v => Matches(v, ScriptTag))
            .Where(m => !Regex.IsMatch(m, @"\ssrc\s*=") && IsExecutable(m) && !m.Contains("nonce=\"@CspNonce.Value\""))
            .ToList();

        Assert.True(found.Count == 0,
            "The content security policy refuses an inline script without the nonce. " +
            "Open it as <script nonce=\"@CspNonce.Value\">:\n" + string.Join("\n", found));
    }

    private static bool IsExecutable(string tag)
    {
        var type = Regex.Match(tag, @"\stype\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
        return ExecutableTypes.Contains(type.Success ? type.Groups[1].Value.Trim().ToLowerInvariant() : "");
    }

    /// <summary>Each view's markup, with Razor comments and script bodies blanked out.</summary>
    /// <remarks>
    /// A comment or a script may mention onclick or a script tag without being one. Blanking
    /// keeps every line where it was, so a finding names the right line.
    /// </remarks>
    private static IEnumerable<(string Path, string Text)> Views()
    {
        var root = ViewsRoot();
        foreach (var path in Directory.EnumerateFiles(root, "*.cshtml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            text = Regex.Replace(text, @"@\*.*?\*@", Blank, RegexOptions.Singleline);
            text = Regex.Replace(text, @"(<script\b[^>]*>)(.*?)(</script>)",
                m => m.Groups[1].Value + Blank(m.Groups[2]) + m.Groups[3].Value,
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            yield return (Path.GetRelativePath(Path.GetDirectoryName(root)!, path), text);
        }
    }

    private static string Blank(Capture c) => Regex.Replace(c.Value, @"[^\r\n]", " ");

    private static IEnumerable<string> Matches((string Path, string Text) view, Regex pattern)
    {
        foreach (Match m in pattern.Matches(view.Text))
        {
            var line = view.Text.Take(m.Index).Count(c => c == '\n') + 1;
            yield return $"{view.Path}:{line}: {m.Value}";
        }
    }

    /// <summary>RouteSyncWeb/Views, found by walking up from wherever the tests run.</summary>
    private static string ViewsRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var views = Path.Combine(dir.FullName, "RouteSyncWeb", "Views");
            if (Directory.Exists(views)) return views;
        }
        throw new DirectoryNotFoundException("RouteSyncWeb/Views was not found above " + AppContext.BaseDirectory);
    }
}
