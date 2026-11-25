import { afterEach, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import type { RemoteDrag } from "../../../boards/boardSession";
import BoardPresence from "./BoardPresence.vue";

afterEach(() => { vi.restoreAllMocks(); document.body.innerHTML = ""; });

const jana = (x: number): RemoteDrag => ({ userId: "u2", displayName: "Jana", color: "#2a9d8f", cursor: { x, y: 20 }, dragging: [{ itemId: "a", x, y: 6, width: 200, height: 200 }] });
// The real TransitionGroup, not the test stub: its measuring is what this is about.
const presence = (remote: RemoteDrag[]) => mount(BoardPresence, { attachTo: document.body, props: { remote, viewport: { x: 0, y: 0, zoom: 1 } }, global: { stubs: { transition: false, "transition-group": false } } });

it("moves a colleague's ghost and pointer without the group measuring them again, which laid out the whole board on every preview (COL2-1)", async () => {
  const frames: FrameRequestCallback[] = [];
  vi.spyOn(globalThis, "requestAnimationFrame").mockImplementation(callback => frames.push(callback));
  const marks = presence([jana(10)]);
  const pointer = marks.get("[data-board-cursor]").element as HTMLElement;
  expect(pointer.style.transform).toBe("translate(10px, 20px)");
  const measured = vi.spyOn(Element.prototype, "getBoundingClientRect");
  await marks.setProps({ remote: [jana(60)] });
  await marks.setProps({ remote: [jana(110)], viewport: { x: 5, y: 0, zoom: 1 } });
  expect(marks.get("[data-board-ghost]").attributes("style")).toContain("translate(115px, 6px)");
  // The pointer glides in board coordinates and is drawn through the viewport: it moved with the board at once, then towards 110.
  expect(pointer.style.transform).toBe("translate(15px, 20px)");
  const start = performance.now();
  for (let i = 1; i <= 60; i++) frames.splice(0).forEach(callback => callback(start + i * 16));
  expect(pointer.style.transform).toBe("translate(115px, 20px)");
  expect(marks.get("[data-board-cursor]").text()).toBe("Jana");
  expect(measured).not.toHaveBeenCalled();
});

it("fades a mark out at its last place when a colleague stops, and lets it go after the fade", async () => {
  vi.useFakeTimers();
  const marks = presence([]);
  await marks.setProps({ remote: [jana(10)] });
  expect(marks.get("[data-board-ghost]").classes()).not.toContain("is-leaving");
  await marks.setProps({ remote: [{ ...jana(10), dragging: [] }] });
  const leaving = marks.get("[data-board-ghost]");
  expect(leaving.classes()).toContain("is-leaving");
  expect(leaving.attributes("style")).toContain("translate(10px, 6px)");
  expect(marks.get("[data-board-cursor]").classes()).not.toContain("is-leaving");
  await vi.advanceTimersByTimeAsync(400);
  expect(marks.find("[data-board-ghost]").exists()).toBe(false);
  // Starting again within the fade brings the same mark back.
  await marks.setProps({ remote: [jana(30)] });
  expect(marks.get("[data-board-ghost]").classes()).not.toContain("is-leaving");
  vi.useRealTimers();
});

it("draws an AI's pointer in the AI gradient with its stars writing in its name, a person's in their colour", () => {
  const marks = presence([jana(10), { userId: "ai:0f3c", displayName: "Nexus", color: "#7c3aed", cursor: { x: 40, y: 20 }, dragging: [] }]);
  const [person, ai] = marks.findAll("[data-board-cursor]");
  expect(person!.classes()).not.toContain("is-agent");
  expect(person!.find("[data-ai-glyph]").exists()).toBe(false);
  expect(ai!.classes()).toContain("is-agent");
  expect(ai!.get("path").attributes("fill")).toBe(`url(#${ai!.get("linearGradient").attributes("id")})`);
  expect(ai!.get("[data-ai-glyph]").attributes("data-state")).toBe("writing");
  expect(ai!.text()).toBe("Nexus");
});

it("draws a colleague's connector in progress as a dashed line in their colour, from the side of its start to the item it would land at (A8)", () => {
  const boxes: Record<string, { x: number; y: number; width: number; height: number }> = { a: { x: 0, y: 0, width: 100, height: 100 }, b: { x: 300, y: 0, width: 100, height: 100 } };
  const drawing: RemoteDrag = { userId: "u3", displayName: "Petr", color: "#e76f51", dragging: [], connecting: { from: "a", to: "b", x: 250, y: 50 } };
  const marks = mount(BoardPresence, { attachTo: document.body, props: { remote: [drawing], viewport: { x: 10, y: 0, zoom: 1 }, boxOf: (id: string) => boxes[id] } });
  const line = marks.get("[data-board-remote-connector]");
  expect([line.attributes("x1"), line.attributes("y1"), line.attributes("x2"), line.attributes("y2")]).toEqual(["110", "50", "310", "50"]);
  expect(line.attributes("style")).toContain("--collaboration-color");
});
