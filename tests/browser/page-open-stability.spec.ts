// Opening a page from the tree (3.6, „show fast, signal loading“): the document is on screen at once and stays there. No frame without
// it, nothing pushes it down and nothing moves (article and page dock, CLS ≈ 0). The server seeds every page's live state (3.6), so its
// shared editor is on screen at once; only without a seed it opens through the local stand-in while the shared editor builds hidden
// (.editor-hydrating), and the hidden editor itself takes the stand-in's place. Under reduced motion every block of that editor once stayed hidden for its own visibility transition (UX open blink).
//
// Sign-in: ATLAS_UX_DEV_LOGIN=1 (a Development host) or the acceptance wiki's browser-admin (ATLAS_E2E_PASSWORD, ATLAS_E2E_ALLOW_COLLAB=1).
// It writes its own space and two pages.
import { test, expect, type BrowserContext } from "@playwright/test";
import { createPage, createSpace, heading, login, publish, stories, summaryNode } from "./projector-helpers";
import { PAGE_TEXT } from "./editing";

const devLogin = process.env.ATLAS_UX_DEV_LOGIN === "1";
test.use({ trace: "off", screenshot: "off", video: "off" });
test.skip(!devLogin && !stories, "Requires ATLAS_UX_DEV_LOGIN=1 on a Development host, or the isolated acceptance wiki (ATLAS_E2E_PASSWORD, ATLAS_E2E_ALLOW_COLLAB=1).");

async function signIn(context: BrowserContext) {
  if (!devLogin) return login(context);
  const page = await context.newPage();
  await page.goto("/");
  await page.getByRole("button", { name: "Přihlásit jako vývojář" }).click();
  const onboarding = page.getByRole("button", { name: "Otevřít wiki" }), search = page.getByRole("button", { name: /^Hledat ve wiki/ }).first();
  await expect(onboarding.or(search)).toBeVisible();
  if (await onboarding.isVisible()) await onboarding.click();
  await expect(search).toBeVisible();
  return page;
}

const words = "portál integrace přístup server certifikát konfigurace rozhraní dokumentace síť účet registr provoz".split(" ");
const text = (seed: number, length: number) => { let out = ""; for (let i = 0; out.length < length; i++) out += (out ? " " : "") + words[(seed * 5 + i * 3) % words.length]; return out; };
const paragraph = (value: string) => ({ type: "paragraph", content: [{ type: "text", text: value }] });
/** An overview card, then sections of long paragraphs: taller than the window, with headings for the dock's outline. (No requirement
 *  blocks: the helpers write their pre-3.6 shape, which the page would carry until the conversion job runs again.) */
const documentOf = (seed: number) => [summaryNode(), ...Array.from({ length: 8 }, (_, section) => [
  heading(`Oddíl ${section + 1}: ${text(seed + section, 16)}`),
  ...Array.from({ length: 4 }, (_, at) => paragraph(text(seed + section * 4 + at, 90 + at * 60))),
]).flat()];

for (const motion of ["reduce", "no-preference"] as const) {
  test(`a page opened from another stands at once and never blanks or moves (motion: ${motion})`, async ({ browser }) => {
    test.setTimeout(120_000);
    const context = await browser.newContext({ reducedMotion: motion, viewport: { width: 1440, height: 900 } });
    try {
      const page = await signIn(context);
      const space = await createSpace(context, "Otevření stránky");
      const first = await createPage(context, space, "První stránka", ...documentOf(1));
      const second = await createPage(context, space, "Druhá stránka", ...documentOf(7));
      await publish(context, first.id);
      await publish(context, second.id);
      await page.evaluate(id => { location.hash = `#/pages/${id}`; }, first.id);
      await expect(page.locator(PAGE_TEXT).first()).toBeVisible();
      await page.waitForTimeout(1500);

      await page.evaluate(() => {
        type Probe = { blank: number[]; tops: number[]; cls: number; hidden?: Element; handedOver: boolean; sameNode?: boolean };
        const probe: Probe = { blank: [], tops: [], cls: 0, handedOver: false };
        (window as unknown as { __probe: Probe }).__probe = probe;
        const start = performance.now();
        new PerformanceObserver(list => { for (const entry of list.getEntries() as (PerformanceEntry & { value: number; hadRecentInput: boolean })[]) if (!entry.hadRecentInput) probe.cls += entry.value; })
          .observe({ type: "layout-shift" });
        const frame = () => {
          const hidden = document.querySelector(".document-body .atlas-document-editor.editor-hydrating");
          if (hidden) probe.hidden = hidden;
          const shown = document.querySelector(".document-body .atlas-document-editor:not(.editor-hydrating)");
          const block = shown?.querySelector(".ProseMirror")?.firstElementChild;
          const waiting = !!document.querySelector("[data-page-arrival], .document-body .loading-skeleton");
          if (!waiting && (!block || getComputedStyle(block).visibility === "hidden")) probe.blank.push(Math.round(performance.now() - start));
          if (block && !waiting) probe.tops.push(Math.round(block.getBoundingClientRect().top));
          if (probe.hidden && !hidden && shown) { probe.handedOver = true; probe.sameNode = shown === probe.hidden; return; }
          // Seeded by the server (3.6: every page), the shared editor is on screen from its first frame: no hidden phase to hand over.
          if (!probe.hidden && performance.now() - start > 3000) { probe.handedOver = true; probe.sameNode = true; return; }
          if (performance.now() - start < 15_000) requestAnimationFrame(frame);
        };
        requestAnimationFrame(frame);
      });
      await page.evaluate(id => { location.hash = `#/pages/${id}`; }, second.id);
      // The second page opens at once from its seeded live state; only without one (the server's seed failed) through the stand-in, and
      // the probe then stops once the shared editor took over.
      await page.waitForFunction(() => (window as unknown as { __probe: { handedOver: boolean } }).__probe.handedOver, undefined, { timeout: 20_000 });
      await page.waitForTimeout(800);
      const probe = await page.evaluate(() => { const { blank, tops, cls, sameNode } = (window as unknown as { __probe: { blank: number[]; tops: number[]; cls: number; sameNode?: boolean } }).__probe; return { blank, tops, cls, sameNode }; });
      expect(probe.blank, "frames with the page but no visible document (ms after the click)").toEqual([]);
      expect(probe.sameNode, "the editor built hidden is the one that takes the stand-in's place").toBe(true);
      expect(new Set(probe.tops).size, `the document's first block moved: ${[...new Set(probe.tops)].join(" → ")}`).toBe(1);
      expect(probe.cls, "layout shift of the open (article and page dock)").toBeLessThan(0.01);
    } finally {
      await context.close();
    }
  });
}
