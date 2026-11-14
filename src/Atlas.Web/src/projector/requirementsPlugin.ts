import { defineMarkdownCommand, type MarkdownPlugin } from "@atlas/markdown/core";
import { Extension, type Editor } from "@tiptap/core";
import { useModules } from "../composables/useModules";
import { captureTypeAhead, type TypeAhead } from "../lib/typeAhead";
import type { Transaction } from "@tiptap/pm/state";
import { visibleInsertionPoint, visibleTop } from "../lib/firstVisibleBlock";
import { lazyVueComponent, openVuePopover } from "./openVuePopover";
import { catalogSize, isCatalogPage } from "./requirementCatalog";
import type { PeopleSearch } from "../contacts/contactSlash";
import type { projectorApi } from "./projectorApi";
import { autoOverviewPlugin } from "./requirementAutoOverview";
import { insertResolved } from "./requirementCommands";
import { RequirementInline } from "./requirementInline";
import { pageRequirements, RequirementNode } from "./requirementNode";
import { isDependencyLine, isRequirementHeading, moveLegacyParty, RequirementHeadingAttributes } from "./requirementHeading";
import { headingIntentions, insertRequirementHeading, requirementHeadingExtensions, warmStateMenu } from "./requirementHeadingView";
import { RequirementRuntime, runtimeOf, storageOf } from "./requirementRuntime";
import { requirementSummaryText, titleOfJson } from "./requirementSummary";
import { defaultRequirementLevel, playRequirementLevel, requirementLevels, requirementViewPlugin } from "./requirementViewMode";
import RequirementSummaryCard from "./RequirementSummaryCard.vue";
import { cardRequirementNodes, projectRequirementHeadings, type DeploymentDocument } from "./deploymentMap";
import type { PageRequirement } from "./requirementModel";
import { requirementDefaults } from "./requirementTypes";
import { api } from "../api";
import { h, type FunctionalComponent } from "vue";
import RequirementSummaryView from "./RequirementSummaryView.vue";
import { registerOverviewKind, type OverviewCardAction, type OverviewJson } from "../overview/overviewKinds";
import { insertOverview, LegacyOverviewNode, levelPlugin, noteSerialized, OverviewNode, overviewSlashActions, revealOverview } from "../overview/overviewNode";
import "./requirements.css";
import t from "../i18n/projector";

/** What a page editor gives the blocks: the page and its space for the catalog and the summary's menu, and where to go next. */
export interface ProjectorHost {
  spaceId(): string;
  pageId(): string | undefined;
  /** The page's parent, so a deployment copied from it lands beside it. */
  parentId?(): string | null | undefined;
  /** The page's title, for the copy's name. */
  pageTitle?(): string;
  /** A page of the space by id, for the name of the catalog page a definition comes from. */
  titleOf?(pageId: string): string | undefined;
  /** Whether the page has changes that are not saved yet; a copy of the page is made from what is saved. */
  unsaved?(): boolean;
  /** Whether the page is a requirement catalog: its own flag (the page DTO's `isCatalog`); undefined while the page does not say. */
  isCatalog?(): boolean | undefined;
  /** The summary's menu marked or unmarked the page (the server answered): the page takes the answer as its flag. */
  setCatalog?(isCatalog: boolean): void;
  /** The page can be written (its right, not the lock): the summary keeps its menu's place while the editor is not editable yet. */
  writable?(): boolean;
  /** The page's deployment board is there to open (App.vue `deploymentBoardOffered`): the summary of a page read offers „Tabule nasazení“. */
  boardOffered?(): boolean;
  /** People for the party field (contacts/contactSources.ts). */
  people?: PeopleSearch;
  /** The live name of a directory contact (contactApi.live), so a party that is a contact reads as the contact is called now, with its
   * logo or photo (package G). */
  contact?(contactId: string): Promise<{ name: string; photo?: string } | undefined>;
  api: Pick<typeof projectorApi, "catalog" | "definitions" | "setCatalogPage" | "createDeployment">
    & Partial<Pick<typeof projectorApi, "waiting" | "presets" | "deployments" | "pageDefinitions" | "pageRequirements" | "prerequisites" | "dependents"
      | "cycle" | "searchRequirements">>;
  /** Opens a page, e.g. the deployment just created. */
  open(pageId: string): void;
}

/** The summary over collected requirements (a board): the page's card with the level of `scope`; a block of another page is not shown from here. */
const RequirementsReadCard: FunctionalComponent<{ items: readonly PageRequirement[]; scope: string; actions?: readonly OverviewCardAction[] }> = props =>
  h(RequirementSummaryCard, { items: props.items, viewPage: props.scope, show: () => {}, actions: props.actions, board: true });
/** The projection's dependency on another page (`pageId#headingId`) as the model's `key@pageId` (requirementModel.ts `crossPage`). */
const modelRequires = (requires: unknown) => String(requires ?? "").split(",").filter(Boolean)
  .map(key => { const at = key.indexOf("#"); return at > 0 ? `${key.slice(at + 1)}@${key.slice(0, at)}` : key; }).join(",");
/**
 * The requirements of a stored document (a page read without an editor: history, a board's blocks; a board's product and service cards
 * as JSON): its requirement headings as the block shape the server and the board's cards project them to (deploymentMap.ts), and any
 * old block, with their nesting.
 */
function requirementsOf(document: OverviewJson): PageRequirement[] {
  const items: PageRequirement[] = [];
  const walk = (node: OverviewJson, ancestors: string[]) => node.content?.forEach(child => {
    if (child.type !== "requirement") return walk(child, ancestors);
    const id = (child.attrs?.id as string | null) ?? "";
    items.push({ id, title: titleOfJson(child), attrs: { ...requirementDefaults, ...child.attrs, requires: modelRequires(child.attrs?.requires) } as PageRequirement["attrs"], ancestors });
    walk(child, [...ancestors, id]);
  });
  walk({ ...document, content: projectRequirementHeadings(document.content ?? []) }, []);
  return items;
}
/** A page's stored document for a board's product and service cards (the reader's rights; none for a page they cannot read). */
const storedPage = (pageId: string) => api<{ contentJson: string }>(`/pages/${encodeURIComponent(pageId)}`)
  .then(page => (JSON.parse(page.contentJson) as { document?: DeploymentDocument }).document ?? null, () => null);

/**
 * The deployment summary: the overview of kind `requirements` (overviewKinds.ts), its card the summary of the blocks. On a board it sums
 * up the board's requirement blocks and the requirements its product and service cards show (`cardRequirementNodes`). A change of level
 * plays the shared motion over the blocks (overviewMotion.ts).
 */
registerOverviewKind<PageRequirement>({
  // `label` is the Markdown's content (Czech); the tool shows `title`. Getters: read where shown, after the language has arrived.
  kind: "requirements", label: "Stav nasazení", get title() { return t.card.deploymentState; }, card: RequirementSummaryView, readCard: RequirementsReadCard,
  collect: document => ("descendants" in document ? pageRequirements(document) : requirementsOf(document)),
  boardNodes: items => cardRequirementNodes(items, storedPage),
  text: document => requirementSummaryText(pageRequirements(document)),
  levels: requirementLevels, defaultLevel: defaultRequirementLevel, itemSelector: "[data-requirement-view]", animate: playRequirementLevel,
  // Products, services and their connectors as the page's own board, opened or composed (composeDeploymentBoard.ts, loaded on use).
  actions: [{ id: "fill-prerequisites", get label() { return t.plugin.fillPrerequisites; }, icon: "link", pageOnly: true,
    // The runtime's index, as the card counts them: it knows the page, so a link to a heading of this page that is gone is missing here.
    run: async ({ editor }) => { if (editor) await (await import("./fillMissingPrerequisites")).fillMissingPrerequisites(editor, runtimeOf(editor).index.value.items); } },
    { id: "compose-board", get label() { return t.plugin.deploymentBoard; }, icon: "layout-dashboard", pageOnly: true, readable: editor => !!storageOf(editor).host?.boardOffered?.(),
    run: async ({ editor }) => { if (editor) await (await import("./composeDeploymentBoard")).composeDeploymentBoard(editor); } }],
});

// The same overview at the top of a page without a summary block (requirementAutoOverview.ts), and the reader's view of the blocks.
// A catalog page has no levels and no reader's view: it is read as written (its own level plugin, in place of the parent's).
const Overview = OverviewNode.extend<{ host?: ProjectorHost }>({
  addProseMirrorPlugins() {
    const { editor } = this, host = this.options.host, catalog = () => !!host && isCatalogPage(editor, host);
    return [levelPlugin(() => host?.pageId(), kind => kind !== "requirements" || !catalog()), autoOverviewPlugin(editor, host),
      requirementViewPlugin(() => catalog() ? undefined : host?.pageId())];
  },
});
// The insert panels load apart from the editor (3.6-G): taking the editor prefetches them in idle time (Preload); opened sooner, the
// panel stands at once as a skeleton of its size (lazyVueComponent).
const catalogPanel = lazyVueComponent(() => import("./RequirementCatalogPanel.vue")), sourcePanel = lazyVueComponent(() => import("./RequirementSourcePanel.vue"));

/** Where the reader is when the caret is not: the page menu opened the panel while the page was scrolled. Undefined while the caret shows. */
function placeInView(editor: Editor): number | undefined {
  const { view } = editor, bottom = view.dom.ownerDocument.defaultView?.innerHeight ?? Infinity;
  try {
    const at = view.coordsAtPos(editor.state.selection.from);
    if (at.bottom > visibleTop(view.dom.ownerDocument) && at.top < bottom) return undefined;
  } catch { /* no layout for the caret: the first block in view */ }
  return visibleInsertionPoint(view);
}

/**
 * „Moduly produktu“ (plan 3.5 C, owner O2: one entry): the catalog, wide, as a tree of product › category › module, single
 * requirements found by a search in it; a pick goes under the page's heading of its product › category › module, else at the caret, or,
 * when the caret is out of view, between the top-level blocks where the reader is (W4-2). That place follows the document while the
 * panel is open (a collaborator's edit).
 */
export function openCatalogPanel(editor: Editor, host: ProjectorHost, typeAhead?: TypeAhead) {
  openInsertPanel(editor, host, t.plugin.catalogTitle, catalogPanel, { typeAhead, tree: true }, "atlas-requirement-popover is-wide", { rows: 9, rowHeight: 40 });
}

/** „Požadavek z nasazení“ and „Požadavky ze sady“ (M4a): another deployment's or a set's blocks, placed as a catalog pick is. */
export function openSourcePanel(editor: Editor, host: ProjectorHost, presetsOnly: boolean, typeAhead?: TypeAhead) {
  openInsertPanel(editor, host, presetsOnly ? t.plugin.presetTitle : t.plugin.sourceTitle, sourcePanel, { typeAhead, presetsOnly }, undefined, { rows: 6, rowHeight: 40 });
}

function openInsertPanel(editor: Editor, host: ProjectorHost, title: string, component: Parameters<typeof openVuePopover>[0]["component"], props: Record<string, unknown>,
  className?: string, stub?: Parameters<typeof openVuePopover>[0]["stub"]) {
  if (editor.isDestroyed || !editor.isEditable) return;
  let at = placeInView(editor);
  const follow = ({ transaction }: { transaction: Transaction }) => { if (at !== undefined) at = transaction.mapping.map(at); };
  if (at !== undefined) editor.on("transaction", follow);
  const anchor = () => {
    try { const box = editor.view.coordsAtPos(at ?? editor.state.selection.from); return new DOMRect(box.left, box.top, 0, box.bottom - box.top); }
    catch { return editor.view.dom.getBoundingClientRect(); }
  };
  openVuePopover({ editor, anchor: editor.view.dom, positionAnchor: { getBoundingClientRect: anchor }, title, component, stub, ...(className ? { className } : {}),
    closed: () => editor.off("transaction", follow),
    props: { host, editor, ...props, insert: (items: Parameters<typeof insertResolved>[1], options?: Parameters<typeof insertResolved>[3]) => insertResolved(editor, items, at, options) } });
}

// „Požadavek“ inserts a requirement heading (Atlas 3.6, model 2026-11-03); the old block comes only from „[] “ and the catalog until pages convert.
const insertBlock = defineMarkdownCommand({ id: "requirements.insert", apply: () => ({ editor, tr, dispatch }) => !dispatch || (editor.isEditable && insertRequirementHeading(tr)) });
// „Souhrn nasazení“: the shortcut to the overview of kind requirements.
const insertSummary = defineMarkdownCommand({ id: "requirements.summary", apply: () => ({ editor, commands, dispatch }) =>
  !dispatch || revealOverview(editor, "requirements") || commands.insertContent({ type: "overview", attrs: { kind: "requirements" } }) });

/**
 * Requirement blocks and the deployment summary (Projector v1, spec 2026-10-27): Atlas's own nodes, outside the editor package. Every
 * display context registers them, so any page opens; the insert actions appear only with the projector module, the catalog only
 * in a page editor that has a `host`.
 */
export function createRequirementsPlugin(options: {
  host?: ProjectorHost;
  /** Whether the insert actions are offered; the projector module by default (the gallery offers them without a server). */
  offered?: () => boolean;
} = {}): MarkdownPlugin {
  const { host, offered = () => useModules().enabled("projector") } = options;
  const openCatalog = defineMarkdownCommand<{ slash?: boolean } | void>({ id: "requirements.catalog", apply: args => ({ editor, dispatch }) => {
    if (!host) return false;
    // The slash menu offers what is enabled: the catalog (first, by its priority) while the space has one; the page menu always.
    if (!dispatch) return !args?.slash || catalogSize(editor, host) !== 0;
    // After the slash menu has closed, so its teardown does not take the new panel with it.
    // What is typed on meanwhile is the search (`/katalog` Enter `vpn` Enter in one go).
    const typed = args?.slash ? captureTypeAhead(editor.view.dom.ownerDocument) : undefined;
    setTimeout(() => openCatalogPanel(editor, host, typed));
    return true;
  } });
  // „Požadavek z nasazení“ / „Požadavky ze sady“: offered with a host that reads another page's blocks.
  const openSource = defineMarkdownCommand<{ presets?: boolean; slash?: boolean } | void>({ id: "requirements.fromDeployment", apply: args => ({ editor, dispatch }) => {
    if (!host?.api.pageDefinitions) return false;
    if (!dispatch) return true;
    const typed = args?.slash ? captureTypeAhead(editor.view.dom.ownerDocument) : undefined;
    setTimeout(() => openSourcePanel(editor, host, !!args?.presets, typed));
    return true;
  } });
  // Taking an editable editor asks for the light catalog (no blocks), so the first slash already knows whether to offer it (U4-13), and
  // fetches the insert panels when the page idles.
  // The host before the first state and view: the index, the marks and the cards read as the view is built (`create` comes a task later)
  // and so know the page, a link to a heading of this page that is gone is a missing prerequisite after a reload as it is live.
  const Preload = Extension.create({ name: "requirementCatalogPreload", onBeforeCreate() { storageOf(this.editor).host = host; }, onCreate() { if (this.editor.isEditable) warmStateMenu(); }, onFocus() {
    if (this.editor.isEditable) warmStateMenu();
    if (!host || !this.editor.isEditable || !offered()) return;
    catalogSize(this.editor, host);
    catalogPanel.prefetchWhenIdle();
    if (host.api.pageDefinitions) sourcePanel.prefetchWhenIdle();
  } });
  // The Czech words stay searchable in every language (typed from habit); the English ones beside them.
  const insert = { id: "requirements.insert", label: t.plugin.requirement, description: t.plugin.requirementHint, icon: "svg:list-checks", group: t.plugin.group,
    keywords: ["požadavek", "nasazení", "projector", "vstup", "requirement", "deployment", "input"], command: insertBlock() };
  const summary = { id: "requirements.summary", label: t.plugin.summary, description: t.plugin.summaryHint, icon: "svg:activity", group: t.plugin.group,
    keywords: ["souhrn", "stav", "nasazení", "přehled", "summary", "status", "overview"], command: insertSummary() };
  // One entry, one rule: equal matches keep the registry's order (priority, then id), so the catalog ranks before the plain block.
  const catalog = (slash: boolean) => host ? [{ id: "requirements.catalog", label: t.plugin.catalogTitle, description: t.plugin.catalogHint,
    icon: "svg:book-open", group: t.plugin.group, priority: slash ? -1 : 0, keywords: ["katalog", "šablona", "požadavek", "produkt", "modul", "konfigurátor", "catalog", "template", "requirement", "product", "module", "configurator"], command: openCatalog(slash ? { slash } : undefined) }] : [];
  const sources = (slash: boolean) => host?.api.pageDefinitions ? [
    { id: "requirements.fromDeployment", label: t.plugin.sourceTitle, description: t.plugin.sourceHint,
      icon: "svg:copy", group: t.plugin.group, keywords: ["nasazení", "kopie", "převzít", "požadavek", "deployment", "copy", "take", "requirement"], command: openSource(slash ? { slash } : undefined) },
    { id: "requirements.preset", label: t.plugin.presetTitle, description: t.plugin.presetHint,
      icon: "svg:layout-template", group: t.plugin.group, keywords: ["sada", "předvolba", "šablona", "nasazení", "set", "preset", "template", "deployment"], command: openSource({ presets: true, slash }) },
  ] : [];
  return {
    // Schema 2 (Atlas 3.4): the overview node and the requirement's stateLabel and system. A version 1 document is valid version 2
    // content; the raised version keeps a 3.3 tab out of a shared document it would strip them from (docs/markdown/collaboration-schema.md).
    // Schema 3 (Atlas 3.6): requirement headings (the heading's state, kind, service, provenance and aliases, the paragraph's
    // `dependencies`) beside the legacy block; a version 2 document is valid version 3 content.
    // Schema 4 (Atlas 3.6): the heading's `party` and `partyContactId` (the facts line keeps only the deadline); a version 3 document's
    // „od … · do …“ lines move into its headings as the server moves them (moveLegacyParty, RequirementHeadings.MoveParty).
    id: "atlas-requirements", apiVersion: 1, schemaVersion: 4, profiles: ["rich"],
    migrations: [{ from: 1, to: 2, migrate: document => ({ ...document }) }, { from: 2, to: 3, migrate: document => ({ ...document }) }, { from: 3, to: 4, migrate: document => moveLegacyParty(document) }],
    create: () => ({
      // The legacy block stays a schema node only (stored documents and their history parse; the server converts a page on open).
      extensions: [RequirementHeadingAttributes, ...requirementHeadingExtensions, RequirementNode, Overview.configure({ host }), LegacyOverviewNode.configure({ host }), RequirementRuntime, Preload, RequirementInline],
      commands: [insertBlock, insertSummary, insertOverview, openCatalog, openSource],
      // „Přehled…“ lists the registered kinds (overviewNode.ts); without the projector module the other kinds (contacts) stay.
      actions: offered() ? { slash: [insert, ...catalog(true), ...sources(true), summary, ...overviewSlashActions(() => true)], toolbar: [insert, ...catalog(false), ...sources(false), summary] }
        : { slash: overviewSlashActions(kind => kind !== "requirements") },
      // The Markdown projection is readable text (spec §2.3); it is never read back. The page source checks the round trip itself
      // (ignoreIdentity): an overview written as the dialect's ```a-overview fence and a requirement heading with its {#id state=…}
      // block read back there.
      markdownLossy(document, options) {
        // The projection asks this of the document it is about to write; a nested overview counts that one (overviewNode.ts).
        noteSerialized(document);
        let found = false;
        // A requirement heading and its dependency line are lossy too: Markdown keeps their text, not their state or links (as the old block).
        document.descendants(node => { found ||= ["requirement", "requirementSummary"].includes(node.type.name) || ((node.type.name === "overview" || isRequirementHeading(node)) && !options?.ignoreIdentity) || isDependencyLine(node); return !found; });
        return found;
      },
      // Ctrl+. in a requirement heading: its pickers and the PM's actions.
      intentions: ({ editor }) => headingIntentions(editor),
    }),
  };
}
