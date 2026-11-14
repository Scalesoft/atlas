import { beforeEach, describe, vi } from "vitest";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { emojiNode, insertEmoji } from "@atlas/markdown/plugins/emoji";
import { setConcealedRevealMode } from "@atlas/markdown/plugins/concealed";
import { runEditabilityContract } from "./editability";

/*
 * The editability contract (3.6-0) of the components that met it first; each other component adds its own
 * `editability.<component>.contract.test.ts` with one case for runEditabilityContract (contracts/editability.ts).
 */

beforeEach(() => { vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null); });

const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
const page = (plugin: { id: string; version: number }, ...content: object[]) => ({ format: "atlas-rich", version: 1, plugins: [plugin], document: { type: "doc", content } }) as MarkdownRichValue;
/** The position of the first node of `type`. */
const at = (doc: import("@tiptap/pm/model").Node, type: string) => {
  let found = -1;
  doc.descendants((node, pos) => { if (found < 0 && node.type.name === type) found = pos; return found < 0; });
  return found;
};

describe("emoji", () => {
  runEditabilityContract({
    name: "emoji",
    document: page({ id: "emoji", version: 1 }, { type: "paragraph", content: [{ type: "text", text: "Hotovo " }, emojiNode("grinning")] }),
    selector: '[data-type="emoji"]',
    act: ({ controller, editor }) => { editor.commands.setTextSelection(editor.state.doc.content.size - 1); return controller.dispatch(insertEmoji("heart")); },
    // The `:` picker at the caret.
    open: async ({ editor }) => {
      editor.commands.focus("end", { scrollIntoView: false });
      editor.commands.insertContent(" :heart");
      return vi.waitFor(() => { const menu = document.querySelector('[data-testid="emoji-menu"]'); if (!menu) throw new Error("no picker"); return menu; });
    },
    remote: editor => { const pos = at(editor.state.doc, "emoji"); return editor.state.tr.delete(pos, pos + 1); },
  });
});

describe("concealed content", () => {
  runEditabilityContract({
    name: "skrytý obsah",
    document: page({ id: "concealed", version: 2 }, paragraph("Úvod."), { type: "concealedContent", content: [paragraph("Překvapení v pátek.")] }),
    selector: ".md-concealed-block",
    // The invisible ink and the chip are overlays (position: absolute) over a body that has the same box in every state.
    affordances: [".md-concealed-block__ink", ".md-concealed-block__chip"],
    settle: () => Promise.all([import("@atlas/markdown/concealment")]),
    act: ({ controller, editor }) => { editor.commands.setTextSelection(at(editor.state.doc, "concealedContent") + 2); return controller.dispatch(setConcealedRevealMode({ reveal: "button" })); },
    // The settings at the block: Enter on its label.
    open: async ({ root }) => {
      const chip = root()!.querySelector<HTMLElement>(".md-concealed-block__chip")!;
      return vi.waitFor(() => {
        chip.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
        const panel = document.querySelector(".md-concealed-settings");
        if (!panel) throw new Error("no settings");
        return panel;
      });
    },
    remote: editor => { const pos = at(editor.state.doc, "concealedContent"); return editor.state.tr.setNodeAttribute(pos, "reveal", "button"); },
  });
});
