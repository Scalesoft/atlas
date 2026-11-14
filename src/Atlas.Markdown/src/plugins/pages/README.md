# Wiki page references

```ts
import { createPagesPlugin, insertPageReference } from '@atlas/markdown/plugins/pages'

const plugins = [createPagesPlugin({
  source: {
    search: (query, { signal }) => wiki.searchPages(query, { signal }),
    resolve: (id, { signal }) => wiki.getVisiblePage(id, { signal }),
  },
  typing: true,
  navigate: id => router.openPage(id),
})]
controller.dispatch(insertPageReference({ id: 'page-42', title: 'Team guide', url: '/wiki/guide' }))
```

The rich-only, version-1 `pages` plugin stores `pageReference` inline atoms containing a stable `id`, readable `title` snapshot and a safe fallback `url`. Install it before parsing rich values. The host page source is independent of the contact directory. Without a source, saved references still work and insertion commands remain available, but no search action appears. The source's optional `currentId()` names the page being edited: its own title in its text is never underlined or offered as a link to itself.

`/odkaz na stránku` starts the shared cursor picker. The optional `typing: true` also recognizes `[[query`; it is off by default to preserve existing Markdown input. Search starts after the first character, debounces by 200 ms and displays at most 20 results. `debounceMs` and `limit` configure these limits. New queries, readonly and destruction cancel requests and discard late answers. Escape/Tab/outside dismissal preserves typed text. Empty/error/loading states use the same list as contacts and emoji.

A reference that links a part of a page (a section) is told so by another plugin: a node decoration on it with the spec `{ [pageSectionSpec]: { title, open? } }` (`MarkdownPageSection`). It then shows that live `title` (the section's, not the page's), keeps its stored url and opens with `open` when given, else by its url; the resolver only checks the page can be opened. A changed decoration redraws only the label. Plain page references are unaffected.

The optional resolver refreshes only the rendered label and destination. It never rewrites the stored snapshot. A missing or failed target keeps its readable label and removes the link destination. The host must enforce current permissions when navigating; a document snapshot is never an access grant. The resolver must return the same page ID. `navigate` receives the stable identity so routing can survive renames and moves.

Plain Markdown exports a normal link and text exports the saved title. Markdown is explicitly lossy because it cannot retain the page ID. Rich JSON is the authoritative value. Resolvers receive an AbortSignal owned by each rendered reference; detachment, replacement and static-render cleanup abort them.

The gallery source is synthetic. A production wiki API, permission model and notifications remain responsibilities of the host application.

Recreate the controller/plugin configuration when replacing the source, so outstanding searches and reference resolvers are disposed before results from the new source are shown. The explicit search action works after ordinary text and is unavailable inside code or a link; dismissing its picker leaves general `[[` typing disabled unless `typing: true` was configured.

Recreate the plugin/controller when replacing the page service; disposal cancels pending searches and permission-sensitive resolver state.
