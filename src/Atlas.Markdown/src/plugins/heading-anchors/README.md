# Heading anchors

```ts
import {
  createHeadingAnchorsPlugin,
  migrateHeadingAnchors,
  navigateToHeading,
} from '@atlas/markdown/plugins/heading-anchors'

const plugins = [createHeadingAnchorsPlugin({ baseUrl: 'https://wiki.example/page' })]
const value = migrateHeadingAnchors(existingRichValue)
// Create the rich controller with these plugins and the migrated value.
// In the page's routing code, resolve #heading=<id> in the chosen document view:
navigateToHeading(documentRoot, headingId)
```

Install the optional `@tiptap/extension-unique-id` peer from the same Tiptap release as the editor. Only the `rich` profile is supported. The plugin adds version 2 of the `headingId` attribute to headings (a letter or digit, then letters, digits, `.`, `_` or `-`, 128 characters at most; version 1 had no dot) and `aliases`, the heading's former ids comma separated (cleared by `{ copy: true }`); other nodes and existing review/wiki identities are untouched.

Run `migrateHeadingAnchors` explicitly before opening old data. It clones the value, preserves the first valid occurrence of an ID, creates missing IDs, repairs duplicates and adds the schema manifest. Repeating the migration is idempotent. For a new copy of a document, use `{ copy: true }` to replace every heading identity. Restoring a revision of the same document retains the revision's IDs.

For collaboration, the host must migrate one authoritative snapshot and coordinate the schema upgrade before publishing edits to clients. Set `collaborationReady: () => migratedAndSynchronized` only after that gate is satisfied. Without it, collaborative clients do not assign IDs. Remote `y-sync$` transactions never trigger ID generation, including initial sync. This callback does not migrate Yjs data or implement a server lock; do not run the helper independently on each client.

An existing heading keeps its ID while renamed, moved or changed in level. Splitting in the middle creates a new identity on the right. Splitting at the start inserts the normal paragraph before the heading and preserves the original heading's identity. Joining keeps the first heading's ID. Upstream paste handling strips copied IDs before new IDs are assigned. The same-document drag behavior follows upstream UniqueID.

Each mounted editor/static renderer has its own DOM-ID prefix. Shareable links use the host's canonical URL and `#heading=<id>`; `headingAnchorUrl` constructs that URL, and `navigateToHeading` resolves the identity within a specific view. TOC and outline entries consume the stable identity when it is available. A `baseUrl` enables the shared selection toolbar's “Kopírovat odkaz na nadpis” action; unavailable clipboard access opens a labeled anchored copy field. There is no permanent control inside heading content.

For routers that already use the fragment to identify a page, supply `getHeadingUrl: headingId => absolutePageAndHeadingUrl` instead of `baseUrl`. The callback takes precedence and must return an HTTP(S) URL without embedded credentials. For example, a host can retain `#/pages/<pageId>?heading=<headingId>` and pass the parsed identity to `navigateToHeading`. Navigation focuses the target with a temporary `tabindex="-1"`; the original attribute is restored on blur, preserving keyboard focus in Chromium.

Readonly and static rendering never write IDs into the document. Plain Markdown omits identity and is correctly flagged lossy when identified headings are present. Older clients without the versioned plugin reject rich data that uses its attribute. See `docs/heading-anchors.md` for the identity policy and migration ownership.
