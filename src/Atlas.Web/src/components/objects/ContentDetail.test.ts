import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import ContentDetail from "./ContentDetail.vue";
import ContactCard from "../contacts/ContactCard.vue";
import { objectApi, type ObjectDto } from "../../objects/objectApi";
import type { DetailRow } from "../../objects/relationsApi";

vi.mock("../../objects/objectApi", () => ({ objectApi: { get: vi.fn() } }));
const id = "88888888-8888-4888-8888-888888888888";
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.resetAllMocks(); });
const show = (props: { row: DetailRow; graph?: boolean }) => { wrapper = mount(ContentDetail, { props: { tab: "overview" as const, ...props } }); return flushPromises().then(() => wrapper!); };

it("shows a contact as its directory card in Přehled (the tree's reference node to a contact opens here)", async () => {
  vi.mocked(objectApi.get).mockResolvedValue({ id, typeId: "atlas.contact", data: { name: "Jana Nováková" } } as unknown as ObjectDto);
  const app = await show({ row: { id, typeId: "atlas.contact", title: "Jana Nováková", spaceId: "s" } });
  expect(objectApi.get).toHaveBeenCalledWith(id);
  expect(app.findComponent(ContactCard).props("contact")).toMatchObject({ name: "Jana Nováková" });
  // A contact has no frame of its own to open; a host without a graph offers none.
  await app.setProps({ graph: false });
  expect(app.find("[data-content-detail-open]").exists()).toBe(false);
  expect(app.find("[data-content-detail-focus]").exists()).toBe(false);
});

it("reads nothing for a page and offers Otevřít and Ukázat v grafu", async () => {
  const app = await show({ row: { id, typeId: "atlas.page", title: "Úvod", spaceId: "s" } });
  expect(objectApi.get).not.toHaveBeenCalled();
  expect(app.find("[data-content-detail-open]").exists()).toBe(true);
  expect(app.find("[data-content-detail-focus]").exists()).toBe(true);
});
