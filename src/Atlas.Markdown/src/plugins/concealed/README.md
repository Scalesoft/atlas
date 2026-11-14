# Concealed content plugin

Optional entry: `@atlas/markdown/plugins/concealed`. No extra dependency. Register the same plugin in the rich editor and `MarkdownRenderer`.

```ts
import { createConcealedContentPlugin } from '@atlas/markdown/plugins/concealed'

const plugins = [createConcealedContentPlugin()]
```

A block that hides part of a page under the shared invisible ink ([concealed content](../../../../../docs/markdown/concealed-content.md)). Use it for spoilers, quiz answers or surprises. **It is visual concealment, not access control:** the content is part of the document, its Markdown/text projections, history and collaboration. Passwords and other sensitive values belong in the [secrets plugin](../secrets/README.md). Hosts enable this plugin deliberately; it is not part of the core editor so that no host (for example Atlas) offers it by accident as a way to protect data.

## Document contract (schema v2)

Plugin metadata is `{ "id": "concealed", "version": 2 }`, `rich` profile only. One node type, `concealedContent`, with `block+` content (paragraphs, lists, headings, quotes, images, …). Each block stores its own reader settings:

| Attribute | Values | Default | Meaning |
| --- | --- | --- | --- |
| `reveal` | `'scratch'`, `'button'` | `'scratch'` | Scratch the mist away, or uncover only with an explicit Reveal button. |
| `reconcealAfterMs` | integer 0–600000, or `null` | `1500` | Delay before the mist returns once the reader leaves; `null` keeps it uncovered. |
| `completeAtPercent` | integer 10–100, or `null` | `70` | Scratching only: once this share is scratched open, the rest parts on its own from the stroke; `null` uncovers only what is scratched. |

Other values are rejected on load and in commands. v1 documents migrate unchanged and read the new attributes as their defaults. HTML is `<div data-md-concealed data-reveal data-reconceal data-complete>…</div>` and pastes back as the same block. Markdown keeps the content and reports `markdownLossy: true` for the lost wrapper.

## Editing

- Select text or blocks and press **Skrýt obsah** in the formatting bubble (next to Quote) or in the selected-blocks toolbar. The same button, lit while the selection is inside a concealed block, removes the concealment again. `/` **Skrytý obsah** and the plugin toolbar action do the same.
- Authors see and edit the content inside a quiet ink frame that measures exactly what the reader's sealed block does (editing adds only the frame and the chip straddling its top edge). The chip shows a summary (for example *Setřením · zakryje za 1,5 s*) and owns the selection toolbar. The block is edited where it is shown (interaction rule P1): a click on the chip, or Enter or F2 on the selected block, opens the anchored settings panel (reveal mode, re-cover delay, completion share, with a reminder that this is visual protection, not security), the same gesture closes it, and the toolbar's **Upravit** opens it too; **Náhled** shows the reader's ink in place, **Převést na text** unwraps (the content stays) and the last button deletes. The panel keeps a draft and applies it as one undo step on Použít; the controls are hidden while readonly.
- Commands: `toggleConcealedContent()` wraps the selected blocks or unwraps the surrounding block; `setConcealedContentSettings({ reveal?, reconcealAfterMs?, completeAtPercent? })` changes the surrounding block's settings (`setConcealedRevealMode` remains for the mode alone).

## Reading

The ink is a view against shoulder-surfing, not access control: whoever has the document may reveal it. An author's editor locked against accidental edits keeps showing the content, only without its editing controls. The renderer and an editor that has never been editable (a reader's) keep the content laid out but hidden (`visibility: hidden`, so it is not read aloud, found or selected) and lay the ink over it, so the page does not jump. Reveal or scratching shows a copy of the content under the parting mist; leaving covers it again after the block's `reconcealAfterMs` (never with `null`), and scratching past `completeAtPercent` uncovers the rest on its own. Keyboard: Tab to the surface, Enter to reveal, Escape to cover. Reduced motion cross-fades instead of moving.

## Tests

```sh
pnpm --filter @atlas/markdown exec vitest --configLoader runner --run src/plugins/concealed
```

The gallery `/?story=concealment` includes a document with two blocks and an editor mode; `tests/e2e/concealment.spec.ts` covers reading, Reveal and hiding a selection from the bubble.
