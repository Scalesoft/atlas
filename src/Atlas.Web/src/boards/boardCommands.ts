import type { MarkdownIconName } from "@atlas/markdown/icons";
import { rankExactFirst } from "../lib/rankExactFirst";
import type { BoardItem } from "./boardModel";
import type { EditorBlockModule } from "./editorBlocks";
import t from "../i18n/boards";

/**
 * The entries of the board's command panel (objects spec §5.2): one searchable list of what the board can do here. Creating comes
 * from the board's own kinds, components from the page editor's slash registry (`useEditorBlockModules`, so a new plugin appears
 * without a change here), placing and the selection's actions from the board. The panel itself is the editor's suggestion list
 * (BoardCommandPanel); the canvas runs the chosen id. Pure, no Vue.
 */
export type CommandSection = "selection" | "create" | "component" | "place" | "board" | "find";
export interface BoardCommand {
  id: string;
  title: string;
  description?: string;
  /** A name of the editor's icon registry, as the slash menu draws it. */
  icon: MarkdownIconName;
  keywords: readonly string[];
  section: CommandSection;
}
export interface CommandContext {
  canEdit: boolean;
  selection: readonly BoardItem[];
  modules: readonly EditorBlockModule[];
  /** Which pickers and services the host gave the canvas; `links`: the board's space for its derived lists, `waiting` with Projector. */
  /** `arrange`: the host lays out the selection's cards, or with no selection some of the board's („Uspořádat jako architekturu“, BoardBlockHost.layout). */
  can: { pickPage: boolean; pickIssues: boolean; pickContacts: boolean; createPage: boolean; links?: boolean; waiting?: boolean; arrange?: boolean };
}

const command = (section: CommandSection, id: string, title: string, icon: MarkdownIconName, keywords: string[] = [], description?: string): BoardCommand =>
  ({ id, title, icon, keywords, section, ...(description && { description }) });

const alignHows = ["left", "center", "right", "top", "middle", "bottom"] as const;

function selectionCommands({ selection, can }: CommandContext): BoardCommand[] {
  const cards = selection.filter(item => item.kind !== "connector");
  const c = t.commands;
  const single = selection.length === 1 ? selection[0] : undefined;
  if (!selection.length) return [];
  return [
    ...(cards.length > 1 ? [command("selection", "selection:group", c.group.title, "folder-plus", c.group.words, "Ctrl+G")] : []),
    ...(selection.some(item => item.kind === "group") ? [command("selection", "selection:ungroup", c.ungroup.title, "folder-input", c.ungroup.words, "Ctrl+Shift+G")] : []),
    ...(single?.kind === "group" ? [single.props?.collapsed
      ? command("selection", "selection:expand", c.expand.title, "chevrons-up-down", c.expand.words)
      : command("selection", "selection:collapse", c.collapse.title, "chevrons-down-up", c.collapse.words)] : []),
    ...(cards.length > 1 ? alignHows.map(how => command("selection", `selection:align-${how}`, c.align[how], how === "center" ? "align-center" : how === "right" ? "align-right" : "align-left", c.alignWords)) : []),
    ...(can.arrange ? [command("selection", "board:arrange", c.arrange.selection, "layout-dashboard", c.arrange.words, c.arrange.description)] : []),
    command("selection", "selection:duplicate", c.duplicate.title, "copy", c.duplicate.words, "Ctrl+D"),
    ...(single?.kind === "note" && can.createPage ? [command("selection", "selection:to-page", c.toPage.title, "file-plus", c.toPage.words)] : []),
    command("selection", "selection:delete", onlyCards(selection) ? c.removeFromBoard : c.delete.title, "trash-2", c.delete.words, "Delete"),
  ];
}

/** The item opens a detail in the dock: a page card (the board's object detail), an object card but a board's (ContentDetail, a contact's card, Umí, Použito na, Diskuse). */
export const opensDetail = (item: BoardItem | undefined) =>
  !!item?.targetResourceId && (item.kind === "page" || (item.kind === "object" && (item.props as { typeId?: unknown } | undefined)?.typeId !== "atlas.board"));
const isContactCard = (item: BoardItem | undefined) => item?.kind === "object" && (item.props as { typeId?: unknown } | undefined)?.typeId === "atlas.contact";

/** P12: cards (a page, board, requirement or contact placed here) are removed from the board, their objects stay („Odebrat“); the board's own items are deleted („Smazat“). */
export const onlyCards = (selection: readonly BoardItem[]) => selection.length > 0 && selection.every(item => item.kind === "page" || item.kind === "object");

/** Everything the panel offers in the context, the selection's actions first; a reader gets only what changes nothing. */
export function boardCommands(context: CommandContext): BoardCommand[] {
  const { canEdit, selection, modules, can } = context;
  const c = t.commands;
  const single = selection.length === 1 ? selection[0] : undefined;
  const detail = opensDetail(single) ? [command("selection", "selection:detail", c.openDetail.title, "panel-right-close", c.openDetail.words)] : [];
  const board = [
    ...(canEdit && can.arrange && !selection.length ? [command("board", "board:arrange", c.arrange.title, "layout-dashboard", c.arrange.words, c.arrange.description)] : []),
    command("board", "board:settings", c.settings.title, "settings-2", c.settings.words),
    command("board", "board:detail", c.boardDetail.title, "info", c.boardDetail.words),
  ];
  if (!canEdit) return [...detail, ...board];
  return [
    ...selectionCommands(context).flatMap(entry => (entry.id === "selection:delete" ? [...detail, entry] : [entry])),
    command("create", "create:note", c.note.title, "pin", c.note.words, "N"),
    command("create", "create:text", c.text.title, "type", c.text.words, "T"),
    command("create", "create:shape", c.shape.title, "box", c.shape.words, "S"),
    command("create", "create:frame", c.frame.title, "layout-dashboard", c.frame.words, "F"),
    // The pickers stand in for the plugins' own entries of the same blocks, as in the tool rail (one entry per use case).
    ...(can.pickIssues ? [command("component", "pick:youtrack", c.youtrack.title, "ticket", c.youtrack.words, "I")] : []),
    ...(can.pickContacts ? [command("component", "pick:contact", c.contact.title, "user", c.contact.words, "C")] : []),
    ...modules.filter(entry => !(can.pickIssues && entry.id === "youtrack.search") && !(can.pickContacts && entry.id === "contacts.insert"))
      .map(entry => ({ ...command("component", `component:${entry.id}`, entry.label, entry.icon as MarkdownIconName, [...entry.keywords]), ...(entry.description && { description: entry.description }) })),
    // Lists derived from the relations (P15): the board composes them, nobody writes them by hand.
    ...(can.links ? [
      command("component", "links:people", c.people.title, "users", c.people.words, c.people.description),
      ...(can.waiting ? [command("component", "links:waiting", c.waiting.title, "hourglass", c.waiting.words, c.waiting.description)] : []),
      command("component", "links:usage", c.usage.title, "link", c.usage.words, c.usage.description),
    ] : []),
    ...(can.pickPage ? [command("place", "place:page", c.placePage.title, "file-text", c.placePage.words, "P")] : []),
    ...board,
  ];
}

/** What the one selected item offers beside the selection's actions: its editing in place, what it opens, a link to copy. */
export interface ItemOffer {
  /** Enter, F2 and a double-click start its editing. */
  edit?: boolean;
  /** The words of „Otevřít“ (a page, a board, the page of a requirement); Ctrl+Enter does it. */
  open?: string;
  /** It has an address of its own. */
  link?: boolean;
}

/**
 * The rows of „Co s tím jde dělat“ on the selection (P2; right button, Ctrl+Space, Alt+Enter): its editing and opening first (P1:
 * opening is a named action, never Enter), then the selection's actions of the panel, the same ids. A reader gets only what changes nothing.
 */
export function actionCommands(context: CommandContext, offer: ItemOffer): BoardCommand[] {
  const single = context.selection.length === 1 ? context.selection[0] : undefined;
  const c = t.commands;
  const detail = opensDetail(single) ? [command("selection", "selection:detail", c.openDetail.title, "panel-right-close", c.openDetail.words, offer.open ? undefined : "Ctrl+Enter")] : [];
  // A contact card's business card and QR code: the toolbar's actions as rows (P2), for a reader too (they change nothing).
  const contact = isContactCard(single) ? [command("selection", "selection:vcard", t.toolbar.contactVcf, "download"),
    command("selection", "selection:qr", t.toolbar.contactQr, "qr-code")] : [];
  return [
    ...(offer.edit ? [command("selection", "selection:edit", c.edit.title, "pencil", c.edit.words, "Enter")] : []),
    ...(offer.open ? [command("selection", "selection:open", offer.open, "external-link", c.open.words, "Ctrl+Enter")] : []),
    ...detail,
    ...contact,
    ...(offer.link ? [command("selection", "selection:copy-link", c.copyLink.title, "link", c.copyLink.words)] : []),
    ...(context.canEdit ? selectionCommands(context) : []),
  ];
}

/** The commands matching what was typed, as the slash menu ranks its items: the exact title, the title's start, then words, keywords, inside. */
export function findCommands(query: string, commands: readonly BoardCommand[]): BoardCommand[] {
  return rankExactFirst(query, commands, entry => ({ title: entry.title, keywords: entry.keywords }));
}

/** Where each kind of item is named in the finder (Najít na tabuli): the editor's icon of what it is. */
const findIcons: Partial<Record<BoardItem["kind"], MarkdownIconName>> = { note: "pin", text: "type", shape: "box", frame: "layout-dashboard", group: "folder-plus", page: "file-text", object: "user", block: "layout-dashboard", links: "link", connector: "arrow-right" };
/**
 * Every item of the board as an entry for the panel's search („Najít na tabuli“, BD2-7): the id is `find:<item>`, the title is the
 * item's accessible name. The panel lists them only for a typed query, after the board's own commands. `label` names an item
 * (boardFlow's itemLabel, a drawn connector's accessible name, or what a card's object says); items without one are left out.
 * A connector is found by its ends and its label: the keyboard's way to select one (review 3.5.1, finding 9).
 */
export function itemCommands(items: readonly BoardItem[], label: (item: BoardItem) => string | undefined): BoardCommand[] {
  return items.flatMap(item => {
    const title = item.deleted ? undefined : label(item);
    return title ? [command("find", `find:${item.id}`, title, findIcons[item.kind] ?? "pin", [], t.commands.find)] : [];
  });
}
