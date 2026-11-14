import { buildShellHash, type PageAccessTab } from "../../shellRoute";

/**
 * The page access view („Kdo má přístup“): `#/pages/<id>/access`, with its other parts at `/links` and
 * `/history`. Each part is its own address, so SettingsTabs are real links and Back returns to the part.
 * The shell's grammar (`shellRoute.ts`) lists the parts and reads and writes the address.
 */
export type { PageAccessTab };

export function pageAccessRoute(pageId: string, tab: PageAccessTab = "access") {
  return buildShellHash({ view: "pageAccess", pageId, tab });
}
