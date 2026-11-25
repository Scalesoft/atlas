# @atlas/markdown

A reusable, headless-friendly Markdown / rich-text editor for Vue 3, built on
Tiptap. This repository is the package's source of truth.

## Features

- Notion-like UX: slash menu (`/`), drag handles with custom drag preview +
  yield animation, paper mode, fullscreen toggle.
- Multi-block selection drag (heading + paragraphs in one drag).
- A portable `markdown-safe` profile and a lossless, versioned JSON `rich` value. Markdown export of rich content can be lossy.
- The document footer reports current content fidelity: `Markdown` when its Markdown projection is lossless, `Rich dokument` otherwise. It updates after edits, component removal and undo, using built-in and plugin loss checks. This indicator does not change the configured profile or persisted value type; rich documents retain their complete JSON data.
- Tables with hover handles, row/column editing, portable column alignment in Markdown-safe, and per-cell alignment in rich mode.
  The rich profile also enables row resize, merge/split and cell backgrounds.
- Markdown round-trip (`tiptap-markdown`) — paste preserves source markdown,
  `dirty` state diffs against initial content.
- Optional Yjs collaboration adapter for ASP.NET Core SignalR.
- TypeScript-first; ships `.d.ts` declarations.

## Install

The package is part of the Atlas repository and is not published to a registry: it lives in the
`src/Atlas.Web` pnpm workspace, and Atlas imports its built `dist`. The Tiptap packages are peer
dependencies, so a host installs them itself:

```bash
npm install vue @tiptap/core @tiptap/vue-3 @tiptap/starter-kit @tiptap/pm \
  @tiptap/extension-character-count @tiptap/extension-drag-handle-vue-3 \
  @tiptap/extension-highlight @tiptap/extension-image \
  @tiptap/extension-link @tiptap/extension-node-range \
  @tiptap/extension-placeholder @tiptap/extension-table \
  @tiptap/extension-table-cell @tiptap/extension-table-header \
  @tiptap/extension-table-row @tiptap/extension-task-item \
  @tiptap/extension-task-list @tiptap/extension-text-align \
  @tiptap/extension-typography @tiptap/extension-underline \
  @tiptap/suggestion @floating-ui/dom tiptap-markdown
```

## Controller-backed Vue editor

```vue
<script setup lang="ts">
import { ref } from 'vue'
import { MarkdownEditor } from '@atlas/markdown/vue'
import '@atlas/markdown/styles/base.css'
import '@atlas/markdown/themes/ocean.css'
const description = ref('A **short** description.')
</script>

<template>
  <MarkdownEditor v-model="description" variant="compact"
    :visible-lines="2" ariaLabel="Description" />
</template>
```

`variant="document"` is the default. Compact mode defaults to an inline-only toolbar, no block handle or fullscreen. `visibleLines` (1–3) fixes viewport height, **not** content length: longer text scrolls and remains in the value. Local updates are synchronous. Supply a nonempty `aria-label` (validated at runtime). `readonly` hides editing controls; `MarkdownRenderer` accepts `value`, `profile`, and `aria-label` for a read-only view; `variant="document"` lays it out like the editor's document variant (paper typography, wrapped tables, `contenteditable="false"` content) and `outline` adds the editor's outline over the rendered headings, so a reading view can use the renderer instead of a readonly editor. Add `createStaticCodeHighlighting()` from `/plugins/editor-tools` to its plugins for the editor's code highlighting.

## Hostitelské volby prezentace

Pro editor uvnitř rámečku lze ponechat vlevo jen úchyt a přidávat bloky jemným plusem mezi nimi:

```vue
<MarkdownEditor v-model="text" aria-label="Dokument"
  :show-block-add-button="false" :show-block-inserter="true" />
```

`showBlockAddButton` (výchozí `true`) ovládá plus vedle úchytu. Nezávislé
`showBlockInserter` (výchozí `false`) zobrazí plus u mezery pod ukazatelem,
včetně začátku a konce dokumentu. Obě volby lze měnit za běhu a podporuje je
také `EditorSurface` a legacy `PoolEditor`. Compact a readonly režim meziblokové
ovládání nezobrazují. `Alt+Insert` zaměří plus za aktuálním blokem; na dotyku
se nabídne po klepnutí do bloku. Kliknutí otevře stejnou slash nabídku jako boční
plus a vytvoří samostatný krok undo. Linka i plus používají akcent a jemné
prolínání společné s vizualizací přesunu, respektují reduced motion a nezabírají
místo v toku dokumentu. Při samotném přetahování ustoupí indikátoru přesunu.

Nepovinné props ladí povrch editoru pro hostitelskou aplikaci; výchozí hodnoty
zachovávají dosavadní vzhled.

| Prop | Výchozí | Účinek |
|---|---|---|
| `toolbarPresentation` | `'expanded'` | `'insert-menu'` sbalí akce pluginů do jediného tlačítka „Vložit“ s nabídkou (`role="menu"`); `'expanded'` ponechá řadu tlačítek. Jiná hodnota se chová jako `'expanded'`. |
| `showProfileLabel` | `true` | `false` skryje v patičce pouze štítek „Rich dokument“ / „Markdown“; klávesová nápověda i přepínač celé obrazovky zůstávají. |
| `showPresence` | `false` | `true` vykreslí nad obsahem stopu spolupracovníků, ale jen když je spojení `online` a je přítomen aspoň jeden vzdálený uživatel. |
| `smoothCaret` | `false` | `true`: kurzor při psaní a pohybu plynule doklouže na nové místo (transform, `--md-motion-fast`) a bliká až v klidu; nativní kurzor je průhledný. Při výběru, na vybraném uzlu, u mezerového kurzoru a bez fokusu se skryje, s omezenými animacemi se jen přesune a nebliká. Barva `--md-caret-color` (výchozí barva textu). Nastavuje se jednou. |
| `documentTitle` | *(nepovinné)* | Zobrazí se v záhlaví celé obrazovky místo obecného `ariaLabel`; vpravo je vždy tlačítko „Zavřít“ + `<kbd>Esc</kbd>`. |

```vue
<MarkdownEditor v-model="page" ariaLabel="Stránka" documentTitle="Domácí řád"
  toolbar-presentation="insert-menu" :show-profile-label="false" :show-presence="true" />
```

Nabídka „Vložit“ se zavírá klávesou Escape nebo Tab, kliknutím mimo i výběrem položky;
šipky přecházejí mezi položkami. V editoru je vždy otevřená nejvýše jedna nabídka.
Vlastní slot `toolbar` má přednost před oběma režimy lišty.

## Themes

Ocean is the default light theme. Sand and Forest provide warmer light alternatives;
Midnight is the complete dark theme. Import the base stylesheet and one or more
themes. Ocean applies by default, while the other themes are scoped so several
editor themes can coexist on one wiki page.

```vue
<script setup lang="ts">
import '@atlas/markdown/styles/base.css'
import '@atlas/markdown/themes/ocean.css'
import '@atlas/markdown/themes/sand.css'
import '@atlas/markdown/themes/forest.css'
import '@atlas/markdown/themes/midnight.css'
</script>

<template>
  <MarkdownEditor v-model="dayPage" ariaLabel="Day page" />
  <MarkdownEditor v-model="nightPage" class="md-theme-midnight" ariaLabel="Night page" />
</template>
```

Use `md-theme-sand`, `md-theme-forest`, or `md-theme-midnight` on the editor
component or `MarkdownRenderer`. Midnight sets `color-scheme: dark`, provides a
dark canvas for read-only pages, and body-level editor overlays inherit the same
tokens. A host can also override the public CSS custom properties on
`.md-editor` to create a custom palette without changing the document model.

Set the optional `formatting` list to choose tools in the floating toolbar. Omit it to keep the existing defaults; `[]` hides the floating toolbar. The list is reactive and can change without remounting or losing content/history. Tools retain their standard grouping and order. It configures the toolbar only, not the document schema, keyboard shortcuts, slash menu or permissions.

```vue
<MarkdownEditor v-model="richValue" profile="rich" variant="compact"
  :formatting="['bold', 'italic', 'underline', 'link', 'headings']"
  ariaLabel="Short description">
  <template #toolbar="{ controller, state }">
    <MyCompactTools :controller="controller" :state="state" />
  </template>
</MarkdownEditor>
```

`headings` exposes H1–H6 using the existing smaller compact typography. Underline and highlight require `profile="rich"` and a `MarkdownRichValue`; the Markdown-safe profile hides these controls even if requested, because it cannot preserve their content. Hiding a tool never strips existing formatting. Available items (type `MarkdownFormattingItem`, exported from `/vue`): `headings`, `bold`, `italic`, `underline`, `strike`, `highlight`, `code`, `link`, `bullet-list`, `ordered-list`, `task-list`, `blockquote`, `code-block`, `alignment`.

Use the existing slots for additional Vue components, or `plugins` for reusable actions and content features. Plugin toolbar/slash actions can declare `variants: ['compact']`. Plugin `actions.selection` appear over a selection, in the formatting bubble after the block tools and in the selected-blocks toolbar. The `toolbar` slot replaces only the plugin toolbar; floating text formatting remains available. Try `/?story=compact-formatting` in the playground; the existing `compact-1`, `compact-2` and `compact-3` stories retain their defaults.

Rich mode requires `{ format: 'atlas-rich', version: 1, document: … }`, exported as `MarkdownRichValue` from `/core`. Documents stored before 5.0 carry the legacy id `scalesoft-rich`: every input (`MarkdownRichInput`, `isRichFormat`) still reads it, and every written value uses `RICH_FORMAT` (`atlas-rich`). Invalid envelopes fail atomically. Do not persist rich content via its Markdown projection. Profiles, plugins and collaboration identity are mount-time configuration; use a new Vue `key` to switch them. `variant`, `placeholder` and `imageUpload` are reactive view configuration: switching to compact mode safely leaves fullscreen and closes an open slash menu before applying its new command set, changing the placeholder redraws the empty paragraph, while replacing the upload service aborts its pending requests and file picker before later uploads use the new service. Disabling `fullscreenable` while fullscreen also returns the surface to its embedded position.

Markdown-safe tables retain one header row. Arbitrary header toggles and merged cells are unavailable because they cannot roundtrip through GFM. Alignment commands apply to the whole selected column; rich mode retains per-cell control. Rich tables that cannot be represented as a GFM grid keep their cell content in the Markdown projection, with `markdownLossy: true`; the rich JSON remains authoritative.

Shift+Enter in a table cell roundtrips using conventional `<br>` markup. Only bare break tags inside table cells are recognized: general HTML remains disabled, and escaped tags or tags inside inline code remain literal. A rich text node containing raw newline characters is projected as cell breaks with `markdownLossy: true`, because its original JSON shape differs.

The public component exposes `controller`, emits `created(controller)` once after local initialization or authoritative realtime hydration, and `change(value, { origin })` for subsequent content changes. Host-driven local value replacement does not echo `update:modelValue`. Collaborative mode never emits `update:modelValue`.

## Headless controller and custom Vue UI

### Markdown paste and import

Pasting `text/plain` parses supported Markdown in both profiles (headings, lists,
GFM tables, links, images and fenced code). Hold Shift while pasting to insert
literal text. Code blocks always keep text literal. Clipboard HTML takes the
native HTML path instead of being interpreted as Markdown. Table size and
attribute validation still applies; a rejected paste leaves the document intact.
Each paste is one undo step, separate from adjacent typing and other pastes.

The link form supports HTTP(S), mailto, tel, relative paths and fragments. Raw
whitespace/control characters and other protocols are rejected; encode spaces as
`%20`. Zrušit discards the draft, Použít confirms it, and Odebrat odkaz removes the
mark while preserving text. Each confirmation/removal is a separate undo step.

For a **whole local document** import in a browser, the existing public projection
can convert the supported Markdown subset into a rich value:

```ts
import { createMarkdownEditorController, type MarkdownRichValue } from '@atlas/markdown/core'

function importMarkdown(markdown: string): MarkdownRichValue {
  const importer = createMarkdownEditorController({
    profile: 'markdown-safe', source: { kind: 'local', value: markdown },
  })
  try {
    importer.attachView(document.createElement('div'), { ariaLabel: 'Markdown import' })
    return importer.getProjection().rich
  } finally {
    importer.destroy()
  }
}
// Pass the result as the initial rich value, or to a local controller's setValue.
```

This is a schema-based Markdown import, not an arbitrary HTML/plugin converter.
Unsupported syntax may remain literal or be normalized; raw HTML is not enabled.
Show a preview before replacing an existing document. This does not recover rich
plugin data from a lossy Markdown export. `setValue` is a whole local-document
replacement, not a collaborative import or insertion command. To import at the
current selection, paste into the editor; that path is undoable. Persist the rich
JSON afterward, not its Markdown projection.

#### Markdown dialect: `a-contact` and `a-chart`

Contacts and charts have a lossless Markdown form, written by the projection and read on every Markdown path where
the schema has the node. A list of contacts is a fence with an a-YAML sequence (a strict YAML subset: maps,
sequences, short `[a, b]` lists and `|` blocks; no anchors, aliases, tags or flow maps; a plain value of a field the
component types as text is read exactly as written, so `phones: [+420777123456]` and `title: 2026` stay text), a single contact in text is
a link whose label is the name and whose query carries the data (`phone` and `email` repeat, `internalUser.id`):

````markdown
```a-contact
- name: Jana Nováková
  title: Vedoucí provozu
  phones: ["+420 777 123 456"]
  emails: [jana.novakova@example.test]
```

Volejte [Jana Nováková](a-contact:?name=Jana%20Nováková&phone=+420%20777%20123%20456).
````

A chart is a fence with an optional header (`title`, `kind` unless `bar`, `source`), a `---` line and its grid as a
GFM table (`\|`, `\\` and `\n` escape a cell). A chart linked to a table names it by the `{#id}` paragraph written
after that table and by the 1-based rows and columns of the rectangle, header row included; on reading the table's
values win over the grid. A chart whose link the dialect cannot carry keeps the readable text fallback.

````markdown
| Období | Tržby |
| --- | --- |
| Leden | 12,5 |

{#tabulka-trzby}

```a-chart
title: Tržby 2026
kind: line
source:
  table: tabulka-trzby
  rows: 1-2
  columns: 1-2
---
| Období | Tržby |
| --- | --- |
| Leden | 12,5 |
```
````

The `a-` namespace is reserved: a code block whose language starts with `a-` is written as ```` ```a-code a-x ````.
Reading is lenient everywhere (an unknown or invalid component stays a code block or literal text). An API that
writes Markdown uses the strict mode, which reports every such construct with its line and column instead:

```ts
import { parseMarkdownDialect } from '@atlas/markdown/core'

const { document, errors, warnings } = parseMarkdownDialect(editor, markdown, { strict: true })
// errors: [{ line: 3, column: 1, construct: 'a-chart', message: 'Neplatná komponenta a-chart: …' }]
```

Validation and limits are the plugins' own (`normalizeContacts`, `normalizeChart`).

The dialect loads on demand, so a page that only shows documents never downloads it. Creating an editor controller
or a source session starts `loadMarkdownDialect()`; until it resolves, parsing Markdown that contains an `a-` component
or writing a document with a contact or chart throws instead of keeping the component as code or text. Markdown without
components parses and writes as before. A host that parses or serializes right away, without an editor, awaits it once:

```ts
import { loadMarkdownDialect, parseMarkdownDialect } from '@atlas/markdown/core'

const dialect = await loadMarkdownDialect() // the a-YAML, link and fence helpers: dialect.parseMarkdownDialectYaml, …
const { document, errors } = parseMarkdownDialect(editor, markdown, { strict: true })
```

#### Pasting and dropping foreign-wiki Markdown

Markdown text paste and `.md` file drop share one import boundary (GitHub wiki,
Obsidian and Wiki.js pages are the reference inputs):

- Headings keep their level H1–H6 (`#` to `######`), also in Markdown values
  loaded into the editor and in the page source.
- CommonMark soft line wraps become spaces; only explicit breaks (trailing `\` or
  two spaces) become line breaks. GFM tables import on paste and drop alike.
- A leading YAML front matter block (`---` … `---`) is never inserted.
- Math is edited where it is shown. The runtime's `edit` (a click or Enter on the formula, or the toolbar) opens the
  LaTeX field under a block formula or in the anchored popover of an inline one; the node changes with every keystroke
  (one typing step of the history, Esc closes, Ctrl+Z undoes), a parse error is shown under the field while the last good
  render stays. Typing `$x^2$ ` (no space inside the `$`, not digits only, not in code or links) makes an inline formula and
  Backspace right after gives the text back; `$$` + Enter in an empty paragraph makes a block formula and opens its field.
  While a `$…` is open, text rules such as `^2` → ² do not rewrite it.
- `$…$` and `$$…$$` keep their TeX source untouched by Markdown escapes and
  emphasis. With the math plugin they become inline/block math nodes; otherwise
  the literal source, delimiters included, is inserted as text. Math is part of
  the editor dialect (below): with the plugin, stored Markdown values parse it
  too; without it they keep plain CommonMark semantics (their serialized
  backslashes must round-trip). Plugins can claim the same source through
  `defineMarkdownBlockModule({ parseHTML: [{ tag: 'span[data-md-math="inline"]', data }] })`;
  core marks it as `span`/`div[data-md-math="inline"|"block"]` with the TeX in
  `data-md-expression`. The module's optional `markdown(data)` returns the source
  to write back, or `undefined` for its lossy text fallback
  ([content modules](../../docs/markdown/content-modules.md)).
- A dropped file that still cannot be represented reports the localized
  `markdownStrings.markdownImportFailed` notice instead of an engine error.

Foreign-wiki syntax maps onto existing nodes during paste and drop only (stored
values stay CommonMark), except the editor dialect described after the table. A
construct maps only when the schema has its node or mark; otherwise it stays
literal text and is reported (see below):

| Source syntax | Result |
| --- | --- |
| GitHub/Obsidian alerts `> [!NOTE]`, `> [!warning] Title`; Wiki.js `> …` followed by `{.is-warning}` | Callout (`note`/`tip`/`warning`/`important`/`caution`; Obsidian `danger`, `error`, `bug`, `failure` and Wiki.js `is-danger` → `caution`), a title becomes a bold first paragraph. Markdown-safe: a quote headed by the bold label. |
| Obsidian foldable `> [!tip]- Title` / `+` | A heading `Title` over the callout (every heading folds). |
| ```` ```math ```` | Math block (math plugin), else a `math` code block. |
| ```` ```mermaid ```` | Diagram (diagrams plugin, subject to its validation), else a `mermaid` code block. |
| `==text==` | Highlight mark (rich). Not inside code spans, link destinations, autolinks or bare URLs. |
| `[^1]` + `[^1]: text` | Superscript number (`¹`, or the `superscript` mark if a plugin adds one) and a rule plus numbered endnote list at the end, in reference order. |
| `~sub~`, `^sup^` | `subscript`/`superscript` marks when a plugin provides them. |
| `[[Page]]`, `[[Page#Heading]]`, `[[Page\|alias]]` | A link from `resolveWikiLink`. |
| `![[image.png]]`, `![[Page#Heading]]` | An image or link from `resolveEmbed`. |
| `<br>` | Line break. Other raw HTML and `<!-- -->` / `%%…%%` comments stay literal. |

**Editor Markdown dialect.** GitHub/Obsidian alerts, `==text==`, ```` ```mermaid ````,
```` ```math ```` and `$…$`/`$$…$$` are also the editor's own Markdown: they are parsed
on every path (value load, `insertContent`, the legacy editor, the page source and
imports) whenever the schema has the callout, highlight, diagram or math node, and
the Markdown projection writes those nodes back the same way, so they no longer make
it `markdownLossy`:

- A callout becomes `> [!NOTE]`, `> [!TIP]`, `> [!WARNING]`, `> [!IMPORTANT]` or
  `> [!CAUTION]` followed by its quoted content; when the content does not start with a
  paragraph, an empty `>` line follows the marker. All five GitHub alerts round-trip unchanged.
- Highlight is written as `==text==`.
- A diagram without a caption becomes a ```` ```mermaid ```` fence (longer than any
  backtick run in its source); a caption keeps the text fallback and stays lossy.
- Block math becomes `$$` lines, or a ```` ```math ```` fence when the TeX has a blank
  or indented line or `$$`; inline math becomes `$…$` when one line without an
  unescaped `$` can carry it. Other TeX keeps the lossy `\[…\]`/`\(…\)` fallback.

Wiki.js classes, wiki links, footnotes, sub/superscript, front matter and the
`degraded` report stay import-only. The markdown-safe profile has none of the target
nodes, so there a quote stays a quote and `==x==` stays text. Plain text that looks
like the dialect (a literal `==x==` or `$x$`) is not escaped: it would come back
formatted, so the page source protects such a block (see
[page source](../../docs/markdown/page-source-editor.md)). A ```` ```mermaid ```` or
```` ```math ```` code block now reads back as a diagram or math and is protected
there for the same reason.

Pasting from a code editor inserts Markdown too: VS Code copies
(`vscode-editor-data` with mode `markdown`, or unknown mode with Markdown-looking
text) and other IDE copies whose HTML is only a monospace rendering of the same
text are parsed from their plain text instead of the coloured HTML. VS Code
copies in other languages keep Tiptap's code-block paste. Shift+paste stays
literal. **Vložit jako Markdown** (slash menu, or Ctrl+Shift+Alt+V /
Cmd+Shift+Option+V; AltGr+Shift+V keeps typing its character) reads the clipboard
text (browser clipboard permission) and inserts it through the same boundary as
one undo step; hosts with their own text dispatch `{ type: 'paste-markdown', text }`.
Markdown text is accepted up to 5 MiB: a larger paste is inserted literally, and
Markdown the document cannot hold is reported through `markdownInputErrorEvent`
instead of changing the document. Imports follow Pandoc for TeX: `$` hugs its
content and a closing `$` is not followed by a letter or digit (`$HOME/$USER` and
`$5 and $10` stay text), and an unclosed `$$` ends at the first blank line.

Observe front matter, a leading H1 and constructs kept as text with
`onMarkdownImport`, for example to fill an empty page title. It runs synchronously
before insertion, only when the import has front matter, starts with an H1 or has
`degraded` parts, and may drop that H1 in the same undo step. Resolve wiki links and
embeds with `resolveWikiLink` / `resolveEmbed`:

```ts
const controller = createMarkdownEditorController({
  profile: 'rich', source,
  onMarkdownImport({ source, fileName, title, frontMatter, leadingHeading, degraded }) {
    if (title && !page.title) page.title = title
    if (degraded) telemetry.importDegraded(degraded) // [{ kind: 'wiki-link', count: 2, excerpt: '[[Home]]' }, …]
    return { omitLeadingHeading: !!leadingHeading && leadingHeading === (title ?? page.title) }
  },
  // Synchronous; return null to keep the literal text.
  resolveWikiLink: ({ target, heading, alias, raw, source }) => {
    const page = wiki.findByTitle(target)
    return page ? { href: `/wiki/${page.slug}${heading ? `#${slugify(heading)}` : ''}`, label: alias } : null
  },
  resolveEmbed: ({ target }) => /\.(png|jpe?g|gif|webp)$/i.test(target)
    ? { src: `/files/${encodeURIComponent(target)}`, alt: target }
    : null, // or { href, label } for a transcluded page or file
})
```

`resolveWikiLink` and `resolveEmbed` receive a frozen `{ target, heading?, alias?,
raw, source }` split the Obsidian way (`target#heading|alias`); GitHub wikis write
`[[Link text|Page-Name]]`, so such hosts read `raw` instead. `href` must be an
encoded `http(s):`, `mailto:`, `tel:` or relative URL and `src` an `http(s):` or
relative URL; anything else, `null` or an exception keeps the literal text. The
default link text is the alias, else `target › heading`, else the target.
`[[_TOC_]]` is never sent to the resolver.

`degraded` lists each kind of construct kept as literal text once, with its
`count` and first `excerpt` (at most 80 characters): `wiki-link`, `embed`, `html`,
`comment`, `footnote` (reference without definition), `highlight`, `subscript`,
`superscript`, `math` (no math plugin) and `toc`. The same list is dispatched as
the bubbling `markdownImportNoticeEvent` (`{ source, fileName?, degraded }`) from
the editor DOM; `MarkdownEditor` shows it as a non-blocking `role="status"` notice
("N částí bylo vloženo jako text") with an expandable list. The notice does not hide
by itself (a timer cannot tell whether a keyboard or screen reader user has reached
it); its close button dismisses it and the next such import replaces it.

`source` is `'paste'` (also text dragged in) or `'file'` (with `fileName`).
`frontMatter` holds the top-level keys as JSON values: strings, numbers, booleans,
`null` and flat lists; nested maps are skipped. `title` always stays text
(`title: 2024` is `'2024'`). The front matter reader is a small
built-in one because the `yaml` package is an optional peer of the OpenAPI plugin
only, and paste parsing is synchronous. Exceptions thrown by the callback are
logged and never block the paste or drop. `MarkdownEditor` accepts the same
functions as `@markdown-import` / `:on-markdown-import`, `:resolve-wiki-link` and
`:resolve-embed`; they are read on every call.

### Controller lifecycle

```ts
import { createMarkdownEditorController } from '@atlas/markdown/core'
const controller = createMarkdownEditorController({
  profile: 'markdown-safe', source: { kind: 'local', value: '**Hello**' },
})
const detach = controller.attachView(element, { ariaLabel: 'Description' })
controller.dispatch({ type: 'toggle-bold' })
controller.getProjection() // { markdown, text, rich, markdownLossy }
detach() // retains document/session for another view
controller.destroy() // idempotent; caller owns lifetime
```

`/core` has no Vue, Yjs or SignalR runtime dependency and can be imported/constructed without DOM. Attach a view before requesting a projection. Only one view may be attached at a time. `query(command)` reports enabled/active state without modifying content; `subscribe` returns an unsubscribe function and publishes immutable snapshots. Publications are ordered under reentrant changes, and one failing subscriber cannot interrupt a committed edit or later subscribers. Pass `onSubscriberError(error, state)` in the controller options to observe those consumer failures. Content revisions do not increase on focus or network changes.

Detaching a collaborative view retains its live Y.Doc and connection, but suspends projection rendering. While detached, `getValue()` and `getProjection()` throw instead of returning stale content. Reattaching synchronizes the current document, publishes a content revision if it changed, and resumes projection work.

For Vue use `useMarkdownEditor(options)`, `MarkdownEditorRoot` and `MarkdownEditorContent` from `/vue`. The composable destroys its controller with the Vue scope; a Root borrowing a controller does not. Root's default slot provides `dispatch`, `query`, `state` and `controller`. Put custom buttons and Content inside that slot. Use `@mousedown.prevent` on toolbar buttons to preserve text selection. Wrap the content in `.md-editor` for base styles.

## Plugins and host services

`createMarkdownEditorController`, `useMarkdownEditor`, `MarkdownEditor` and `MarkdownRenderer` accept `plugins`. Definitions are fixed for a controller's lifetime; remount with a new Vue key to change them. Each definition creates a separate instance for each controller. The `/core` export provides `MarkdownPlugin` and `defineMarkdownCommand<Args>()` for typed commands; command IDs are namespaced and checked for collisions. Actions contribute to the ready-made toolbar and slash menu. Command-only and behavior-only Tiptap extensions need no content schema version and do not become document requirements.

Static read-only enhancements use `onRender({ container, document, ownerDocument, signal })`. The hook runs after validated content is inserted, may return a synchronous cleanup, and has no Vue dependency. Replacement, render failure and unmount abort the signal before cleanup, so chart observers and requests cannot outlive their rendered document. The public `MarkdownReadonlyJSONContent` type reflects the deeply frozen document snapshot; the supplied container remains mutable for rendering.
Preparing a replacement does not touch the current DOM or lifecycle; an invalid new value therefore leaves the last valid preview active. The renderer publishes its disposer before invoking plugin code and checks its generation after reentrant callbacks, so a synchronous host unmount cannot leave chart observers or requests alive.

```ts
import { createYouTrackPlugin } from '@atlas/markdown/plugins/youtrack'

const plugins = [createYouTrackPlugin({
  client: {
    async search(query, { signal }) {
      const response = await fetch(`/api/issues?q=${encodeURIComponent(query)}`, { signal })
      if (!response.ok) throw new Error('Issue search failed')
      // Host endpoint returns [{ id, key, title, url }]. The plugin validates each result.
      return response.json()
    },
  },
})]
```

```vue
<MarkdownEditor v-model="richValue" profile="rich" :plugins="plugins" ariaLabel="Project notes">
  <template #inspector="{ controller, state }">
    <MyInspector :controller="controller" :editable="state.editable" />
  </template>
</MarkdownEditor>
```

YouTrack is an optional subpath; import it explicitly. The host supplies the authenticated API client and may override the picker. The bundled picker searches, inserts a versioned issue reference and supports cancellation. Rich values retain the issue identity and display snapshot; Markdown exports a link and reports `markdownLossy: true`. The playground's `/?story=plugins` uses local demo data.

The ready-made editor exposes `toolbar`, `before-content`, `inspector` and `status` slots with `{ controller, state }`. A toolbar slot replaces the default plugin toolbar. Plugin actions use the same `query` and `dispatch` contract as headless controls, including readonly and an optional `replaceRange` for slash commands. Tiptap extensions can provide keyboard shortcuts and Vue node views with the consumer's injection context.

A block module (`defineMarkdownBlockModule`) whose runtime has `edit` is editable where it is shown: in an editable editor a click on the rendered block selects it and runs `edit` with the block element as `anchor`, and so do Enter and F2 while the block is the selected node; a second click closes a popover it opened. Links, buttons, form fields, `summary` and anything inside `[data-md-no-select]` never trigger it. **A click you handle yourself → `event.preventDefault()`**: the block is still selected, but `edit` does not run. Esc in the opened form returns focus to the selected block; the toolbar's „Upravit: …“ button stays as the visible mouse path.

Plugins that portal UI outside the editor shell register its exact root with `registerMarkdownOverlay(controller, element, { consumesEscape, modal })` from `/core`, and call the returned cleanup before removing it. Registration requires an attached view, is scoped to the editor and its `ownerDocument`, and does not expose the internal Tiptap editor. Fullscreen focus and Escape handling remain correct even when multiple editors share a portal container.

Content plugins declare `schemaVersion` and supply Tiptap extensions, JSON validation through schema attributes, and Markdown/text serializers. Rich envelopes are a closed JSON format: unknown envelope or plugin-metadata fields and non-JSON values are rejected rather than normalized away. Rich output is revalidated and transactions that introduce non-JSON attributes do not commit. Rich envelopes record only plugins that actually contribute nodes, marks or attributes. Attribute ownership follows the declaring extension instead of installation order. Missing, incompatible or unversioned custom content is rejected before replacing the document. Built-in table geometry, spans, column widths, row heights, colors and alignment are checked against the same limits as public commands before a rich value can be mounted or saved. Plugins add schema fields; they cannot override existing extension names, attribute contributions, `extendNodeSchema` or `extendMarkSchema`. A content plugin must explicitly list `markdown-safe` in `profiles` and supply `markdownLossy`; known-lossy plugin transactions are rejected and `getValue()` will not silently emit incomplete Markdown. Version migrations are explicit host operations, outside a live editing session.

Pass `imageUpload: (file, { signal }) => Promise<{ url, alt? }>` to the controller or ready-made editor for paste, drop and image insertion. The feature maps insertion targets through intervening edits and cancels on deleted targets, readonly, view detachment or destruction. Upload errors bubble as `md-image-upload-error`; YouTrack insertion errors as `md-youtrack-error`. Credentials and host data access stay in the supplied services.

## Document schema and server validation

`describeMarkdownSchema(plugins, options)` (`@atlas/markdown/schema`) describes the document schema of an editor configuration as JSON (nodes,
marks, attributes with defaults and value constraints, content expressions, plugin ownership and versions); the manifest
of every built-in plugin ships as `@atlas/markdown/schema.json`. Attributes declare their value rule next to their
validator with `defineMarkdownAttributeValidator` (`@atlas/markdown/core`). The .NET package `Atlas.Markdown.Sync` embeds the manifest:
`MarkdownDocumentValidator` validates stored documents on a host's write paths and the collaboration content guard
validates every collaborative update with the same rules. See [document validation](../../docs/markdown/document-validation.md).

## Controller-backed collaboration

Large rich documents can use opt-in [revision notifications and explicit snapshots](../../docs/markdown/editor-revision-snapshots.md) through `getMarkdownContentCapability()` or Vue `mode="revision"` with `initialValue`. Default `v-model` behavior remains unchanged; external controllers without the capability return `null`.

The separate `@atlas/markdown/draft` entry provides an optional [periodic local draft adapter](../../docs/markdown/local-draft-snapshots.md) with scoped IndexedDB snapshots and explicit capture/read/discard. It does not replace host saving or automatically restore content.

Block reordering is temporarily unavailable with the current Yjs binding: deleting and copying a block can lose another user's concurrent edits. The grip explains the restriction; text editing and adding blocks remain available. Native move drops are also blocked by the collaboration adapter. Standalone reordering is unaffected. Move review/approval is not implemented; see `docs/collaborative-block-moves-and-review.md` in the repository for the agreed behavior, regression coverage and prerequisites.

```ts
import { createSignalRMarkdownAdapter } from '@atlas/markdown/collaboration/signalr'
const collaboration = createSignalRMarkdownAdapter({
  hubUrl: '/hubs/markdown', documentId: 'wiki:team',
  presence: { id: 'alice', name: 'Alice' },
  accessToken: () => getAccessToken(), // supplied by your application
})
```

```vue
<MarkdownEditor :collaboration="collaboration" profile="rich" ariaLabel="Team wiki" />
```

Do not combine collaboration with `v-model`. An adapter can be claimed by only one controller; creating it does not connect to the network. The controller exposes connection, durable document-save and projection-save states separately, plus pending changes and peers. A connected socket does not imply saved content. The server's document write permission is combined with the host's `readonly` preference; read-only sessions hydrate normally but never enable editing or snapshot writes. By default, offline updates are retained only in the current window. Optional [manual recovery checkpoints](../../docs/markdown/collaboration-recovery-checkpoints.md) can preserve periodic local copies in IndexedDB; they do not enable automatic restore/replay or guarantee capture of every unacknowledged edit.

`prepareMarkdownRecovery` from the same SignalR entry point prepares an explicitly selected local recovery branch and revision for manual preview/export in a browser DOM runtime. It requires the host's verified scope and exact schema/fragment, and returns immutable `value` plus rich JSON, Markdown, text and `markdownLossy` projections. It does not start a provider, write/discard the store or replay pending data. This is local recovery content, not an authoritative server version. This manual-checkpoint API has no bundled panel; the separate V2 journal has its own `MarkdownRecoveryPanel` described in [the V2 guide](../../docs/markdown/collaboration-durable-v2.md). See the [preview API example and limits](../../docs/markdown/collaboration-recovery-checkpoints.md#prepare-a-selected-manual-preview-and-export).

The SignalR adapter pauses editing while disconnected or rehydrating by default. The Vue surface explains the pause, reconnects automatically and resumes only after synchronization; pending edits are preserved in memory. Set `allowOfflineEditing: true` in adapter options to explicitly allow unconfirmed offline edits after initial hydration (the previous behavior). Host readonly and server write permissions still apply. This default applies to the controller adapter; the legacy `PoolEditor` binding is unchanged.

### Connection feedback never moves the document

Everything the surface says about the link is an overlay anchored to the editor shell, never a block
in the document flow. A shared session reconnects often — a laptop lid, a tunnel, a redeployed
server — and a banner inserted above the content would push the whole document down and pull it back
each time. Two surfaces carry the state:

- **A small dot in the editor's top-right corner**, always the same box, dim while the link is
  healthy. Its three-segment meter loses segments as the link degrades; hovering it or reaching it
  with the keyboard opens a detail panel with the measured round-trip, the number of drops in this
  session, unconfirmed changes and who else is present.
- **A card below it**, only for states a person must act on, and only after the interruption has
  lasted about 1.2 s. A shorter blip never reaches the screen at all.

Both sit in one right-aligned overlay column, so the detail panel and the card stack instead of
covering each other, and the left gutter stays free for the block handle.

`state.link` carries the measurements behind that panel: `latencyMs` is the median server
acknowledgement round-trip of recent local edits (`null` until the first sample), `drops` counts lost
connections in this session, and `stalled` means an acknowledgement is overdue. It is `null` for a
local document. Transient content, projection and file-drop messages use the same rule and float
above the bottom edge of the shell.

### Rehearsing connection states without a server

The interesting states are the ones a healthy server will not produce on request, so host chrome can
be exercised against a transport-free adapter that publishes the same `MarkdownEditorState`:

```ts
import { createSimulatedCollaboration } from '@atlas/markdown/collaboration/simulator'

const simulation = createSimulatedCollaboration({ initial: 'live', seed: richValue })
simulation.apply('reconnecting') // 'slow' | 'stalled' | 'not-saving' | 'read-only'
                                 // 'connecting' | 'offline' | 'failed'
const stop = simulation.flap(700) // alternate healthy/interrupted like a flaky link
```

```vue
<MarkdownEditor :collaboration="simulation.adapter" profile="rich" ariaLabel="Shared document" />
```

It carries no CRDT and opens no connection: use it to rehearse presentation and to write layout
regression tests, never to rehearse convergence. The playground's `/?story=connection` walks every
state and reports the document's vertical drift, which must stay at zero.

Controller sessions negotiate their profile and content plugin versions before hydration. The server persists this identity and rejects incompatible clients. Existing unversioned documents remain in legacy mode; they require an explicit migration to the versioned API. Custom `IDocumentStore` implementations must implement durable schema binding before accepting versioned sessions; the default interface implementation accepts legacy sessions only. See the repository's `docs/collaboration-schema.md` for deployment and migration details.

`projectionSave: 'pending'` means the projection has not been confirmed current by this session. Only the elected snapshot writer receives that confirmation; other peers may remain pending even after the writer succeeds. It must not be interpreted as lost or unacknowledged document updates.

## Legacy quick start (compatible API)

```vue
<script setup lang="ts">
import { ref } from 'vue'
import { PoolEditor } from '@atlas/markdown'
import '@atlas/markdown/style.css'

const content = ref('# Hello\n\nType `/` for the slash menu.')
</script>

<template>
  <PoolEditor
    v-model="content"
    profile="markdown-safe"
    placeholder="Type / for commands"
    paper
    fullscreenable
    min-height="320px"
  />
</template>
```

## Legacy PoolEditor props

| Prop | Type | Default | Description |
|------|------|---------|-------------|
| `modelValue` | `string` | — | Markdown source (v-model). |
| `profile` | `'markdown-safe' \| 'rich'` | `'markdown-safe'` | Portable Markdown features, or explicitly lossy rich-table features. |
| `placeholder` | `string` | `"Napiš '/' pro příkazy…"` | Empty-paragraph placeholder. |
| `imageUploadUrl` | `string?` | `null` | If set, `/Obrázek` slash item enables paste / drop / upload. POSTs `multipart/form-data` with field `file`; expects `{ url, alt? }` response. |
| `minHeight` | `string` | `"240px"` | CSS height passed to the editor body. |
| `readonly` | `boolean` | `false` | Disable editing; toolbar / drag handle hidden. |
| `paper` | `boolean` | `false` | Paper layout (820px centered, generous padding, footer with word count). |
| `compact` | `boolean` | `false` | Tight inline mode (no drag handle, smaller padding/font). |
| `hideBlockHandle` | `boolean` | `false` | Explicit override to hide the drag handle outside compact mode. |
| `fullscreenable` | `boolean` | `true` | Show fullscreen toggle (top-right or paper-footer placement). |
| `extensions` | `AnyExtension[]?` | `[]` | Project-specific Tiptap extensions appended after the core kit. |
| `slashSuggestionExtra` | `SlashCommandItem[]?` | `[]` | Extra slash-menu items (e.g. an `@-mention` chip). |

## Legacy PoolEditor events

| Event | Payload | When |
|-------|---------|------|
| `update:modelValue` | `string` (markdown) | Editor content changed. |
| `created` | `Editor` | After Tiptap finishes initial setContent — register custom upgraders here. |
| `content-replaced` | `Editor` | After every parent-driven `setContent` from the v-model watch. |

## Optional CSS tokens

Base styles include fallbacks so empty tables remain visible without host CSS.
The Ocean theme provides scoped defaults. Override tokens on the `.md-editor` shell (not only on `:root`), for example:

```css
.my-theme .md-editor {
  --bg-1:    #fff;
  --bg-2:    #f7f7f8;
  --bg-3:    #ececef;
  --line:    #e1e1e6;
  --line-2:  #d0d0d6;
  --text:           #1c1c20;
  --text-dim:       #6a6a73;
  --text-dimmer:    #a0a0aa;
  --accent:         #4a6cf7;
  --accent-ink:     #fff;
  --accent-line:    rgba(74, 108, 247, 0.42);
  --accent-soft:    rgba(74, 108, 247, 0.10);
  --chip-accent-fg: #2a4ed1;
  --chip-red-fg:    #c4302b;
  --red:    #de4940;
  --green:  #2fa45a;
  --amber:  #d68f1f;
  --paper:        #fafafa;
  --paper-line:   #e8e8ec;
  --paper-shadow: 0 1px 3px rgba(0,0,0,0.06), 0 8px 24px rgba(0,0,0,0.05);
  --shadow-1: 0 1px 2px rgba(0,0,0,0.06);
  --shadow-2: 0 6px 18px rgba(0,0,0,0.10);
  --font-sans: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
  --font-mono: ui-monospace, "SF Mono", Menlo, Monaco, Consolas, monospace;
  --z-sticky: 100;
  --z-modal:  1000;
}
```

The editor inherits these — host can re-skin freely.

### Dimension tokens (radius, control height, icon, focus, elevation)

`styles/tokens.css` defines one dimension scale, shared by every button, popover, dialog and
picker in the editor — the bubble toolbar, context menus, the slash-command list, the YouTrack
picker, module dialogs. It is declared with `:where(...)`, so it carries zero specificity and a
host overrides any of them with a single plain rule instead of fighting cascade order:

```css
.atlas {
  --radius-sm: 6px;    /* buttons, fields, small popovers */
  --radius-md: 8px;    /* menus, popovers, pickers */
  --radius-lg: 12px;   /* dialogs, cards */
  --control-h: 36px;   /* text buttons, menu items */
  --control-h-sm: 32px; /* icon buttons (40px under a coarse pointer) */
  --icon: 18px;         /* a standalone icon */
  --icon-sm: 16px;      /* an icon inside a control or menu item */
  --focus-ring: 2px solid var(--accent);
  --focus-offset: 2px;
  --shadow-menu: 0 8px 28px rgb(16 32 57 / .14);
  --shadow-modal: 0 20px 90px rgb(16 32 57 / .2);
  --backdrop: rgb(16 28 48 / .56);
}
```

`core/overlayTheme.ts` copies these `--*` custom properties onto body-level portals (fullscreen,
the YouTrack picker, module dialogs), so a value changed on `.md-editor` — or by the host, on its
own outer element — reaches every surface, even the ones that render outside `.md-editor` in the
DOM. There is exactly one `:focus-visible` rule for every control across these surfaces; a
component's own stylesheet must not declare a second one.

### Motion tokens

`styles/motion.css` is the one source of the editor's motion: the values of the owner's motion prototype
(Atlas 2.0), declared on `:where(:root)` with zero specificity. Without a host the editor moves exactly the same.

| Token | Default | Reduced motion | Use |
|---|---|---|---|
| `--md-motion-fast` | 120ms | 120ms | hover, colours, small states |
| `--md-motion-base` | 200ms | 160ms | content entering, rows, drag neighbours |
| `--md-motion-slow` | 280ms | 160ms | unfolding, expanding |
| `--md-motion-menu` | 140ms | 140ms | menus and popovers |
| `--md-motion-target` (alias `--md-motion-surface`) | 160ms | 160ms | a surface or drop target appearing, a block returning |
| `--md-motion-pop` | 320ms | 160ms | a success state |
| `--md-motion-glow` | 1600ms | 1600ms (background only) | the afterglow of an accepted change |
| `--md-motion-loop` / `-spin` / `-pulse` | 1600 / 800 / 1000ms | 0s (stands still) | work in progress only |
| `--md-motion-ease` / `-ease-io` | `cubic-bezier(.22,1,.36,1)` / `cubic-bezier(.65,0,.35,1)` | `linear` | every curve |
| `--md-motion-rise` / `-pop-from` | 4px / .8 | 0px / 1 | travel and scale of entrances |
| `--md-motion-time-scale` | 1 | — | multiplies only fixed literals in JavaScript |

The invisible ink and the secrets vault keep their own long timing (a signature, not the editor's quick motion) in the
`--md-conceal-*` group of the same file: durations `-fast` 200ms, `-enter` 520ms, `-unfold` 900ms, `-roll` 2800ms,
`-aurora` 42s, `-stagger` 70ms and the curves `-ease`, `-ease-rail`, `-ease-roll`. Under reduced motion every
entry is at most 160ms and `linear`, the aurora and the stagger stand (0s); a host remaps the group like the editor
tokens. Nothing else in the styles carries a literal duration or curve: a contract test (`tests/contracts`) fails on a
new one and keeps a short allowlist of motion that is not style.

**Reduced motion never switches feedback off.** With `prefers-reduced-motion: reduce` (or `data-md-motion="reduced"`
on an ancestor, e.g. a preview toggle) the tokens above take their reduced values, keyframes keep only their fade,
loops stand still, and inside `.md-editor` and `.md-outline-layout` transitions are limited to opacity and colour,
linear: a dragged block, a table axis or the block handle never shows an in-between position. Web Animations follow
the same rule through `animateMarkdownMotion` (only opacity and colour, at most 160 ms × time scale, linear).

**Host remap contract — one source of truth in the host.** A host with its own motion system maps the editor
tokens to it once, globally, instead of copying values:

```css
/* Atlas src/motion.css: on :root for body-level overlays and fullscreen, on the editor for scoped previews. */
:root,
.atlas :where(.md-editor, .md-renderer) {
  --md-motion-fast: var(--atlas-motion-fast);   --md-motion-base: var(--atlas-motion-base);
  --md-motion-slow: var(--atlas-motion-slow);   --md-motion-menu: var(--atlas-motion-menu);
  --md-motion-target: var(--atlas-motion-surface); --md-motion-pop: var(--atlas-motion-pop);
  --md-motion-glow: var(--atlas-motion-glow);   --md-motion-loop: var(--atlas-motion-loop);
  --md-motion-spin: var(--atlas-motion-spin);   --md-motion-pulse: var(--atlas-motion-pulse);
  --md-motion-ease: var(--atlas-ease-out);      --md-motion-ease-io: var(--atlas-ease-inout);
}
```

- The host's tokens must carry their own reduced path: at most 160 ms and `linear` under reduced motion, and
  **0s for loop, spin and pulse** (that is how the editor's loops stand still). Do not map to fixed values.
- A host that slows its motion down (acceptance 3×) passes slowed tokens; it does not set `--md-motion-time-scale`
  for them, or durations would be slowed twice. The time scale only multiplies JavaScript literals.
- Leave `--md-motion-rise` and `--md-motion-pop-from` alone: the geometry is the editor's, and reduced motion
  zeroes it by itself.
- To force reduced motion from a host setting or preview, set `data-md-motion="reduced"` on an ancestor (the
  playground's „Omezený pohyb“ toggle does this on `<html>`).
- Body-level overlays inherit the tokens from the document; `copyMarkdownOverlayTheme` copies a motion token only
  when the editor's value differs from the document's (a remap scoped to the editor).
- The assist module's `--md-assist-*` tokens default to these (`--md-assist-menu` to `--md-motion-target`), so the
  core mapping covers it; the assist names still override.

From `@atlas/markdown/core`: `markdownMotion(element, reducedMotion?)` reads the tokens (durations in ms,
`scaled()` for literals, `reduced`, `ease`, `rise`), `animateMarkdownMotion()` is the prototype's `anim()`,
`markdownScrollBehavior(element)` returns `auto` under reduced motion, `isMarkdownReducedMotion()`,
`markdownMotionDefaults` and `markdownMotionTokens` list the defaults and names.

## Icons (`@atlas/markdown/icons`)

One icon registry backs the entire package — the Vue toolbar icons, the DOM-based context menus,
plugin toolbar actions and the wiki shell's own icon component all resolve a name to the same
Lucide-derived drawing (viewBox `0 0 24 24`, stroke width 1.75). Import the public entry instead
of bundling a separate icon set:

```ts
import { MarkdownIcon, createIconElement, iconMarkup, type MarkdownIconName } from '@atlas/markdown/icons'
```

- `MarkdownIcon` — a Vue component: `<MarkdownIcon name="trash-2" :size="16" />`.
- `createIconElement(doc, name, size?)` — a framework-neutral DOM factory for headless code:
  `createIconElement(document, 'link', 18)` returns an `<svg data-icon="link">`.
- `iconMarkup` / `iconSvgMarkup(name, size)` — the raw registry, for anyone assembling markup by hand.
- An unknown `name` falls back to `circle-help` and (in dev builds) a `console.warn` — never a
  silently wrong icon.

## Shared strings (`ui/strings.ts`)

Every user-visible text of the editor and its first-party plugins — labels, `aria-label`s,
tooltips, statuses and errors shown to the user — is read from one dictionary, re-exported from
`@atlas/markdown/core`. The handful of words that repeat across every popover, dialog and
picker — Apply/Cancel/Close/Remove/Loading/Retry/Fullscreen/Read-only/Insert — sit at the top
level; everything else is grouped in one flat section per area. Core sections: `slash`, `blocks`,
`images`, `drag`, `suggestions`, `table`, `toolbar`, `surface`, `outline`, `modules`, `input`,
`source`, `collaboration`, `review`, `history`. Plugin sections: `attachments`, `charts`,
`concealed`, `contacts`, `diagrams`, `editorTools`, `emoji`, `headingAnchors`, `math`, `openapi`,
`pages`, `secrets`, `toc`, `youtrack`. The keys are typed in `src/ui/strings/*.ts`. Each section's
Czech defaults ship with the code that renders them (`src/ui/strings/<section>.ts` for core,
`src/plugins/<name>/strings.ts` and, for text only a lazy runtime shows, `runtimeStrings.ts`) and join
the dictionary through `registerMarkdownStrings` when that module loads — a core-only page carries no
plugin text. Overrides win regardless of order: keys set before a section loads are kept.

```ts
import { markdownStrings, setMarkdownStrings, type MarkdownStringsOverrides } from '@atlas/markdown/core'

// Once, at host startup — before creating editors or plugins. Sections merge key by key.
setMarkdownStrings({ apply: 'Apply', cancel: 'Cancel', table: { deleteRowLabel: 'Delete row' } })
```

Defaults are Czech (`Použít`, `Zrušit`, `Zavřít`, `Odstranit`, `Načítání…`, `Zkusit znovu`, `Celá
obrazovka`, `Ukončit celou obrazovku`, `Pouze pro čtení`, `Vložit`, …). A text that includes a
number or a name is a function returning the whole sentence. The slash item "Vložit jako Markdown"
reads `pasteAsMarkdown` and `pasteAsMarkdownDescription`; its shortcut is appended in the platform's
spelling (Ctrl+Shift+Alt+V, ⌥⇧⌘V on Apple devices). The `labels` options of the secrets and
concealed plugins still override their section per plugin instance. Search keywords of slash items,
sample content of inserted blocks and developer-facing errors are not part of the dictionary. This
is a dictionary, not a translation framework — locale loading, plural rules and message syntax are
out of scope until a second language is actually needed.
The word `locale` (default `cs-CZ`, `en-US` in an English dictionary) is the one formatting hint: table sorting, attachment sizes and the history timeline follow it.

## Headless extension kit

Consumers that own their own editor shell can use the same schema and profile
selection without rendering `PoolEditor`:

```ts
import { createMarkdownKit } from '@atlas/markdown'

const { extensions, capabilities } = createMarkdownKit({
  profile: 'markdown-safe',
})
```

## Optional SignalR collaboration

Install the optional collaboration peers and import the adapter through its
separate entry point:

```ts
import {
  SignalRYjsProvider,
  useCollab,
} from '@atlas/markdown/collaboration/signalr'
```

The backend contract is implemented by the `Atlas.Markdown.Sync` NuGet
package in this repository. Node.js is required for frontend development and
packaging only; the deployed server runtime is .NET.

`useCollab()` exposes `status`, `synced`, `pendingUpdates` and
`flushDegraded`. A local Yjs update remains pending until the .NET server has
durably appended it; reconnect and retry reuse the same client update ID.

## Composing your own slash items

Slash search ignores case and diacritics (`/zminka` finds "Zmínka") and ranks a title prefix before a word start, a keyword and a substring; ties keep list order. Actions take `keywords` (also `defineMarkdownBlockModule({ keywords })`), and `foldSearchText`, `rankSuggestion` and `rankSuggestions` are exported from `/core` for custom lists. An action with `parameter: { label, command(text) }` continues after a space once the full action label or one of its keywords is typed: `/kontakt Petr` shows "{label}: vyhledat „Petr“" and runs `command('Petr')`; after a name without a parameter a space closes the menu as before. A plugin joins the block grip's keyboard path (Alt+F10, then Tab) with `registerMarkdownBlockKeyboardHook(controller, { id, controls(block) })` from `/core`.

```ts
import { PoolEditor, type SlashCommandItem } from '@atlas/markdown'

const myMention: SlashCommandItem = {
  title: 'Customer',
  description: 'Insert a customer chip',
  searchTerms: ['customer', 'cust'],
  icon: '🏢',
  command: ({ editor, range }) => {
    editor.chain().focus().deleteRange(range).insertContent('@CUSTOMER').run()
  },
}
```

```vue
<PoolEditor v-model="md" :slash-suggestion-extra="[myMention]" />
```

## Releasing

Changes per version are listed in [CHANGELOG.md](./CHANGELOG.md). The editor has no releases or packages of
its own: it is released with Atlas.

## License

[MIT](./LICENSE)

Includes attribution to upstream MIT-licensed dependencies — see
[NOTICES.md](./NOTICES.md).
# Obsahové moduly

Rich editor obsahuje obrázky s úpravami a informační boxy (rozbalovací blok nemá: sbaluje se každý nadpis).
Volitelné importy `plugins/contacts`, `plugins/charts`, `plugins/attachments`,
`plugins/toc`, `plugins/diagrams` a `plugins/math` načítají runtime až při použití.
Viz [návod a ukázky](../../docs/markdown/content-modules.md).

Obsah dokumentu (`plugins/toc`) se upravuje na místě: název je pole (klik nebo Tab+Enter, Enter/odchod uloží, Esc zruší, prázdný název vrátí výchozí), rozsah nadpisů čip „H1–H3“ s malou nabídkou voleb (šipky + Enter, změna hned a vratná). Runtime `edit` (klik nebo Enter na blok) jen zaměří název; formulář s tlačítkem Uložit už není. Data uzlu i Markdown jsou beze změny.

Klávesnice: Alt+F10 zpřístupní úchyt aktuálního bloku bez hoveru (v tabulce úchyt řádku buňky), Enter
otevře menu bloku (Změnit na…, Duplikovat, Posunout, Smazat; psaní filtruje), mezerník vybere
komponentu a Tab vstoupí na její lištu. Escape vrací focus do editoru;
otevřený formulář se zavře před lištou/fullscreenem. Ve spolupráci lze komponenty
vybírat a upravovat, ale přesuny zůstávají vypnuté s viditelným vysvětlením.
Podrobnosti a výjimka tabulek: [ovládání klávesnicí](../../docs/markdown/content-modules.md#ovládání-klávesnicí).

The ready-made `MarkdownEditor` and `EditorSurface` accept `showMarkdownSource`
(default false). Document surfaces show an optional Markdown bubble with a live,
read-only projection, including readonly mode. Compact surfaces hide it. Rich
projections show a notice when component details cannot be represented fully.

Whole-surface file drop imports supported images, Markdown, text and registered
Mermaid nodes; other files use `createAttachmentsPlugin({ upload })`. It preserves
batch order and provides a single undo step. Hosts without an attachment upload
service receive a visible error for unsupported files. See
[content modules](../../docs/markdown/content-modules.md) for headless drop handlers and
progress/error events. Server-side persistence remains the host application's job.
# Obsah a navigace dokumentem

Volitelný `outline="floating"` nabízí vlnovou osnovu s připínáním; `outline="panel"` zobrazuje horní panel sbalitelný do ikonky bez vlnek. Výchozí stav je `hidden`. K dispozici je pasivní živý náhled, klávesnicová navigace a plynulé zvýraznění nadpisu v dokumentu. Podporují jej `MarkdownEditor` i `PoolEditor`; v kompaktním režimu se nezobrazuje.

```vue
<MarkdownEditor v-model="text" :outline="{ mode: 'panel', collapsed: true, preview: true }" ariaLabel="Dokument" />
```

Podrobnosti a hranice náhledu: [navigace dokumentem](../../docs/markdown/document-outline.md).
# Optional editing extensions

The optional authoritative V2 collaboration and durable local journal are described in
[the V2 integration guide](../../docs/markdown/collaboration-durable-v2.md). The separate
`collaboration/recovery-panel` entry provides local preview/export and explicit branch selection.
Hosts can configure shared image/import/export limits using the
[resource policy](../../docs/markdown/editor-resource-policy.md). Existing V1 and value-binding defaults remain available.

The opt-in extensions share the editor's suggestion, toolbar and anchored-panel UI:

| Entry point | Factory | Content contract |
| --- | --- | --- |
| `plugins/contacts` | `createContactsPlugin({ directory, suggestions: true })` | Existing rich `contactInline` schema; `@` is off by default. |
| `plugins/emoji` | `createEmojiPlugin()` | Versioned rich emoji nodes; Markdown-safe inserts Unicode text. |
| `plugins/pages` | `createPagesPlugin({ source, typing: true })` | Rich page IDs and readable snapshots; Markdown exports a lossy normal link. A search result may carry a display-only `detail` (space, "Koncept") that the suggestion list shows instead of the URL and that is never stored in the document. `/stránka vpn` starts the `[[` search with that text. |
| `plugins/editor-tools` | `createEditorToolsPlugin()` | Find/replace and invisible-character decorations in either profile. |
| `plugins/editor-tools` | `await createCodeBlockHighlighting()` | Pass the result as the `codeBlock` controller/editor option before mounting. |
| `plugins/heading-anchors` | `createHeadingAnchorsPlugin({ baseUrl })` | Rich heading identities; migrate old values with `migrateHeadingAnchors`. |

Install each optional Tiptap peer only for the entry points you use, at the same release as core (this implementation is tested with 3.35.0). Highlighting also needs `lowlight` and `highlight.js`. The base core entry point does not import these optional packages or Vue. Emoji data and code language definitions stay behind their optional entry points. Ctrl/Cmd+F belongs to the focused editor and also works readonly; replacements require edit permission. Invisible markers never enter JSON, Markdown or history.

Typing suggestions keep focus in text, use listbox/option roles, and support arrows, Home/End and Enter. Escape and Tab close without insertion. Remote edits do not open local pickers. See each plugin's source README, the [implementation and validation record](../../docs/markdown/tiptap-extensions-implementation-plan-2026-09-05.md), and [heading identity/migration rules](../../docs/markdown/heading-anchors.md). Production directories, wiki routing and authoritative collaboration migrations remain host services.

## Source editor (`@atlas/markdown/source`)

The entry itself is a ~2 KiB loader; Monaco (one lazy chunk shared by every source view) loads on the first mount, and no public type mentions Monaco. Everything it exports uses the `Source*`/`SourceEditor*` prefix, the headless page session `MarkdownSource*`:

| Export | Purpose |
| --- | --- |
| `mountSourceEditor(host, options: SourceEditorOptions): Promise<SourceEditor>` | Mounts the editor. `options`: `language` (`SourceLanguage`: `'yaml' \| 'json' \| 'markdown'`), `value`, `ariaLabel`, `readonly`, `readOnlyMessage`, `modelScope`, `protectedBlocks`, `onChange`, `onProtectedEditRejected`, `setup`. A read-only skeleton with the real text appears at once. |
| `SourceEditor` | `getValue`, `setValue(value, SourceSetValueOptions)`, `setLanguage`, `setDiagnostics(SourceDiagnostic[], owner?)`, `setReadonly(readonly, message?)`, `revealLine`, `focus`, `layout`, `destroy`. |
| `prefetchSourceEditor(): boolean` | Starts the download on intent (hover/focus of whatever opens a source view, never on page load). Returns `false` and does nothing under Save-Data. |
| `SourceProtectedBlock` | A block that may only be deleted or moved as a whole: `{ id, label?, startLine?, endLine? }`; session ranges pass straight in. |
| `SourceDiagnostic` | `{ line, column?, endLine?, endColumn?, message, severity: 'error' \| 'warning' \| 'info', source? }`. `MarkdownSourceDiagnostic` is the same type. |
| `SourceEditorSetup`, `SourceEditorSetupEnvironment`, `SourceWorkerOptions`, `SourceDisposable` | The `setup(monaco, environment)` hook for view-specific language services (Monaco typed `unknown`) and their workers. |
| `createMarkdownSourceSession({ value, plugins }): Promise<MarkdownSourceSession>` | The async, lazily loaded headless session: `text` with protected fences (```` ```md-protected <id> ````), `protectedBlocks: MarkdownSourceProtectedRange[]`, `blocks`, `apply(text): MarkdownSourceApplyResult`, `destroy()`. |

With `protectedBlocks` the fences can only be deleted or moved as a whole: partial edits are refused with a screen-reader alert, a lock marks each block, and Ctrl/⌘+Shift+B or a click on the lock selects the whole block. Ctrl+M (macOS Ctrl+Shift+M) switches Tab between indenting and moving focus, so the keyboard can always leave the editor. Markdown starts no Monaco worker. See [the design](../../docs/markdown/page-source-editor.md).

## Page source view and detail workspace (`@atlas/markdown/vue`)

`useMarkdownSourceDraft({ value, plugins?, readonly?, save })` owns a page source draft: protected Markdown of the live `value`, a local draft and a host-owned `save(value, text, { signal })` (a new revision on the server, never a write through the live collaborative editor). Resolve `save` with `{ ok: false, reason: 'conflict' }` on HTTP 409 (a thrown error with `status: 409` counts too) or `'live-edits'` while others have unsaved live edits. It returns refs (`text`, `baseText`, `protectedBlocks`, `diagnostics`, `status`, `notice`, `dirty`, `stale`, `busy`, `readonly`, `canApply`, `revision`) and `open()`, `apply()`, `discard()`, `reload()`, `close()`. The behaviour, the session and Monaco load on the first `open()`; the composable itself costs the eager bundle almost nothing. A clean draft follows a changed `value`, except while the reader works in the editor (it follows once they leave); a dirty draft becomes `stale` and Apply waits for Reload. `discard()` is ignored while a save is in flight.

`MarkdownSourcePanel` (`draft`, `ariaLabel?`, emits `applied`) is the „Zdroj“ view over such a draft: the editor with protected blocks, one delayed loading status, notices for conflicts and changed pages, a diagnostics list, Zahodit změny / Použít and a readonly fallback when Monaco cannot load. It is an async component (its chunk loads when rendered, with an in-place error when that download fails) and remounts on a replaced `draft`.

`MarkdownDraftConfirmation` (`open`, `label`, `message`, `applyLabel?`, `discardLabel?`, `continueLabel?`, `busy?`; emits `continue`, `discard`, `apply`) is the eager, native modal alert dialog for resolving a draft before a detail closes or switches. Focus starts on Pokračovat v úpravách, Escape continues editing, the message is its accessible description. Put it in the workspace's `confirmation` slot so it keeps the document's theme.

`MarkdownDetailWorkspace` (`open`, `v-model:active`, `tabs: { id, label }[]`, `title`, `label?`) wraps the document editor in place as one detail with tabs (Dokument/API/YAML or Dokument/Zdroj). `label` names the region (default „Detail komponenty“, e.g. „Zdroj stránky“). It emits `request-close`, `closed` and `tab-intent` (pointer enter or focus of a tab, before activation): call `prefetchSourceEditor()` / `prefetchOpenApiSourceEditor()` there. The document's theme tokens are copied onto the workspace while it is open and removed when it closes.

```vue
<MarkdownDetailWorkspace :open="open" v-model:active="tab" :tabs="[{ id: 'document', label: 'Dokument' }, { id: 'source', label: 'Zdroj' }]"
  title="Provozní příručka" label="Zdroj stránky" @tab-intent="id => id === 'source' && prefetchSourceEditor()" @request-close="close">
  <template #document><MarkdownEditor v-model="page" profile="rich" ariaLabel="Stránka" :readonly="draft.dirty.value" /></template>
  <template #source><MarkdownSourcePanel v-if="open" :draft="draft" /></template>
  <template #confirmation><MarkdownDraftConfirmation :open="confirm" label="Rozpracovaný zdroj" message="Zdroj obsahuje neuložené změny." @continue="confirm = false" @discard="discardAndClose" /></template>
</MarkdownDetailWorkspace>
```

### Node contexts for host UI (`nodeContext`, `onEditableChange`)

A content module's render context (`MarkdownModuleContext`) aborts its `signal` on every re-render, including a readonly toggle. UI that outlives a render, such as a detail workspace, takes `context.nodeContext()`: a `MarkdownModuleNodeContext` whose `signal` aborts only when the block is removed or replaced or the editor is destroyed. It carries `anchor` (the block's stable element: position or identify against it, never render into it; the module's renderer owns its children), `data` (a snapshot; read the current node through `getPos`), `document`, `editor?`, `getPos?`, `update?` (refused while readonly), `closeHistory?` and `onEditableChange(listener): () => void`, which reports readonly/editable changes instead of aborting. The OpenAPI plugin's `openDetail(context, view)` receives this node context.

## Optional OpenAPI plugin

`@atlas/markdown/plugins/openapi` needs the `yaml` peer (`^2.9.1`); it is marked optional only for consumers that never import the plugin. Scalar, Monaco and `monaco-yaml` are bundled and need no install. The Scalar reference (about 3.8 MB) and the Monaco workers are separate hashed files under `dist/assets/`, fetched only when a reference or source editor opens. Hosts building their own `openDetail(context, view)` workspace (the context is a `MarkdownModuleNodeContext`, see above) use the public `mountOpenApiReference` and `mountOpenApiSourceEditor`, and may warm the source editor with `prefetchOpenApiSourceEditor()` on hover/focus (never on page load; like `prefetchSourceEditor` it returns `false` and does nothing under Save-Data). Monaco's CSS ships inside its lazy chunk, not in `style.css`. The host CSP must allow same-origin module scripts in the reference iframe and module workers (`script-src`/`worker-src 'self'`) plus `style-src 'unsafe-inline'`. See the plugin README (`src/plugins/openapi/README.md` in the repository) for the full CSP and deployment notes.

## Optional concealed content

`@atlas/markdown/plugins/concealed` exports `createConcealedContentPlugin`, `toggleConcealedContent` and `setConcealedRevealMode`. It adds a rich `concealedContent` block that readers see under the shared invisible ink (scratch or Reveal button) while authors edit it in place; hide a selection from the formatting bubble next to Quote. It is visual concealment only: the content stays in the document and its projections. See the [plugin README](src/plugins/concealed/README.md).

## Optional secrets pilot

`@atlas/markdown/plugins/secrets` exports `createSecretsPlugin`, `insertSecretReference`, `insertInlineSecretReference` and `updateSecretReferencePresentation`. Schema v4 persists block `secretReference` and inline `secretInlineReference` nodes with a `secretId` plus non-sensitive presentation attributes only, such as which KeePass entry fields to show (v1–v3 documents migrate on load); the contract is defined in the plugin README. Markdown carries only the reference attributes as a lossless `::secret{secretId=…}` / `:secret{…}` directive, so the page source keeps references; plain text and HTML/clipboard omit identifiers and values. `createSecretsPlugin` returns host controls `conceal(secretId?)` and `refresh(secretId?)`, and access requests carry the reader's `reason`. Reveal and copy are explicit releases through the host's `reveal`/`copy` services, never during load; released values appear in place under the invisible ink as host-created elements and never enter the document. The host owns authorization, MFA, transient plaintext and durable release audit. Hidden references must be filtered by the server before JSON/history/collaboration delivery; client hiding cannot protect their existence. See the [service contract and adversarial pilot tests](src/plugins/secrets/README.md) before enabling it.

## Optional assist module (`@atlas/markdown/assist`)

Block suggestions (a marker at the block, a list on Alt+Enter, a warning bar) and the anchored before/after proposal panel. It is a **separate optional entry with its own stylesheet**: the core, `./vue`, the root entry and every `./plugins/*` never import it, not even dynamically. Without `createAssistPlugin` in an editor's `plugins` it has no effect on behaviour, DOM, CSS, schema, keyboard or bundle size; `tests/contracts/architecture.mjs` and `tests/package/bundle-budget.test.mjs` enforce that. It adds no document nodes (no `schemaVersion`). Behaviour, motion and the boundary are described in [docs/assist.md](../../docs/markdown/assist.md).

```ts
import { createAssistPlugin, openMarkdownProposalPanel } from '@atlas/markdown/assist'
import '@atlas/markdown/assist/style.css'
```

The public API (frozen for 0.8.0; the module imports only `@atlas/markdown/core`):

```ts
import type { Editor, JSONContent } from '@tiptap/core'
import type { Node as ProseMirrorNode } from '@tiptap/pm/model'
import type { MarkdownCommand, MarkdownDispatchOptions, MarkdownIconName, MarkdownPlugin, MarkdownPluginController } from '@atlas/markdown/core'

interface MarkdownAssistRange { readonly from: number; readonly to: number }
type MarkdownAssistTrigger = 'idle' | 'leave' | 'paste' | 'request'

interface MarkdownAssistBlock {
  readonly pos: number                    // before the textblock
  readonly node: ProseMirrorNode
  readonly text: string                   // one character per position: hard break '\n', other inline atoms U+FFFC
  readonly trigger: MarkdownAssistTrigger
  readonly pasted?: MarkdownAssistRange   // trigger 'paste' only
  rangeOf(start: number, end: number): MarkdownAssistRange   // text offsets -> document range
}
interface MarkdownAssistApplyContext {
  readonly editor: Editor; readonly controller: MarkdownPluginController
  readonly signal: AbortSignal            // aborted on readonly, detach or destroy
  readonly range: MarkdownAssistRange | null   // mapped range; null once its text changed or vanished
  dispatch(command: MarkdownCommand, options?: MarkdownDispatchOptions): boolean   // one separate undo step
  propose(options: MarkdownAssistProposal): Promise<boolean>   // proposal panel at the range; true once written
  readonly effects: { conceal(range: MarkdownAssistRange): Promise<void> }   // decoration only
}
type MarkdownAssistProposal = Omit<MarkdownProposalOptions, 'editor' | 'range' | 'signal' | 'reducedMotion' | 'strings'> & { readonly range?: MarkdownAssistRange }
interface MarkdownAssistSecondaryAction { readonly id: string; readonly label: string; run(context: MarkdownAssistApplyContext): void | Promise<void> }
interface MarkdownAssistSuggestion {
  readonly id: string                     // stable: kind + hash of the span (dismissal, deduplication)
  readonly kind: string                   // e.g. 'youtrack.issue'; „Nenabízet v této stránce“ suppresses the kind
  readonly title: string; readonly description?: string
  readonly icon: MarkdownIconName         // component icon; the module never draws an AI icon itself
  readonly tone?: 'default' | 'warning'   // warning: persistent marker, warning tone on the range, warning bar
  readonly applyLabel?: string; readonly applyIcon?: MarkdownIconName   // primary button of the warning bar
  readonly ai?: boolean                   // model output only (phase 4): „AI“ tag; rules never set it
  readonly range?: MarkdownAssistRange
  readonly reason?: string                // „Proč tento návrh?“
  apply(context: MarkdownAssistApplyContext): boolean | Promise<boolean>
  readonly secondary?: readonly MarkdownAssistSecondaryAction[]
}
interface MarkdownAssistSource {
  readonly id: string
  suggest(block: MarkdownAssistBlock, context: { readonly signal: AbortSignal }): readonly MarkdownAssistSuggestion[] | Promise<readonly MarkdownAssistSuggestion[]>
}
interface AssistPluginOptions {
  readonly sources: readonly MarkdownAssistSource[]
  readonly idleMs?: number                // 1500
  readonly minLength?: number             // 12 (idle trigger only)
  readonly strings?: Partial<AssistStrings>
  readonly reducedMotion?: () => boolean  // in addition to prefers-reduced-motion
  onApplied?(suggestion: MarkdownAssistSuggestion): void
  onDismissed?(suggestion: MarkdownAssistSuggestion, scope: 'suggestion' | 'kind'): void
}
function createAssistPlugin(options: AssistPluginOptions): MarkdownPlugin   // extensions, Alt+Enter, a status panel; no schema

interface MarkdownProposalOptions {
  readonly editor: Editor; readonly range: MarkdownAssistRange
  readonly title: string; readonly before: JSONContent | string; readonly after: JSONContent   // after: a node, or a doc of blocks
  readonly summary?: string               // textual alternative of the difference (computed for text when omitted)
  readonly allowEdit?: boolean            // „Upravit před použitím“: write, then select the result for editing
  readonly signal?: AbortSignal; readonly ai?: boolean
  readonly reducedMotion?: () => boolean; readonly strings?: Partial<AssistStrings>
  write?(range: MarkdownAssistRange): boolean   // custom write instead of replacing the range with `after`
}
type MarkdownProposalResult = 'applied' | 'edited' | 'cancelled'
function openMarkdownProposalPanel(options: MarkdownProposalOptions): Promise<MarkdownProposalResult>

const defaultAssistStrings: Readonly<AssistStrings>   // Czech defaults; AssistStrings lists every visible word
interface AssistModule { createAssistPlugin; openMarkdownProposalPanel; defaultAssistStrings }   // the namespace for import()
```

Tokens `--md-assist-fast`, `-base`, `-slow`, `-menu`, `-glow`, `-ease`, `-ease-io` and `--md-assist-time-scale` default to the core motion tokens (`--md-motion-fast`, `-base`, `-slow`, `-target`, `-glow`, `-ease`, `-ease-io`, `-time-scale`, see [Motion tokens](#motion-tokens)), so a host that maps the core needs nothing more; `--md-assist-morph`, `-solidify` and `--md-assist-glow-color` are the module's own. The 0.9 names still override on the editor (the module copies them onto its body-level overlays). Token durations are used as the host resolved them; the time scale multiplies only the module's fixed literals (ripple, icon, conceal steps).
