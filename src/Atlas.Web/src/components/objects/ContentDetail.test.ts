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
  // A contact of nobody's account is no colleague: no activity.
  expect(app.find("[data-person-activity]").exists()).toBe(false);
});

it("shows an internal person's activity under the card and opens its pages", async () => {
  vi.mocked(objectApi.get).mockResolvedValue({ id, typeId: "atlas.contact", data: { name: "Eva Dvořáková", directoryId: "acc-1" } } as unknown as ObjectDto);
  const app = await show({ row: { id, typeId: "atlas.contact", title: "Eva Dvořáková", spaceId: "s" } });
  await vi.dynamicImportSettled();
  await flushPromises();
  const activity = app.findComponent({ name: "PersonActivity" });
  expect(activity.props("accountId")).toBe("acc-1");
  activity.vm.$emit("open", "page-1");
  expect(app.emitted("open")?.[0]).toEqual(["page-1", "atlas.page"]);
});

it("reads nothing for a page and offers Otevřít and Ukázat v grafu", async () => {
  const app = await show({ row: { id, typeId: "atlas.page", title: "Úvod", spaceId: "s" } });
  expect(objectApi.get).not.toHaveBeenCalled();
  expect(app.find("[data-content-detail-open]").exists()).toBe(true);
  expect(app.find("[data-content-detail-focus]").exists()).toBe(true);
});
