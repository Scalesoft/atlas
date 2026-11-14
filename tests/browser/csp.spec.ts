import { expect, test, type BrowserContext, type Page, type Response } from "@playwright/test";
import { startEditing } from "./editing";

// Content-Security-Policy acceptance against the published server (README, "Content-Security-Policy"):
// the app shell, a page with an OpenAPI block (API tab with Scalar, YAML tab with Monaco and its workers)
// and a Mermaid diagram, and a public /s/* share page. Any violation fails, whether the server sends the
// policy enforced (default) or Report-Only: browsers report both as `securitypolicyviolation` events and
// console messages. The only tolerated report is the Scalar zod `new Function` probe (see scalarEvalProbe).
// Creates one page and one share link, revokes/trashes them afterwards.
test.use({ trace: "off", screenshot: "off", video: "off" });
const username = process.env.ATLAS_E2E_USER ?? "browser-admin", password = process.env.ATLAS_E2E_PASSWORD;
test.skip(!password || process.env.ATLAS_E2E_ALLOW_COLLAB !== "1",
  "Requires an isolated installed acceptance database, ATLAS_E2E_PASSWORD and explicit ATLAS_E2E_ALLOW_COLLAB=1.");

const apiTitle = "CSP Billing API";
const apiSource = `openapi: 3.1.0\ninfo:\n  title: ${apiTitle}\n  version: '1.0'\npaths:\n  /invoices:\n    get:\n      responses:\n        '200':\n          description: OK\n`;
const contentJson = JSON.stringify({ format: "atlas-rich", version: 1, plugins: [{ id: "openapi", version: 1 }, { id: "diagrams", version: 2 }], document: { type: "doc", content: [
  { type: "paragraph", content: [{ type: "text", text: "Ověření Content-Security-Policy." }] },
  { type: "openapi", attrs: { data: { version: 1, filename: "billing.yaml", source: apiSource, mode: "reference" } } },
  { type: "diagram", attrs: { data: { version: 1, source: "flowchart LR\n  A[Čtenář] --> B[Atlas]", caption: "Tok CSP" } } },
] } });

/** Records every violation of every frame (the Scalar reference is a srcdoc iframe) of the context's pages. */
async function recordViolations(context: BrowserContext, violations: string[]) {
  await context.exposeBinding("__atlasCspViolation", ({ frame }, text: string) => { violations.push(`${frame.url()}: ${text}`); });
  await context.addInitScript(() => window.addEventListener("securitypolicyviolation", event => {
    const report = (window as unknown as { __atlasCspViolation(text: string): void }).__atlasCspViolation;
    void report(`${event.disposition} ${event.effectiveDirective} blocked ${event.blockedURI || "inline"} at ${event.sourceFile}:${event.lineNumber}`);
  }, true));
  context.on("console", message => {
    if (/Content[- ]Security[- ]Policy/i.test(message.text())) violations.push(`console ${message.type()}: ${message.text()}`);
  });
}

/** The document was served with the Atlas policy in either mode; without it this test would prove nothing. */
async function expectPolicy(response: Response | null) {
  // headers() omits security-related headers; allHeaders() returns the raw response headers.
  const headers = (await response?.allHeaders()) ?? {};
  expect(headers["content-security-policy"] ?? headers["content-security-policy-report-only"], "The shell must carry the document policy.")
    .toContain("font-src 'self' data:");
}

/**
 * The one known, harmless violation: the bundled Scalar reference (its zod runtime) probes whether `new Function`
 * is allowed and falls back when it is not. Under the enforced policy (default) that probe is one
 * `securitypolicyviolation` event from the Scalar asset; under the report-only policy it is also reported as the
 * browser's console notice. Only that event, and the notice beside it, is dropped; an eval anywhere else, or the
 * console notice without the Scalar event, still fails.
 */
const scalarEvalProbe = /^about:srcdoc: (?:enforce|report) script-src blocked eval at https?:\/\/[^/]+\/assets\/scalar-standalone-[\w-]+\.js:\d+$/;
const reportOnlyEvalNotice = /^console info: Evaluating a string as JavaScript violates the following Content Security Policy directive because 'unsafe-eval' is not an allowed source of script: "script-src 'self'"\.\s+The policy is report-only, so the violation has been logged but no further action has been taken\.$/;
function unexpectedViolations(violations: readonly string[]) {
  const probed = violations.some(text => scalarEvalProbe.test(text));
  return violations.filter(text => !scalarEvalProbe.test(text) && !(probed && reportOnlyEvalNotice.test(text)));
}

/** Lets late loads (fonts, workers, lazy chunks) report before the check. */
async function expectNoViolations(page: Page, violations: string[]) {
  await page.evaluate(() => document.fonts.ready.then(() => undefined));
  await page.waitForTimeout(500);
  expect(unexpectedViolations(violations), "Content-Security-Policy violations").toEqual([]);
}

test("app shell, OpenAPI detail with Monaco, Mermaid and a public share page load without CSP violations", async ({ browser }) => {
  test.setTimeout(180_000);
  const violations: string[] = [];
  const context = await browser.newContext();
  const anonymous = await browser.newContext();
  await recordViolations(context, violations);
  await recordViolations(anonymous, violations);
  let headers: Record<string, string> = {};
  let fixture: { id: string; spaceId: string } | undefined;
  let link: { id: string } | undefined;
  let shareUrl = "";
  let restoreSharing: string | undefined;
  try {
    const page = await context.newPage();
    await test.step("app shell and sign-in", async () => {
      await expectPolicy(await page.goto("/"));
      await page.getByLabel("Uživatelské jméno", { exact: true }).fill(username);
      await page.getByLabel("Heslo", { exact: true }).fill(password!);
      await page.getByRole("button", { name: "Přihlásit se", exact: true }).click();
      await expect(page.getByRole("button", { name: /^Hledat ve wiki/ })).toBeVisible();
      await expectNoViolations(page, violations);
    });

    await test.step("published fixture page and share link", async () => {
      headers = { "X-CSRF-TOKEN": (await (await context.request.get("/api/auth/csrf")).json()).token };
      const spaces: { id: string; role: string }[] = await (await context.request.get("/api/spaces")).json();
      const space = spaces.find(item => item.role === "Admin");
      expect(space, "The account must administer a space to share a page.").toBeTruthy();
      const created = await context.request.post("/api/pages", { headers, data: {
        spaceId: space!.id, parentId: null, title: `CSP ${Date.now()}`, contentJson,
        markdown: `Ověření Content-Security-Policy.\n\n${apiTitle}\n\nTok CSP`,
      } });
      expect(created.ok()).toBe(true);
      const page = await created.json();
      fixture = { id: page.id, spaceId: space!.id };
      expect((await context.request.post(`/api/pages/${page.id}/publish`, { headers, data: { expectedVersion: page.version } })).ok()).toBe(true);
      const sharing = await (await context.request.get(`/api/spaces/${space!.id}/external-sharing`)).json();
      if (!sharing.enabled) {
        const enabled = await context.request.put(`/api/spaces/${space!.id}/external-sharing`, { headers, data: { enabled: true, expectedVersion: sharing.version } });
        expect(enabled.ok()).toBe(true);
        restoreSharing = (await enabled.json()).version;
      }
      const issued = await context.request.post(`/api/pages/${page.id}/share-links`, { headers, data: {
        name: "CSP", expiresAt: new Date(Date.now() + 60 * 60_000).toISOString(),
      } });
      expect(issued.status()).toBe(201);
      const result = await issued.json();
      link = result.link;
      shareUrl = result.url;
    });

    await test.step("page with a Mermaid diagram, the API tab and the YAML editor", async () => {
      // A hash route on the already open shell is a same-document navigation without a response;
      // reload so this document load is checked as well.
      await page.goto(`/#/pages/${fixture!.id}`);
      await expectPolicy(await page.reload());
      // Mermaid output is rendered as an image (data: SVG) inside the captioned figure.
      await expect(page.getByRole("figure", { name: "Tok CSP" }).getByRole("img", { name: "Tok CSP" })).toBeVisible();
      await startEditing(page);
      await page.locator(".md-openapi-card .md-btn-primary").click();
      await expect(page.getByRole("dialog").getByRole("tab", { name: "Dokumentace", exact: true })).toBeVisible();
      const reference = page.getByTestId("openapi-detail-reference").locator("iframe");
      await expect(reference).toBeVisible();
      await expect(reference.contentFrame().locator("body")).toContainText(apiTitle, { timeout: 30_000 });
      // 3.6: the YAML editor (Monaco) loads on demand behind its tab of the detail dialog.
      await page.getByRole("tab", { name: "YAML", exact: true }).click();
      const monaco = page.getByTestId("openapi-detail-source").locator(".monaco-editor");
      await expect(monaco.locator(".view-lines")).toContainText("openapi", { timeout: 30_000 });
      await expect.poll(() => page.workers().length, { message: "Monaco starts its workers" }).toBeGreaterThan(0);
      // The find widget renders codicons, so the codicon font (a data: URL in Monaco's CSS) is loaded.
      await monaco.locator(".view-lines").click();
      await page.keyboard.press("Control+F");
      await expect(monaco.locator(".find-widget")).toBeVisible();
      await expectNoViolations(page, violations);
      await page.keyboard.press("Escape");
    });

    await test.step("public share page", async () => {
      const reader = await anonymous.newPage();
      await expectPolicy(await reader.goto(shareUrl));
      await expect(reader.getByRole("document", { name: "Sdílený dokument pouze pro čtení" })).toContainText("Content-Security-Policy");
      await expect(reader.getByRole("figure", { name: "Tok CSP" }).getByRole("img", { name: "Tok CSP" })).toBeVisible();
      await reader.locator(".md-openapi-card .md-btn-primary").click();
      const reference = reader.locator("iframe").first();
      await expect(reference).toBeVisible();
      await expect(reference.contentFrame().locator("body")).toContainText(apiTitle, { timeout: 30_000 });
      await expectNoViolations(reader, violations);
    });
  } finally {
    if (fixture && link) await context.request.delete(`/api/pages/${fixture.id}/share-links/${link.id}`, { headers });
    if (fixture && restoreSharing) await context.request.put(`/api/spaces/${fixture.spaceId}/external-sharing`, { headers, data: { enabled: false, expectedVersion: restoreSharing } });
    if (fixture) {
      const current = await (await context.request.get(`/api/pages/${fixture.id}`)).json();
      const tree = await (await context.request.get(`/api/spaces/${fixture.spaceId}/tree`)).json();
      await context.request.post(`/api/pages/${fixture.id}/trash`, { headers, data: { expectedVersion: current.version, expectedTreeVersion: tree.version, includeSubtree: false } });
    }
    await context.close();
    await anonymous.close();
  }
});
