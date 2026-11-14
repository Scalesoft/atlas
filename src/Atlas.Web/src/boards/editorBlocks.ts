import { slashItems } from "@atlas/markdown";
import {
  createMarkdownEditorController, parseMarkdownContent, type MarkdownCommand, type MarkdownEditorController, type MarkdownLexiconSource, type MarkdownPlugin,
  type MarkdownRichValue, type MarkdownSchemaPlugin,
} from "@atlas/markdown/core";
import { lexiconSource } from "../linking/lexicon";
import { createEditorToolsPlugin } from "@atlas/markdown/plugins/editor-tools";
import type { MarkdownPageSource } from "@atlas/markdown/plugins/pages";
import { createYouTrackPlugin, type YouTrackClient, type YouTrackIssue } from "@atlas/markdown/plugins/youtrack";
import type { Editor } from "@tiptap/core";
import { api, ApiError } from "../api";
import type { Schema } from "@tiptap/pm/model";
import { emptyDocument } from "../document";
import { createDocumentWidgets } from "../documentWidgets";
import { createPageSource, createPeopleServices, type PeopleServices } from "../editorDirectories";
import type { OverviewBoardItem } from "../overview/overviewKinds";
import type { ProjectorHost } from "../projector/requirementsPlugin";
import type { BoardBlockNode, BoardChange } from "./boardApi";
import type { BoardItem } from "./boardModel";
import type { BoardLinksApi } from "./boardLinks";
import t from "../i18n/boards";

/**
 * Editor blocks on a board: the board has no contact, chart or task of its
 * own; a `block` item holds one node of the page editor, rendered and edited by the editor's own plugin. What a board can insert
 * is read from the same registry the page editor's slash menu reads (the plugins' slash actions and the editor's own slash
 * items), so a new plugin appears on boards without a change here. Only what a board cannot hold is left out, with its reason.
 */

/** One component of the board's „Komponenta“ menu: the page editor's slash entry. */
export interface EditorBlockModule {
  /** The slash entry's id (`contacts.insert`, `callout`). */
  id: string;
  label: string;
  description?: string;
  /** A name of the editor's icon registry (`@atlas/markdown/icons`), without its `svg:` prefix. */
  icon: string;
  /** Extra search words of the slash menu; the label always matches. */
  keywords: readonly string[];
  /** What the slash menu dispatches: inserts the block with the plugin's defaults, or opens the plugin's picker (YouTrack). */
  command?: MarkdownCommand;
  /**
   * Instead of a command: opens a panel of its own on the block's editor, which inserts what is picked (the requirement catalog,
   * whose pick brings what it requires); the insertion ends with every block it inserted.
   */
  open?(editor: Editor): void;
}


/**
 * Slash entries of the page editor a board leaves out, each with its reason. Password references are not here: their plugin is
 * never registered on a board at all (reserved, spec §3.6), so they cannot even be pasted into a block.
 */
export const boardBlockExclusions: Readonly<Record<string, string>> = Object.freeze({
  "attachments.insert": "Příloha patří stránce: nahrání i vlastnictví souboru server váže na stránku, tabule žádné úložiště souborů nemá.",
  "openapi.insert": "Úpravy API žijí v panelu detailu stránky (OpenApiDetailPanels), který tabule nemá.",
  "toc.insert": "Obsah dokumentu čte nadpisy stránky; blok na tabuli je dokument o jediném bloku a žádné nadpisy nemá.",
  "contact-inline.insert": "Zmínka je část textu; na tabuli je kontaktem blok Kontakty.",
  "math-inline.insert": "Vzorec v textu je část odstavce; na tabuli je vzorcem blok Matematický vzorec.",
  "emoji.picker": "Emoji vkládá znak do textu; texty tabule jsou Markdown poznámek a textů.",
  ...Object.fromEntries([1, 2, 3, 4, 5, 6].map(level => [`heading-${level}`, "Nadpis je formát textu; na tabuli patří do poznámky nebo textu (Markdown)."])),
  "bullet-list": "Seznam je formát textu poznámky nebo textu (Markdown).",
  "ordered-list": "Seznam je formát textu poznámky nebo textu (Markdown).",
  "task-list": "Úkoly jsou formát textu poznámky nebo textu (Markdown).",
  "blockquote": "Citace je formát textu poznámky nebo textu (Markdown).",
  "horizontal-rule": "Čára na tabuli je spojnice nebo tvar.",
  "paste-markdown": "Vložení Markdownu patří do textu; vložení do plochy umí tabule sama (boardPaste).",
});

/** A slash entry as the plugins (`MarkdownAction`) and the editor's own items describe it. */
export interface SlashEntry {
  id?: string; label: string; description?: string; icon?: string; keywords?: readonly string[]; command?: MarkdownCommand;
}

/** The board's modules of the slash entries, in their order: no excluded entry, no entry without an id or a command, each id once. */
export function toBlockModules(entries: readonly SlashEntry[], exclusions: Readonly<Record<string, string>> = boardBlockExclusions): EditorBlockModule[] {
  const seen = new Set<string>();
  const modules: EditorBlockModule[] = [];
  for (const { id, label, description, icon, keywords, command } of entries) {
    if (!id || !command || id in exclusions || seen.has(id)) continue;
    seen.add(id);
    modules.push({ id, label, ...(description ? { description } : {}), icon: icon?.replace(/^svg:/, "") || "box", keywords: [...keywords ?? []], command });
  }
  return modules;
}

interface DocumentNode { type?: string; content?: readonly unknown[] }
const isEmptyParagraph = (node: DocumentNode) => node.type === "paragraph" && !node.content?.length;

/** The block a document holds: its first node that is not an empty paragraph (the editor keeps one after an atom), or null. */
export function firstBlock(document: DocumentNode): BoardBlockNode | null {
  const node = (document.content ?? []).find(child => !isEmptyParagraph(child as DocumentNode));
  return (node as BoardBlockNode | undefined) ?? null;
}
/** Every block a document holds, without the empty paragraphs the editor keeps. */
export const blocksOf = (document: DocumentNode): BoardBlockNode[] =>
  (document.content ?? []).filter(child => !isEmptyParagraph(child as DocumentNode)) as BoardBlockNode[];

/**
 * The rich document of one block, as the renderer reads it and the server validates `props.block`: with the plugin metadata of
 * the board's editor, which the editor requires before it reads a plugin's node.
 */
export function blockDocument(node: BoardBlockNode): MarkdownRichValue {
  return { format: "atlas-rich", version: 1, plugins: boardEditor().plugins, document: { type: "doc", content: [node as never] } };
}

/** Host services of the editor's plugins on a board. The canvas or session passes them; nothing here knows a board. */
export interface BoardBlockHost {
  /** Issue data of the YouTrack plugin (search, resolve) in the context of the board's space. */
  youTrack: YouTrackClient;
  /** Pages for `[[` links in the board's texts, found as a page finds them (the wiki search, the board's space first). */
  pages?: MarkdownPageSource;
  /** People for `@` mentions, contacts and name recognition, as a page of the space has them (editorDirectories.ts `createPeopleServices`). */
  people?: PeopleServices;
  /** The space's lexicon for recognition in the text (names, page titles, YouTrack keys), as the page's editor reads it. */
  lexicon?: MarkdownLexiconSource;
  /** The space's requirement catalog, for „Požadavek z katalogu“ (a board of a space with the projector module). */
  projector?: ProjectorHost;
  /**
   * „Uspořádat jako architekturu“ (a deployment board, with the projector module): `offers` says which items it lays out, `changes` gives
   * the moves for those of `ids` (none: of the whole board). The canvas offers it where such items are and sends the changes as one batch.
   */
  layout?: { offers: (item: BoardItem) => boolean; changes: (items: readonly BoardItem[], ids: readonly string[]) => Promise<readonly BoardChange[]> };
  /** The board's space for its lists derived from the relations (`links`, P15); none: the board offers none (the relations graph). */
  links?: { spaceId: () => string; api?: BoardLinksApi };
  /** The board itself for its overview blocks (OverviewReadCard): its id (the levels' scope) and its items, read over the whole board. */
  board?: { id: string; items(): readonly OverviewBoardItem[] };
  /**
   * Whether this person may write to the board now (its right, the lock, the connection), asked when a component acts (3.6-0,
   * documentWidgets.ts `gateWrites`): the board's texts and blocks keep their editors and plugins when it changes. None: always.
   */
  canWrite?: () => boolean;
}

/** A host without services (gallery, listing the modules): YouTrack answers nothing. */
export const inertBlockHost: BoardBlockHost = Object.freeze({
  youTrack: { search: async () => [], resolve: async () => null },
});

/**
 * The host services of a board: its own YouTrack endpoints (`/api/boards/{id}/youtrack/…`, spec §4), which map the board's space to
 * projects and cache as the endpoints of a page of the space do. An issue the board cannot see resolves as none.
 */
export function boardContextBlockHost(boardId: string, spaceId?: () => string | undefined): BoardBlockHost {
  const base = `/boards/${encodeURIComponent(boardId)}/youtrack`;
  return {
    pages: createPageSource(() => [], { spaceId }),
    // The space's people as its pages have them; a board is no page, so no accounts search (the lexicon has the space's accounts).
    people: createPeopleServices(() => undefined, spaceId ?? (() => undefined)),
    lexicon: lexiconSource(spaceId ?? (() => undefined)),
    youTrack: {
      search: (query, { signal }) => api<YouTrackIssue[]>(`${base}/search?q=${encodeURIComponent(query)}`, { signal }),
      async resolve(reference, { signal }) {
        try {
          return await api<YouTrackIssue>(`${base}/resolve?reference=${encodeURIComponent(reference)}`, { signal });
        } catch (failure) {
          if (failure instanceof ApiError && failure.status === 404) return null;
          throw failure;
        }
      },
    },
  };
}

const pluginSets = new WeakMap<BoardBlockHost, MarkdownPlugin[]>();
/**
 * The page editor's content plugins for a board's blocks, one list per host (a plugin definition serves every editor and renderer):
 * the same plugin ids as a page in its static („history“) policy, which keeps the stored snapshots of contacts and pages and allows
 * no OpenAPI requests, plus the code tools and YouTrack with the host's client. Without password references (reserved, spec §3.6):
 * a block cannot render, keep or take in one, and the server refuses it anyway.
 */
export function boardBlockPlugins(host: BoardBlockHost): MarkdownPlugin[] {
  let plugins = pluginSets.get(host);
  if (!plugins) {
    plugins = [createEditorToolsPlugin(), ...createDocumentWidgets({ kind: "history" }).filter(plugin => plugin.id !== "secrets"),
      createYouTrackPlugin({ client: host.youTrack })];
    pluginSets.set(host, plugins);
  }
  return plugins;
}

const textPluginSets = new WeakMap<BoardBlockHost, MarkdownPlugin[]>(), fieldPluginSets = new WeakMap<BoardBlockHost, MarkdownPlugin[]>();
/**
 * The page editor's content plugins of a board's texts and blocks (editing on a board is editing a page; one editor),
 * one list per host, read and written by the same editors: those of a page in its editing
 * policy behind the host's `canWrite`, with `[[` page links from the host and YouTrack from its client, and without password
 * references. `@` mentions, contacts and recognised names with the host's people (a board's space, a page's comments), as a page has
 * them. `boardBlockPlugins` stays the static renderer's list (BoardMarkdown on the surfaces outside the board).
 */
export function boardTextPlugins(host: BoardBlockHost, { entities = true }: { entities?: boolean } = {}): MarkdownPlugin[] {
  // An entity's own field (`entities: false`, a contact's name): the same plugins without any people (no `@`, no search, no chips), so
  // recognition never runs inside the entity it would link to.
  const sets = entities ? textPluginSets : fieldPluginSets;
  let plugins = sets.get(host);
  if (!plugins) {
    plugins = [createEditorToolsPlugin(),
      ...createDocumentWidgets({ kind: "edit", ...(host.canWrite ? { canWrite: host.canWrite } : {}), ...(host.pages ? { pages: host.pages } : {}), ...(entities && host.people) })
        .filter(plugin => plugin.id !== "secrets"),
      createYouTrackPlugin({ client: host.youTrack })];
    sets.set(host, plugins);
  }
  return plugins;
}

let textReader: { editor: Editor; plugins: MarkdownRichValue["plugins"] } | undefined;
/**
 * The rich document of a board text's Markdown (spec §3.4), read as the page editor reads Markdown (`parseMarkdownContent`, the
 * editor's dialect included), by one detached editor of the board's configuration kept for it; with the plugin metadata, its own (the
 * board's: the same plugins; a second editor built only for them was a third of a first edit's cost).
 */
export function boardTextDocument(markdown: string): MarkdownRichValue {
  if (!markdown.trim()) return emptyDocument();
  if (!textReader) {
    let editor: Editor | undefined;
    const controller = createMarkdownEditorController({ profile: "rich", source: { kind: "local", value: emptyDocument() },
      plugins: [...boardBlockPlugins(inertBlockHost), { id: "atlas-board-text-reader", apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; } }) }] });
    controller.attachView(document.createElement("div"), { ariaLabel: t.block.readText });
    textReader = { editor: editor!, plugins: controller.schema.plugins };
  }
  const parsed = parseMarkdownContent(textReader.editor, markdown).toJSON() as MarkdownRichValue["document"];
  return { format: "atlas-rich", version: 1, plugins: textReader.plugins, document: parsed };
}

/** Runs `use` on a detached editor holding an empty document, with `extra` plugins, and destroys it. */
function headless<T>(use: (controller: MarkdownEditorController<"rich">) => T, extra: MarkdownPlugin[] = []): T {
  const controller = createMarkdownEditorController({ profile: "rich", source: { kind: "local", value: emptyDocument() },
    plugins: [...boardBlockPlugins(inertBlockHost), ...extra] });
  try {
    controller.attachView(document.createElement("div"), { ariaLabel: t.block.view });
    return use(controller);
  } finally { controller.destroy(); }
}

let modules: readonly EditorBlockModule[] | undefined;
/** The components a board can insert: the plugins' slash actions, then the editor's own blocks (callout, table, code…). */
export function useEditorBlockModules(): readonly EditorBlockModule[] {
  return (modules ??= Object.freeze(toBlockModules([
    ...headless(controller => controller.actions.slash),
    ...slashItems.map(item => ({ id: item.id, label: item.title, description: item.description, icon: item.icon, keywords: item.searchTerms, command: item.commandValue })),
  ])));
}

/**
 * The node the slash menu inserts for `module` with the plugin's defaults, as the editor itself makes it (so its normalisation
 * applies); null for a module that opens a picker instead (YouTrack) or inserts nothing. A board inserts such a module by opening
 * `BoardBlock` with `insert`, where the plugin's own picker runs.
 */
export function defaultBlockNode(module: EditorBlockModule): BoardBlockNode | null {
  const command = module.command;
  if (!command) return null;
  return headless(controller => controller.dispatch(command) ? firstBlock(controller.getValue().document as DocumentNode) : null);
}

let board: { schema: Schema; plugins: readonly MarkdownSchemaPlugin[] } | undefined;
/** The schema and plugin metadata of the board's editor, read once from a detached editor (a plugin's create hook sees the schema). */
function boardEditor() {
  if (!board) {
    let schema: Schema | undefined;
    const plugins = headless(controller => controller.schema.plugins,
      [{ id: "atlas-board-schema", apiVersion: 1, create: () => ({ onCreate: ({ editor }) => { schema = editor.schema; } }) }]);
    board = { schema: schema!, plugins };
  }
  return board;
}
const boardSchema = () => boardEditor().schema;

/** Whether the board's editor knows the node's type: a block of a plugin this version lacks renders as a fallback that keeps it. */
export function isKnownNode(node: BoardBlockNode): boolean {
  return !!boardSchema().nodes[node.type];
}

/**
 * The block to render or edit for a stored node: an inline node of the editor (a YouTrack issue, a mention) stands in a paragraph,
 * as in a page and as the server validates it; a block, also one of a plugin this editor does not know, stays as it is.
 */
export function asBlockNode(node: BoardBlockNode): BoardBlockNode {
  return boardSchema().nodes[node.type]?.isInline ? { type: "paragraph", content: [node] } : node;
}

/** What a board stores of an edited block: a paragraph that only carries one inline component is that component (as pickers store it). */
export function asStoredNode(node: BoardBlockNode): BoardBlockNode {
  const only = node.type === "paragraph" && node.content?.length === 1 ? node.content[0] as BoardBlockNode : undefined;
  return only && only.type !== "text" && boardSchema().nodes[only.type]?.isInline ? only : node;
}
