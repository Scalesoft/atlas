import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PageOrganizationPanel from "./PageOrganizationPanel.vue";
import { ApiError, type Page } from "../api";
import { organizationApi, type OrganizationTree, type TrashList } from "../organization";
import { objectApi } from "../objects/objectApi";

vi.mock("../organization", () => ({ organizationApi: {
  tree: vi.fn(), trashList: vi.fn(), move: vi.fn(), moveSpace: vi.fn(), trash: vi.fn(), untrash: vi.fn(), rename: vi.fn(),
} }));
vi.mock("../objects/objectApi", () => ({ objectApi: { purge: vi.fn() } }));
let wrapper: VueWrapper | undefined;
const page: Page = {
  id: "page", spaceId: "space", parentId: null, title: "Příručka", version: 3,
  isPublished: true, publishedVersion: 2, contentJson: "live-rich", activeFrom: "2026-01-01T08:00:00Z", canEdit: true,
};
function tree(version = 7, pageVersion = 3): OrganizationTree {
  return { spaceId: "space", version, items: [
    { id: "page", parentId: null, title: "Příručka", version: pageVersion, sortOrder: 1024, isPublished: true, canEdit: true },
    { id: "child", parentId: "page", title: "Podstránka", version: 1, sortOrder: 1024, isPublished: false, canEdit: true },
    { id: "target", parentId: null, title: "Cíl", version: 1, sortOrder: 2048, isPublished: true, canEdit: true },
  ] };
}
function trashList(): TrashList {
  return { spaceId: "space", version: 7, hasMore: false, items: [
    { id: "trashed", parentId: "missing", title: "Smazaná", version: 2, trashedAt: "2026-09-06T12:00:00Z", trashedById: "admin", trashedByName: "Správce", trashBatchId: "batch" },
  ] };
}
async function open(overrides: Record<string, unknown> = {}) {
  vi.mocked(organizationApi.tree).mockResolvedValue(tree());
  vi.mocked(organizationApi.trashList).mockResolvedValue(trashList());
  wrapper = mount(PageOrganizationPanel, {
    props: { spaceId: "space", spaceRole: "Writer", isWikiAdmin: false, page, ...overrides }, attachTo: document.body,
  });
  await flushPromises();
  return wrapper;
}
afterEach(() => {
  wrapper?.unmount(); wrapper = undefined; vi.resetAllMocks(); document.body.innerHTML = "";
});

describe("page organization", () => {
  it("gives the tabs one thumb that is placed first and then follows the chosen tab", async () => {
    // jsdom has no layout: Obecné sits at 4 px (348 px wide), Umístění at 356 px (348 px wide).
    const left = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetLeft");
    const width = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetWidth");
    const location = (element: HTMLElement) => element.textContent === "Umístění";
    Object.defineProperty(HTMLElement.prototype, "offsetLeft", { configurable: true, get() { return location(this) ? 356 : 4; } });
    Object.defineProperty(HTMLElement.prototype, "offsetWidth", { configurable: true, get() { return this.tagName === "BUTTON" && this.closest(".organization-tabs") ? 348 : 0; } });
    const frame = () => new Promise<void>(done => requestAnimationFrame(() => done()));
    try {
      await open();
      await frame(); await frame(); await flushPromises();
      const track = wrapper!.get(".organization-tabs .option-pills-group");
      expect(track.attributes("data-segmented")).toBeDefined();
      expect(track.attributes("style")).toContain("--thumb-x: 4px");
      expect(track.attributes("style")).toContain("--thumb-w: 348px");
      expect(track.attributes("data-thumb")).toBe("ready");
      await wrapper!.get(".organization-tabs button:nth-child(2)").trigger("click");
      await flushPromises(); await frame();
      expect(track.attributes("style")).toContain("--thumb-x: 356px");
    } finally {
      if (left) Object.defineProperty(HTMLElement.prototype, "offsetLeft", left); else delete (HTMLElement.prototype as { offsetLeft?: number }).offsetLeft;
      if (width) Object.defineProperty(HTMLElement.prototype, "offsetWidth", width); else delete (HTMLElement.prototype as { offsetWidth?: number }).offsetWidth;
    }
  });
  it('finds a sibling position without a flat select and sends the chosen before-page ID', async () => {
    await open();
    await wrapper!.get('.organization-tabs button:nth-child(2)').trigger('click');
    expect(wrapper!.find('select#organization-before').exists()).toBe(false);
    await wrapper!.get('[aria-controls=organization-position-reveal]').trigger('click'); await flushPromises();
    await wrapper!.get('#organization-before input').setValue('Cil');
    await wrapper!.get('#organization-before [data-page-id="target"]').trigger('click');
    vi.mocked(organizationApi.move).mockResolvedValue({ version: 8, affectedIds: ['page'], hasSiblingTitleCollision: false });
    await wrapper!.get('form[aria-label="Přesunout stránku"]').trigger('submit'); await flushPromises();
    expect(organizationApi.move).toHaveBeenCalledWith('page', null, 'target', 7);
  });
  it("U14: places a board: only its location (no rename, trash or move to another space), sent to the board's own move", async () => {
    vi.mocked(organizationApi.tree).mockResolvedValue({ ...tree(), items: [...tree().items, { id: "board-1", parentId: "page", title: "Plán", version: 1, sortOrder: 4096, isPublished: true, canEdit: true, type: "board" }] });
    vi.mocked(organizationApi.trashList).mockResolvedValue(trashList());
    wrapper = mount(PageOrganizationPanel, { props: { spaceId: "space", spaceRole: "Writer", isWikiAdmin: false, board: { id: "board-1", title: "Plán" },
      spaces: [{ id: "space", name: "Source", role: "Writer" }, { id: "destination", name: "Target", role: "Writer" }] as never }, attachTo: document.body });
    await flushPromises();
    expect(wrapper.find('form[aria-label="Přejmenovat stránku"]').exists()).toBe(false);
    expect(wrapper.find(".organization-tabs").exists()).toBe(false);
    expect(wrapper.find(".organization-trash-trigger").exists()).toBe(false);
    expect(wrapper.find(".organization-transfer").exists()).toBe(false);
    expect(wrapper.get('#organization-parent [data-page-id="page"]').attributes("aria-selected")).toBe("true");
    expect(document.activeElement).toBe(wrapper.get('#organization-parent .location-search input').element);
    await wrapper.get('#organization-parent [data-page-id="target"]').trigger("click");
    vi.mocked(organizationApi.move).mockResolvedValue({ version: 8, affectedIds: ["board-1"], hasSiblingTitleCollision: false });
    await wrapper.get('form[aria-label="Přesunout tabuli"]').trigger("submit"); await flushPromises();
    expect(organizationApi.move).toHaveBeenCalledWith("board-1", "target", null, 7, "board");
    expect(wrapper.emitted("changed")?.[0]?.[0]).toEqual({ kind: "move", affectedIds: ["board-1"] });
  });
  it("U17: the Umístění tab puts the caret into the search of the parent picker", async () => {
    await open();
    await wrapper!.get('.organization-tabs button:nth-child(2)').trigger('click');
    await flushPromises();
    expect(document.activeElement).toBe(wrapper!.get('#organization-parent .location-search input').element);
  });
  it("separates location from general settings and asks before trashing a leaf page", async () => {
    await open({ page: { ...page, id: 'target', title: 'Cíl' } });
    expect(wrapper!.get('form[aria-label="Přejmenovat stránku"]').isVisible()).toBe(true);
    expect(wrapper!.get('form[aria-label="Přesunout stránku"]').isVisible()).toBe(false);
    await wrapper!.get('button[aria-label="Přesunout do koše"]').trigger('click');
    expect(organizationApi.trash).not.toHaveBeenCalled();
    expect(wrapper!.get('.organization-trash-confirm').text()).toContain('Cíl');
    vi.mocked(organizationApi.trash).mockResolvedValue({ version: 8, affectedIds: ['target'], hasSiblingTitleCollision: false });
    await wrapper!.get('.organization-trash-confirm form').trigger('submit'); await flushPromises();
    expect(organizationApi.trash).toHaveBeenCalledWith('target', false, 7, 1);
  });
  // T7: the page menu's „Přesunout do koše“ is the same ConfirmDialog as a board's, with the subpages' choice inside it.
  it("asks for the trash from the page menu in the shared confirmation, the subpages included only once chosen", async () => {
    await open({ startWithTrash: true });
    const dialog = wrapper!.getComponent({ name: "ConfirmDialog" });
    expect(dialog.props("title")).toBe("Přesunout stránku do koše?");
    const confirm = () => document.querySelector<HTMLButtonElement>("[data-confirm-action]")!;
    expect(confirm().disabled).toBe(true);
    const include = document.querySelector<HTMLInputElement>(".organization-check input[type=checkbox]")!;
    include.click(); await flushPromises();
    expect(confirm().disabled).toBe(false);
    vi.mocked(organizationApi.trash).mockResolvedValue({ version: 8, affectedIds: ['page', 'child'], hasSiblingTitleCollision: false });
    confirm().click(); await flushPromises();
    expect(organizationApi.trash).toHaveBeenCalledWith('page', true, 7, 3);
    expect(wrapper!.emitted('changed')).toEqual([[{ kind: 'trash', affectedIds: ['page', 'child'] }]]);
  });
  // The move to another space is a collapsed option: open it, then pick the space from the searchable list.
  async function pickTargetSpace(onlyOptions = false) {
    await wrapper!.get("[aria-controls=organization-transfer-reveal]").trigger("click"); await flushPromises();
    expect(wrapper!.findAll("#organization-target-space [data-value]").map(x => x.attributes("data-value"))).toEqual(["destination"]);
    if (!onlyOptions) await wrapper!.get("#organization-target-space [data-value=destination]").trigger("click");
  }
  it("requires destination access acknowledgement and subtree confirmation before a cross-space move", async () => {
    await open({ spaces: [{ id: "space", name: "Source", role: "Writer" }, { id: "destination", name: "Target", role: "Writer" }, { id: "reader", name: "Read only", role: "Reader" }] });
    await pickTargetSpace(true);
    vi.mocked(organizationApi.tree).mockResolvedValue({ spaceId: "destination", version: 11, items: [] });
    await wrapper!.get("#organization-target-space [data-value=destination]").trigger("click"); await flushPromises();
    const form = wrapper!.findAll("form").find(form => form.text().includes("Přesun do jiného prostoru"))!;
    await form.trigger("submit"); expect(organizationApi.moveSpace).not.toHaveBeenCalled();
    await wrapper!.get("#organization-ack-access").setValue(true);
    await form.trigger("submit"); expect(organizationApi.moveSpace).not.toHaveBeenCalled();
    await form.findAll("input[type=checkbox]")[0]!.setValue(true);
    vi.mocked(organizationApi.moveSpace).mockResolvedValue({ sourceSpaceId: "space", targetSpaceId: "destination", sourceVersion: 9, targetVersion: 13, affectedIds: ["page", "child"] });
    await form.trigger("submit"); await flushPromises();
    expect(organizationApi.moveSpace).toHaveBeenCalledWith("page", { targetSpaceId: "destination", parentId: null, beforePageId: null, includeSubtree: true, expectedSourceTreeVersion: 7, expectedTargetTreeVersion: 11, expectedVersion: 3, acknowledgeAccessChange: true });
    expect(wrapper!.emitted("changed")?.[0]?.[0]).toEqual({ kind: "move-space", sourceSpaceId: "space", targetSpaceId: "destination", affectedIds: ["page", "child"] });
    expect(wrapper!.emitted("close")).toHaveLength(1);
  });
  it("keeps the target and acknowledgement on cross-space CAS conflicts and reloads both tree versions", async () => {
    await open({ spaces: [{ id: "destination", name: "Target", role: "Writer" }] });
    vi.mocked(organizationApi.tree).mockResolvedValue({ spaceId: "destination", version: 11, items: [] });
    await pickTargetSpace(); await flushPromises();
    const form = wrapper!.findAll("form").find(form => form.text().includes("Přesun do jiného prostoru"))!;
    await form.findAll("input[type=checkbox]")[0]!.setValue(true); await wrapper!.get("#organization-ack-access").setValue(true);
    vi.mocked(organizationApi.moveSpace).mockRejectedValue(new ApiError(409, "Conflict", "tree_conflict"));
    await form.trigger("submit"); await flushPromises();
    expect(wrapper!.get("#organization-target-space [data-value=destination]").attributes("data-state")).toBe("checked");
    expect(wrapper!.get("#organization-ack-access").element).toHaveProperty("checked", true);
    vi.mocked(organizationApi.tree).mockImplementation(async id => id === "space" ? tree(12) : { spaceId: "destination", version: 15, items: [] });
    await wrapper!.get(".organization-refresh").trigger("click"); await flushPromises();
    expect(organizationApi.tree).toHaveBeenLastCalledWith("destination");
    expect(wrapper!.emitted("changed")).toBeUndefined();
  });
  it("keeps drafts on CAS conflicts and requires explicit reload before rename can retry", async () => {
    await open();
    await wrapper!.get("#organization-name").setValue("Můj nový název");
    vi.mocked(organizationApi.rename).mockRejectedValueOnce(new ApiError(409, "Conflict"));
    await wrapper!.findAll("form")[0]!.trigger("submit"); await flushPromises();
    expect(wrapper!.get("#organization-name").element).toHaveProperty("value", "Můj nový název");
    expect(wrapper!.get("fieldset").attributes("disabled")).toBeDefined();
    vi.mocked(organizationApi.tree).mockResolvedValue(tree(8, 4));
    await wrapper!.get(".organization-refresh").trigger("click"); await flushPromises();
    expect(wrapper!.get("#organization-name").element).toHaveProperty("value", "Můj nový název");
    vi.mocked(organizationApi.rename).mockResolvedValue({ ...page, title: "Můj nový název", version: 5 });
    await wrapper!.findAll("form")[0]!.trigger("submit"); await flushPromises();
    expect(organizationApi.rename).toHaveBeenLastCalledWith("page", "Můj nový název", 4);
    expect(wrapper!.emitted("renamed")?.[0]?.[0]).toMatchObject({ contentJson: "live-rich", version: 5 });
    expect(page.contentJson).toBe("live-rich");
  });

  it("excludes the page and descendants as move destinations and never submits without the host guard", async () => {
    const guard = vi.fn().mockResolvedValue(false);
    await open({ beforeMutation: guard });
    await wrapper!.get('.organization-tabs button:nth-child(2)').trigger('click');
    expect(wrapper!.get("#organization-parent").findAll('[role=treeitem]').map(item => item.attributes('data-page-id'))).toEqual(["", "target"]);
    await wrapper!.get('#organization-parent [data-page-id="target"]').trigger('click');
    await wrapper!.findAll("form")[1]!.trigger("submit"); await flushPromises();
    expect(guard).toHaveBeenCalledOnce(); expect(organizationApi.move).not.toHaveBeenCalled();
    guard.mockResolvedValue(true);
    vi.mocked(organizationApi.move).mockResolvedValue({ version: 8, affectedIds: ["page"], hasSiblingTitleCollision: false });
    await wrapper!.findAll("form")[1]!.trigger("submit"); await flushPromises();
    expect(organizationApi.move).toHaveBeenCalledWith("page", "target", null, 7);
    expect(wrapper!.emitted("changed")).toEqual([[{ kind: "move", affectedIds: ["page"] }]]);
  });

  it("requires explicit subtree confirmation for trash and carries the current content CAS", async () => {
    await open();
    await wrapper!.get('button[aria-label="Přesunout do koše"]').trigger('click');
    await wrapper!.findAll("form")[2]!.trigger("submit"); await flushPromises();
    expect(organizationApi.trash).not.toHaveBeenCalled();
    await wrapper!.get(".organization-trash input[type=checkbox]").setValue(true);
    vi.mocked(organizationApi.trash).mockResolvedValue({ version: 9, affectedIds: ["page", "child"], hasSiblingTitleCollision: false });
    await wrapper!.findAll("form")[2]!.trigger("submit"); await flushPromises();
    expect(organizationApi.trash).toHaveBeenCalledWith("page", true, 7, 3);
    expect(wrapper!.emitted("changed")).toEqual([[{ kind: "trash", affectedIds: ["page", "child"] }]]);
    expect(wrapper!.emitted("close")).toHaveLength(1);
  });

  it("does not request administration data for Readers or expose trash restoration to Writers", async () => {
    await open({ spaceRole: "Reader", page: { ...page, canEdit: false } });
    expect(organizationApi.tree).not.toHaveBeenCalled(); expect(wrapper!.find("form").exists()).toBe(false);
    await wrapper!.setProps({ page: undefined, spaceRole: "Writer" }); await flushPromises();
    expect(organizationApi.trashList).not.toHaveBeenCalled(); expect(wrapper!.find("form").exists()).toBe(false);
  });

  it("restores to an explicit active destination and shows a duplicate-title notice without replacing content", async () => {
    await open({ page: undefined, spaceRole: "Admin" });
    await wrapper!.get(".organization-trash-list button").trigger("click");
    expect(wrapper!.get('#organization-restore-parent [data-page-id=""]').attributes('aria-selected')).toBe('true');
    await wrapper!.get('#organization-restore-parent [data-page-id="target"]').trigger('click');
    vi.mocked(organizationApi.untrash).mockResolvedValue({ version: 8, affectedIds: ["trashed"], hasSiblingTitleCollision: true });
    await wrapper!.get("form").trigger("submit"); await flushPromises();
    expect(organizationApi.untrash).toHaveBeenCalledWith("trashed", "target", false, 7, undefined);
    expect(wrapper!.text()).toContain("obě zůstaly zachované");
    expect(wrapper!.emitted("changed")).toEqual([[{ kind: "untrash", affectedIds: ["trashed"] }]]);
    expect(wrapper!.emitted("renamed")).toBeUndefined();
  });

  it("lists a trashed board with the pages, restores it through its own endpoint and never offers a board as a parent", async () => {
    vi.mocked(organizationApi.tree).mockResolvedValue({ ...tree(), items: [...tree().items, { id: "plan", parentId: "target", title: "Plán", version: 0, sortOrder: 4096, isPublished: true, canEdit: true, type: "board" }] });
    vi.mocked(organizationApi.trashList).mockResolvedValue({ ...trashList(), items: [...trashList().items,
      { id: "old-board", parentId: "target", title: "Stará tabule", version: 0, trashedAt: "2026-10-26T12:00:00Z", trashedById: "admin", trashedByName: null, trashBatchId: "b2", type: "board" }] });
    wrapper = mount(PageOrganizationPanel, { props: { spaceId: "space", spaceRole: "Admin", isWikiAdmin: false }, attachTo: document.body });
    await flushPromises();
    const boardRow = wrapper.get('[data-trash-row="old-board"]');
    expect(boardRow.text()).toContain("Tabule · do koše"); expect(boardRow.get("[data-trash-restore]").attributes("aria-label")).toBe("Obnovit: Stará tabule");
    await boardRow.get("[data-trash-restore]").trigger("click"); await flushPromises();
    // Back where it was, under its page; the board in the tree is no place to restore to, and a board has no subpages.
    expect(wrapper.get('#organization-restore-parent [data-page-id="target"]').attributes("aria-selected")).toBe("true");
    expect(wrapper.find('#organization-restore-parent [data-page-id="plan"]').exists()).toBe(false);
    expect(wrapper.text()).not.toContain("Obnovit i podstránky");
    vi.mocked(organizationApi.untrash).mockResolvedValue({ version: 8, affectedIds: ["old-board"], hasSiblingTitleCollision: false });
    await wrapper.get("form").trigger("submit"); await flushPromises();
    expect(organizationApi.untrash).toHaveBeenCalledWith("old-board", "target", false, 7, "board");
    expect(wrapper.text()).toContain("Tabule byla obnovena na vybrané místo.");
  });

  it("deletes a page and a board permanently on the one object path, after asking, and reads the trash again after a page", async () => {
    vi.mocked(organizationApi.tree).mockResolvedValue(tree());
    vi.mocked(organizationApi.trashList).mockResolvedValue({ ...trashList(), items: [...trashList().items,
      { id: "old-board", parentId: "target", title: "Stará tabule", version: 0, trashedAt: "2026-10-26T12:00:00Z", trashedById: "admin", trashedByName: null, trashBatchId: "b2", type: "board" }] });
    vi.mocked(objectApi.purge).mockResolvedValue();
    wrapper = mount(PageOrganizationPanel, { props: { spaceId: "space", spaceRole: "Admin", isWikiAdmin: false }, attachTo: document.body });
    await flushPromises();
    const confirm = () => document.querySelector("[data-confirm-action]") as HTMLButtonElement;
    await wrapper.get('[data-trash-row="trashed"] [data-trash-purge]').trigger("click"); await flushPromises();
    expect(document.body.textContent).toContain("Podstránky, které šly do koše s ní");
    expect(objectApi.purge).not.toHaveBeenCalled();
    vi.mocked(organizationApi.trashList).mockClear();
    confirm().click(); await flushPromises();
    expect(objectApi.purge).toHaveBeenCalledWith("trashed");
    expect(organizationApi.trashList).toHaveBeenCalled();
    await wrapper.get('[data-trash-row="old-board"] [data-trash-purge]').trigger("click"); await flushPromises();
    confirm().click(); await flushPromises();
    expect(objectApi.purge).toHaveBeenLastCalledWith("old-board");
    expect(organizationApi.untrash).not.toHaveBeenCalled();
  });

  it("drops a late mutation response after permission is lost and Escape restores opener focus", async () => {
    const opener = document.createElement("button"); document.body.append(opener); opener.focus();
    await open();
    expect(wrapper!.get(".app-dialog-header h2").text()).toBe("Uspořádat stránku");
    // Focus starts on the panel's first control, not on the close button before it (keyboard pass).
    expect(wrapper!.get('dialog').element.contains(document.activeElement)).toBe(true);
    expect(document.activeElement).not.toBe(wrapper!.get('.app-dialog-header [aria-label="Zavřít"]').element);
    let resolve!: (value: Page) => void;
    vi.mocked(organizationApi.rename).mockReturnValue(new Promise(done => { resolve = done; }));
    await wrapper!.findAll("form")[0]!.trigger("submit"); await flushPromises();
    await wrapper!.setProps({ spaceRole: "Reader", page: { ...page, canEdit: false } });
    resolve({ ...page, version: 4 }); await flushPromises();
    expect(wrapper!.emitted("renamed")).toBeUndefined();
    await wrapper!.get("dialog").trigger("cancel");
    expect(wrapper!.emitted("close")).toHaveLength(1);
    wrapper!.unmount(); wrapper = undefined;
    expect(document.activeElement).toBe(opener);
  });

  it("titles the trash on its own and never leaks the internal organisation label", async () => {
    vi.mocked(organizationApi.tree).mockResolvedValue({ spaceId: "space", version: 3, items: [] });
    vi.mocked(organizationApi.trashList).mockResolvedValue({ spaceId: "space", version: 7, items: [], hasMore: false });
    wrapper = mount(PageOrganizationPanel, { props: { spaceId: "space", spaceRole: "Admin" as const, isWikiAdmin: false }, attachTo: document.body });
    await flushPromises();
    expect(wrapper.get(".app-dialog-header h2").text()).toBe("Koš stránek");
    expect(wrapper.get(".app-dialog-header button").attributes("aria-label")).toBe("Zavřít koš stránek");
    expect(wrapper.html()).not.toContain("Zavřít organizaci");
  });

  it("shows an empty trash as its empty state alone, without a restore hint or an empty list frame", async () => {
    vi.mocked(organizationApi.tree).mockResolvedValue({ spaceId: "space", version: 3, items: [] });
    vi.mocked(organizationApi.trashList).mockResolvedValue({ spaceId: "space", version: 7, items: [], hasMore: false });
    wrapper = mount(PageOrganizationPanel, { props: { spaceId: "space", spaceRole: "Admin" as const, isWikiAdmin: false }, attachTo: document.body });
    await flushPromises();
    expect(wrapper.text()).toContain("Koš stránek je prázdný");
    expect(wrapper.text()).not.toContain("Obnova zachová");
    expect(wrapper.find(".organization-trash-list ul").exists()).toBe(false);
  });
});
