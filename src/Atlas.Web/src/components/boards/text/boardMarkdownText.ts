import type { MarkdownPlugin } from "@atlas/markdown/core";
import { Extension } from "@tiptap/core";
import { Plugin } from "@tiptap/pm/state";

/**
 * Rules of a board's Markdown texts that the editor does not apply itself:
 * links only to http(s), mailto and Atlas, and the inline texts (a connector's label, a frame's title) are one line of inline
 * Markdown.
 */

/**
 * Text input of several characters at once (an IME or dictation committing, autocorrect, an automated browser) goes in one
 * character at a time, each through the editor's input rules, as if typed. Works around `run` in @tiptap/core 3.31 (InputRule.ts):
 * for such input it puts the rule's range `match.length − text.length` past the caret, so a mark rule (`**x**`) throws
 * „Position N out of range“ and the text lands as literal `\*\*x\*\*`. A composition in progress belongs to the IME.
 * shortcut: board texts only; drop it once tiptap or the editor skips matches shorter than the input.
 */
export const boardTextInput: MarkdownPlugin = {
  id: "atlas-board-text-input", apiVersion: 1,
  create: () => ({
    extensions: [Extension.create({
      name: "atlasBoardTextInput",
      // Ahead of every extension's input rules.
      priority: 10_000,
      addProseMirrorPlugins: () => [new Plugin({
        props: {
          handleTextInput(view, from, to, text) {
            const characters = Array.from(text);
            if (view.composing || characters.length < 2) return false;
            let start = from, end = to;
            for (const character of characters) {
              const insert = () => view.state.tr.insertText(character, start, end);
              if (!view.someProp("handleTextInput", handle => handle(view, start, end, character, insert))) view.dispatch(insert());
              // The caret after this character, also when a rule rewrote the text before it.
              start = end = view.state.selection.from;
            }
            return true;
          },
        },
      })],
    })],
  }),
};

/** A link a board text may keep: http(s), mailto, or a link into Atlas (relative, a hash route, the same origin). */
export function allowedBoardLink(href: string, origin = location.origin): boolean {
  if (!href || /[\s\u0000-\u001f\u007f]/.test(href)) return false;
  try {
    const url = new URL(href, `${origin}/`);
    return ["http:", "https:", "mailto:"].includes(url.protocol);
  } catch { return false; }
}

/**
 * The static renderer's hook that turns every other link into its text: the editor renders `tel:` (and whatever an older writer
 * stored), a board text does not.
 */
export const boardLinkPolicy: MarkdownPlugin = {
  id: "atlas-board-links", apiVersion: 1,
  create: () => ({
    onRender: ({ container }) => {
      for (const link of container.querySelectorAll<HTMLAnchorElement>("a[href]"))
        if (!allowedBoardLink(link.getAttribute("href") ?? "")) link.replaceWith(...link.childNodes);
    },
  }),
};

type LinkedJson = { marks?: { type: string; attrs?: { href?: unknown } }[]; content?: LinkedJson[] };
/**
 * The document of a board text without the links a board text may not keep (`allowedBoardLink`), their text kept: what
 * `boardLinkPolicy` does for the static renderer, for the editor that draws the text.
 */
export function keepBoardLinks<T extends { document: unknown }>(value: T): T {
  const strip = (node: LinkedJson): LinkedJson => ({
    ...node,
    ...(node.marks && { marks: node.marks.filter(mark => mark.type !== "link" || allowedBoardLink(String(mark.attrs?.href ?? ""))) }),
    ...(node.content && { content: node.content.map(strip) }),
  });
  return { ...value, document: strip(value.document as LinkedJson) };
}

/** One line, its start escaped where Markdown would begin a block (heading, quote, list, rule, fence), for inline rendering. */
export function escapeInlineMarkdown(text: string): string {
  const line = text.replace(/\s*\n\s*/g, " ");
  if (/^\s*([-*_])(\s*\1){2,}\s*$/.test(line)) return line.replace(/^(\s*)/, "$1\\");
  return line
    .replace(/^(\s*)(#{1,6}(?=\s|$)|>|[-+*](?=\s)|```|~~~)/, "$1\\$2")
    .replace(/^(\s*\d+)([.)])(?=\s)/, "$1\\$2");
}

/** The inline text of what an editor made of a label: block markup at the start of each line dropped, the lines joined. */
export function flattenInlineMarkdown(markdown: string): string {
  return markdown.split("\n")
    .map(line => line.replace(/^\s*(?:>\s?)*(?:#{1,6}\s+|(?:[-+*]|\d+[.)])\s+(?:\[[ xX]\]\s+)?)?/, "").trim())
    .filter(Boolean).join(" ");
}

/**
 * Whether Markdown renders as one plain paragraph of exactly this text: a letter first, then only letters, digits, spaces and calm
 * punctuation (dashes, slash and plus inside the line, a dot or colon only before a space or the end, a dot between digits, an
 * ampersand before a space); no markup character, no entity, no link, no line break.
 * Such a text is drawn without the static renderer, which builds a whole editor for it (BD2-1: that is most of a note's cost).
 */
export const isPlainBoardText = (text: string) => text.length <= 600 && /^\p{L}(?:[\p{L}\p{N} ,;()'"„“”‘’–—…%€+/-]|[.:!?](?=\s|$)|\.(?=\d)|&(?=\s))*$/u.test(text) && !/\s{2}|\s$/.test(text);

/**
 * No spelling marks on a board's text at rest: it is read there, as a page's reader reads it; the editing turns them on again
 * (`writing` is read whenever the editor updates, and switching the right to write updates it).
 */
export function restingSpellcheck(writing: () => boolean): MarkdownPlugin {
  return {
    id: "atlas-board-resting-spellcheck", apiVersion: 1,
    create: () => ({
      extensions: [Extension.create({
        name: "atlasBoardRestingSpellcheck",
        addProseMirrorPlugins: () => [new Plugin({ props: { attributes: (): Record<string, string> => (writing() ? {} : { spellcheck: "false" }) } })],
      })],
    }),
  };
}
