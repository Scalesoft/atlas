type ActionsMenu = typeof import("./boardActionsMenu");
let loaded: ActionsMenu | undefined;

/** Loads the chunk of the board's actions menu (boardActionsMenu.ts): when an editable board idles, else for the first menu. */
export const loadActionsMenu = (): Promise<ActionsMenu> => import("./boardActionsMenu").then(module => (loaded = module));
/** The chunk once it is in: the menu then opens in the same task as its key or click (P17). */
export const actionsMenuReady = (): ActionsMenu | undefined => loaded;
