import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, nextTick, onMounted, watch } from "vue";
import { readdirSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { toast } from "vue-sonner";
import { normalizeContact } from "@atlas/markdown/plugins/contacts";
import { rankPeople, type PersonHit } from "../contacts/contactSearch";
import type { InteractionCheck, InteractionDeviation } from "./interaction";
import { runInteractionContract, type InteractionDescriptor, type Probe, type ProbeResult } from "./contractRunner";
import deviations from "./deviations/app.json";
import { jsonResponse, memoryStorage, mountApp as mountShell, quietRoutes, resetBrowser, routedFetch, sessionRoutes, type Route } from "../testing/appHarness";
import { refreshLexicon } from "../linking/lexicon";
import { pageSearchQuery } from "../editor/pageSearchState";
import { emptyDocument } from "../document";
import { entityApi, type EntityItem } from "../entities/entityApi";
import type { Job, JobApi } from "../jobs/jobApi";
import { createLiveObjects } from "../objects/liveObjects";
import { memoryRelations, type MemoryObject } from "../objects/memoryRelations";
import type { ObjectDto, ObjectSummary } from "../objects/objectApi";
import type { PersonalItem } from "../personal/personalApi";
import type { TreePage } from "../treeMove";
import { catalogModules } from "../projector/catalogSelection";
import { iconPackCatalog, loadIconPackCatalog, loadIconPackMarkup } from "../iconPack";
import { appearanceIcons } from "../components/settings/appearanceIcons";
import { spaceCoverName } from "../spaceCoverCatalog";
import type { SpaceCoverChoice } from "../spaceCover";
import type { BoardCommand } from "../boards/boardCommands";
import type { CatalogDefinition } from "../projector/projectorApi";
import { loadActionsMenu } from "../components/boards/canvas/actionsMenuChunk";
import BoardCommandPanel from "../components/boards/canvas/BoardCommandPanel.vue";
import BoardPagePicker from "../components/boards/BoardPagePicker.vue";
import BoardContactPicker from "../components/boards/pickers/BoardContactPicker.vue";
import BoardIssuePicker from "../components/boards/pickers/BoardIssuePicker.vue";
import BoardPickList from "../components/boards/pickers/BoardPickList.vue";
import SpaceCoverGallery from "../components/library/SpaceCoverGallery.vue";
import AppearanceIconBrowser from "../components/settings/AppearanceIconBrowser.vue";
import ContactDirectory from "../components/contacts/ContactDirectory.vue";
import ContactOverview from "../components/contacts/ContactOverview.vue";
import ContactPicker from "../components/contacts/ContactPicker.vue";
import { mountPage } from "../components/settings/general/testing";
import JobsPage from "../components/settings/jobs/JobsPage.vue";
import PageLocationPicker from "../components/PageLocationPicker.vue";
import PageSearch from "../components/PageSearch.vue";
import PageTags from "../components/PageTags.vue";
import PageTree from "../components/PageTree.vue";
import PersonalItems from "../components/personal/PersonalItems.vue";
import AccessEntryRow from "../components/settings/access/AccessEntryRow.vue";
import TrashList from "../components/settings/TrashList.vue";
import SearchableChoiceList from "../components/settings/SearchableChoiceList.vue";
import SearchableTags from "../components/settings/SearchableTags.vue";
import SearchField from "../components/settings/SearchField.vue";
import SettingsShell from "../components/settings/SettingsShell.vue";
import SidebarSpace from "../components/SidebarSpace.vue";
import SpacePageList from "../components/SpacePageList.vue";
import CatalogPicker from "../projector/CatalogPicker.vue";
import PartyInput from "../projector/PartyInput.vue";

/*
 * Area C of the interaction contract (docs/interaction-rules.md P3, P6, P7, P8, P13, P17): the pickers, searches and lists of the
 * application. Every one is an AppDescriptor with a `component` and an `open()` that mounts it with its API mocked and returns a
 * Session, the same few handles for all of them (type, key, rows, preselected row, what it took). The probes below ask the
 * same question of each; a pair that cannot be asked in jsdom is `unchecked`, and every break of a rule is a row of
 * deviations/app.json (a TODO of the alignment area). Two static checks keep the table honest: a file with a picker or search must
 * be a descriptor or say here why not, and a loading text must be a skeleton.
 */

// ---------------------------------------------------------------- scaffolding

const mounted: VueWrapper[] = [], cleanups: (() => void)[] = [];
/** The listeners a session put on the document go with the session. */
const onReset = (run: () => void) => cleanups.push(run);
function reset() {
  cleanups.splice(0).forEach(run => run());
  mounted.splice(0).forEach(wrapper => wrapper.unmount());
  resetBrowser();
}
afterEach(reset);
function mountIt(component: object, props: Record<string, unknown> = {}, options: Record<string, unknown> = {}) {
  const wrapper = mount(component, { attachTo: document.body, props, ...options }) as VueWrapper;
  mounted.push(wrapper);
  return wrapper;
}
/** A debounce is 120 to 260 ms: fake timers where an adapter types, real ones where a component schedules too much for them. */
const fake = () => vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
async function flush(ms = 0) {
  if (ms) { if (vi.isFakeTimers()) await vi.advanceTimersByTimeAsync(ms); else await new Promise(done => setTimeout(done, ms)); }
  await flushPromises();
  await nextTick();
}
const find = <T extends HTMLElement = HTMLElement>(selector: string) => document.querySelector<T>(selector);
const all = (selector: string) => () => [...document.querySelectorAll<HTMLElement>(selector)];
const words = (element: Element | null | undefined) => (element?.textContent ?? "").replace(/\s+/g, " ").trim();
function fill(field: HTMLInputElement, text: string) {
  field.value = text;
  field.dispatchEvent(new Event("input", { bubbles: true }));
}
/** A key as the browser handles it after the page did not: Esc clears a search field and makes the modal dialog around it fire `cancel` (jsdom does neither). */
function press(target: Element, key: string, init: KeyboardEventInit = {}) {
  const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...init });
  target.dispatchEvent(event);
  if (key === "Escape" && !event.defaultPrevented) {
    if (target instanceof HTMLInputElement && target.type === "search" && target.value) fill(target, "");
    target.closest("dialog[open]")?.dispatchEvent(new Event("cancel", { cancelable: true }));
  }
  return event;
}
/** Counts the Escapes that got past the element to the document, where the owner (a popover, a dialog) closes. */
function watchEsc() {
  const seen = { count: 0 };
  const count = (event: KeyboardEvent) => { if (event.key === "Escape") seen.count++; };
  document.addEventListener("keydown", count);
  onReset(() => document.removeEventListener("keydown", count));
  return seen;
}

/** What every picker or list hands the probes. `rows` are its results in order, `pre` the one Enter takes, `taken` what it took. */
interface Session {
  readonly field: HTMLInputElement;
  type(text: string): Promise<void>;
  /** An early session holds the answer of `type`; this lets it arrive. */
  arrive(): Promise<void>;
  key(key: string, init?: KeyboardEventInit): Promise<void>;
  rows(): HTMLElement[];
  pre(): HTMLElement | null;
  /** The row the keyboard is on: the focused one, else the preselected. */
  current(): HTMLElement | null;
  taken(): string[];
  value(): string;
  /** The surface closed itself (an overlay, a filter), as against an Esc that went on to the owner. */
  closed(): boolean;
}
interface Parts {
  field: HTMLInputElement;
  early?: boolean;
  rows: () => HTMLElement[];
  pre?: () => HTMLElement | null;
  taken: () => string[];
  type?: (text: string) => Promise<void>;
  arrive?: () => Promise<void>;
  closed?: () => boolean;
}
function session(parts: Parts): Session {
  const { field, rows } = parts;
  field.focus();
  const pre = parts.pre ?? (() => null);
  return {
    field, rows, pre, taken: parts.taken, value: () => field.value, closed: parts.closed ?? (() => false),
    type: parts.type ?? (async text => { fill(field, text); await flush(parts.early ? 0 : 300); }),
    arrive: parts.arrive ?? (() => flush(300)),
    current: () => rows().find(row => row.contains(document.activeElement)) ?? pre(),
    async key(key, init) {
      const at = document.activeElement;
      press(at && at !== document.body ? at : field, key, init);
      await flush();
    },
  };
}

interface AppDescriptor extends InteractionDescriptor {
  /** The .vue file, relative to src. */
  readonly component: string;
  /** Typed to get results: the first and second result contain these words; `early` descriptors can be asked with the answer held back. */
  readonly query?: string;
  readonly first?: string;
  readonly second?: string;
  readonly early?: boolean;
  /** The empty field lists suggestions with the first one preselected, and Enter takes it (the search palette's recent pages, F13). */
  readonly suggests?: boolean;
  readonly open?: (early: boolean) => Promise<Session>;
  /** Checks that are a scenario of the element, not a question of its Session. */
  readonly scenario?: Partial<Record<InteractionCheck, () => Promise<ProbeResult>>>;
}

// ---------------------------------------------------------------- test data

const names = [
  { value: "jana", label: "Jana Nováková", description: "jana@a.cz" },
  { value: "petr", label: "Petr Novák", description: "petr@a.cz" },
  { value: "eva", label: "Eva Svobodová", description: "eva@a.cz" },
];
const labelOf = (value: string) => names.find(option => option.value === value)?.label ?? value;
const stale = [{ value: "old", label: "Starý výsledek" }];
const match = (text: string) => names.filter(option => option.label.toLowerCase().includes(text.toLowerCase()));

const personHit = (key: string, name: string, more: Record<string, unknown> = {}, origin = "Adresář") => ({ key, contact: { name, ...more }, source: "directory", origin });
const people = [personHit("obj:1", "Honza Kovařík", { organization: "AB Klinika" }), personHit("obj:2", "Jan Novák", { emails: ["jan@a.cz"] }), personHit("obj:3", "Jan Novák", { emails: ["jan@b.cz"] }, "Stránka Kontakty")];

// ---------------------------------------------------------------- adapters: pickers

async function openContactPicker(early: boolean) {
  fake();
  const picked: string[] = [];
  mountIt(ContactPicker, { search: async () => people, pick: (contact: unknown) => picked.push(JSON.stringify(contact)), close: () => {}, normalize: normalizeContact });
  await flush();
  return session({ early, field: find<HTMLInputElement>("[data-contact-picker-query]")!, taken: () => picked,
    rows: all("[data-testid=contact-picker-list] [role=option]"), pre: () => find("[data-testid=contact-picker-list] [aria-selected=true]") });
}

/** Pickers fed by their owner's list: typing tells the owner, which answers with `options`; an early session answers late. */
function ownerFed(component: object, base: (early: boolean) => Record<string, unknown>, handlers: (set: (next: Record<string, unknown>) => void, picks: string[]) => Record<string, unknown>, ownerSearches: boolean, rows: string, pre: string) {
  return async (early: boolean): Promise<Session> => {
    const picks: string[] = [];
    let wrapper!: VueWrapper, typed = "";
    const set = (next: Record<string, unknown>) => wrapper.setProps(next);
    wrapper = mountIt(component, { label: "Osoba", options: early ? stale : names, query: "", loading: false, ...base(early), ...handlers(set, picks) });
    await flush();
    const input = find<HTMLInputElement>("input[type=search]")!;
    return session({ early, field: input, rows: all(rows), pre: () => find(pre), taken: () => picks.map(labelOf),
      async type(text) {
        typed = text;
        fill(input, text);
        await flush();
        if (early) await set({ loading: true }); else if (ownerSearches) await set({ options: match(text) });
      },
      async arrive() { await set({ options: match(typed), loading: false }); await flush(); } });
  };
}
const openChoiceList = ownerFed(SearchableChoiceList, early => ({ modelValue: null, remote: early }),
  (set, picks) => ({ "onUpdate:query": (query: string) => set({ query }), onPick: (value: string) => picks.push(value) }), false, "[role=radio]", "[data-first]");
const openBoardPick = ownerFed(BoardPickList, () => ({ picked: [] }),
  (set, picks) => ({ "onUpdate:query": (query: string) => set({ query }), onToggle: (option: { value: string }) => picks.push(option.value) }), true, ".pick-item", "[data-first]");

async function openSearchableTags() {
  const wrapper = mountIt(SearchableTags, { options: names, label: "Kdo", modelValue: [], "onUpdate:modelValue": (value: string[]) => wrapper.setProps({ modelValue: value }) });
  await flush();
  return session({ field: find<HTMLInputElement>("input")!, rows: all("[data-suggestion]"), pre: () => find("[data-first]"),
    taken: () => ((wrapper.props() as { modelValue: string[] }).modelValue).map(labelOf), type: async text => { fill(find<HTMLInputElement>("input")!, text); await flush(); } });
}

const parties = [{ id: "a", value: "Jan Novák (Modelsoft)", description: "Dva lidé tohoto jména" }, { id: "b", value: "Jan Novák (AB)", description: "Dva lidé tohoto jména" }];
async function openPartyInput(early: boolean) {
  fake();
  let typed = "";
  const wrapper = mountIt(PartyInput, { modelValue: "", suggest: async () => parties, "onUpdate:modelValue": (value: string) => wrapper.setProps({ modelValue: value }) });
  await flush();
  const field = find<HTMLInputElement>("input")!;
  return session({ early, field, rows: all("[data-testid=requirement-party-list] [role=option]"), pre: () => find("[data-testid=requirement-party-list] [aria-selected=true]"),
    taken: () => (typed && field.value !== typed ? [field.value] : []),
    async type(text) { typed = text; fill(field, text); await flush(early ? 0 : 300); } });
}

const definitions: CatalogDefinition[] = ["VPN přístup pro správu serveru", "VPN klient", "Objednávky otestovány"].map((title, index) =>
  ({ key: `portal.${index}`, title, titleMarkdown: title, direction: "inbound", requires: [], pageId: "cat", bundle: "Core" }));
async function openCatalog(early: boolean) {
  const submits: string[][] = [];
  const known = { definitions, modules: catalogModules(definitions), status: "ready" };
  const wrapper = mountIt(CatalogPicker, { ...(early ? { definitions: [], modules: [], status: "loading" } : known), selected: [], label: "Katalog", onSubmit: (_keep: boolean, keys: string[]) => submits.push(keys) });
  await flush();
  return session({ early, field: find<HTMLInputElement>("[data-catalog-query]")!, rows: all("[data-catalog-row]"), pre: () => find("[data-active]"),
    taken: () => submits.flat().map(key => definitions.find(definition => definition.key === key)?.title ?? key),
    type: async text => { fill(find<HTMLInputElement>("[data-catalog-query]")!, text); await flush(); },
    arrive: async () => { await wrapper.setProps(known); await flush(); } });
}

/** The tag editor of a page: the dialog's combobox fed by GET /tags?q, the page's tags empty. */
const tagCatalog = ["API", "Návody", "Nasazení", "Onboarding"];
const foldedTag = (text: string) => text.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase();
function tagRoutes() {
  vi.stubGlobal("fetch", routedFetch((path, init) => {
    // Tags save as they go: the page keeps what the last PUT sent, as the server does.
    if (path === "/api/pages/page-1/tags") return jsonResponse({ tags: init?.method === "PUT" ? (JSON.parse(String(init.body)).names as string[]).map(name => ({ id: `tag-${name}`, name })) : [] });
    if (path.startsWith("/api/tags?")) {
      const q = foldedTag(decodeURIComponent(path.split("q=")[1] ?? ""));
      return jsonResponse({ items: tagCatalog.filter(name => foldedTag(name).includes(q)).map(name => ({ id: `tag-${name}`, name })) });
    }
  }, sessionRoutes));
}
async function openTagEditor() {
  tagRoutes();
  fake();
  mountIt(PageTags, { pageId: "page-1", userId: "user-1", canEdit: true });
  await flush(10);
  const opener = find<HTMLButtonElement>(".edit-page-tags")!;
  opener.focus();
  opener.click();
  await flush(300);
  return opener;
}
async function openPageTags(early: boolean) {
  await openTagEditor();
  const dialog = find<HTMLDialogElement>("dialog")!;
  return session({ early, field: find<HTMLInputElement>("[role=combobox]")!, rows: all("[role=option]"), pre: () => find("[role=option][aria-selected=true]"),
    taken: () => [...document.querySelectorAll(".tag-draft")].map(words), closed: () => !dialog.hasAttribute("open") });
}

const places = [{ id: "a", parentId: null, title: "Návody", canEdit: true }, { id: "b", parentId: null, title: "Nasazení", canEdit: true }, { id: "c", parentId: null, title: "Provoz", canEdit: true }];
function mountLocationPicker() {
  const picked: string[] = [];
  mountIt(PageLocationPicker, { modelValue: "", items: places, label: "Umístění", "onUpdate:modelValue": (id: string) => picked.push(places.find(place => place.id === id)?.title ?? id) });
  return session({ field: find<HTMLInputElement>("input")!, rows: all(".location-row:not(.location-root)"), pre: () => find("[data-first]"), taken: () => picked });
}

// ---------------------------------------------------------------- adapters: lists and searches

const summary = (id: string, name: string): ObjectSummary =>
  ({ id, typeId: "atlas.contact", scope: "wiki", spaceId: null, title: name, version: 1, updatedAt: "2026-10-26T10:00:00Z", data: { name, emails: [] } });
const contacts = [summary("c1", "Jana Nováková"), summary("c2", "Petr Novák"), summary("c3", "Eva Svobodová")];
const matching = (text: string | undefined) => contacts.filter(item => item.title.toLowerCase().includes((text ?? "").toLowerCase()));
function mountDirectory(uses = 0) {
  const source = {
    list: vi.fn(async (query: { q?: string }) => ({ items: matching(query.q), total: matching(query.q).length, page: 1, pageSize: 50, canCreate: true })),
    create: vi.fn(), trash: vi.fn(async () => {}), trashList: vi.fn(async () => ({ items: [], total: 0, page: 1, pageSize: 50 })),
    restore: vi.fn(), purge: vi.fn(),
  };
  const store = createLiveObjects({ get: vi.fn(async (id: string) => ({ ...contacts.find(item => item.id === id)!, canEdit: true }) as unknown as ObjectDto), change: vi.fn() });
  mountIt(ContactDirectory, { source, store, usage: { count: vi.fn(async (id: string) => ({ objectId: id, total: uses, visible: uses, hidden: 0 })) } });
  return source;
}
async function openDirectory() {
  fake();
  mountDirectory();
  await flush(300);
  return session({ field: find<HTMLInputElement>("[data-contact-search]")!, rows: all("[data-contact-row]"), pre: () => find("[data-contact-row][data-first]"), taken: () => [...document.querySelectorAll("[data-contact-row] .is-editing")].map(words) });
}

const entity = (name: string): EntityItem => ({ identityKey: `email:${name}`, kind: "contact", attributes: { name, emails: [] },
  sources: [{ resourceId: `p-${name}`, resourceType: "atlas.page", title: `Stránka ${name}`, spaceId: "s", spaceName: "Provoz", anchor: null, revisionKind: "published" }], sourceCount: 1 });
async function openOverview() {
  fake();
  const clicked: string[] = [];
  const everyone = contacts.map(item => entity(item.title));
  const source = { exportUrl: entityApi.exportUrl, list: async (_kind: string, query: { q?: string }) => {
    const items = everyone.filter(item => (item.attributes.name ?? "").toLowerCase().includes((query.q ?? "").toLowerCase()));
    return { items, total: items.length, page: 1, pageSize: 50 };
  } };
  mountIt(ContactOverview, { source });
  await flush(300);
  const open = (event: Event) => { event.preventDefault(); clicked.push(words((event.target as Element).closest("[data-entity-row]"))); };
  document.addEventListener("click", open, true);
  onReset(() => document.removeEventListener("click", open, true));
  return session({ field: find<HTMLInputElement>("input[data-entity-search]")!, rows: all("[data-entity-row]"), pre: () => find("[data-entity-row][data-first]"), taken: () => clicked });
}

/** Úlohy na pozadí: the filter of the list; Enter opens the first job, which is where the address goes, so that is what it took. */
const jobTitles: Record<string, string> = { "backups.git": "Záloha do Gitu", "backups.zip": "Záloha ZIP", retention: "Údržba provozních logů" };
async function openJobs() {
  const jobs: Job[] = Object.entries(jobTitles).map(([id, title]) => ({ id, title, description: `Popis: ${title}`, module: "platform", schedule: "Po události nebo ručně", retry: "Bez opakování",
    known: true, paused: false, nextDueAt: null, completedAt: null, lastRun: null, recurring: true, lastSuccessAt: null, typicalSeconds: null, overdue: false, stuck: false }));
  const unused = async () => { throw new Error("not used by the list"); };
  const api: JobApi = { list: async () => ({ jobs, systemServices: [], summary: { running: 0, queued: 0, failedDay: 0, failedWeek: 0, days: [], failures: [] } }), runs: unused, run: unused, runNow: unused, retry: unused, cancel: unused, setPaused: unused };
  mounted.push(mountPage(JobsPage, { api }, { hash: "#/settings/general/jobs" }).wrapper as unknown as VueWrapper);
  await flush();
  const opened = () => decodeURIComponent(location.hash.split("/general/jobs/")[1] ?? "");
  return session({ field: find<HTMLInputElement>("input[data-job-filter]")!, rows: all("[data-job-row]"), pre: () => find("[data-job-row][data-first]"), taken: () => (opened() ? [jobTitles[opened()] ?? opened()] : []) });
}

const note = (id: string, title: string): PersonalItem => ({ id, typeId: "atlas.note", title, updatedAt: "2026-10-29T09:00:00Z", version: 1, canEdit: false, shares: 0, ownerName: null, level: null, expiresAt: null, excerpt: "" });
const notes = [note("n1", "Porada o rozpočtu"), note("n2", "Porada týmu"), note("n3", "Nákup")];
function mountPersonal() {
  const source = { mine: vi.fn(async () => notes), shared: vi.fn(async () => notes), note: vi.fn(async (id: string) => ({ version: 1, canEdit: false, data: { title: id, text: "" } })),
    createNote: vi.fn(), changeNote: vi.fn(), trash: vi.fn(async () => {}), trashList: vi.fn(async () => ({ items: [], total: 0, page: 1, pageSize: 50 })), restore: vi.fn(), purge: vi.fn() };
  mountIt(PersonalItems, { mode: "mine", source });
  return source;
}
async function openPersonal() {
  const source = mountPersonal();
  await flush();
  return session({ field: find<HTMLInputElement>("input[data-personal-search]")!, rows: all("[data-personal-row]"), pre: () => find("[data-personal-row][data-first]"),
    taken: () => source.note.mock.calls.map(([id]) => notes.find(item => item.id === id)!.title) });
}

// „Archiv porad“ is the first row of the unfiltered list, so an Enter that opens the previous answer's first row is told from the right one.
const spaceObjects: MemoryObject[] = [
  { id: "44444444-4444-4444-8444-444444444444", typeId: "atlas.page", title: "Archiv porad", spaceId: "s", spaceName: "Provoz", readable: true },
  { id: "11111111-1111-4111-8111-111111111111", typeId: "atlas.page", title: "Provozní příručka", spaceId: "s", spaceName: "Provoz", readable: true },
  { id: "22222222-2222-4222-8222-222222222222", typeId: "atlas.board", title: "Plán vydání", spaceId: "s", spaceName: "Provoz", readable: true },
  { id: "33333333-3333-4333-8333-333333333333", typeId: "atlas.page", title: "Plán školení", spaceId: "s", spaceName: "Provoz", readable: true },
];
async function openSpaceList(early: boolean) {
  const inner = memoryRelations(spaceObjects, []);
  const held: (() => void)[] = [];
  let hold = false;
  const relations = { ...inner, contents: (id: string, query: Parameters<typeof inner.contents>[1], signal?: AbortSignal) =>
    hold ? new Promise<Awaited<ReturnType<typeof inner.contents>>>((resolve, reject) => held.push(() => inner.contents(id, query, signal).then(resolve, reject))) : inner.contents(id, query, signal) };
  const wrapper = mountIt(SpacePageList, { pages: [], canWrite: false, spaceId: "s", relations });
  await flush();
  return session({ field: find<HTMLInputElement>("[data-content-search]")!, rows: all(".content-row"), pre: () => find(".content-row.is-active"),
    taken: () => ((wrapper.emitted("open") ?? []) as string[][]).map(([id]) => spaceObjects.find(item => item.id === id)?.title ?? String(id)),
    async type(text) { hold = early; fill(find<HTMLInputElement>("[data-content-search]")!, text); await flush(early ? 0 : 260); },
    async arrive() { await flush(260); held.splice(0).forEach(release => release()); await flush(); } });
}

const sections = [{ id: "a", items: [{ path: "people", label: "Lidé", icon: "users" }, { path: "groups", label: "Skupiny osob", icon: "users" },
  { path: "access", label: "Skupiny přístupů", icon: "users" }, { path: "ai", label: "AI", icon: "ai" }] }];
async function openSettingsShell() {
  const rail = document.createElement("div");
  rail.id = "atlas-settings-rail";
  document.body.append(rail);
  const opened: string[] = [];
  rail.addEventListener("click", event => { event.preventDefault(); opened.push(words((event.target as Element).closest("a"))); });
  mountIt(SettingsShell, { title: "Správa wiki", groups: sections, navLabel: "Správa wiki", currentPath: "people" }, { slots: { default: "<p>obsah</p>" } });
  await flush();
  return session({ field: rail.querySelector<HTMLInputElement>("input[type=search]")!, rows: () => [...rail.querySelectorAll<HTMLElement>("nav a[href]")], pre: () => rail.querySelector<HTMLElement>("nav a[data-first]"), taken: () => opened });
}

const treePages: TreePage[] = ["Návody", "Nasazení", "Provoz"].map((title, index) => ({ id: `p${index}`, parentId: null, spaceId: "s", title, version: 1, isPublished: true, sortOrder: index, canEdit: true }));
const space = { id: "s", name: "Provoz", icon: "book", color: "blue", role: "Admin" as const, pinPolicy: "None" as const, pinned: true, pageCount: 3, lastChangedAt: null };
async function openTreeFilter() {
  const wrapper = mountIt(SidebarSpace, { space, pages: treePages, expanded: true, canWrite: true });
  await flush();
  const row = find(".tree-page")!;
  row.focus();
  wrapper.get<HTMLButtonElement>("[data-sidebar-space-find]").element.click();
  await flush();
  const filter = () => find("[data-sidebar-tree-filter]");
  return { row, wrapper, session: session({ field: find<HTMLInputElement>("[data-sidebar-tree-filter] input")!,
    rows: () => [...document.querySelectorAll<HTMLElement>("[data-tree-row]")].filter(item => !item.closest("li")?.classList.contains("tree-filtered-out")),
    pre: () => find(".tree-filter-selected"), closed: () => !filter(),
    taken: () => ((wrapper.emitted("select") ?? []) as TreePage[][]).map(([page]) => page.title) }) };
}

async function openSearchField() {
  const wrapper = mountIt(SearchField, { label: "Hledat", modelValue: "", "onUpdate:modelValue": (value: string) => wrapper.setProps({ modelValue: value }) });
  await flush();
  return session({ field: find<HTMLInputElement>("input")!, rows: () => [], taken: () => [] });
}

/** The board's page picker: the editor's `[[` page search over GET /search (the current space and everywhere), the editor's list in the board's popover. */
const boardPages = [{ id: "bp1", title: "Plán vydání", spaceId: "s", spaceName: "Provoz" }, { id: "bp2", title: "Plán školení", spaceId: "s", spaceName: "Provoz" }, { id: "bp3", title: "Provozní příručka", spaceId: "s", spaceName: "Provoz" }];
async function openBoardPagePicker(early: boolean) {
  fake();
  vi.stubGlobal("fetch", routedFetch(path => {
    if (!path.startsWith("/api/search?")) return;
    const q = foldedTag(new URL(path, "http://board").searchParams.get("q") ?? "");
    return jsonResponse({ items: boardPages.filter(page => foldedTag(page.title).includes(q)), hasMore: false });
  }));
  const wrapper = mountIt(BoardPagePicker, { open: true, spaceId: "s" });
  await flush();
  return session({ early, field: find<HTMLInputElement>("[data-board-page-query]")!, rows: all("[data-testid=board-page-list] [role=option]"), pre: () => find("[data-testid=board-page-list] [aria-selected=true]"),
    taken: () => ((wrapper.emitted("pick") ?? []) as { title: string }[][]).map(([page]) => page!.title), closed: () => !!wrapper.emitted("cancel") });
}

/** The board's command panel (`/`, Ctrl+K): the editor's list over the board's commands, no network. */
const boardCommands: BoardCommand[] = [
  { id: "create:note", title: "Nová poznámka", icon: "pin", keywords: [], section: "create" },
  { id: "create:frame", title: "Nový rám", icon: "layout-dashboard", keywords: [], section: "create" },
  { id: "create:contact", title: "Nový kontakt", icon: "user", keywords: [], section: "create" },
];
const commandPanelProps = { commands: boardCommands, at: { x: 40, y: 40 }, room: { width: 800, height: 600 } };
async function openCommandPanel() {
  const ran: string[] = [];
  let closes = 0;
  mountIt(BoardCommandPanel, { ...commandPanelProps, onRun: (id: string) => ran.push(id), onClose: () => closes++ });
  await flush();
  return session({ field: find<HTMLInputElement>("[data-board-command-query]")!, rows: all("[data-testid=board-command-list] [role=option]"), pre: () => find("[data-testid=board-command-list] [aria-selected=true]"),
    taken: () => ran.map(id => boardCommands.find(command => command.id === id)?.title ?? id), closed: () => closes > 0,
    type: async text => { fill(find<HTMLInputElement>("[data-board-command-query]")!, text); await flush(); } });
}

/** The space cover gallery: a search over the drawings' names and tags, Enter chooses the first drawing as a click would. */
const coverProps = { space: { id: "s", icon: "book", color: "blue" }, cover: { source: "Auto" as const, catalogKey: null } };
async function openCoverGallery() {
  const wrapper = mountIt(SpaceCoverGallery, coverProps);
  await flush();
  return session({ field: find<HTMLInputElement>("[data-space-cover-search]")!, rows: all("[data-space-cover-options] [role=radio]"), pre: () => find("[data-space-cover-options] [data-first]"),
    taken: () => ((wrapper.emitted("choose") ?? []) as [SpaceCoverChoice, boolean][]).flatMap(([choice]) => (choice.catalogKey ? [spaceCoverName(choice.catalogKey) ?? choice.catalogKey] : [])),
    type: async text => { fill(find<HTMLInputElement>("[data-space-cover-search]")!, text); await flush(); } });
}

/** The appearance picker's icon browser (the 87 icons and the pack): mounted once the pack catalog has loaded, as AppearancePicker does. */
const iconLabel = (id: string) => (appearanceIcons.find(item => item.id === id) ?? iconPackCatalog.value?.icons.find(item => item.id === id))?.label ?? id;
async function mountIconBrowser(chosen: string[] = []) {
  await Promise.all([loadIconPackCatalog(), loadIconPackMarkup()]);
  const wrapper = mountIt(AppearanceIconBrowser, { modelValue: "server", color: "teal-2", "onUpdate:modelValue": (value: string) => { chosen.push(value); void wrapper.setProps({ modelValue: value }); } });
  await flush();
  return wrapper;
}
async function openIconBrowser() {
  const chosen: string[] = [];
  await mountIconBrowser(chosen);
  return session({ field: find<HTMLInputElement>("[data-appearance-search]")!, rows: all("[data-appearance-results] [role=option]"), pre: () => find("[data-appearance-results] [data-first]"),
    taken: () => chosen.map(iconLabel), type: async text => { fill(find<HTMLInputElement>("[data-appearance-search]")!, text); await flush(); } });
}

// The page editor is not what is measured here; the shell mounts it as a stand-in, as AppLibrary.test.ts does.
vi.mock("../components/DocumentEditor.vue", () => ({ __esModule: true, default: defineComponent({
  props: ["value", "readonly"], emits: ["ready", "change", "collaborationStatus"],
  setup(props, { emit, expose }) {
    expose({ focus: () => true, capture: () => ({ contentJson: JSON.stringify(props.value), markdown: "", collaborationGeneration: "generation-1" }), navigateToHeading: () => {}, selectionActions: () => [] });
    onMounted(() => { emit("ready"); emit("collaborationStatus", { enabled: true, canSave: true }); });
    watch(() => props.readonly, () => emit("collaborationStatus", { enabled: true, canSave: true }));
    return () => h("div", { class: "edit-content" });
  },
}) }));
const shellPage = (id: string, title: string) => ({ id, spaceId: "space-1", parentId: null, title, version: 1, isPublished: true, publishedVersion: 1,
  contentJson: JSON.stringify(emptyDocument()), activeFrom: "2026-01-01T08:00:00Z", canEdit: true });
const shellPages = [shellPage("page-1", "První článek"), shellPage("page-2", "Plán vydání"), shellPage("page-3", "Plán školení"), shellPage("page-9", "Starý výsledek"), shellPage("page-8", "Oblíbená stránka")];
const searchHits = (...pages: ReturnType<typeof shellPage>[]) => jsonResponse({ items: pages.map(page => ({ ...page, spaceName: "Engineering", snippet: "" })), spaces: [], hasMore: false });
interface Shell { hold: boolean; held: ((response: Response) => void)[]; requests: string[] }
/** What the shell asks for at the first page: the account, a space with its tree, the page, the search (the answer to „plan“ can be held) and one favourite. */
const shellRoutes = (shell: Shell): Route => (path, init) => {
  shell.requests.push(`${init?.method ?? "GET"} ${path}`);
  if (path === "/api/auth/me") return jsonResponse({ id: "user-1", username: "writer", displayName: "Writer", isWikiAdmin: false, appearance: { layout: "connected", accent: "blue", mode: "system" } });
  if (path === "/api/spaces") return jsonResponse([{ id: "space-1", name: "Engineering", role: "Writer", icon: "book", color: "blue" }]);
  if (path === "/api/spaces/space-1/tree") return jsonResponse({ spaceId: "space-1", version: 1, items: shellPages });
  const renamed = path.match(/^\/api\/pages\/([\w-]+)\/title$/), page = path.match(/^\/api\/pages\/([\w-]+)$/);
  if (renamed && init?.method === "PUT") return jsonResponse({ ...shellPages.find(item => item.id === renamed[1]), title: JSON.parse(String(init.body)).title });
  if (page && !init?.method) return jsonResponse(shellPages.find(item => item.id === page[1]));
  if (path.startsWith("/api/search?")) {
    if (new URL(path, "http://shell").searchParams.get("q") !== "plan") return searchHits(shellPages[3]!);
    return shell.hold ? new Promise<Response>(resolve => shell.held.push(resolve)) : searchHits(shellPages[1]!, shellPages[2]!);
  }
  if (path.startsWith("/api/integrations/youtrack/search?")) return jsonResponse([]);
  if (path.startsWith("/api/tags?")) return jsonResponse({ items: [] });
  if (path.startsWith("/api/me/library/")) return jsonResponse({ items: [{ ...shellPages[4], spaceName: "Engineering", isFavorite: true, visitedAt: null, publishedAt: null }], hasMore: false, nextOffset: null });
};
const settle = async () => { await flushPromises(); await vi.dynamicImportSettled(); await flushPromises(); };
async function openShell() {
  vi.stubGlobal("localStorage", memoryStorage().storage);
  fake();
  const shell: Shell = { hold: false, held: [], requests: [] };
  mounted.push(mountShell({ url: "/#/pages/page-1", fetch: routedFetch(shellRoutes(shell), sessionRoutes, quietRoutes) }));
  await settle();
  return shell;
}
/** The palette opened from its button; „early“ types over a shown answer, so the answer to the typed text is still on its way. */
async function openSearch(early: boolean) {
  const shell = await openShell();
  const launch = find<HTMLButtonElement>(".search-launch")!;
  launch.focus();
  launch.click();
  await flush(50);
  const dialog = find<HTMLDialogElement>(".search-dialog")!, field = find<HTMLInputElement>(".omnisearch-input input")!, before = location.hash;
  const opened = () => (location.hash === before ? [] : [shellPages.find(page => location.hash.endsWith(page.id))?.title ?? location.hash]);
  const search = session({ early, field, rows: all("#search-results [role=option]"), pre: () => find('#search-results [role=option][aria-selected="true"]'), taken: opened, closed: () => !dialog.open,
    async type(text) {
      if (early) { fill(field, "zzz"); await flush(250); shell.hold = true; }
      fill(field, text);
      await flush(early ? 100 : 250);
    },
    async arrive() {
      shell.hold = false;
      shell.held.splice(0).forEach(answer => answer(searchHits(shellPages[1]!, shellPages[2]!)));
      await flush(250);
    } });
  return { search, launch, shell };
}

const treeStubs = { global: { stubs: { AtlasIcon: true, PageIcon: true, PageStatusBadge: true } } };
async function openTree() {
  const wrapper = mountIt(PageTree, { pages: treePages, menu: true }, treeStubs);
  await flush();
  return { wrapper, row: () => find<HTMLElement>('.tree-page[data-tree-item="p0"]')!, input: () => find<HTMLInputElement>(".tree-rename") };
}
const afterFrame = () => new Promise<void>(done => requestAnimationFrame(() => done()));
/** The tree with its first row being renamed (F2), the field holding `text`. */
async function renaming(text?: string) {
  const tree = await openTree();
  tree.row().focus();
  press(tree.row(), "F2");
  await flush();
  const field = tree.input();
  if (field && text !== undefined) { field.focus(); fill(field, text); }
  return { ...tree, field };
}
const renamedTo = (wrapper: VueWrapper) => ((wrapper.emitted("rename") ?? []) as [TreePage, string][]).map(([, title]) => title);

// ---------------------------------------------------------------- scenarios

/** Opens the trash view of a list and leaves it with Esc: the keyboard must be on the button that opened it. */
async function trashReturn(open: () => Promise<unknown>, opener: string, section: string): Promise<ProbeResult> {
  await open();
  const button = find<HTMLButtonElement>(opener)!;
  button.focus();
  button.click();
  await flush(300);
  press(find(section)!, "Escape");
  await flush();
  // The toolbar is rendered anew, so the button that opened the trash is a new element.
  return document.activeElement === find(opener) ? true : `after Esc the keyboard is on ${document.activeElement?.tagName.toLowerCase()}, not on the button that opened the trash`;
}
const acted = (spy: { mock: { calls: unknown[][] } }) => spy.mock.calls.some(([, options]) => (options as { action?: { label?: string } } | undefined)?.action?.label === "Zpět");

/** A row of a list answers the five gestures of „Co s tím jde dělat“ (P2) with the same menu, which offers what the row's buttons do and returns the keyboard to the row. */
async function rowMenu(row: HTMLElement, expected: string[]): Promise<ProbeResult> {
  await loadActionsMenu();
  const gestures: [string, () => void][] = [
    ["Ctrl+mezerník", () => press(row, " ", { ctrlKey: true, code: "Space" })],
    ["Alt+Enter", () => press(row, "Enter", { altKey: true })],
    ["Ctrl+.", () => press(row, ".", { ctrlKey: true })],
    ["Shift+F10", () => press(row, "F10", { shiftKey: true })],
    ["pravé tlačítko", () => { row.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: 40, clientY: 40 })); }],
  ];
  for (const [name, gesture] of gestures) {
    row.focus();
    gesture();
    await flush(50);
    // The page editor's own menu; a row's label without its shortcut hint (Delete, F2: RowActionsMenu runs those keys on the row).
    const menu = find("[data-testid=intentions]");
    if (!menu) return `${name} opens no menu on the row`;
    if (!menu.querySelector("[role=option][aria-selected=true]")) return `${name} opens the menu with no row preselected`;
    const labels = [...menu.querySelectorAll("[role=option] .t")].map(item => words(item));
    if (labels.join("|") !== expected.join("|")) return `${name} offers ${shown(labels)}, not ${shown(expected)}`;
    press(menu, "Escape");
    await flush(50);
    if (find("[data-testid=intentions]")) return "Esc left the menu open";
    if (document.activeElement !== row) return `after Esc the keyboard is on ${document.activeElement?.tagName.toLowerCase()}, not on the row`;
  }
  return true;
}
const trashed = [{ id: "t1", title: "Stará stránka" }];
const accessGrant = { via: "direct" as const, label: "", level: "read" as const, version: "v-read", startAt: null, expiresAt: null, effective: true };

const scenarios: Record<string, AppDescriptor["scenario"]> = {
  trash: { async "P2.menu"() {
    mountIt(TrashList, { label: "Koš", items: trashed, icon: () => "file-text", detail: () => "Smazala Jana", restore: async () => {}, purge: async () => {}, loaded: true });
    await flush();
    return rowMenu(find<HTMLElement>("[data-trash-restore]")!, ["Obnovit", "Smazat trvale"]);
  } },
  access: { async "P2.menu"() {
    mountIt(AccessEntryRow, { row: { key: "space:s", kind: "space", resourceId: "s", title: "Projekt", level: "read", sources: [accessGrant] } });
    await flush();
    return rowMenu(find<HTMLElement>("[data-change-access]")!, ["Změnit", "Odebrat"]);
  } },
  search: {
    async "P7.return"() {
      const { search, launch } = await openSearch(false);
      await search.key("Escape");
      await flush(50);
      return document.activeElement === launch ? true : `closing the palette left the keyboard on ${document.activeElement?.tagName.toLowerCase()}, not on the button that opened it`;
    },
    async "P17.cached"() {
      const { search, launch } = await openSearch(false);
      await flush(50);
      if (!find("#suggested-result-0")) return "the first opening shows no recent page even after its answer";
      await search.key("Escape");
      await flush(50);
      launch.click();
      await nextTick();
      await nextTick();
      return find("#suggested-result-0") ? true : "reopened, the palette lists no recent page until the request answers (it clears them and shows „Načítání…“)";
    },
  },
  tree: {
    async "P1.start"() {
      const { wrapper, row, input } = await openTree();
      row().click();
      await flush();
      return input() ? true : `a click on the row ${wrapper.emitted("select") ? "opens the page" : "does nothing"}: rename starts only with F2 or the row menu`;
    },
    async "P1.enter"() {
      const { field } = await renaming();
      return field?.value === "Návody" ? true : "F2 on the row did not open the rename field with the title";
    },
    async "P3.esc"() {
      const { wrapper, row, input } = await renaming("Jiný název");
      press(input()!, "Escape");
      await flush();
      await afterFrame();
      if (renamedTo(wrapper).length) return "Esc saved the draft";
      if (input()) return "Esc left the field open";
      return document.activeElement === row() ? true : "after Esc the keyboard is not on the row";
    },
    async "P4.blur"() {
      const { wrapper, input } = await renaming("Jiný název");
      input()!.blur();
      await flush();
      return renamedTo(wrapper).join() === "Jiný název" ? true : `leaving the field sent ${shown(renamedTo(wrapper))}, not the new title`;
    },
    async "P7.focus"() {
      const { wrapper, row, input } = await renaming("Jiný název");
      press(input()!, "Enter");
      await flush();
      await afterFrame();
      if (renamedTo(wrapper).join() !== "Jiný název") return "Enter did not send the new title";
      return document.activeElement === row() ? true : `after Enter the keyboard is on ${document.activeElement?.tagName.toLowerCase()}, not on the renamed row`;
    },
    async "P6.toast"() {
      const success = vi.spyOn(toast, "success");
      const shell = await openShell();
      const row = find<HTMLElement>('.tree-page[data-tree-item="page-1"]')!;
      row.focus();
      press(row, "F2");
      await flush();
      const input = find<HTMLInputElement>(".tree-rename")!;
      input.focus();
      fill(input, "Nový název");
      press(input, "Enter");
      await settle();
      await flush(300);
      if (!shell.requests.includes("PUT /api/pages/page-1/title")) return "the rename was not sent";
      return acted(success) ? true : "the rename is announced by the old banner (notify) only: no toast with „Zpět“";
    },
  },
  contactPicker: {
    async "P17.cached"() {
      fake();
      const search = Object.assign(async () => people, { cached: (text: string) => rankPeople(text, people as PersonHit[]) });
      mountIt(ContactPicker, { search, pick: () => {}, close: () => {}, normalize: normalizeContact, typeAhead: { take: () => ({ text: "novak", enter: false }) } });
      await flush();
      const rows = [...document.querySelectorAll("[data-testid=contact-picker-list] [role=option]")].map(row => words(row));
      return rows.filter(row => row.includes("Jan Novák")).length === 2 ? true : `the first render lists ${shown(rows)}, not the two known people of the typed text`;
    },
    async "P7.return"() {
      const picker = await openContactPicker(false);
      await picker.type("novak");
      await picker.key("End");
      await picker.key("Enter");
      const name = find<HTMLInputElement>("[data-contact-name]");
      if (!name) return "Enter on „Nový kontakt“ did not open the form";
      press(name, "Escape");
      await flush();
      return document.activeElement === picker.field ? true : "Esc in the new-contact form did not return the keyboard to the search";
    },
  },
  directory: {
    "P7.return": () => trashReturn(openDirectory, "[data-contact-trash-open]", ".contact-trash"),
    async "P2.menu"() {
      mountDirectory(0);
      await flush(300);
      return rowMenu(find<HTMLElement>("[data-contact-trash]")!, ["Upravit", "Smazat"]);
    },
    async "P6.toast"() {
      const success = vi.spyOn(toast, "success");
      const source = mountDirectory(0);
      await flush(300);
      find<HTMLButtonElement>("[data-contact-trash]")!.click();
      await flush(300);
      if (!source.trash.mock.calls.length) return "the contact was not moved to the trash";
      return acted(success) ? true : "an unused contact goes to the trash at once and the only trace is the vanished row: no toast with „Zpět“";
    },
  },
  personal: {
    "P7.return": () => trashReturn(openPersonal, "[data-personal-trash-open]", ".personal-trash"),
    async "P6.toast"() {
      const success = vi.spyOn(toast, "success");
      const source = mountPersonal();
      await flush();
      find<HTMLButtonElement>("[data-personal-trash]")!.click();
      await flush(300);
      if (!source.trash.mock.calls.length) return "the note was not moved to the trash";
      return acted(success) ? true : "a note goes to the trash and the only trace is the vanished row: no toast with „Zpět“";
    },
    async "P2.menu"() {
      mountPersonal();
      await flush();
      return rowMenu(find<HTMLElement>("[data-personal-share]")!, ["Sdílet", "Smazat"]);
    },
  },
  tags: {
    async "P7.return"() {
      const opener = await openTagEditor();
      press(find("[role=combobox]")!, "Escape");
      await flush(300);
      return document.activeElement === opener ? true : `closing the tag dialog left the keyboard on ${document.activeElement?.tagName.toLowerCase()}, not on „Upravit tagy“`;
    },
    async "P17.cached"() {
      await openTagEditor();
      await flush(300);
      if (!document.querySelector("[role=option]")) return "the first opening shows no tag even after the answer";
      press(find("[role=combobox]")!, "Escape");
      await flush(300);
      find<HTMLButtonElement>(".edit-page-tags")!.click();
      await flush();
      return document.querySelectorAll("[role=option]").length ? true : "reopened, the dialog lists no tag until the 150 ms timer and the request have run";
    },
  },
};

// ---------------------------------------------------------------- the descriptor table

/** A picker given its list shows the rows in its first render: nothing waits for a timer or a request. */
const rowsAtOnce = (component: object, props: Record<string, unknown>, rows: string) => async (): Promise<ProbeResult> => {
  mountIt(component, props);
  return document.querySelectorAll(rows).length ? true : "the first render has no rows";
};
/** A board picker (issues, contacts) opened again after adding something lists what was added in its first render, before any search. */
const pickerReopens = (component: object, props: Record<string, unknown>, confirm: string) => async (): Promise<ProbeResult> => {
  fake();
  const wrapper = mountIt(component, { open: true, ...props });
  await flush();
  fill(find<HTMLInputElement>("input[type=search]")!, "nov");
  await flush(300);
  const box = find<HTMLInputElement>(".pick-item input[type=checkbox]");
  if (!box) return "the search lists nothing to pick";
  box.click();
  await flush();
  find<HTMLButtonElement>(confirm)!.click();
  await flush();
  if (!wrapper.emitted("pick")) return "the pick was not confirmed";
  await wrapper.setProps({ open: false });
  await wrapper.setProps({ open: true });
  return document.querySelectorAll(".pick-item").length ? true : "opened again, the picker lists nothing until something is typed and searched";
};
const contactSummary: ObjectSummary = { id: "c-jana", typeId: "atlas.contact", scope: "wiki", spaceId: null, title: "Jana Nováková", version: 1, updatedAt: "2026-10-26T10:00:00Z", data: { name: "Jana Nováková" } };
const boardPickersReopen = async (): Promise<ProbeResult> => {
  const issues = await pickerReopens(BoardIssuePicker, { spaceId: "s", fetchIssues: async () => [{ id: "1", key: "ATL-1", title: "Nová verze", url: "https://yt.example/issue/ATL-1" }] }, "[data-board-issue-confirm]")();
  if (issues !== true) return `BoardIssuePicker: ${issues}`;
  reset();
  const contacts = await pickerReopens(BoardContactPicker, { search: async () => [], directory: { list: async () => ({ items: [contactSummary], total: 1, page: 1, pageSize: 50, canCreate: true }) } }, "[data-board-contact-confirm]")();
  return contacts === true ? true : `BoardContactPicker: ${contacts}`;
};
/** Hledat ve stránce: the results are the matches in a rendered page; Enter "takes" a result by jumping to it, which asks the match's range for its place. */
async function openPageSearch() {
  pageSearchQuery.value = "";
  const jumped: string[] = [];
  // jsdom has no layout: a range answers with a zero box, and the jump (a frame later) is seen through that question.
  vi.stubGlobal("requestAnimationFrame", (run: FrameRequestCallback) => { run(0); return 0; });
  (Range.prototype as { getBoundingClientRect: () => DOMRect }).getBoundingClientRect = function (this: Range) {
    jumped.push(words(this.startContainer.parentElement));
    return new DOMRect();
  };
  window.scrollBy = () => {};
  onReset(() => { delete (Range.prototype as { getBoundingClientRect?: unknown }).getBoundingClientRect; });
  const root = document.createElement("article");
  root.innerHTML = `<div class="ProseMirror">${names.map(option => `<p>${option.label}</p>`).join("")}</div>`;
  document.body.append(root);
  mountIt(PageSearch, { root });
  await flush();
  return session({ field: find<HTMLInputElement>("[data-page-search-input]")!, rows: all(".page-search-result"), pre: () => find(".page-search-result[aria-selected=true]"), taken: () => jumped });
}
const descriptors: AppDescriptor[] = [
  { id: "SearchField", label: "Filtr seznamu v nastavení (SearchField)", component: "components/settings/SearchField.vue", saves: "none", query: "pet", open: openSearchField,
    touch: "Pole a tlačítko Vymazat mají cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny); Esc na dotyku nemá obdobu, maže tlačítko. Ověřit při 375 px.", checks: ["P3.search-esc"] },
  { id: "ContactPicker", label: "Kontakt v textu (ContactPicker)", component: "components/contacts/ContactPicker.vue", saves: "none", query: "novak", first: "jan@a.cz", second: "jan@b.cz", early: true, open: openContactPicker,
    touch: "Pole v popoveru a seznam 8 řádků, každý řádek cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny); tap na řádek osobu vloží. Ověřit při 375 px, klávesnice nezakryje seznam.", checks: ["P8.first", "P8.enter", "P8.arrows", "P8.tab", "P3.search-esc", "P7.return", "P17.cached"],
    scenario: scenarios.contactPicker },
  { id: "BoardPickList", label: "Výběr na tabuli (BoardPickList)", component: "components/boards/pickers/BoardPickList.vue", saves: "apply", query: "nov", first: "Jana Nováková", second: "Petr Novák", early: true, open: openBoardPick,
    touch: "Řádky s políčkem v dialogu jsou cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny), vybrané zůstávají jako štítky pod seznamem. Ověřit při 375 px.", checks: ["P8.first", "P8.enter", "P8.arrows", "P3.search-esc", "P17.cached"],
    scenario: { "P17.cached": boardPickersReopen } },
  { id: "SearchableChoiceList", label: "Výběr z dlouhého seznamu (SearchableChoiceList)", component: "components/settings/SearchableChoiceList.vue", saves: "live", query: "nov", first: "Jana Nováková", second: "Petr Novák", early: true, open: openChoiceList,
    touch: "Dlaždice 52 px v radiogroup pod polem 46 px; tap vybere. Ověřit při 375 px.", checks: ["P8.first", "P8.enter", "P8.arrows", "P8.tab", "P3.search-esc", "P17.cached"],
    scenario: { "P17.cached": rowsAtOnce(SearchableChoiceList, { options: names, label: "Osoba", modelValue: null }, "[role=radio]") } },
  { id: "SearchableTags", label: "Více lidí do filtru (SearchableTags)", component: "components/settings/SearchableTags.vue", saves: "live", query: "nov", first: "Jana Nováková", second: "Petr Novák", open: openSearchableTags,
    touch: "Štítky v poli, návrhy jako pilulky pod ním; compact rozbalí návrhy jen se zaměřeným polem. Ověřit při 375 px.", checks: ["P8.first", "P8.enter", "P8.arrows", "P8.tab", "P3.search-esc", "P17.cached"],
    scenario: { "P17.cached": rowsAtOnce(SearchableTags, { options: names, label: "Kdo", modelValue: [] }, "[data-suggestion]") } },
  { id: "PartyInput", label: "Strana požadavku (PartyInput)", component: "projector/PartyInput.vue", saves: "live", query: "novak", first: "Modelsoft", second: "AB", early: true, open: openPartyInput,
    touch: "Textové pole s nabídkou editoru pod ním; tap na návrh jméno vloží. Ověřit při 375 px.", checks: ["P8.first", "P8.enter", "P8.arrows", "P8.tab", "P17.cached"],
    scenario: { "P17.cached": rowsAtOnce(PartyInput, { modelValue: "", suggest: Object.assign(async () => parties, { cached: () => parties }) }, "[data-testid=requirement-party-list] [role=option]") } },
  { id: "CatalogPicker", label: "Katalog požadavků (CatalogPicker)", component: "projector/CatalogPicker.vue", saves: "none", query: "vpn", first: "VPN přístup", second: "VPN klient", early: true, open: openCatalog,
    touch: "Řádky s políčkem (cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny)) a souhrnem plánu pod nimi; mezerník dotyk nahrazuje tapem na řádek. Ověřit při 375 px.", checks: ["P8.first", "P8.enter", "P8.arrows", "P3.search-esc", "P17.cached"],
    scenario: { "P17.cached": rowsAtOnce(CatalogPicker, { definitions, modules: catalogModules(definitions), selected: [], label: "Katalog" }, "[data-catalog-row]") } },
  { id: "PageTags", label: "Tagy stránky (PageTags)", component: "components/PageTags.vue", saves: "live", query: "na", first: "Návody", second: "Nasazení", early: true,
    open: openPageTags,
    touch: "Modální dialog s polem a řádky nabídky, každý cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny); tagy se ukládají hned, bez paty s tlačítky. Ověřit při 375 px, klávesnice nezakryje nabídku.", checks: ["P8.first", "P8.enter", "P8.arrows", "P8.tab", "P3.search-esc", "P7.return", "P17.cached"], scenario: scenarios.tags },
  { id: "PageLocationPicker", label: "Umístění stránky (PageLocationPicker)", component: "components/PageLocationPicker.vue", saves: "none", query: "na", first: "Návody", second: "Nasazení", open: async () => mountLocationPicker(),
    touch: "Strom řádků pod hledáním, každý řádek cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny); tap vybere umístění. Ověřit při 375 px.", checks: ["P8.first", "P8.enter", "P8.arrows", "P8.tab", "P3.search-esc", "P17.cached"],
    scenario: { "P17.cached": rowsAtOnce(PageLocationPicker, { modelValue: "", items: places, label: "Umístění" }, ".location-row:not(.location-root)") } },
  { id: "ContactDirectory", label: "Adresář kontaktů (ContactDirectory)", component: "components/contacts/ContactDirectory.vue", saves: "live", query: "nov", first: "Jana Nováková", second: "Petr Novák", open: openDirectory,
    touch: "Karty přes celou šířku, lišta se zalamuje; ikona koše je cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny). Ověřit při 375 px.", checks: ["P8.first", "P8.enter", "P8.arrows", "P3.search-esc", "P7.return", "P6.toast", "P2.menu"], scenario: scenarios.directory },
  { id: "ContactOverview", label: "Kontakty napříč wiki (ContactOverview)", component: "components/contacts/ContactOverview.vue", saves: "none", query: "nov", first: "Jana Nováková", second: "Petr Novák", open: openOverview,
    touch: "Řádky s místy jako odkazy; přepínač rozsahu se zalamuje nad hledáním. Ověřit při 375 px.", checks: ["P8.first", "P8.enter", "P8.arrows", "P3.search-esc"] },
  { id: "JobListView", label: "Úlohy na pozadí (JobListView)", component: "components/settings/jobs/JobListView.vue", saves: "none", query: "zalo", first: "Záloha do Gitu", second: "Záloha ZIP", open: openJobs,
    touch: "Řádky přes celou šířku: stav a Spustit teď se zalamují pod popis, název je odkaz; ↓ z filtru jde na řádky. Ověřeno při 375 px bez vodorovného posouvání.", checks: ["P8.first", "P8.enter", "P8.arrows", "P3.search-esc"] },
  { id: "PersonalItems", label: "Můj prostor (PersonalItems)", component: "components/personal/PersonalItems.vue", saves: "live", query: "porada", first: "Porada o rozpočtu", second: "Porada týmu", open: openPersonal,
    touch: "Řádky přes celou šířku, tap otevře poznámku na místě; Koš a Nová poznámka se zalamují pod hledání. Ověřit při 375 px.", checks: ["P8.first", "P8.enter", "P8.arrows", "P3.search-esc", "P7.return", "P6.toast", "P2.menu"], scenario: scenarios.personal },
  { id: "SpacePageList", label: "Obsah prostoru (SpacePageList)", component: "components/SpacePageList.vue", saves: "none", query: "plan", first: "Plán školení", second: "Plán vydání", early: true, open: openSpaceList,
    touch: "Řádky tabulky se zúží na název a cestu, dlaždice druhů se posouvají do strany. Ověřit při 375 px.", checks: ["P8.first", "P8.enter", "P8.arrows", "P3.search-esc"] },
  { id: "SettingsShell", label: "Hledat sekci (SettingsShell)", component: "components/settings/SettingsShell.vue", saves: "none", query: "skup", first: "Skupiny osob", second: "Skupiny přístupů", open: openSettingsShell,
    touch: "V postranní liště aplikace na širokých obrazovkách; pod 1025 px drží sekce list Sekce bez hledání. Ověřit při 375 px.", checks: ["P8.first", "P8.enter", "P8.arrows", "P3.search-esc"] },
  { id: "SidebarSpace", label: "Najít ve stromu (SidebarSpace)", component: "components/SidebarSpace.vue", saves: "none", query: "n", first: "Návody", second: "Nasazení", open: async () => (await openTreeFilter()).session,
    touch: "Na dotyku otevře filtr lupa na řádku prostoru (na řádku se nedá psát). Ověřit při 375 px.", checks: ["P8.first", "P8.enter", "P8.arrows", "P3.search-esc", "P7.return"],
    scenario: { async "P7.return"() {
      const { row, session: filter } = await openTreeFilter();
      await filter.key("Escape");
      await flush();
      return document.activeElement === find(`.tree-page[data-tree-item="${row.getAttribute("data-tree-item")}"]`) ? true : `after Esc the keyboard is on ${document.activeElement?.tagName.toLowerCase()}, not on the row the filter was opened from`;
    } } },
  { id: "PageSearch", label: "Hledat ve stránce (PageSearch)", component: "components/PageSearch.vue", saves: "none", query: "nov", first: "Jana Nováková", second: "Petr Novák", open: async () => openPageSearch(),
    touch: "Záložka v panelu stránky: pole s tlačítky Předchozí a Další, řádky výsledků cíl dotyku aspoň 44 × 44 px, tap skočí na výsledek. Ověřit při 375 px. Esc (do stránky, hledání zůstane) a F3 nemají na dotyku obdobu; P3.search-esc se neověřuje, protože první Esc záměrně nemaže pole.", checks: ["P8.first", "P8.enter", "P8.arrows"] },
  { id: "BoardPagePicker", label: "Karta stránky na tabuli (BoardPagePicker)", component: "components/boards/BoardPagePicker.vue", saves: "none", query: "plan", first: "Plán vydání", second: "Plán školení", early: true, open: openBoardPagePicker,
    touch: "Lehký popover u horního okraje tabule: pole a seznam stránek, každý řádek cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny), tap na stránku ji přidá. Ověřit při 375 px, klávesnice nezakryje seznam.", checks: ["P8.first", "P8.enter", "P8.arrows", "P8.tab", "P3.search-esc", "P17.cached"],
    scenario: { async "P17.cached"() {
      // A space whose lexicon is known (its own id, so the other checks keep an unknown one): its pages are the first render's rows.
      vi.stubGlobal("fetch", routedFetch(path => (path.startsWith("/api/search?") ? jsonResponse({ items: [], hasMore: false })
        : path === "/api/spaces/known/lexicon" ? jsonResponse({ version: "1", truncated: false, people: [], pages: boardPages.map(({ id, title }) => ({ id, title })), youTrackProjects: [] }) : undefined)));
      await refreshLexicon("known");
      mountIt(BoardPagePicker, { open: true, spaceId: "known" });
      return document.querySelectorAll("[data-testid=board-page-list] [role=option]").length ? true : "the first render has no page row";
    } } },
  { id: "BoardCommandPanel", label: "Příkazy tabule (BoardCommandPanel)", component: "components/boards/canvas/BoardCommandPanel.vue", saves: "none", query: "nov", first: "Nová poznámka", second: "Nový rám", open: openCommandPanel,
    touch: "Panel 300 px u místa klepnutí nebo nad výběrem; řádky seznamu jsou cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny). Mezerník na dotyku otevírá panel klepnutím. Ověřit při 375 px.", checks: ["P8.first", "P8.enter", "P8.arrows", "P8.tab", "P3.search-esc", "P17.cached"],
    scenario: { "P17.cached": rowsAtOnce(BoardCommandPanel, commandPanelProps, "[data-testid=board-command-list] [role=option]") } },
  { id: "SpaceCoverGallery", label: "Obrázek prostoru (SpaceCoverGallery)", component: "components/library/SpaceCoverGallery.vue", saves: "live", query: "cloud", first: "Cloud a hosting", second: "Migrace a modernizace", open: openCoverGallery,
    touch: "Mřížka náhledů v několika sloupcích pod polem a skupinami; tap vybere. Ověřit při 375 px, bez vodorovného posouvání.", checks: ["P8.first", "P8.enter", "P8.arrows", "P8.tab", "P3.search-esc", "P17.cached"],
    scenario: { "P17.cached": rowsAtOnce(SpaceCoverGallery, coverProps, "[data-space-cover-options] [role=radio]") } },
  { id: "AppearanceIconBrowser", label: "Ikony vzhledu (AppearanceIconBrowser)", component: "components/settings/AppearanceIconBrowser.vue", saves: "live", query: "mail", first: "E-mail", second: "E-mail hledání", open: openIconBrowser,
    touch: "Mřížka ikon po 3 až 6 sloupcích, cíl 44 px; kategorie jako pilulky se zalamují. Ověřit při 375 px.", checks: ["P8.first", "P8.enter", "P8.arrows", "P8.tab", "P3.search-esc", "P17.cached"],
    scenario: { async "P17.cached"() {
      await mountIconBrowser();
      return document.querySelector("[data-appearance-results] [role=option]") ? true : "the first render has no icons";
    } } },
  { id: "TrashList", label: "Řádek koše (TrashList)", component: "components/settings/TrashList.vue", saves: "none",
    touch: "Řádek s tlačítky Obnovit a Smazat trvale; na dotyku nabídku nahrazují tato tlačítka (nabídka u řádků je klávesová a myší). Ověřit při 375 px.", checks: ["P2.menu"], scenario: scenarios.trash },
  { id: "AccessEntryRow", label: "Řádek přístupu (AccessEntryRow)", component: "components/settings/access/AccessEntryRow.vue", saves: "none",
    touch: "Řádek s akcemi pod ním na dotyku; nabídka je klávesová a myší, na dotyku platí tlačítka Změnit a Odebrat. Ověřit při 375 px.", checks: ["P2.menu"], scenario: scenarios.access },
  { id: "PageTree rename", label: "Přejmenování ve stromu (PageTree, F2)", component: "components/PageTree.vue", saves: "live",
    touch: "Na dotyku není F2: Přejmenovat je v nabídce „⋯“ řádku; pole je cíl dotyku aspoň 44 × 44 px (neviditelná plocha na pointer: coarse, viditelná velikost beze změny). Ověřit při 375 px.", checks: ["P1.start", "P1.enter", "P3.esc", "P4.blur", "P7.focus", "P6.toast"], scenario: scenarios.tree },
  { id: "App search", label: "Hledání a příkazy (App.vue)", component: "App.vue", saves: "none", query: "plan", first: "Plán vydání", second: "Plán školení", early: true, suggests: true, open: async early => (await openSearch(early)).search,
    touch: "Dialog přes celou šířku pod 640 px, řádky 52 px, nápověda kláves se na dotyku schová. Ověřit při 375 px.", checks: ["P8.first", "P8.enter", "P8.arrows", "P8.tab", "P3.search-esc", "P7.return", "P17.cached"], scenario: scenarios.search },
];

// ---------------------------------------------------------------- probes

const same = (a: HTMLElement | null | undefined, b: HTMLElement | null | undefined) => !!a && !!b && words(a) === words(b);
const shown = (list: string[]) => (list.length ? list.map(item => `„${item}“`).join(", ") : "nothing");

/** A fresh session: what an earlier one mounted, stubbed or faked is gone. */
const start = (d: AppDescriptor, early = false) => { reset(); return d.open!(early); };

const probes: Partial<Record<InteractionCheck, Probe<AppDescriptor>>> = {
  async "P8.first"(d) {
    const s = await start(d);
    await s.type(d.query!);
    const [first] = s.rows();
    if (!first || !words(first).includes(d.first!)) return `the first result is „${words(first)}“, not one with „${d.first}“`;
    if (!s.pre()) return "the results show no preselected row, so the user cannot see what Enter will take";
    return same(s.pre(), first) ? true : `the preselected row is „${words(s.pre())}“, not the first result „${words(first)}“`;
  },
  /** The three halves of „Enter takes the first“ are all asked, so a deviation row can name every one that fails. */
  async "P8.enter"(d) {
    const problems: string[] = [];
    let s = await start(d);
    await s.type(d.query!);
    await s.key("Enter");
    if (!s.taken().join(" ").includes(d.first!)) problems.push(`typing + Enter took ${shown(s.taken())}, not the first result`);
    s = await start(d);
    await s.key("Enter");
    if (!d.suggests && s.taken().length) problems.push(`Enter in the empty field took ${shown(s.taken())}`);
    if (d.suggests && !s.taken().length) problems.push("Enter in the empty field took nothing, not the preselected suggestion");
    if (d.early) {
      s = await start(d, true);
      await s.type(d.query!);
      await s.key("Enter");
      await s.arrive();
      if (!s.taken().join(" ").includes(d.first!)) problems.push(`Enter before the answer came took ${shown(s.taken())}, not the first result of the answer`);
    }
    return problems.length ? problems.join("; ") : true;
  },
  async "P8.arrows"(d) {
    const s = await start(d);
    await s.type(d.query!);
    const [first, second] = s.rows();
    if (!second || !words(second).includes(d.second!)) return `the second result is „${words(second)}“, not one with „${d.second}“`;
    for (let press = 0; press < 3 && !same(s.current(), second); press++) await s.key("ArrowDown");
    if (!same(s.current(), second)) return `ArrowDown never reached „${words(second)}“ (the keyboard is on „${words(s.current())}“)`;
    await s.key("ArrowUp");
    return same(s.current(), first) ? true : `ArrowUp left the keyboard on „${words(s.current())}“, not on the first result`;
  },
  async "P8.tab"(d) {
    const s = await start(d);
    await s.type(d.query!);
    await s.key("Tab");
    return s.taken().join(" ").includes(d.first!) ? true : `Tab took ${shown(s.taken())}, not the preselected suggestion`;
  },
  async "P3.search-esc"(d) {
    const s = await start(d);
    await s.type(d.query!);
    const esc = watchEsc();
    await s.key("Escape");
    if (s.closed() || esc.count) return `the first Esc closed it at once${s.value() ? ` and kept „${s.value()}“` : " (the browser cleared the search field, but the Esc went on)"}`;
    if (s.value()) return `the first Esc kept the typed text „${s.value()}“`;
    await s.key("Escape");
    return s.closed() || esc.count ? true : "the second Esc closed nothing";
  },
};
for (const check of ["P2.menu", "P6.toast", "P7.return", "P7.focus", "P1.start", "P1.enter", "P3.esc", "P4.blur", "P13.skeleton", "P17.cached"] as const) {
  probes[check] = d => d.scenario?.[check]?.() ?? Promise.resolve(`${d.id} has no scenario for ${check}`);
}

// ---------------------------------------------------------------- static checks over src/**/*.vue

const srcRoot = `${resolve(import.meta.dirname, "..").split("\\").join("/")}/`;
const vueFiles = (readdirSync(srcRoot, { recursive: true }) as string[]).map(file => file.split("\\").join("/")).filter(file => file.endsWith(".vue") && !file.startsWith("components/dev/")).sort();
const sources = new Map(vueFiles.map(file => [file, readFileSync(srcRoot + file, "utf8")]));

/** Files that pick from a list or search in one; each is a descriptor's component or here, with the reason it needs none. */
const notCovered: Record<string, string> = {
  "components/contacts/TopicField.vue": "one topic by typing: the first topic is preselected and Enter takes it, or the typed one as new when nothing matches (SkillRatePanel.test covers the keys)",
  "components/contacts/SkillGraph.vue": "filters a graph with SearchField and FilterMenu: they are the descriptors",
  "components/contacts/SkillBars.vue": "filters a person's topics with SearchField (from 20 topics): SearchField is the descriptor",
  "components/PageCreationFlow.vue": "creation flow that only uses pickOnEnter to create on Enter in the title; its location step embeds PageLocationPicker",
  "components/SimilarPagesSuggestion.vue": "links to existing pages under the creation title, reached by ↓ from it (focusFirst): ↑ ↓ move, Enter is the link's, Esc back to the title; no search or selection of its own (PageCreationFlow.test covers the keys)",
  "components/SearchSuggestions.vue":"the recent-pages listbox under the App search: its rows are driven by the App search descriptor",
  "components/PageHistoryPanel.vue": "filters the versions by author with SearchField (the first Esc clears the filter, the second closes the side panel): SearchField is the descriptor",
  "components/SpaceDirectory.vue": "filters the space cards with SearchField: SearchField is the descriptor",
  "components/SpaceDraftApproval.vue": "filters drafts with SearchField: SearchField is the descriptor",
  "components/settings/space/SpaceContentHealthPage.vue": "filters its list with SearchField and FilterMenu (the descriptors) and picks an owner with SearchableChoiceList (first result preselected, Enter picks); the rows' ↑ ↓ Space V G keys are covered by SpaceContentHealthPage.test",
  "components/settings/FilterMenu.vue": "a filter's dropdown menu; its search over more than eight options preselects the first and Enter picks it (FilterMenu.test)",
  "components/settings/access/PrincipalAccessView.vue": "filters a table with SearchField: SearchField is the descriptor",
  "components/settings/access/ResourceAccessPanel.vue": "filters a table with SearchField: SearchField is the descriptor",
  "components/settings/general/WikiJsImportModule.vue": "filters a table with SearchField: SearchField is the descriptor",
  "components/settings/global/AccessKindPage.vue": "filters a table with SearchField: SearchField is the descriptor",
  "components/settings/global/AllVaultsPage.vue": "filters a table with SearchField: SearchField is the descriptor",
  "components/settings/global/SpacesAccessPage.vue": "filters a table with SearchField: SearchField is the descriptor",
  "components/settings/people/AccountsPage.vue": "filters a table with SearchField: SearchField is the descriptor",
  "components/settings/people/ApiTokensPanel.vue": "filters a table with SearchField: SearchField is the descriptor",
  "components/settings/people/GroupsPage.vue": "filters a table with SearchField: SearchField is the descriptor",
  "components/settings/space/VaultSecretsTab.vue": "filters a table with SearchField: SearchField is the descriptor",
};
const pickerMark = /\b(?:pickEnter|isPickEnter|pickOnEnter|focusFirst)\b|<SearchField\b|type="search"|role="(?:combobox|listbox)"/;

const TAG = /<(\/?)([A-Za-z][^\s/>]*)((?:"[^"]*"|'[^']*'|[^'">])*)>/g;
const VOID_TAGS = new Set(["br", "hr", "img", "input", "meta", "link", "source", "col", "wbr"]);
/** Elements whose text says „loading“ without drawing the place: not a skeleton, a note or a region. A text for a screen reader beside a
 * drawn skeleton (`*-sr`, `visually-hidden`) and the caption of a busy button say it for something else. */
function bareLoadingTexts(source: string) {
  const html = source.replace(/<script[\s\S]*?<\/script>/g, "").replace(/<style[\s\S]*?<\/style>/g, "").replace(/<!--[\s\S]*?-->/g, "");
  const open: { name: string; attrs: string }[] = [], found: string[] = [];
  let last = 0;
  for (const tag of html.matchAll(TAG)) {
    const text = html.slice(last, tag.index), at = open.at(-1);
    if (/Načít(?:ání|ám|áme)/.test(text) && at && !/^(?:button|a|label|option|LoadingNote|LoadingSkeleton|LoadingRegion)$/i.test(at.name) && !/\b(?:[\w-]*-sr|sr-only|st-sr-only|visually-hidden)\b/.test(at.attrs)) found.push(text.trim());
    last = tag.index + tag[0].length;
    const [, close, name, attrs] = tag;
    if (close) { let index = open.length - 1; while (index >= 0 && open[index]!.name !== name) index--; if (index >= 0) open.length = index; }
    else if (!attrs.trimEnd().endsWith("/") && !VOID_TAGS.has(name.toLowerCase())) open.push({ name, attrs });
  }
  return found;
}
/** One descriptor per file with a bare loading text, plus every file a deviation row already names: a new offender has no row and fails, a fixed one asks for the row's deletion. */
const loadingFiles = [...new Set([
  ...vueFiles.filter(file => bareLoadingTexts(sources.get(file)!).length),
  ...(deviations as InteractionDeviation[]).filter(row => row.check === "P13.skeleton").map(row => row.element.replace(/^loading:/, "")),
])].sort();
for (const file of loadingFiles) {
  descriptors.push({ id: `loading:${file}`, label: `Načítání v ${file}`, component: file, saves: "none", touch: "Statický rozbor šablony; kostra má stejný rozměr při 375 px jako na šířku, ověřit u opravené komponenty.", checks: ["P13.skeleton"],
    scenario: { async "P13.skeleton"() {
      const bare = sources.has(file) ? bareLoadingTexts(sources.get(file)!) : [];
      return bare.length ? `a plain element says „${bare[0]}“ with no reserved size` : true;
    } } });
}

describe("the pickers and searches of the application are all in the contract", () => {
  it("has a descriptor or a reason for every file that picks from a list or searches in one", () => {
    const covered = new Set(descriptors.filter(descriptor => !descriptor.id.startsWith("loading:")).map(descriptor => descriptor.component));
    const missing = vueFiles.filter(file => pickerMark.test(sources.get(file)!) && !covered.has(file) && !(file in notCovered));
    expect(missing, "add a descriptor to appInteraction.contract.test.ts, or a reason to notCovered").toEqual([]);
    expect(Object.keys(notCovered).filter(file => covered.has(file) || !pickerMark.test(sources.get(file) ?? "")), "notCovered lists files that are covered or no longer pick").toEqual([]);
  });
  it("points every descriptor at a component that exists", () => {
    expect(descriptors.filter(descriptor => !sources.has(descriptor.component)).map(descriptor => descriptor.id)).toEqual([]);
  });
});

runInteractionContract({ surface: "app pickers and lists (area C)", descriptors, deviations: deviations as InteractionDeviation[], probes });
