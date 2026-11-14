// Editing a page in Atlas 3.6: there is no Upravit, Uložit koncept or
// Hotovo any more, and no formatting row. A page is ONE ProseMirror editor, editable from its first frame for a writer (a click puts the
// caret there, typing works at once); it saves by itself (SaveState: Ukládám… / Uloženo); Publikovat publishes; „Uložit verzi“ (Ctrl+S)
// names a version. The lock beside Publikovat (`e`) makes the same editor read-only for its writer, which a touch screen starts with.
// Formatting: the selection bubble (data-testid="bubble-*", Alt+F10), the `/` menu, the block handle and the keys.
import { expect, type Locator, type Page } from "@playwright/test";

/** The editor taking input: contenteditable, and not the hidden shared editor still building behind the local one (.editor-hydrating,
 * only for a page whose live state is not yet stored). */
export const LIVE_EDITOR = '.document-body .md-editor:not(.editor-hydrating) .ProseMirror[contenteditable="true"]';
export const liveEditor = (page: Page) => page.locator(LIVE_EDITOR);
/** The page's text as shown: the editor (read-only while locked or while the page's live state is still being built), never the hidden
 * editor building behind it (.editor-hydrating): it lies at the same place and counts as visible, but takes no click or key. */
export const PAGE_TEXT = ".document-body .ProseMirror:not(.editor-hydrating *)";

export interface SessionLease { documentId: string; generation: string; currentVersion: number }
/** The live session the page opens with: 3.6 names it in the page bundle (`GET /api/pages/{id}?bundle=true`, no lease request);
 * only without a bundled state (or for a new generation) the editor joins through `POST /api/pages/{id}/collaboration`. Call before
 * the navigation. */
export function sessionLease(page: Page, id: string): Promise<SessionLease> {
  const joined = page.waitForResponse(response => response.url().endsWith(`/api/pages/${id}/collaboration`) && response.request().method() === "POST");
  joined.catch(() => {});
  const read = page.waitForResponse(response => new URL(response.url()).pathname === `/api/pages/${id}` && response.url().includes("bundle=true"));
  return (async () => {
    const response = await read;
    expect(response.status()).toBe(200);
    const bundled = (await response.json()).bundle?.collaboration;
    if (bundled) return { documentId: bundled.documentId, generation: bundled.generation, currentVersion: bundled.currentVersion };
    const join = await joined;
    expect(join.status()).toBe(200);
    return join.json();
  })();
}

/** Clicks into the page's text, near the start of its first text block (an empty page: its empty line), and waits for the editable editor
 * to have the focus. `within` narrows it to text inside a part of the page (a block, a section). */
export async function startEditing(page: Page, within?: Locator) {
  const text = (within ?? page.locator(PAGE_TEXT).first()).locator("p, h1, h2, h3, h4, li").first();
  await expect(text).toBeVisible();
  // A few pixels into its text: the middle of a line may be a link or a chip, which open instead of placing the caret; a requirement's
  // title starts past its padding, the check's room (3.5.1).
  // A requirement heading starts with its round check (the state), so the caret goes past the check's right edge, never onto it.
  const left = await text.evaluate(element => {
    const check = element.querySelector(":scope > .rq-check")?.getBoundingClientRect().right;
    return check ? check + 6 - element.getBoundingClientRect().left : parseFloat(getComputedStyle(element).paddingLeft) || 0;
  });
  await text.click({ position: { x: left + 3, y: 6 } });
  await expect(liveEditor(page)).toBeFocused();
}

/** The page's own save state (SaveState in the header) once the last changes are stored: what „Koncept uložen.“ was. */
export async function expectSaved(page: Page) {
  // The state, not the text: right after a change the badge still shows „Uloženo“ while it leaves (SaveState's out-in transition).
  const state = page.locator("[data-page-save-state]");
  await expect(state).toHaveAttribute("data-save-state", "saved", { timeout: 20_000 });
  await expect(state).toContainText("Uloženo");
}

/** Publikovat (the main part of the split button): what Hotovo was, publishing the page's last changes. */
export async function publishPage(page: Page) {
  await page.locator(".document-primary-actions .publish-main").click();
  await expect(page.getByText("Stránka byla publikována.", { exact: true })).toBeVisible();
}

/** The page's edit lock (beside Publikovat; on a phone in the bottom bar): `true` makes the page read-only for this writer. */
export async function setLocked(page: Page, locked: boolean) {
  const lock = page.locator("[data-edit-lock], [data-page-mobile-lock]").filter({ visible: true }).first();
  if ((await lock.getAttribute("aria-pressed")) !== String(locked)) await lock.click();
  await expect(lock).toHaveAttribute("aria-pressed", String(locked));
}
