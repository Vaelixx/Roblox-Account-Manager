using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace RobloxAccountManager.Views;

/// <summary>
/// Tiny markdown-ish renderer for GitHub release notes: headings (#), bullets (-/*),
/// **bold** and `code` runs; [links](url) collapse to their text. Anything fancier
/// degrades gracefully to plain text — good enough for changelogs, no WebView needed.
/// </summary>
internal static class ReleaseNotesRenderer
{
    private static readonly Regex InlineToken = new(@"(\*\*.+?\*\*|`[^`]+`)", RegexOptions.Compiled);
    private static readonly Regex LinkPattern = new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex ImagePattern = new(@"!\[[^\]]*\]\([^)]+\)", RegexOptions.Compiled);

    public static void Render(string? markdown, Panel target)
    {
        target.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            // Reached only when this build shipped without embedded notes *and* GitHub was
            // unreachable. Say which of those the user can do something about.
            target.Children.Add(Line(Services.L.T("Updates.NotesUnavailable"), muted: true));
            return;
        }

        foreach (string raw in LogicalLines(markdown))
        {
            string line = raw.TrimEnd();
            line = ImagePattern.Replace(line, "");
            line = LinkPattern.Replace(line, "$1");

            if (line.Trim().Length == 0)
            {
                target.Children.Add(new Border { Height = 6 });
                continue;
            }

            // A horizontal rule separates versions in the full changelog; drawing it as a line
            // beats printing a literal "---".
            string bare = line.Trim();
            if (IsRule(bare))
            {
                target.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 10, 0, 10),
                    Background = Brush("HairlineStrongBrush"),
                });
                continue;
            }

            if (line.StartsWith('#'))
            {
                var heading = Line("", muted: false);
                heading.FontWeight = FontWeights.SemiBold;
                heading.FontSize = 13;
                heading.Foreground = Brush("TextPrimaryBrush");
                heading.Margin = new Thickness(0, 6, 0, 2);
                AddInlines(heading, line.TrimStart('#', ' '));
                target.Children.Add(heading);
                continue;
            }

            string trimmed = line.TrimStart();

            // Blockquote: keep the text, drop the ">" and set it apart.
            if (trimmed.StartsWith(">"))
            {
                var quote = Line("", muted: true);
                quote.Margin = new Thickness(10, 2, 0, 2);
                quote.FontStyle = FontStyles.Italic;
                AddInlines(quote, trimmed.TrimStart('>', ' '));
                target.Children.Add(quote);
                continue;
            }

            bool bullet = trimmed.StartsWith("- ") || trimmed.StartsWith("* ");
            var block = Line("", muted: false);
            if (bullet)
            {
                block.Margin = new Thickness(10, 1, 0, 1);
                block.Inlines.Add(new Run("•  ") { Foreground = Brush("TextMutedBrush") });
                AddInlines(block, trimmed[2..]);
            }
            else
            {
                AddInlines(block, line);
            }
            target.Children.Add(block);
        }
    }

    /// <summary>
    /// The changelog is wrapped at a fixed width in the file. A paragraph or bullet continued on the
    /// next line is joined back into one line here, so it wraps to the window instead of breaking
    /// wherever the file did.
    /// </summary>
    private static List<string> LogicalLines(string markdown)
    {
        var lines = new List<string>();
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string bare = raw.Trim();
            string previous = lines.Count > 0 ? lines[^1].Trim() : "";
            bool continues = bare.Length > 0 && previous.Length > 0
                             && !StartsBlock(bare) && !previous.StartsWith('#') && !IsRule(previous);
            if (continues) lines[^1] = lines[^1].TrimEnd() + " " + bare;
            else lines.Add(raw);
        }
        return lines;
    }

    private static bool StartsBlock(string bare)
        => bare.StartsWith('#') || bare.StartsWith("- ") || bare.StartsWith("* ") || bare.StartsWith('>')
           || bare.StartsWith('|') || bare.StartsWith("```") || IsRule(bare);

    private static bool IsRule(string bare)
        => bare.Length >= 3 && (bare.All(c => c == '-') || bare.All(c => c == '*') || bare.All(c => c == '_'));

    private static TextBlock Line(string text, bool muted) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(muted ? "TextMutedBrush" : "TextSecondaryBrush"),
    };

    private static void AddInlines(TextBlock tb, string text)
    {
        foreach (string part in InlineToken.Split(text))
        {
            if (part.Length == 0) continue;

            if (part.Length > 4 && part.StartsWith("**") && part.EndsWith("**"))
                tb.Inlines.Add(new Run(part[2..^2])
                {
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("TextPrimaryBrush"),
                });
            else if (part.Length > 2 && part[0] == '`' && part[^1] == '`')
                tb.Inlines.Add(new Run(part[1..^1])
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Foreground = Brush("TextPrimaryBrush"),
                });
            else
                tb.Inlines.Add(new Run(part));
        }
    }

    private static Brush Brush(string key)
        => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
