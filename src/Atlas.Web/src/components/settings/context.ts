import { inject, provide, type InjectionKey } from "vue";
import type { Space } from "../../api";
import type { SpaceSettings } from "../../spaceSettings";

/**
 * What the application shell hands to a settings surface: who is signed in, the spaces they see,
 * the backup warning, and callbacks back into the shell (a space was renamed, a module imported a
 * space, access changed). Pages read it; primitives never do.
 */
export interface SettingsContext {
  readonly userId: string;
  readonly isWikiAdmin: boolean;
  readonly spaces: Space[];
  readonly backupWarning: string | null;
  readonly version: string | undefined;
  /** The wiki has the MCP server on (`features.mcp`): the space AI section is then offered even without AI. */
  readonly mcpEnabled?: boolean;
  imported(spaceId: string): void;
  warning(value: string | null): void;
  /** Open a page (a template page from Šablony stránek) in the wiki workspace; `pdfExport` also opens its automatic PDF export (Úložiště). */
  openPage(pageId: string, pdfExport?: boolean): void;
  /** Start the new-page flow in template mode for a space. */
  newTemplate(spaceId: string): void;
  /** Start the new-page flow for a space, optionally with the title already filled in (a query nobody found). */
  newPage(spaceId: string, options?: { title?: string }): void;
  spaceChanged(value: SpaceSettings): void;
  /** The space went to the trash (Obecné → Přesunout prostor do koše): it leaves every list and its settings close. */
  spaceTrashed(spaceId: string): void;
  /** Spaces came back (Koš prostorů): the shell reads its list again. */
  spacesChanged(): void;
  accessChanged(): void;
  /**
   * Open Správa wiki from "Správa wiki" in space settings, so that its way back leads to these settings: the shell
   * cannot tell that link from the browser's own way back by the address alone. Without a shell the link is a plain link.
   */
  openWikiSettings?(): void;
  /** Open the space's „Koš stránek“ (the restore view of the space menu) from its settings. */
  openTrash?(spaceId: string): void;
}

const key: InjectionKey<SettingsContext> = Symbol("settings-context");

export function provideSettingsContext(context: SettingsContext) {
  provide(key, context);
}
export function useSettingsContext(): SettingsContext {
  const context = inject(key, undefined);
  if (!context) throw new Error("useSettingsContext() needs provideSettingsContext() in an ancestor");
  return context;
}
/** Spaces whose settings (and vaults) the caller manages. The shell, which has no settings context, passes the two facts it reads. */
export function managedSpaces(context: Pick<SettingsContext, "spaces" | "isWikiAdmin">) {
  return context.spaces.filter((space) => !space.itemAccessOnly && (context.isWikiAdmin || space.role === "Admin"));
}
