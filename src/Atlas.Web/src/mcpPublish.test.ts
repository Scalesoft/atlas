import { afterEach, describe, expect, it, vi } from "vitest";
import { clearCsrf } from "./api";
import { findingsFor } from "./mcpPublish";

afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); clearCsrf(); });

describe("mcpPublish", () => {
  it("runs the browser rules over the page only when it is at the requested version", async () => {
    const doc = JSON.stringify({ document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Úvod" }] }, { type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: "Prázdná" }] }] } });
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ version: 3, links: [] }), { status: 200, headers: { "Content-Type": "application/json" } })));
    const sent = { pageId: "p1", version: 3, findings: { version: 3, links: [{ targetPageId: "srv", state: "unavailable" as const, title: null }] } };
    const at = await findingsFor(sent, async () => ({ id: "p1", version: 3, contentJson: doc }));
    expect(at.map(finding => finding.id)).toEqual(expect.arrayContaining(["link:srv"]));
    expect(at.some(finding => finding.kind === "section")).toBe(true);
    expect(at.every(finding => !("target" in finding))).toBe(true);
    const other = await findingsFor(sent, async () => ({ id: "p1", version: 4, contentJson: doc }));
    expect(other.map(finding => finding.id)).toEqual(["link:srv"]);
    const failed = await findingsFor(sent, async () => { throw new Error("x"); });
    expect(failed.map(finding => finding.id)).toEqual(["link:srv"]);
  });
});
