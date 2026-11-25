import type { MarkdownIntentionsMenu } from "@atlas/markdown/core";
import { editorThemeClass } from "../../../editor/editorSetup";
import { whenIdle } from "../../../idle";
import type { ActionMenuItem } from "../../ActionMenu.vue";
import { atlasIconNames } from "../../atlasIconNames";

type ActionsMenu = typeof import("./boardActionsMenu");
let loaded: ActionsMenu | undefined;

/** Loads the chunk of the board's actions menu (boardActionsMenu.ts): when an editable board idles, else for the first menu. */
export const loadActionsMenu = (): Promise<ActionsMenu> => import("./boardActionsMenu").then(module => (loaded = module));
/** The chunk once it is in: the menu then opens in the same task as its key or click (P17). */
export const actionsMenuReady = (): ActionsMenu | undefined => loaded;
/** Loads it when the page idles, so a row's first menu opens at once too (a tree or a list with row menus calls it on mount). */
let preheating = false;
export const preheatActionsMenu = (): void => {
  if (loaded || preheating) return;
  preheating = true;
  whenIdle(() => void loadActionsMenu().catch(() => undefined));
};

let rowMenu: MarkdownIntentionsMenu | undefined;
let rowLoading = false;
/** Counts the row menus asked for: a load that a later gesture or a close overtook opens nothing. */
let rowRun = 0;
/** A row's menu is open or on its way: the menu key's own `contextmenu` (sent after its keydown opened it) opens no second one. */
export const rowActionsMenuOpen = (): boolean => !!rowMenu || rowLoading;
export function closeRowActionsMenu(): void { rowRun++; rowLoading = false; rowMenu?.close(); }
/** A row of an ActionMenuItem list as a row of the editor's menu: its icon by the app's name, its group parted by a line. */
const intentions = (items: readonly ActionMenuItem[], select: (id: string) => void) => items.map((item, index) => ({
  id: item.id, label: item.label, run: () => select(item.id),
  ...item.icon && { icon: `svg:${Object.hasOwn(atlasIconNames, item.icon) ? atlasIconNames[item.icon] : item.icon}` },
  ...item.shortcut && { shortcut: item.shortcut }, ...item.checked && { checked: true }, ...item.destructive && { destructive: true },
  ...item.disabled && { disabled: true }, ...index > 0 && item.group !== items[index - 1]!.group && { separated: true },
}));

/**
 * „Co s tím jde dělat“ of a tree or list row (P2): the page editor's own menu (openMarkdownIntentionsMenu, as the board opens it) at
 * `anchor`, in the editor's theme, with the row's items and named by `label`; typing filters them. `host` is an element in the row: the menu follows its
 * scrolling and stays in its dialog's top layer. While it is open the document's keys go to it first and the focus stays where it is.
 * `onClose` runs before a chosen row's action, so an action that moves the focus (a dialog, renaming) has the last word. One row menu at
 * a time: a new one closes the last; the first opens once the chunk is in, unless another gesture or a close came meanwhile.
 */
export function openRowActionsMenu(options: { items: readonly ActionMenuItem[]; label: string; anchor: () => DOMRect; host: Element; select: (id: string) => void; onClose?: () => void }): void {
  closeRowActionsMenu();
  const run = rowRun;
  const show = (chunk: ActionsMenu) => {
    if (run !== rowRun || !options.host.isConnected) return;
    rowLoading = false;
    const root = document.documentElement;
    const theme = document.createElement("span");
    theme.className = `md-editor atlas-document-editor ${editorThemeClass(root.dataset.mode === "dark", root.dataset.accent)}`;
    theme.hidden = true;
    options.host.append(theme);
    const keys = (event: KeyboardEvent) => { if (!event.isComposing && menu.key(event)) { event.preventDefault(); event.stopPropagation(); } };
    const menu = chunk.openActionsMenu({ document, label: options.label, anchor: options.anchor, contextElement: theme, rows: [intentions(options.items, options.select)],
      onClose: () => {
        document.removeEventListener("keydown", keys, true);
        theme.remove();
        if (rowMenu === menu) rowMenu = undefined;
        options.onClose?.();
      } });
    rowMenu = menu;
    document.addEventListener("keydown", keys, true);
  };
  if (loaded) show(loaded);
  else { rowLoading = true; void loadActionsMenu().then(show, () => { if (run === rowRun) rowLoading = false; }); }
}
