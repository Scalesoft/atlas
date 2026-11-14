// @vitest-environment node
import { beforeEach, describe, expect, it } from "vitest";
import { defineComponent } from "vue";
import { useWorkspacePanel } from "./useWorkspacePanel";

const A = defineComponent({ template: "<p>A</p>" }), B = defineComponent({ template: "<p>B</p>" });

describe("useWorkspacePanel", () => {
  beforeEach(() => useWorkspacePanel().reset());
  it("shows at most one panel: a second show replaces the first", () => {
    const panel = useWorkspacePanel();
    panel.show({ label: "A", content: A });
    panel.show({ label: "B", content: B, props: { x: 1 } });
    expect(panel.open.value).toBe(true);
    expect(panel.label.value).toBe("B");
    expect(panel.content.value).toBe(B);
    expect(panel.contentProps.value).toEqual({ x: 1 });
    panel.hide();
    expect(panel.open.value).toBe(false);
  });
});
