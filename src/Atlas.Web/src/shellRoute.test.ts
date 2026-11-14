// @vitest-environment node
import { describe, expect, it } from "vitest";
import { buildShellHash, buildShellHashEncoded, parseShellHash, type ShellRoute } from "./shellRoute";

const home: ShellRoute = { view: "home" };

describe("shell route grammar", () => {
  it("reads every address form and writes the same address back", () => {
    const forms: [string, ShellRoute][] = [
      ["#/", home],
      ["#/library/favorites", { view: "library", tab: "favorites" }],
      ["#/library/updates", { view: "library", tab: "updates" }],
      ["#/spaces/space-1", { view: "space", spaceId: "space-1" }],
      ["#/spaces/space-1/about", { view: "space", spaceId: "space-1", tab: "about" }],
      ["#/spaces/space-1/boards", { view: "space", spaceId: "space-1", tab: "boards" }],
      ["#/spaces/space-1/deployments", { view: "space", spaceId: "space-1", tab: "deployments" }],
      ["#/spaces/space-1/waiting", { view: "space", spaceId: "space-1", tab: "waiting" }],
      ["#/boards/0a1b2c3d-0000-0000-0000-000000000009", { view: "board", boardId: "0a1b2c3d-0000-0000-0000-000000000009" }],
      ["#/new-space", { view: "spaceCreation" }],
      ["#/whats-new", { view: "whatsNew" }],
      ["#/spaces/space-1/new-page", { view: "pageCreation", spaceId: "space-1", kind: "new-page" }],
      ["#/spaces/space-1/new-template", { view: "pageCreation", spaceId: "space-1", kind: "new-template" }],
      ["#/spaces/space-1/new-board", { view: "pageCreation", spaceId: "space-1", kind: "new-board" }],
      ["#/spaces/space-1/page-templates", { view: "pageTemplates", spaceId: "space-1" }],
      ["#/pages/0a1b2c3d-0000-0000-0000-000000000002", { view: "page", pageId: "0a1b2c3d-0000-0000-0000-000000000002" }],
      ["#/pages/page-1?heading=pristupy", { view: "page", pageId: "page-1", heading: "pristupy" }],
      ["#/pages/page-1?block=b-1", { view: "page", pageId: "page-1", block: "b-1" }],
      ["#/pages/page-1/access", { view: "pageAccess", pageId: "page-1", tab: "access" }],
      ["#/pages/page-1/access/links", { view: "pageAccess", pageId: "page-1", tab: "links" }],
      ["#/pages/page-1/access/history", { view: "pageAccess", pageId: "page-1", tab: "history" }],
      ["#/profile/security", { view: "profile", tab: "security" }],
      ["#/settings/people/groups", { view: "settings", settings: { scope: "global", hash: "#/settings/people/groups", redirect: false } }],
      ["#/spaces/provoz/settings/vaults?stav=locked", { view: "settings", settings: { scope: "space", spaceId: "provoz", hash: "#/spaces/provoz/settings/vaults?stav=locked", redirect: false } }],
    ];
    for (const [hash, route] of forms) {
      expect(parseShellHash(hash), hash).toEqual(route);
      expect(buildShellHash(route), hash).toBe(hash);
      // The parser accepts only ids that percent-encoding leaves alone, so both writers agree on them.
      expect(buildShellHashEncoded(route), hash).toBe(hash);
    }
  });

  it("reads no tab or flow outside the closed sets", () => {
    for (const hash of ["#/library", "#/library/trash", "#/profile/billing", "#/profile/ai/x", "#/spaces/s/new-space", "#/spaces/s/abouts", "#/spaces/s/about/x", "#/boards", "#/boards/b/items"])
      expect(parseShellHash(hash), hash).toEqual(home);
  });

  it("brings old and tolerated spellings to the canonical address", () => {
    // The bare #/profile of older links is the account section; a trailing slash is tolerated here and on two more forms.
    for (const hash of ["#/profile", "#/profile/"]) expect(buildShellHash(parseShellHash(hash)), hash).toBe("#/profile/account");
    expect(buildShellHash(parseShellHash("#/profile/ai/"))).toBe("#/profile/ai");
    expect(buildShellHash(parseShellHash("#/pages/p/access/"))).toBe("#/pages/p/access");
    expect(buildShellHash(parseShellHash("#/pages/p/access/history/"))).toBe("#/pages/p/access/history");
    // The other forms take no trailing slash.
    for (const hash of ["#/pages/p/", "#/spaces/s/", "#/library/spaces/", "#/new-space/", "#/spaces/s/new-page/", "#/spaces/s/about/"]) expect(parseShellHash(hash), hash).toEqual(home);
    // An old settings link carries the address it is redirected to; the settings grammar itself stays in settingsRoutes.ts.
    expect(parseShellHash("#/settings")).toEqual({ view: "settings", settings: { scope: "global", hash: "#/settings/security/overview", redirect: true } });
    expect(buildShellHash(parseShellHash("#/settings/users"))).toBe("#/settings/people/accounts");
    expect(buildShellHash(parseShellHash("#/spaces/provoz/settings"))).toBe("#/spaces/provoz/settings/general");
  });

  it("reads a board id in lower case, as the server names the board's live channel", () => {
    expect(parseShellHash("#/boards/0A1B2C3D-0000-0000-0000-00000000000A")).toEqual({ view: "board", boardId: "0a1b2c3d-0000-0000-0000-00000000000a" });
    expect(buildShellHash(parseShellHash("#/boards/0A1B2C3D-0000-0000-0000-00000000000A"))).toBe("#/boards/0a1b2c3d-0000-0000-0000-00000000000a");
  });

  it("tells a view from the id or the look-alike next to it", () => {
    // An id may spell a keyword of the grammar.
    expect(parseShellHash("#/spaces/settings")).toEqual({ view: "space", spaceId: "settings" });
    expect(parseShellHash("#/spaces/new-page")).toEqual({ view: "space", spaceId: "new-page" });
    expect(parseShellHash("#/spaces/about")).toEqual({ view: "space", spaceId: "about" });
    expect(parseShellHash("#/spaces/about/about")).toEqual({ view: "space", spaceId: "about", tab: "about" });
    expect(parseShellHash("#/pages/access")).toEqual({ view: "page", pageId: "access" });
    // Below a space, settings and the creation flows are views of their own, also when a settings path is named like a flow.
    expect(parseShellHash("#/spaces/s/settings")).toMatchObject({ view: "settings", settings: { scope: "space", spaceId: "s", redirect: true } });
    expect(parseShellHash("#/spaces/settings/settings/new-page")).toMatchObject({ view: "settings", settings: { spaceId: "settings", redirect: false } });
    expect(parseShellHash("#/spaces/new-page/new-page")).toEqual({ view: "pageCreation", spaceId: "new-page", kind: "new-page" });
    for (const hash of ["#/settings-preview/overview", "#/spaces/s/settings-preview", "#/spaces/s/new-pages", "#/profiles", "#/mcp/publish", "#/mcp/publish/", "#/mcp/publish/a/b", "#/mcp/publish/r1"])
      expect(parseShellHash(hash), hash).toEqual(home);
  });

  it("opens the page for a part below it that the shell does not know, as #/pages/<id>/history", () => {
    for (const hash of ["#/pages/p/history", "#/pages/p/garbage", "#/pages/p/accessible", "#/pages/p/access/access", "#/pages/p/access/people", "#/pages/p/history/3"])
      expect(parseShellHash(hash), hash).toEqual({ view: "page", pageId: "p" });
    expect(buildShellHash(parseShellHash("#/pages/p/history"))).toBe("#/pages/p");
  });

  it("treats an empty, unknown or malformed address as home", () => {
    for (const hash of ["", "#", "#/unknown", "#/gallery/ai", "#/pages", "#/pages/", "#/spaces/", "#pages/p", "/pages/p", "#/PAGES/p", "#/pages/a b", "#/pages/a%20b", "#/pages/a_b", "#/pages/č"])
      expect(parseShellHash(hash), hash).toEqual(home);
  });

  it("reads a heading from the query of a page address only", () => {
    const heading = "Přístupy & hesla #1 = 100 %";
    const hash = buildShellHash({ view: "page", pageId: "p", heading });
    expect(hash).toBe("#/pages/p?heading=P%C5%99%C3%ADstupy%20%26%20hesla%20%231%20%3D%20100%20%25");
    expect(parseShellHash(hash)).toEqual({ view: "page", pageId: "p", heading });
    // A heading that cannot be meant opens the page without it: not decodable, a control character, or over 200 characters.
    for (const query of ["%E0%A4%A", "a%0Ab", "x".repeat(201)]) expect(parseShellHash(`#/pages/p?heading=${query}`), query).toEqual({ view: "page", pageId: "p" });
    expect(parseShellHash(`#/pages/p?heading=${"x".repeat(200)}`)).toEqual({ view: "page", pageId: "p", heading: "x".repeat(200) });
    // Nothing but one non-empty heading is a query of the shell.
    for (const hash of ["#/pages/p?heading=", "#/pages/p?heading=a&x=1", "#/pages/p?x=1", "#/pages/p?", "#/pages/p/access?heading=a", "#/library/recent?heading=a"])
      expect(parseShellHash(hash), hash).toEqual(home);
    // A space has no headings; the parameter is dropped.
    expect(parseShellHash("#/spaces/s?heading=a")).toEqual({ view: "space", spaceId: "s" });
    expect(buildShellHash({ view: "page", pageId: "p", heading: "" })).toBe("#/pages/p");
  });

  it("writes an id that is not an address in two ways, neither of which it reads back", () => {
    const route: ShellRoute = { view: "page", pageId: "a b/č?x#y", heading: "a b" };
    expect(buildShellHash(route)).toBe("#/pages/a b/č?x#y?heading=a%20b");
    expect(buildShellHashEncoded(route)).toBe("#/pages/a%20b%2F%C4%8D%3Fx%23y?heading=a%20b");
    expect(parseShellHash(buildShellHash(route))).toEqual(home);
    expect(parseShellHash(buildShellHashEncoded(route))).toEqual(home);
    expect(buildShellHashEncoded({ view: "space", spaceId: "a/b" })).toBe("#/spaces/a%2Fb");
  });
});
