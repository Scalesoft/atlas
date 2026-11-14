import { afterEach, describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { ApiError } from "../api";
import { createSecretExamples } from "../assist/rules";
import { createPublicationChecks, localFindings, PUBLICATION_CHECK_BUDGET_MS, serverFindings } from "./publicationChecks";

const PASSWORD = "Srv#2026!rack";
const text = (value: string) => ({ type: "text", text: value });
const paragraph = (...content: unknown[]) => ({ type: "paragraph", content });
const heading = (level: number, title: string, headingId?: string) => ({ type: "heading", attrs: { level, ...(headingId ? { headingId } : {}) }, content: title ? [text(title)] : [] });
const rich = (...content: unknown[]) => JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content } });

afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe("local publication checks", () => {
  it("finds a plaintext password outside secret nodes and skips marked examples", () => {
    const examples = createSecretExamples();
    const content = rich(
      paragraph(text("Úvod bez tajemství.")),
      heading(2, "Přístup", "pristup"),
      // The password sits after an inline atom and a hard break: offsets count them as one position each.
      paragraph({ type: "pageReference", attrs: { id: "p", title: "VPN", url: "#/pages/p" } }, text(" účet admin"), { type: "hardBreak" }, text(`heslo ${PASSWORD}`)),
      // A password reference is the safe form and is never read as text.
      { type: "secretReference", attrs: { secretId: "s1", label: `heslo ${PASSWORD}` } },
      paragraph(text("Ukázka: heslo Example#2026!x")),
    );
    examples.mark("page-1", "Example#2026!x");
    const findings = localFindings(content, { pageId: "page-1", examples });
    expect(findings).toEqual([{
      id: "secret:2:19", kind: "secret", severity: "warning", text: "Možné heslo v textu (sekce Přístup)",
      target: { kind: "text", path: [2], from: 19, to: 19 + PASSWORD.length },
    }]);
    // Ranges only: the value itself is in no finding.
    expect(JSON.stringify(findings)).not.toContain(PASSWORD);
    // Before any heading the row names no section.
    expect(localFindings(rich(paragraph(text(`heslo ${PASSWORD}`))), { pageId: "page-1", examples })[0]!.text).toBe("Možné heslo v textu");
  });

  it("reports empty sections by heading level", () => {
    const content = rich(
      heading(2, "Instalace", "instalace"),
      heading(3, "Windows", "windows"),
      paragraph(text("Spusťte instalátor.")),
      heading(3, "Linux", "linux"),
      paragraph(),
      heading(2, "Odinstalace"),
      heading(2, ""),
      paragraph(text("Text pod prázdným nadpisem.")),
      heading(2, "Poznámky", "poznamky"),
    );
    expect(localFindings(content, { pageId: "page-1" }).map(({ id, severity, text, target }) => ({ id, severity, text, target }))).toEqual([
      { id: "section:3", severity: "info", text: "Prázdná sekce „Linux“", target: { kind: "heading", id: "linux", path: [3] } },
      { id: "section:5", severity: "info", text: "Prázdná sekce „Odinstalace“", target: { kind: "heading", id: null, path: [5] } },
      { id: "section:8", severity: "info", text: "Prázdná sekce „Poznámky“", target: { kind: "heading", id: "poznamky", path: [8] } },
    ]);
    // A section whose only content is a sub-section is not empty: "Instalace" has Windows.
    expect(localFindings(rich(heading(2, "A"), heading(3, "B"), paragraph(text("x"))), { pageId: "page-1" })).toEqual([]);
  });

  it("never fails on content it cannot read", () => {
    expect(localFindings("{", { pageId: "page-1" })).toEqual([]);
    expect(localFindings(JSON.stringify({ document: null }), { pageId: "page-1" })).toEqual([]);
  });
});

describe("server publication check", () => {
  it("maps unavailable links without a title and unpublished links with one", async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ version: 4, links: [
      { targetPageId: "0a1b2c3d-0000-0000-0000-000000000001", state: "unavailable", title: null },
      { targetPageId: "0a1b2c3d-0000-0000-0000-000000000002", state: "unpublished", title: "Staré VPN" },
    ] }), { headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);
    const findings = await serverFindings("page-1", 4, new AbortController().signal);
    expect(String((fetchMock.mock.calls[0] as unknown[])[0])).toBe("/api/pages/page-1/publication-check?expectedVersion=4");
    expect(findings).toEqual([
      { id: "link:0a1b2c3d-0000-0000-0000-000000000001", kind: "link", severity: "warning", text: "Odkaz na nedostupnou stránku",
        target: { kind: "page", pageId: "0a1b2c3d-0000-0000-0000-000000000001" } },
      { id: "link:0a1b2c3d-0000-0000-0000-000000000002", kind: "link", severity: "warning", text: "Odkaz na nepublikovanou stránku „Staré VPN“",
        target: { kind: "page", pageId: "0a1b2c3d-0000-0000-0000-000000000002" }, openHref: "#/pages/0a1b2c3d-0000-0000-0000-000000000002" },
    ]);
  });
});

describe("publication check run", () => {
  const page = { id: "page-1", version: 4, contentJson: rich(heading(2, "Přístup"), paragraph(text(`heslo ${PASSWORD}`))) };

  it("continues with local findings when the server check times out or fails", async () => {
    vi.useFakeTimers();
    const hanging = vi.fn((_id: string, _version: number, signal: AbortSignal) => new Promise<never>((_, reject) => signal.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")))));
    const run = createPublicationChecks({ page: () => page, save: async () => true, fetchCheck: hanging })()!;
    expect(run.local.map(finding => finding.kind)).toEqual(["secret"]);
    let server: unknown;
    void run.server.then(value => { server = value; });
    await vi.advanceTimersByTimeAsync(PUBLICATION_CHECK_BUDGET_MS - 1);
    expect(server).toBeUndefined();
    await vi.advanceTimersByTimeAsync(1);
    expect(server).toEqual([]);
    expect((hanging.mock.calls[0]![2] as AbortSignal).aborted).toBe(true);

    const failing = vi.fn(async () => { throw new ApiError(500, "Chyba serveru"); });
    expect(await createPublicationChecks({ page: () => page, save: async () => true, fetchCheck: failing })()!.server).toEqual([]);
    // Without a saved page there is nothing to check.
    expect(createPublicationChecks({ page: () => null, save: async () => true })()).toBeNull();
  });

  it("retries once after saving on 409", async () => {
    let current = { ...page };
    const save = vi.fn(async () => { current = { ...current, version: 5 }; return true; });
    const fetchCheck = vi.fn(async (_id: string, version: number) => {
      if (version === 4) throw new ApiError(409, "Stránka se mezitím změnila.");
      return [{ id: "link:x", kind: "link" as const, severity: "warning" as const, text: "Odkaz na nedostupnou stránku" }];
    });
    const run = createPublicationChecks({ page: () => current, save, fetchCheck })()!;
    expect(await run.server).toHaveLength(1);
    expect(save).toHaveBeenCalledOnce();
    expect(fetchCheck.mock.calls.map(call => call[1])).toEqual([4, 5]);

    // A second 409 is not retried again, and a failed save ends the check: publishing goes on as today.
    const always409 = vi.fn(async () => { throw new ApiError(409, "konflikt"); });
    expect(await createPublicationChecks({ page: () => current, save, fetchCheck: always409 })()!.server).toEqual([]);
    expect(always409).toHaveBeenCalledTimes(2);
    const refused = vi.fn(async () => false);
    expect(await createPublicationChecks({ page: () => current, save: refused, fetchCheck: always409 })()!.server).toEqual([]);
    expect(always409).toHaveBeenCalledTimes(3);
  });

  it("does not depend on the editor or on assist being switched on", () => {
    // §0: the check uses only the pure rules, so it works with assist off and without the editor module.
    const source = readFileSync(resolve(__dirname, "publicationChecks.ts"), "utf8");
    const imports = [...source.matchAll(/from\s+["']([^"']+)["']/g)].map(match => match[1]);
    expect(imports.filter(name => /@atlas\/markdown|loadAssist|atlasAssist|editorAssist/.test(name!))).toEqual([]);
    expect(source).not.toMatch(/features|editorAssist/);
  });
});
