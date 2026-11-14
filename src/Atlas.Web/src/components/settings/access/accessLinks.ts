import { spaceSettingsHash } from "../../../settingsRoutes";
import { pageAccessRoute } from "../../sharing/pageAccessRoute";
import type { SettingsRouter } from "../useSettingsRoute";
import type { AccessRow } from "./accessView";

/**
 * Where a row of a person's card leads in Nastavení wiki: a space to its page in the security centre, a vault to its
 * Přístupy in the space settings, a page to its Kdo má přístup, a plugin resource to its kind's page, the wiki role and
 * API tokens to the card's own tabs. `spaceOf` gives the space of a row (the card knows it from the entry).
 */
export function globalRowHref(router: SettingsRouter, cardPath: string, spaceOf: (row: AccessRow) => string | null | undefined) {
  return (row: AccessRow): string | undefined => {
    const space = spaceOf(row);
    switch (row.kind) {
      case "wiki":
        return router.href(`${cardPath}/account`);
      case "token":
        return router.href(`${cardPath}/tokens`);
      case "space":
        return router.href(`access/spaces/${row.resourceId}`);
      case "vault":
        return space ? spaceSettingsHash(space, `vaults/${row.resourceId}/access`) : undefined;
      case "page":
        return pageAccessRoute(row.resourceId);
      case "secret":
        return undefined;
      case "youtrack":
        return router.href("access/youtrack");
      default:
        return router.href(`access/${row.kind}/${row.resourceId}`);
    }
  };
}
