import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import ObjectRelations from "./ObjectRelations.vue";
import { atlasObjectDragType } from "../../boards/boardDrop";
import { memoryRelations, type MemoryObject } from "../../objects/memoryRelations";

const object = (id: string, typeId: string, title: string, extra: Partial<MemoryObject> = {}): MemoryObject => ({ id, typeId, title, spaceId: "s", spaceName: "Provoz", readable: true, ...extra });
const a = "11111111-1111-4111-8111-111111111111", b = "66666666-6666-4666-8666-666666666666", hidden = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const api = memoryRelations(
  [object(a, "atlas.page", "Příručka"), object(b, "atlas.board", "Plán"), object(hidden, "atlas.board", "", { readable: false })],
  [{ sourceId: a, targetId: b, kind: "placement", count: 1 }, { sourceId: a, targetId: hidden, kind: "placement", count: 1 }],
);
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });
async function show() {
  wrapper = mount(ObjectRelations, { props: { objectId: a, api }, attachTo: document.body });
  await flushPromises();
  return wrapper;
}

// Firefox cannot drag a <button>, so a row is a focusable role=button element that keeps Enter and Space.
it("rows are draggable and no <button>; Enter and Space open a readable one, a locked one is neither draggable nor focusable", async () => {
  const app = await show();
  const rows = app.findAll("[data-object-relation]");
  expect(rows).toHaveLength(2);
  const [open, locked] = rows.map(row => row.element as HTMLElement);
  expect(open!.tagName).not.toBe("BUTTON");
  expect(open!.getAttribute("role")).toBe("button");
  expect(open!.tabIndex).toBe(0);
  expect(open!.getAttribute("draggable")).toBe("true");
  await rows[0]!.trigger("keydown", { key: "Enter" });
  await rows[0]!.trigger("keydown", { key: " " });
  expect(app.emitted("open")).toEqual([[b, "atlas.board"], [b, "atlas.board"]]);
  expect(locked!.getAttribute("aria-disabled")).toBe("true");
  expect(locked!.getAttribute("draggable")).toBe("false");
  expect(locked!.tabIndex).toBe(-1);
  await rows[1]!.trigger("keydown", { key: "Enter" });
  expect(app.emitted("open")).toHaveLength(2);
});

it("drags a readable row as its object", async () => {
  const app = await show();
  const data = new Map<string, string>();
  await app.get("[data-object-relation]").trigger("dragstart", { dataTransfer: { setData: (type: string, value: string) => data.set(type, value) } });
  expect(JSON.parse(data.get(atlasObjectDragType)!)).toMatchObject({ id: b, typeId: "atlas.board", title: "Plán" });
});
