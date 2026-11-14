import { beforeAll, beforeEach, describe, vi } from "vitest";
import { loadMarkdownDialect, type MarkdownRichValue } from "@atlas/markdown/core";
import { runEditabilityContract } from "./editability";
import { settleModules } from "./editability.modules";

// ECharts draws nothing in jsdom (no canvas, no layout) and its prepare never settles there: the plugin tests stub it the same way.
vi.mock("../../../Atlas.Markdown/src/plugins/charts/chartRenderer", () => ({ createChartRenderer: () => ({ update() {}, destroy() {} }) }));
vi.setConfig({ testTimeout: 30_000 });
beforeAll(() => loadMarkdownDialect(), 60_000);
beforeEach(() => { vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null); });

const chart = { version: 1, title: "Příjmy podle čtvrtletí", kind: "bar", source: null, sourceStatus: null, grid: [["Čtvrtletí", "Kč"], ["Q1", "1 250"], ["Q2", "1 800"], ["Q3", "1 430"]] };
const value = { format: "atlas-rich", version: 1, plugins: [{ id: "charts", version: 1 }], document: { type: "doc", content: [{ type: "chart", attrs: { data: chart } }] } } as MarkdownRichValue;
const at = (doc: import("@tiptap/pm/model").Node, type: string) => {
  let found = -1;
  doc.descendants((node, pos) => { if (found < 0 && node.type.name === type) found = pos; return found < 0; });
  return found;
};

describe("graf", () => {
  runEditabilityContract({
    name: "graf",
    document: value,
    selector: '[data-md-module="chart"]',
    // The module renders lazily at the viewport; jsdom has no layout, so the page is asked to finish them.
    settle: settleModules,
    // The type chip is laid over the end of the header: an extra control that takes no room.
    affordances: [".md-chart-kind-chip"],
    // The title edited where it is shown: click, type, Enter.
    act: ({ root }) => {
      const title = root()!.querySelector<HTMLElement>(".md-chart-title")!;
      title.click();
      const input = title.querySelector<HTMLInputElement>("input");
      if (!input) return;
      input.value = "Nový název";
      input.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    },
    // The data inspector: a click on the data table.
    open: async ({ root }) => {
      await vi.waitFor(() => { if (!root()!.querySelector(".md-chart-data")) throw new Error("no data table"); }, { timeout: 20_000 });
      root()!.querySelector<HTMLElement>(".md-chart-data")!.click();
      return vi.waitFor(() => { const panel = globalThis.document.querySelector(".md-chart-inspector"); if (!panel) throw new Error("no inspector"); return panel; });
    },
    remote: editor => { const pos = at(editor.state.doc, "chart"); return editor.state.tr.setNodeAttribute(pos, "data", { ...chart, title: "Cizí název" }); },
  });
});
