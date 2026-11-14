# Inline secret references

The user requested password references inside a sentence. Passwords always expose an explicit Reveal control; hover never invokes the host's release/copy operation. A successful authorized release may part synthetic fog with randomized motion. Generic concealed text keeps scratch interaction. Requestable users see a stub, pending users cannot request again, and undiscoverable users must receive no reference from the host.

## Persistence model

The current persisted contract (schema v4), attribute rules and the v1–v3 migration are defined only in the [secrets plugin README](../../src/Atlas.Markdown/src/plugins/secrets/README.md#document-contract-schema-v4). This design introduced the inline atom `secretInlineReference` in schema v2, next to the existing block `secretReference`. Both store a canonical lowercase UUID `secretId` and non-sensitive presentation settings. No labels, passwords, ciphertext, access flags, actual URLs, KeePass paths, notes or copy payloads may be stored; the document only names which entry fields are shown. No database table or server storage change is included.

`insertInlineSecretReference({ secretId })` inserts an authorized host-selected reference at the current position, with its own undo boundary. It does not turn literal selected passwords into securely stored resources. The future protected-section action needs its own host storage and authorization flow.

Both node types omit values and identifiers from static HTML/clipboard and plain-text projections. Markdown carries the reference attributes (never a value) as a lossless directive, see the [Markdown form](../../src/Atlas.Markdown/src/plugins/secrets/README.md#markdown-form). Static enhancement matches constant empty markers to validated document references, preserving inline versus block layout. Rich JSON/history still retain reference IDs. Atlas ingestion and external projection guards must recognize both node types even without plugin declarations; the production secret release integration remains gated.

## Interaction and ownership

Update 2026-09-26: a reference shows any subset of the KeePass entry fields (title, path, username, password, URL, notes) under one invisible-ink surface. One explicit Reveal releases them together in place: the host's `reveal` returns host-created elements that are shown under the parting mist and disposed on hide, and `copy` releases a single field to the clipboard. Detail unfolds the reference into a card with every field. Plaintext still never enters editor state, the document or history. The host owns real values, MFA, auditing, revocation, clipboard operations and disposal. Record authorized issuance for display or copying, not an unverifiable claim that a human read it. Hover and failed authorization must not produce a successful-release audit event. The existing lifetime/timeout/subscription behavior remains shared with block references.

Editing the page permits reading ordinary concealed page text in the eventual protected-section integration. It does not grant secret-release access. Editors may configure the reference/presentation, while the host authorizes browsing/selecting KeePass entries and stores the vault/entry mapping separately. The pilot does not add a real KeePass picker or server audit endpoint.

Expose keyboard and touch access, bounded synthetic particles and reduced motion. Copy must be reachable after touch activation, not require simultaneous hover. The concealed copy placeholder/icon is synthetic UI, not a blur of a real secret. Unknown access and failed discovery hide the node. Duplicate requests are suppressed while sending or pending; server idempotency is still required.

## Verification

Test-first: inline insertion inside a paragraph, schema migration, malformed/extra attributes, undo/redo, serializer/clipboard/text omission, readonly host intents, no release on hover, pending requests, cancellation/revocation, and independent instances. Browser demo includes a sentence containing the inline atom. Run package types/build/packed consumer and the focused browser scenarios; independently review both frontend and Atlas rollout guards before completion.
