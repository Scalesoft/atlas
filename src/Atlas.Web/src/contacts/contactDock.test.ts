import { expect, it, vi } from "vitest";
import { createLiveObjects } from "../objects/liveObjects";
import type { ObjectDto } from "../objects/objectApi";
import { showObjectDetail } from "../objects/objectDetail";
import { showContactDetail } from "./contactDock";

vi.mock("../objects/objectDetail", () => ({ showObjectDetail: vi.fn() }));

it("opens a contact's dock under its name, and nothing for a contact the reader cannot read", async () => {
  const get = vi.fn(async (id: string) => (id === "c1"
    ? { id, typeId: "atlas.contact", title: "Eva", version: 1, data: { name: "Eva", directoryId: "acc-1" }, canEdit: false }
    : Promise.reject(Object.assign(new Error("gone"), { status: 404 }))) as unknown as ObjectDto);
  const store = createLiveObjects({ get, change: vi.fn() });
  await showContactDetail("c1", store);
  expect(vi.mocked(showObjectDetail).mock.calls[0]![0]).toMatchObject({ id: "c1", typeId: "atlas.contact", title: "Eva" });
  await showContactDetail("c2", store).catch(() => {});
  expect(showObjectDetail).toHaveBeenCalledOnce();
});
