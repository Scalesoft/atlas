// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { api } from "../../api";
import { overviewApi } from "./overviewApi";

vi.mock("../../api", async importOriginal => ({ ...await importOriginal<typeof import("../../api")>(), api: vi.fn() }));

describe("overviewApi.overview", () => {
  it("fills in the empty attributes the server leaves out of a cell", async () => {
    vi.mocked(api).mockResolvedValueOnce({ keys: ["sms"], pages: [{ pageId: "p", title: "KBN", percent: 0,
      cells: [{ blockId: "b", key: "sms", direction: "inbound", state: "planned", position: 0, section: ["Portál"] }] }] });

    const overview = await overviewApi.overview("s", "all");

    expect(overview.pages[0].cells[0]).toEqual({ blockId: "b", key: "sms", direction: "inbound", state: "planned", position: 0, section: ["Portál"],
      title: "", date: "", party: "", where: "", requires: [], parentBlockId: null });
  });
});
