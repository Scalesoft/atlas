# Unicode emoji

```ts
import { createEmojiPlugin, insertEmoji } from '@atlas/markdown/plugins/emoji'

const plugins = [createEmojiPlugin({ aliases: { rocket: ['start projektu'] } })]
controller.dispatch(insertEmoji('rocket'))
```

Install the optional peer `@tiptap/extension-emoji` from the same Tiptap release as the editor. The plugin draws, serializes and inserts emoji from its own name table (`unicode.json`: name → character, the first owner of every shortcode, emoticons), generated from that release's Unicode catalog by `emoji.test.ts` (`ATLAS_UPDATE_EMOJI=1` after a Tiptap upgrade). The upstream catalog itself (600 KB) loads through `import()` with the first search of the picker, so a document never loads it; `searchEmoji` is therefore async. The node is the upstream `emoji` node rebuilt on that table with the same schema, HTML and commands. The main editor/core entrypoint does not import emoji data.

Typing `:srdce` or `:heart` opens the shared suggestion list. `/emoji` inserts the colon trigger and starts that same cursor-owned picker. An empty query starts with a curated set of common reactions; standalone Unicode components such as regional-indicator letters, skin tones and hair parts are excluded from picker results. Arrows, Home/End, Enter, Escape, Tab, outside dismissal and readonly follow the shared suggestion contract. The action is available in the slash menu; this version does not add a toolbar action. Searches include upstream names, tags, shortcodes and a small Czech alias table. `aliases` extends that table and `limit` defaults to 20 (maximum 100).

In `rich`, an explicit selection or `insertEmoji(shortcode)` creates the upstream `emoji` inline node with its canonical `name`. The plugin schema is version 1 and must be installed before opening a saved rich document. `emojiNode(shortcode)` creates equivalent JSON for imports. Rich JSON preserves identity; Markdown, text and copied text use the catalog's Unicode character and correctly declare the Markdown projection lossy when a node is present. Unknown names already present in a persisted node render as `:name:`.

In `markdown-safe`, the plugin contributes only a suggestion extension. It does not register the emoji node: selections insert Unicode text and retain the ordinary Markdown round-trip contract.

The default catalog is Unicode only. Image URLs are removed from the upstream data, image support probing is unnecessary, and no GitHub image catalog or CDN is configured. Both profiles therefore render the same native Unicode characters, whose appearance depends on the system font. Custom image emoji are outside this version's contract.

Pasted and typed Unicode remains text, including variation selectors, skin tones and joined sequences. The upstream automatic text-to-node normalizer is disabled because it removes variation selectors; nodes are created by explicit selection/commands. Shortcode paste conversion is also disabled. Ordinary emoticons remain unchanged by default. `enableEmoticons: true` explicitly enables upstream rich input rules; it does not enable conversion in the Markdown-safe profile.

Tests cover versioned rich persistence, static HTML, loss-aware Markdown/text, Unicode preservation, safe schema/round-trip, readonly, discrete undo, both keyboard pickers and trigger exclusions. The plugin does not claim support for custom image emoji or identity-preserving plain Markdown export.
