using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Syntax.Inlines;

namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>Markdig syntax the source wikis add on top of CommonMark and the bundled extensions.</summary>
public static class WikiMarkdownSyntax
{
    /// <summary>
    /// Backslash-escapes every character CommonMark or the extensions of <see cref="WikiMarkdownConverter.Pipeline"/>
    /// could read as syntax, for text that becomes the converter's input (HTML pages and blocks). The stored
    /// projection uses the editor's own escaping instead (<see cref="Platform.Content.EditorMarkdownWriter"/>).
    /// </summary>
    public static string Escape(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '~' or '=' or '$' or '^' or '%' or '+')
            {
                builder.Append('\\');
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    public static MarkdownPipelineBuilder UseWikiSyntax(this MarkdownPipelineBuilder builder)
    {
        builder.InlineParsers.InsertBefore<LinkInlineParser>(new WikiLinkParser());
        builder.InlineParsers.InsertBefore<LinkInlineParser>(new ObsidianCommentParser());
        return builder;
    }

    private sealed class WikiLinkParser : InlineParser
    {
        private const int MaximumLength = 512;

        public WikiLinkParser()
        {
            OpeningCharacters = ['[', '!'];
        }

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            var start = slice.Start;
            var embed = slice.CurrentChar == '!';
            var open = start + (embed ? 1 : 0);
            if (open + 1 > slice.End || slice.Text[open] != '[' || slice.Text[open + 1] != '[')
            {
                return false;
            }
            var innerStart = open + 2;
            var close = -1;
            for (var index = innerStart; index < slice.End && index - innerStart <= MaximumLength; index++)
            {
                var character = slice.Text[index];
                if (character is '\n' or '\r' or '[')
                {
                    return false;
                }
                if (character == ']')
                {
                    if (slice.Text[index + 1] != ']')
                    {
                        return false;
                    }
                    close = index;
                    break;
                }
            }
            if (close <= innerStart)
            {
                return false;
            }
            // Obsidian escapes the alias separator inside tables.
            var inner = slice.Text[innerStart..close].Replace("\\|", "|", StringComparison.Ordinal);
            var separator = inner.IndexOf('|');
            var target = (separator < 0 ? inner : inner[..separator]).Trim();
            var alias = separator < 0 ? null : inner[(separator + 1)..].Trim();
            if (target.Length == 0 && string.IsNullOrEmpty(alias))
            {
                return false;
            }
            processor.GetSourcePosition(start, out var line, out var column);
            processor.Inline = new WikiLinkInline
            {
                Target = target,
                Alias = string.IsNullOrEmpty(alias) ? null : alias,
                IsEmbed = embed,
                Source = slice.Text[start..(close + 2)],
                Line = line,
                Column = column,
            };
            slice.Start = close + 2;
            return true;
        }
    }

    private sealed class ObsidianCommentParser : InlineParser
    {
        public ObsidianCommentParser()
        {
            OpeningCharacters = ['%'];
        }

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            var start = slice.Start;
            if (start + 1 > slice.End || slice.Text[start + 1] != '%')
            {
                return false;
            }
            var end = slice.Text.IndexOf("%%", start + 2, slice.End - start - 1, StringComparison.Ordinal);
            if (end < 0 || end + 1 > slice.End)
            {
                return false;
            }
            processor.GetSourcePosition(start, out var line, out var column);
            processor.Inline = new ObsidianCommentInline { Source = slice.Text[start..(end + 2)], Line = line, Column = column };
            slice.Start = end + 2;
            return true;
        }
    }
}
