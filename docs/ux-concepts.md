# UX concepts: one component per concept

Once Atlas introduces an interaction concept, every page, board, object and settings screen uses the same component and
behaviour for it. A new variant needs a reason in the component gallery (`/?gallery=1`, P16). Detailed rules:
[interaction-rules.md](interaction-rules.md). Phone = viewport at most 760 px (pending owner decision D1; AGENTS.md says 680 px today).
Motion uses tokens only (`--atlas-motion-*`) and reduced motion is respected everywhere. Items marked "pending" wait for the owner.

| Concept | Canonical component | Behaviour rules |
|---|---|---|
| Panel beside the page (detail, discussion, history summary, assistant, person) | `WorkspaceSidePanel` via `useWorkspacePanel().show()` ("the dock") | Trigger toggles; one panel at a time; no click-outside close; Esc closes after inner fields; focus returns to the opener, else `main`; slides from its edge, page moves by FLIP; covers the screen on phone. Everything "beside the page" is the dock. |
| Wide tool that temporarily replaces the page (history compare, OpenAPI, readership heatmap) | `AppDialog size="side"` | Modal; the owner decides on `close` (Esc, backdrop, button); full width on phone. Not for anything the user keeps working next to. |
| Dialog (question, form, irreversible action) | `AppDialog`; `ConfirmDialog`, `ReauthDialog` | Modal; `[autofocus]` then first field; focus returns to the opener; `ConfirmDialog` may also carry a non-destructive question (`destructive=false`). |
| Unsaved work | `UnsavedChangesDialog` (Zůstat / Zahodit / Použít) | One dialog for leaving a dirty form; drafts outside forms use `lib/unsavedWork.ts`. |
| Popover anchored to a control | App: `ui/popover`; document: editor popovers; boards: same look | Click outside or Esc closes; focus returns to the trigger. |
| Actions menu "Co s tím jde dělat" (P2) | Editor intentions menu; list rows use `RowActionsMenu` | Ctrl+Space, Alt+Enter, Ctrl+., Shift+F10, menu key, right button; first row preselected; typing filters. |
| Visible "..." / "Nový" / choice menu | `ActionDisclosure`, `ActionMenu`, `FilterMenu` in the editor menu look | No title, no x; non-modal; works inside dialogs; 44 px rows on phone. |
| Label of an icon-only button | `ui/tooltip` (also on keyboard focus); menu triggers keep `title` | Hover or focus; not shown on touch. |
| Notification | `useToast` (4 s, errors 8 s, `undo` = "Zpět"); settings frame and profile: result bar via `flash` (pending owner decision D3) | Never steals focus. Reversible action: always a toast with "Zpět". The publication state of a page becomes a toast (pending owner decision D4). |
| Delete | Trash plus toast "Zpět"; `ConfirmDialog` only when irreversible or when the user cannot restore | Permanent deletion without trash is "Smazat trvale". Undo instead of confirmation for those who may restore (pending owner decision D5). |
| Loading | `LoadingSkeleton`, `LoadingRegion`, `LoadingNote`; `ui/button busy` for actions | Show at once with a sized skeleton; no hand-made spinners or bare "Načítání...". |
| Empty / error | `EmptyState`, `ErrorState` with Retry; `.notice.error` plus link button in compact rows | A failed region offers Retry. |
| List of items | `SettingsList` + `SettingsListRow`, `TrashList`; columns: `ui/table` or `div role=table` | Arrow keys between rows; Delete and F2 act on the row. |
| Bulk selection | `ui/checkbox` appearing on hover or focus; bar above the list | Start by ticking, no separate "Vybrat" mode. |
| Search field / picker (P8) | `SearchField` in app chrome, `md-input` in editor and board panels; `useSearchKeys` | First result preselected; Enter takes it; first Esc clears, second closes. |
| View switch / tabs | `OptionPills`, `SettingsTabs`, dock tabs | Radiogroup: one Tab stop, arrows select. Board dock tabs follow the page order. |
| Status / date / person | `StatusBadge` (icon plus word); `formatDate.ts`; `SubjectAvatar` | Date: list row "ago" plus exact title; table column short date plus title; detail date and time (pending owner decision D9). Every user in their own colour (pending owner decision D8). |
| Load more | `LoadMoreButton`, "Načíst další" (pending owner decision D10) | Automatic only in search. |
| Discussion | `PageDiscussionButton` + `CommentThread` in the dock | Pages and boards alike; phone: page below content, board in the dock. One board item: „Diskuse“ in its actions menu, as an object's. |
| Contact card | Editor block and `ContactCard` render the same data | Change one, check the other (pending owner decision D7). |
