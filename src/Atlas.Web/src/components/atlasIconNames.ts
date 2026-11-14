import type { MarkdownIconName } from "@atlas/markdown/icons";

/**
 * Wiki-local names that resolve to a shared registry drawing (`@atlas/markdown/icons`).
 *
 * A name here that already matches a registry key (e.g. `book-open`) is a no-op alias, kept
 * so existing data (`space.icon` values persisted before the registry existed) keeps rendering.
 *
 * Two source names deliberately resolve to the same drawing: `identity`/`shield-check` (the OIDC
 * module icon and the profile "Security" section both read as identity/trust) and
 * `workflow`/`users` (a space's "Team" icon and the "Access" menu action are both about people).
 * `layers` and `drafts` have no Lucide equivalent in the registry (no `layers`, no
 * `file-pen-line`); `layout-template` (stacked panels) and `pencil` (in progress) are the closest
 * available stand-ins — see the Task 3.2 report for this deviation from the plan text.
 */
export const atlasIconNames: Record<string, MarkdownIconName> = {
  recent: "clock",
  alert: "octagon-alert",
  star: "star",
  drafts: "pencil",
  updates: "history",
  published: "megaphone",
  download: "download",
  book: "book-open",
  "book-open": "book-open",
  // Server-assigned: a Wiki.js import creates its space with this icon. The registry has no archive drawing.
  archive: "book-open",
  down: "chevron-down",
  right: "chevron-right",
  page: "file-text",
  "file-plus": "file-plus",
  "folder-plus": "folder-plus",
  fingerprint: "fingerprint",
  identity: "shield-check",
  account: "user",
  "shield-check": "shield-check",
  history: "history",
  palette: "palette",
  home: "home",
  layers: "layout-template",
  logout: "log-out",
  menu: "menu",
  plus: "plus",
  search: "search",
  settings: "settings-2",
  close: "x",
  more: "ellipsis",
  "code-xml": "code-xml",
  plug: "plug",
  compass: "compass",
  workflow: "users",
  users: "users",
  blocks: "box",
  moon: "moon",
  sun: "sun",
  monitor: "monitor",
  back: "arrow-left",
  check: "check",
  external: "external-link",
  organize: "folder-input",
  trash: "trash-2",
  link: "link",
  copy: "copy",
  refresh: "refresh-cw",
  list: "list",
  // Záloha do Gitu: the registry has no Lucide `git-branch`; `workflow` (two nodes joined by a branch line) is the closest drawing.
  "git-branch": "workflow",
  // Oznámení (inbox spec 2026-10-29): the registry has no bell, at-sign or share drawing; the closest ones keep the rail in the core.
  notifications: "megaphone",
  "comment-mention": "quote",
  "page-mention": "file-text",
  shared: "users",
  "read-all": "circle-check",
};
