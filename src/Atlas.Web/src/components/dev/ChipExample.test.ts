import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { loadMarkdownDialect } from "@atlas/markdown/core";
import hostSchema from "../../../../Atlas.Server/Platform/Validation/host-markdown-schema.json";
import ChipExample, { sentence } from "./ChipExample.vue";

let wrapper: VueWrapper | undefined;
// The app loads it with DocumentEditor.vue; the example on its own does not.
beforeAll(() => loadMarkdownDialect());
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

describe("chip gallery", () => {
  it("shows a chip of every kind of entity the host manifest names, in the editor and read only", async () => {
    const types = new Set<string>();
    const visit = (node: { type?: string; content?: unknown[] }) => { types.add(node.type ?? ""); (node.content as typeof node[] | undefined)?.forEach(visit); };
    visit(sentence.document as Parameters<typeof visit>[0]);
    expect(hostSchema.entities.map(entity => entity.inline).filter(node => !types.has(node))).toEqual([]);

    wrapper = mount(ChipExample, { attachTo: document.body });
    // The editor and the reader open the sentence (each draws the issue), the states draw their own two.
    await vi.waitFor(() => expect(document.querySelectorAll(".md-youtrack-issue")).toHaveLength(4));
    expect(document.querySelector("[data-example-chip-states] .md-youtrack-issue-key")?.textContent).toBe("PX2-1235: ");
  });
});
