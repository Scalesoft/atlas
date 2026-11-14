import { beforeAll, beforeEach, describe, vi } from "vitest";
import { loadMarkdownDialect, type MarkdownRichValue } from "@atlas/markdown/core";
import { runEditabilityContract } from "./editability";

beforeAll(() => loadMarkdownDialect(), 60_000);
beforeEach(() => { vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null); });

const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
const page = (plugin: { id: string; version: number }, ...content: object[]) => ({ format: "atlas-rich", version: 1, plugins: [plugin], document: { type: "doc", content } }) as MarkdownRichValue;
/** The position of the first node of `type`. */
const at = (doc: import("@tiptap/pm/model").Node, type: string) => {
  let found = -1;
  doc.descendants((node, pos) => { if (found < 0 && node.type.name === type) found = pos; return found < 0; });
  return found;
};

import { insertYouTrackIssue } from "@atlas/markdown/plugins/youtrack";

const issue = { id: "2-1", key: "PX2-1", title: "Zrychlit načtení", url: "https://youtrack.example/issue/PX2-1", state: "Open" };

describe("YouTrack", () => {
  runEditabilityContract({
    name: "YouTrack",
    document: page({ id: "youtrack", version: 1 }, { type: "paragraph", content: [{ type: "text", text: "Úkol " }, { type: "youtrackIssue", attrs: issue }] }),
    selector: "a[data-youtrack-issue]",
    // The issue picker at the caret, from the slash action.
    open: async ({ controller, editor }) => {
      editor.commands.setTextSelection(editor.state.doc.content.size - 1);
      controller.dispatch(controller.actions.slash.find(action => action.id === "youtrack.search")!.command);
      return vi.waitFor(() => { const picker = document.querySelector('[data-testid="youtrack-picker"]'); if (!picker) throw new Error("no picker"); return picker; });
    },
    act: ({ controller, editor }) => { editor.commands.setTextSelection(editor.state.doc.content.size - 1); return controller.dispatch(insertYouTrackIssue({ ...issue, id: "2-2", key: "PX2-2" })); },
    remote: editor => { const pos = at(editor.state.doc, "youtrackIssue"); return editor.state.tr.setNodeAttribute(pos, "state", "Done"); },
  });
});
