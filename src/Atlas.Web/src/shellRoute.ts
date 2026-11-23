import { resolveSettingsHash, type SettingsLocation } from "./settingsRoutes";

/**
 * The address grammar of the application shell: `#/<view>/…` after the application's path.
 *
 * `ShellRoute` is one value for where the shell is. `parseShellHash` reads it from a hash and `buildShellHash`
 * writes it back. The functions are pure: they take the address as an argument, never read `location` and never
 * navigate. What a route opens, and when the address changes, is still App.vue's business.
 *
 * What belongs to others:
 * - Settings (`#/settings/…`, `#/spaces/<id>/settings/…`). `settingsRoutes.ts` recognises the surface and its old
 *   links, and the surface's own router (`components/settings/useSettingsRoute.ts`) parses the path and the filters
 *   below it. Here a settings address is one route that carries the recognised surface unread.
 * - A public share (`/s/<token>`) is read from the path before the shell exists (`parseSharedToken` in `sharing.ts`).
 * - The omnisearch overlay lies over any view and keeps its state in the query before the hash (`?search=1&q=…`);
 *   the search code in App.vue reads and writes it.
 * - The component gallery (`?gallery=1`, development only) replaces the shell in `main.ts` and routes
 *   `#/gallery/…` with a settings router of its own.
 *
 * The grammar is not yet in this one place. These still read or write a shell address by hand, so a change of
 * the grammar has to visit them too:
 * - Reading: App.vue (the selector that spots a page link under the pointer), `pageIdFromUrl` in
 *   `editorDirectories.ts`, `pageHref` in `assist/linkSuggestions.ts`, `pageUrl` in `assist/rules/intentRules.ts`,
 *   `pageHref` in `editor/revealPlugin.ts`, and `print/PrintDocument.vue`, which takes every `#/…` for a link of
 *   the application.
 * - Writing: the heading link and the section link of a selection in `components/DocumentEditor.vue`, the two fixed
 *   links in App.vue's template, and the links written inline in these components: `ProfilePage.vue`,
 *   `auth/IdentityConnections.vue`, `library/LibraryPage.vue`,
 *   `sharing/PageSharingView.vue`, `settings/access/MyAccess.vue`, `settings/general/ai/AiIndexSection.vue`,
 *   `settings/global/OverviewPage.vue` and `settings/space/DuplicatePagesSection.vue`.
 */

/** Knihovna's tabs: the overview of all spaces, the personal lists the server keeps, the contact directory, and the personal space
 * (Můj prostor, Sdíleno se mnou; spec 2026-10-29), then the inbox Oznámení. */
const libraryViews = ["spaces", "recent", "favorites", "drafts", "updates", "contacts", "personal", "shared", "notifications"] as const;
export type LibraryView = (typeof libraryViews)[number];

/** The sections of Váš profil. */
const profileTabs = ["account", "security", "access", "requests", "visits", "activity", "appearance", "ai", "api"] as const;
export type ProfileTab = (typeof profileTabs)[number];
/** The section the bare `#/profile` of older links opens. */
const defaultProfileTab: ProfileTab = "account";

/** The parts of a page's „Kdo má přístup“. */
const pageAccessTabs = ["access", "links", "history"] as const;
export type PageAccessTab = (typeof pageAccessTabs)[number];
/** The part at `#/pages/<id>/access` itself. The others follow it as a segment. */
const defaultPageAccessTab: PageAccessTab = "access";

/** The creation flows of a space: `#/spaces/<id>/new-page`, `…/new-template`, `…/new-board` (module Boards) and `…/new-deployment` (module Projector). */
const pageCreationKinds = ["new-page", "new-template", "new-board", "new-deployment"] as const;
export type PageCreationKind = (typeof pageCreationKinds)[number];

/** The tabs of a space's detail that have an address of their own: `#/spaces/<id>/about`, and `…/boards`, the space's list filtered to its
 * boards (module Boards; the boards live in the list and the tree since Atlas 3, the address stays for links), and `…/deployments` and
 * `…/waiting`, Přehled nasazení and Na co čekáme (module Projector), and `…/contacts`, the contacts of the space (the entity catalog).
 * Stránky is the bare `#/spaces/<id>`. */
const spaceTabs = ["about", "contacts", "boards", "deployments", "waiting"] as const;
export type SpaceTab = (typeof spaceTabs)[number];

export type ShellRoute =
  /** `#/`, and every address the shell does not know: Knihovna with its first tab. */
  | { view: "home" }
  /** `#/library/<tab>`, with `?q=` the tab's search (Kontakty: who knows a topic) */
  | { view: "library"; tab: LibraryView; q?: string }
  /** `#/spaces/<id>`, `#/spaces/<id>/about`, `…/boards`, `…/deployments` and `…/waiting` */
  | { view: "space"; spaceId: string; tab?: SpaceTab }
  /** `#/new-space` */
  | { view: "spaceCreation" }
  /** `#/spaces/<id>/new-page` and `#/spaces/<id>/new-template` */
  | { view: "pageCreation"; spaceId: string; kind: PageCreationKind }
  /** `#/spaces/<id>/page-templates`, the former list of templates: the shell sends it to the space's settings. */
  | { view: "pageTemplates"; spaceId: string }
  /** `#/pages/<id>`, `#/pages/<id>?heading=<heading id>`, `#/pages/<id>?block=<block id>` (a requirement block, U4-2) and `#/pages/<id>?comment=<comment id>` (a notification of a mention) */
  | { view: "page"; pageId: string; heading?: string; block?: string; comment?: string }
  /** `#/pages/<id>/access`, `…/access/links` and `…/access/history` */
  | { view: "pageAccess"; pageId: string; tab: PageAccessTab }
  /** `#/profile/<tab>` */
  | { view: "profile"; tab: ProfileTab }
  /** `#/whats-new`: Co je nového, the release notes. */
  | { view: "whatsNew" }
  /** `#/boards/<id>`: a board of the Boards module (the shell opens it only when the module is enabled). */
  | { view: "board"; boardId: string }
  /** A settings surface. `settings.hash` is its canonical address, also for an old link that is redirected. */
  | { view: "settings"; settings: SettingsLocation };

const spaceCreationHash = "#/new-space";
const whatsNewHash = "#/whats-new";
const pageTemplatesPart = "page-templates";

/** The ids an address may carry. Anything else is not an address of the shell. */
const id = "[a-zA-Z0-9-]+";
const oneOf = (names: readonly string[]) => names.join("|");
const spacePartPattern = new RegExp(`^#/spaces/(${id})/(${oneOf(pageCreationKinds)}|${pageTemplatesPart}|${oneOf(spaceTabs)})$`);
const boardPattern = new RegExp(`^#/boards/(${id})$`);
const libraryPattern = new RegExp(`^#/library/(${oneOf(libraryViews)})(?:\\?q=([^&]*))?$`);
const profilePattern = new RegExp(`^#/profile(?:/(${oneOf(profileTabs)}))?/?$`);
const pageAccessPattern = new RegExp(`^#/pages/(${id})/access(?:/(${oneOf(pageAccessTabs.filter(tab => tab !== defaultPageAccessTab))}))?/?$`);
const contentPattern = new RegExp(`^#/(pages|spaces)/(${id})(?:\\?(heading|block|comment)=([^&]+))?$`);
/** A part below a page the shell does not know (`#/pages/<id>/history`): the page itself, not Knihovna. */
const pagePartPattern = new RegExp(`^#/pages/(${id})/[^?]+$`);

/** The name as a member of its closed set, or undefined. */
function member<T extends string>(names: readonly T[], value: string | undefined) {
  return names.find(name => name === value);
}

/** A heading id from the address: decoded, at most 200 characters, no control characters. Anything else is no heading. */
function headingOf(encoded: string | undefined) {
  if (!encoded) return undefined;
  try {
    const heading = decodeURIComponent(encoded);
    return heading.length <= 200 && !/[\u0000-\u001f]/.test(heading) ? heading : undefined;
  } catch {
    return undefined;
  }
}

/**
 * The route a hash names. The forms are tried in the order below, which is the order the shell has always used.
 * An empty, unknown or malformed hash is `home`, except an unknown part below a page, which opens the page. A heading on a space
 * address is ignored.
 */
export function parseShellHash(hash: string): ShellRoute {
  const spacePart = spacePartPattern.exec(hash);
  if (spacePart) {
    const tab = member(spaceTabs, spacePart[2]);
    if (tab) return { view: "space", spaceId: spacePart[1], tab };
    const kind = member(pageCreationKinds, spacePart[2]);
    return kind ? { view: "pageCreation", spaceId: spacePart[1], kind } : { view: "pageTemplates", spaceId: spacePart[1] };
  }
  const board = boardPattern.exec(hash);
  // Lower case: the server writes ids that way and names the live channel `board:<id>` after them.
  if (board) return { view: "board", boardId: board[1].toLowerCase() };
  const libraryMatch = libraryPattern.exec(hash), library = member(libraryViews, libraryMatch?.[1]);
  if (library) { const q = headingOf(libraryMatch?.[2]); return q ? { view: "library", tab: library, q } : { view: "library", tab: library }; }
  if (hash === spaceCreationHash) return { view: "spaceCreation" };
  if (hash === whatsNewHash) return { view: "whatsNew" };
  const settings = resolveSettingsHash(hash);
  if (settings) return { view: "settings", settings };
  const profile = profilePattern.exec(hash);
  if (profile) return { view: "profile", tab: member(profileTabs, profile[1]) ?? defaultProfileTab };
  const pageAccess = pageAccessPattern.exec(hash);
  if (pageAccess) return { view: "pageAccess", pageId: pageAccess[1], tab: member(pageAccessTabs, pageAccess[2]) ?? defaultPageAccessTab };
  const content = contentPattern.exec(hash);
  if (content?.[1] === "pages") return { view: "page", pageId: content[2], [content[3] === "block" || content[3] === "comment" ? content[3] : "heading"]: headingOf(content[4]) };
  if (content) return { view: "space", spaceId: content[2] };
  const pagePart = pagePartPattern.exec(hash);
  if (pagePart) return { view: "page", pageId: pagePart[1] };
  return { view: "home" };
}

function hashOf(route: ShellRoute, writeId: (value: string) => string): string {
  switch (route.view) {
    case "home": return "#/";
    case "library": return `#/library/${route.tab}${route.q ? `?q=${encodeURIComponent(route.q)}` : ""}`;
    case "space": return `#/spaces/${writeId(route.spaceId)}${route.tab ? `/${route.tab}` : ""}`;
    case "spaceCreation": return spaceCreationHash;
    case "pageCreation": return `#/spaces/${writeId(route.spaceId)}/${route.kind}`;
    case "pageTemplates": return `#/spaces/${writeId(route.spaceId)}/${pageTemplatesPart}`;
    case "page": return `#/pages/${writeId(route.pageId)}${route.heading ? `?heading=${encodeURIComponent(route.heading)}` : route.block ? `?block=${encodeURIComponent(route.block)}` : route.comment ? `?comment=${encodeURIComponent(route.comment)}` : ""}`;
    case "pageAccess": return `#/pages/${writeId(route.pageId)}/access${route.tab === defaultPageAccessTab ? "" : `/${route.tab}`}`;
    case "profile": return `#/profile/${route.tab}`;
    case "whatsNew": return whatsNewHash;
    case "board": return `#/boards/${writeId(route.boardId)}`;
    case "settings": return route.settings.hash;
  }
}

/**
 * The hash of a route, with its ids written as they are given. This is what the shell puts into the address
 * and compares with `location.hash`. The heading is always percent-encoded.
 */
export function buildShellHash(route: ShellRoute) {
  return hashOf(route, value => value);
}

/**
 * The same hash with the ids percent-encoded, as the links to pages and spaces have always written them.
 * Both functions give the same text for every id the parser accepts (letters, digits and hyphens), so they differ
 * only for an id that is not an address anyway. Each caller keeps what it wrote before this module existed;
 * one of the two functions can go once the callers are settled on one form.
 */
export function buildShellHashEncoded(route: ShellRoute) {
  return hashOf(route, encodeURIComponent);
}
