using System.Net;
using System.Text;

namespace RaceTrade.Web.Services;

/// <summary>
/// Renders mIRC colour/formatting codes as HTML spans.
///
/// Used by two surfaces: the chat window (channel traffic really does carry these codes)
/// and the log pages, because the engine's LogColors helpers emit the same encoding to
/// mark up site names, sections, releases and rules. One converter, one set of CSS
/// classes, no second markup dialect to maintain.
/// </summary>
public static class IrcMarkup
{
    private sealed class Style
    {
        public bool Bold { get; set; }
        public bool Underline { get; set; }
        public bool Italic { get; set; }
        public bool Reverse { get; set; }
        public int? Foreground { get; set; }
        public int? Background { get; set; }

        public void Reset()
        {
            Bold = Underline = Italic = Reverse = false;
            Foreground = Background = null;
        }

        public string ClassName()
        {
            var classes = new List<string>();

            if (Bold) classes.Add("irc-bold");
            if (Underline) classes.Add("irc-underline");
            if (Italic) classes.Add("irc-italic");
            if (Reverse) classes.Add("irc-reverse");
            if (Foreground is { } fg) classes.Add($"irc-fg-{fg}");
            if (Background is { } bg) classes.Add($"irc-bg-{bg}");

            return string.Join(" ", classes);
        }
    }

    public static string ToHtml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var output = new StringBuilder(text.Length + 32);
        var segment = new StringBuilder();
        var style = new Style();

        void Flush()
        {
            if (segment.Length == 0) return;

            var encoded = WebUtility.HtmlEncode(segment.ToString());
            var classes = style.ClassName();

            output.Append(string.IsNullOrEmpty(classes)
                ? encoded
                : $"<span class=\"{classes}\">{encoded}</span>");

            segment.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            switch (c)
            {
                case '\x02':
                    Flush();
                    style.Bold = !style.Bold;
                    continue;

                case '\x03':
                    Flush();
                    if (i + 1 >= text.Length || !char.IsDigit(text[i + 1]))
                    {
                        style.Foreground = null;
                        style.Background = null;
                        continue;
                    }

                    i++;
                    style.Foreground = ReadColor(text, ref i);

                    if (i + 2 < text.Length && text[i + 1] == ',' && char.IsDigit(text[i + 2]))
                    {
                        i += 2;
                        style.Background = ReadColor(text, ref i);
                    }
                    else
                    {
                        style.Background = null;
                    }

                    continue;

                case '\x0F':
                    Flush();
                    style.Reset();
                    continue;

                case '\x16':
                    Flush();
                    style.Reverse = !style.Reverse;
                    continue;

                case '\x1D':
                    Flush();
                    style.Italic = !style.Italic;
                    continue;

                case '\x1F':
                    Flush();
                    style.Underline = !style.Underline;
                    continue;
            }

            // \n is kept: this converter now also renders log messages, and a stack
            // trace collapsed onto one line is unreadable. The .msg cells are
            // white-space: pre-wrap, so the newline survives to the browser.
            if (char.IsControl(c) && c != '\t' && c != '\n')
                continue;

            segment.Append(c);
        }

        Flush();
        return output.ToString();
    }

    private static int ReadColor(string text, ref int index)
    {
        var value = text[index] - '0';

        if (index + 1 < text.Length && char.IsDigit(text[index + 1]))
        {
            value = (value * 10) + text[index + 1] - '0';
            index++;
        }

        return value;
    }
}
