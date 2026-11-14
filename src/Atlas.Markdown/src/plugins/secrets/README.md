# Secrets reference plugin

Optional entry: `@atlas/markdown/plugins/secrets`. No extra dependency. Register the same plugin in the rich editor and `MarkdownRenderer`. This is a host integration boundary, not a secret store, permission system, or production vault.

This README is the single source of truth for the persisted contract. Other documents link here instead of restating it.

## Document contract (schema v4)

Plugin metadata in the rich envelope is `{ "id": "secrets", "version": 4 }`. The plugin is `rich`-profile only. It contributes two atom node types. Both reference one KeePass entry and show any chosen subset of its fields:

| Node type | Placement | Rendering |
| --- | --- | --- |
| `secretReference` | block | card; field names in the clear, one invisible-ink surface over all values |
| `secretInlineReference` | inline, inside paragraph text | one glittering capsule inside the sentence |

### Fields

| Field | Meaning |
| --- | --- |
| `title` | entry title |
| `path` | the entry's group path in the vault, e.g. `Company / Infrastructure / VPN` |
| `username` | login name |
| `password` | the password |
| `url` | service address |
| `notes` | free-text notes; may be a few lines long |

The canonical order is `title, path, username, password, url, notes`. The document only stores which fields are shown; every value, including the path and notes, comes from the host at release time.

### Attributes

| Attribute | Type | Default | Nodes | Rule |
| --- | --- | --- | --- | --- |
| `secretId` | string | none (required) | both | Canonical lowercase RFC 9562 UUID, version digit 1–8, variant `8`/`9`/`a`/`b`. Uppercase, braces or missing ID are rejected. |
| `field` | one field name | `'password'` | both | The single field shown while `fields` is `null` (documents older than v4 use only this). |
| `fields` | array of field names, or `null` | `null` | both | 1–6 distinct field names. `null` shows `field`. Fields always render in canonical order. Commands store the array canonically; a loaded array is kept as persisted. |
| `showCopy` | boolean | `true` | both | Offers Copy: beside each released value, or in the rail for a single field (copies without revealing). |
| `showDetails` | boolean | `false` | both | Offers Detail, which unfolds the reference into a card with every field (inline: a floating card under the sentence; block: the card itself grows). |
| `autoHideSeconds` | integer 5–3600, or `null` | `60` | both | Presentation hint only; see [autoHideSeconds is not a lifetime policy](#autohideseconds-is-not-a-lifetime-policy). |

Any other attribute is rejected. That includes values, labels, ciphertext, owner names, actual KeePass paths or notes, policy, audit IDs and access flags. Non-boolean flags, non-integer or out-of-range `autoHideSeconds`, unknown or duplicate field names and empty field lists are rejected. Validation runs when a document is loaded (editor and renderer) and on every command. A rejected document fails to load. It is never partially rendered.

Absent attributes read as their defaults. Loading does not rewrite the stored JSON: `migrateMarkdownValue` and a controller without a view return the stored attributes unchanged. A document is re-serialized with every attribute written out explicitly only once it has been parsed into an editor view. Hosts and server guards must treat an absent attribute as its default.

```json
{
  "format": "atlas-rich", "version": 1, "plugins": [{ "id": "secrets", "version": 4 }],
  "document": { "type": "doc", "content": [
    { "type": "secretReference", "attrs": { "secretId": "d847b3af-4a87-4e97-aab8-a74a4cf48c89", "field": "password",
      "fields": ["path", "username", "password", "notes"], "showCopy": true, "showDetails": true, "autoHideSeconds": 60 } },
    { "type": "paragraph", "content": [
      { "type": "text", "text": "VPN login " },
      { "type": "secretInlineReference", "attrs": { "secretId": "d847b3af-4a87-4e97-aab8-a74a4cf48c89", "field": "password",
        "fields": ["username", "password"], "showCopy": true, "showDetails": true, "autoHideSeconds": null } }
    ] }
  ] }
}
```

Full fixtures for every schema version are in [`fixtures/`](./fixtures).

### Persisted vs. host-provided

| In the document (rich JSON, history, collaboration) | Only at runtime, from the host |
| --- | --- |
| `secretId` and the presentation attributes above | Whether the reader may see the reference: discovery `hidden` / `requestable` / `available` / `{ access: 'requestable', requestPending }` |
| Plugin metadata `{ id: 'secrets', version: 4 }` | Request-access, pending and not-approved state |
| | Every field value (title, path, username, password, URL, notes), plus MFA, audit and release IDs |
| | Revocation events, clipboard writes, value expiry |
| | Field names and UI texts (`fieldLabels` and `labels` options) |

The document never contains a secret value or metadata about one. Editing a reference only changes which fields and actions are requested. It grants no rights.

### autoHideSeconds is not a lifetime policy

`autoHideSeconds` is ordinary document content. Anyone who can edit the page can change it. Once values are released, the plugin starts a timer; when it expires the mist rolls back and the plugin disposes the release, the same way it does for Hide. `null` means the plugin starts no extra timer. It never means "unlimited": the shared surface still covers values after 30 s of inactivity, when focus leaves, when the page hides and on revocation.

**The host must not treat it as authority over the lifetime of a released value.** The host must still enforce its own value expiry, session end, logout and revocation, and call the revocation callback when its policy ends access. It must never extend a release because the document asks for a longer delay.

## Commands

- `insertSecretReference({ secretId, ...presentation })` inserts a block reference.
- `insertInlineSecretReference({ secretId, ...presentation })` inserts an inline reference at the selection.
- `updateSecretReferencePresentation(presentation)` changes the selected reference only. It never changes `secretId`.

The page source can change any attribute, `secretId` included, through the [Markdown form](#markdown-form). Like every document edit, that grants no rights; server guards check new and rewritten references (for collaboration, `IReservedNodeGuard` receives the current and the new `secretId`, see [the content guard](../../../../../docs/markdown/collaboration-content-guard.md)).

Each command validates the same rules, fills in the defaults, stores `fields` canonically and creates an independent undo step. Unknown or extra keys make it return `false`. No command turns selected plaintext into protected storage. Use a host picker to obtain an authorized reference.

In the editor a reference is edited where it is shown (interaction rule P1): a click on the card or on the inline reference, or Enter or F2 on a selected one, opens an anchored panel for the shown fields, Copy, Detail and automatic hiding; the same gesture closes it again, and the shared selection toolbar's **Upravit** opens the same panel. The panel keeps a draft and writes it as one undo step on Použít (Esc and Zrušit throw it away). **Odebrat heslo** removes the reference from the page; the secret stays in the vault. The controls are hidden while readonly. An inline reference is drawn as the core's shared inline chip (`createMarkdownChip`, a tag): the key and the shown fields' names in the clear, the values under the ink, the chip's selected look.

A card in an editable editor also carries its common settings as chips on the card itself: Vyměnit heslo, one toggle per field (the last shown field stays on), „Skrýt po N s“ (on: `autoHideSeconds` = its value or 60, off: `null`) and Další… for the panel. Each chip writes one undo step and focus returns to the same chip on the repainted card. The chips are part of the painted card, so a reference the reader may not discover shows none; readonly editors and `MarkdownRenderer` show none either.

### Swapping the secret (`pickReplacement`)

With `pickReplacement(context)` the editor offers **Vyměnit heslo** as a named action: the card chip and a toolbar button on card and inline references (Enter edits the reference, it never swaps it). The plugin calls `pickReplacement({ secretId, layout, signal })` (the current id, so the host picker can open on it) and expects the chosen secret id, or `null` when the author closed the picker. It writes only that `secretId` into the reference, keeping every display attribute, as one undo step. An invalid id, the same id, a rejected promise, or a reference removed or repointed while the picker was open writes nothing. Like any reference edit this grants no rights: the host picker offers only secrets the author may Discover, and every release is authorized again on the server. Without the option there is no swap control.

## Markdown form

References have a lossless Markdown form, so the page source (`MarkdownSourcePanel`, Atlas's Zdroj) edits them like any other Markdown instead of protecting or dropping them. It is a directive with the reference attributes only; attributes at their default are left out:

```md
::secret{secretId=d847b3af-4a87-4e97-aab8-a74a4cf48c89 fields="path username password notes" showDetails=true}

VPN login :secret{secretId=d847b3af-4a87-4e97-aab8-a74a4cf48c89 fields="username password" autoHideSeconds=null} here.
```

- `::secret{…}` alone on a line (up to three spaces of indentation, also inside lists and quotes) is a block `secretReference`; `:secret{…}` inside text is an inline `secretInlineReference`.
- Attributes are `name=value` pairs separated by spaces; a value with spaces is quoted (`fields="username password"`). Allowed names: `secretId`, `field`, `fields` (space-separated, kept in the persisted order), `showCopy`, `showDetails` (`true`/`false`), `autoHideSeconds` (5–3600 or `null`). The same rules as the [attributes](#attributes) apply.
- A directive with a value, a label or any other unknown attribute, a repeated attribute or an invalid value is refused as a whole: the page source reports a Czech diagnostic that never repeats the text, and nothing is stripped silently.
- Document → Markdown → document keeps block and inline references identical. Text that would itself read as a directive (`:secret{` outside code, or a colon right before an inline reference) cannot round-trip: the document reports `markdownLossy: true` and the page source protects that block.
- The syntax is recognized only where the page's own Markdown is parsed (the page source, stored Markdown, `insertContent` with Markdown). Pasted or dropped Markdown from another wiki or a `.md` file keeps it as literal text, so an import never creates a reference. The rule checks `isMarkdownImportActive()` from `@atlas/markdown/core`.
- `serializeSecretDirective(layout, attrs)` and `parseSecretDirectiveAttributes(text)` expose the same syntax, for example for a server-side projection guard.

## Projections and serialization

Markdown projections carry the directive above: the reference attributes, never a value, with `markdownLossy: false`. Hosts that publish Markdown outside the authorized page (exports, search snippets for other readers, external sharing) must filter `::secret{…}` and `:secret{…}` the same way they filter the rich JSON. The plain-text projection omits both nodes. HTML and clipboard serialization emit only an empty hidden marker (`data-md-secret` / `data-md-secret-inline`) with no identifier or value. HTML paste cannot recreate a reference: the parse rule only accepts markup that the plugin's own Markdown rule wrote in the same page load. Rich JSON, history and collaboration retain the ID by design. Released values live only in host-created DOM elements under the concealed surface, never in the document, a transaction or undo history. The plugin does not import the generic block module, whose data attributes would expose serialized metadata.

## Host service boundary

```ts
import { createSecretsPlugin, insertInlineSecretReference } from '@atlas/markdown/plugins/secrets'
import '@atlas/markdown/concealment/style.css'

const plugin = createSecretsPlugin({
  services: {
    discover: ({ secretId, signal }) => api.discover(secretId, signal),
    // `reason` is what the reader typed in the request panel (trimmed, possibly empty).
    requestAccess: ({ secretId, reason, signal }) => api.requestAccess(secretId, reason, signal),
    // Authorize, ask for a second factor and audit, then hand back one element per field.
    reveal: async ({ secretId, fields, signal, ownerDocument }) => {
      const entry = await api.release(secretId, fields, signal)
      const values = Object.fromEntries(fields.filter(field => field in entry).map(field => {
        const element = ownerDocument.createElement('span'); element.textContent = entry[field]; return [field, element]
      }))
      return { values, dispose: () => entry.clear() }
    },
    copy: ({ secretId, field, signal }) => api.copyToClipboard(secretId, field, signal),
    subscribeRevocation: (secretId, revoke) => accessEvents.subscribe(secretId, revoke),
  },
  revealTimeoutMs: 180_000, // time for the host's second factor; 1000–600000 ms
  requestReason: 'optional', // 'required' does not send without a reason; 'off' sends directly (no panel)
  labels: { reveal: 'Odkrýt', hide: 'Skrýt', copy: 'Kopírovat', details: 'Detail', collapse: 'Sbalit' },
})

// Host controls on the same plugin instance, for every editor and renderer it is registered in:
plugin.conceal(secretId)                    // cover released values again; the reference stays usable
plugin.conceal(undefined, { immediate: true }) // every reference, without the gentle roll-back
plugin.refresh(secretId)                    // rerun discovery: 403 lowered the level, a request was approved
```

### Host controls

`createSecretsPlugin` returns a `SecretsPlugin`: the `MarkdownPlugin` plus two methods that act on the references this instance currently shows. Without an id they act on every reference; with one, on the references naming that canonical `secretId`. Both return how many shown references matched.

- `conceal(secretId?, { immediate? })` covers released values again. The mist rolls back like Hide (`immediate: true` resets at once, as for a security event) and the plugin disposes the release. The reference stays visible and interactive, nothing is revoked and discovery is not repeated. A reference that shows nothing released is unaffected.
- `refresh(secretId?)` runs discovery again without a remount. Use it when the host learns that the access level changed: a release answered 403 and the level dropped back to discovery, an access request was approved, or access returned after a revocation. A discovery still running is aborted. An unchanged answer keeps what is shown, released values included; a changed one repaints the reference (disposing released values) in its new state, or hides it. A revoked reference comes back only through such a refresh.

### Access request reason

**Požádat o přístup** (always visible on a requestable reference, see below) opens a small anchored panel (the shared reader panel: no title or close button, an accessible name, one active panel per editor or rendering) with a reason text area and **Zrušit** / **Odeslat**. Escape, a click outside and Zrušit send nothing and leave the reference requestable; Ctrl+Enter (⌘+Enter) sends from the text area. It works for readers in readonly editors and in `MarkdownRenderer`. `requestAccess` then receives `{ secretId, signal, reason }` with the reason trimmed (at most 1000 characters, empty when none was given). The reason is never stored in the document or plugin state. A host written for 0.5 that ignores `reason` keeps working. `requestReason: 'required'` refuses to send an empty reason; `'off'` sends the request directly without the panel, with an empty `reason`.

`labels` and `fieldLabels` override one plugin instance. Their defaults are Czech and come from `markdownStrings.secrets` (`setMarkdownStrings({ secrets: { … } })` changes them for every instance created afterwards).

Discovery returns `hidden`, `requestable`, `available`, or `{ access: 'requestable', requestPending: boolean, requestRefused?: boolean }`. `requestRefused` (optional, new in 0.7) says the reader's last request ended without access (rejected, cancelled or expired), so the reference shows „Žádost nebyla schválena“ beside a new request, also after a reload; without it, a request this reference showed as pending that a `refresh` finds settled without access shows the same until the next remount. Report `requestPending: true` for a request that is still open, or a reload shows the reference as never requested. It must never release a value. A hidden, malformed or failed result shows nothing. Request-access and release are separate operations, and a request that is pending or being sent cannot be submitted again. A request that does not settle within 60 seconds is aborted. Reader release controls also work in readonly documents: document editability is never an authorization claim.

`reveal` is the explicit release for display. One call releases every field the surface shows, so one Reveal uncovers them together. It must resolve with `{ values, dispose }`: one element per released field, created in `ownerDocument`, omitting fields the entry does not have (the plugin shows a dash there). Plain strings, nodes from another document and malformed results are refused and `dispose` is called. `dispose` runs on Hide, `autoHideSeconds`, inactivity, page hiding, replacement or removal, unmount and revocation. The host clears its values there and ignores delayed responses after `signal` aborts. A release still pending after `revealTimeoutMs` (default 180 s) is aborted. `copy` is the explicit release for copying one field: the host writes its own value to the clipboard, nothing passes through the plugin.

The shared concealed surface draws synthetic fog and particles. Available references show no button by default: on hover, focus or tap, Reveal (plus Copy and Detail when configured) slides in from the right over the glitter. Requestable references show their request at rest: the message „Hodnoty jsou skryté“ and a **Požádat o přístup** button with a lock (a card keeps both in the ink, stacking them on narrow screens; an inline entry carries the button in the sentence and shows the message on hover or focus). Tab reaches the button directly. A sent request replaces the button with „Žádost odeslána · čeká na schválení“ in a calm blue; a request that was not approved shows „Žádost nebyla schválena“ with the button again; an approved one unlocks in place when the host calls `refresh` (the reference fades into its new state). A failed send keeps the button with „Žádost se nepodařilo odeslat. Zkuste to znovu.“ Hover, scratching or tapping the surface never invokes release, copy or a request. See [concealed content](../../../../../docs/markdown/concealed-content.md) for standalone text and image surfaces.

The server must authorize discovery, requesting access and **every** release (including Copy) independently. Bind IDs to the authenticated tenant, document and resource. Editing a reference or knowing a UUID grants no rights. Enforce policy, recent MFA, revocation, rate limits and durable release audit on the server; the `reveal`/`copy` split maps to "released for display" and "released for copying". A client copy event can be useful telemetry, but it cannot prove every copy, screenshot, browser inspection or downstream disclosure.

Released values sit in the page DOM while shown. Isolating them from other scripts on the page (editor plugins, XSS) is the host's decision and needs its own security review: the host may, for example, return elements that host a closed shadow root or a sandboxed frame.

**Hiding presence requires server filtering before delivering JSON/history/collaboration snapshots.** Once a browser receives a reference, JavaScript cannot hide its existence from that reader. A common shared Yjs document containing hidden references does not satisfy per-reader non-discovery. Do not use CSS or the plugin's hidden rendering as a security boundary. Server guards must recognize both node types and the `secrets` plugin requirement for every schema version.

## Display modes

Inline references read like one glittering word in a sentence. At rest the capsule has a synthetic width that depends only on how many fields it shows, with their icons over the glitter. After Reveal the capsule first widens smoothly under the fog to fit the values, then the mist parts and the values rise in one after another, each with Copy. Detail unfolds a floating card with every field out of the capsule.

Block references render as a card: field names on the left, one surface over all values on the right. The notes row has a fixed synthetic height while concealed and grows to the released text (scrolling past about six lines). Detail makes the card grow to every field; values that were already released stay released in one further release.

With reduced motion (for example Windows "Animation effects" turned off) the same steps cross-fade instead of moving.

## Migration from v1/v2/v3

Older documents load through the standard plugin migration runner. All steps (`1 → 2`, `2 → 3`, `3 → 4`) are identity migrations: the plugin metadata advances to version 4, and the document tree is not rewritten. Absent attributes then read as the v4 defaults.

| Stored version | What it contained | After loading |
| --- | --- | --- |
| 1 | block `secretReference` with `secretId` only | `field: 'password'`, `fields: null`, `showCopy: true`, `showDetails: false`, `autoHideSeconds: 60` |
| 2 | adds `secretInlineReference` and `field`, `showCopy`, `showDetails` | keeps those values; adds `autoHideSeconds: 60` and `fields: null` |
| 3 | adds block `fields` and `autoHideSeconds` | unchanged; inline references gain `fields: null` |

v4 allows `fields` on inline references and adds the `path` and `notes` fields. The declared version does not restrict which attributes are allowed: a document labelled v1–v3 is validated by the v4 rules. The runner rejects:

- Versions newer than 4.
- Versions with no migration step (`0`, negative, fractional or string versions).
- A `secrets` requirement without a version.
- Secret nodes without plugin metadata.
- Documents requiring `secrets` when the plugin is not registered.

[`secrets.migration.test.ts`](./secrets.migration.test.ts) covers the [fixtures](./fixtures) and each of these cases.

## Mock pilot and tests

Open the isolated gallery at `/?story=secrets`. Its mock host releases a synthetic KeePass entry into the page: inline entries in a sentence, a card with path and notes, a requestable card and a hidden reference. It counts releases for display and copying, can revoke and restore access, and writes only the synthetic value to the clipboard. It has no network, authentication, MFA or audit.

The adjacent tests use synthetic UUIDs, canary values and host service fakes:

```sh
pnpm --filter @atlas/markdown exec vitest --configLoader runner --run src/plugins/secrets
```

They drive real controllers and the Vue renderer through:

- Hostile attributes, insertion, undo and redo.
- Schema migration.
- HTML, clipboard and projection redaction.
- One release per surface, refusal of plaintext and foreign nodes, per-value and rail Copy.
- Card and inline Detail, notes, readonly actions.
- Hidden references and errors, late responses, timeouts, revocation and replacement.
- Independent instances.

They do not establish the security of a real host service, MFA provider or clipboard implementation. Production rollout remains disabled until that host boundary is implemented and reviewed.

Cross-browser UI tests run from `tests/e2e` with `node node_modules/@playwright/test/cli.js test --config playwright.secrets.config.ts`. They check explicit release, in-place reveal and Detail, per-value copy, request access without implicit approval, and revocation in Chromium, Firefox and WebKit.
