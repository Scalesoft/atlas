import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import ContentDetail from "./ContentDetail.vue";
import ContactCard from "../contacts/ContactCard.vue";
import { createLiveObjects } from "../../objects/liveObjects";
import type { ObjectDto } from "../../objects/objectApi";
import type { DetailRow } from "../../objects/relationsApi";

const id = "88888888-8888-4888-8888-888888888888";
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.restoreAllMocks(); });
/** The directory's live objects over one contact: `data` as stored, `canEdit` the reader's right to change it. */
function directory(data: Record<string, unknown> | null, canEdit = false) {
  const get = vi.fn(async (objectId: string) => ({ id: objectId, typeId: "atlas.contact", title: String(data?.name ?? ""), version: 1, data, canEdit }) as unknown as ObjectDto);
  const change = vi.fn(async () => ({ version: 2, duplicate: false }));
  return { store: createLiveObjects({ get, change }), get, change };
}
async function show(row: DetailRow, store?: ReturnType<typeof directory>["store"], graph = true) {
  wrapper = mount(ContentDetail, { props: { tab: "overview" as const, row, store, graph } });
  await flushPromises(); await vi.dynamicImportSettled(); await flushPromises();
  return wrapper;
}
const contactRow = (title: string): DetailRow => ({ id, typeId: "atlas.contact", title, spaceId: "s" });

it("shows any contact's profile: its card, edited in place through the directory's store, and the account part (Vytvořit účet for an admin)", async () => {
  const { store, get, change } = directory({ name: "Ota Dodavatel", organization: "Partner s.r.o." }, true);
  const app = await show(contactRow("Ota Dodavatel"), store, false);
  expect(get).toHaveBeenCalledWith(id);
  const card = app.findComponent(ContactCard);
  expect([card.props("contact").name, card.props("editable")]).toEqual(["Ota Dodavatel", true]);
  card.vm.$emit("change", { title: "Obchodník" });
  await flushPromises();
  expect(change).toHaveBeenCalledWith(id, { fields: { title: "Obchodník" } });
  expect(store.get(id)?.data).toMatchObject({ title: "Obchodník" });
  // Someone outside the company: no activity or competences, which only an account has; the account part decides by itself who sees it.
  expect(app.findComponent({ name: "PersonActivity" }).exists()).toBe(false);
  expect(app.findComponent({ name: "SkillGraph" }).exists()).toBe(false);
  expect(app.findComponent({ name: "PersonAccount" }).props()).toMatchObject({ accountId: undefined, contact: { id, name: "Ota Dodavatel" } });
  // Every contact's profile names them once (the card) and has no object facts (Druh: Kontakt).
  expect(app.find(".content-detail-head").exists()).toBe(false);
  expect(app.find(".content-detail-facts").exists()).toBe(false);
  // A contact has no frame of its own to open; a host without a graph offers none.
  expect(app.find("[data-content-detail-open]").exists()).toBe(false);
  expect(app.find("[data-content-detail-focus]").exists()).toBe(false);
});

it("shows a colleague's competences graph, activity and account part, reads them again once an account is made, and opens their pages", async () => {
  const { store, get } = directory({ name: "Eva Dvořáková", directoryId: "acc-1" });
  const app = await show(contactRow("Eva Dvořáková"), store);
  expect(app.findComponent(ContactCard).props("editable")).toBe(false);
  expect(app.findComponent({ name: "SkillGraph" }).props("contactId")).toBe(id);
  const activity = app.findComponent({ name: "PersonActivity" });
  expect(activity.props("accountId")).toBe("acc-1");
  activity.vm.$emit("open", "page-1");
  expect(app.emitted("open")?.[0]).toEqual(["page-1", "atlas.page"]);
  const account = app.findComponent({ name: "PersonAccount" });
  expect(account.props("accountId")).toBe("acc-1");
  account.vm.$emit("created", { token: "x", expiresAt: "", purpose: "invitation" });
  await flushPromises();
  expect(get).toHaveBeenCalledTimes(2);
  // A person's profile: the card names them once, and an object's facts (Druh: Kontakt) say nothing about a person.
  expect(app.find(".content-detail-head").exists()).toBe(false);
  expect(app.find(".content-detail-facts").exists()).toBe(false);
});

it("reads nothing for a page and offers Otevřít and Ukázat v grafu", async () => {
  const { store, get } = directory(null);
  const app = await show({ id, typeId: "atlas.page", title: "Úvod", spaceId: "s" }, store);
  expect(get).not.toHaveBeenCalled();
  expect(app.find("[data-content-detail-open]").exists()).toBe(true);
  expect(app.find("[data-content-detail-focus]").exists()).toBe(true);
});
