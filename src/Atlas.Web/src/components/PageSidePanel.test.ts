import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import PageSidePanel from "./PageSidePanel.vue";
import type { PageHistory } from "../history";
import type { PageAccess } from "../sharing";

const base = { pageId: "p1", spaceId: "s1", pageTitle: "Článek", documentRoot: null, version: 3, isPublished: true, publishedVersion: 2, updatedAt: "2026-09-23T08:00:00Z" };
const connections = { get: vi.fn(async () => ({ linksHere: { items: [{ id: "p2", spaceId: "s1", spaceName: "Tým", title: "Odkazující" }], total: 1 } })), relations: vi.fn(async () => null) };
const revision = (version: number, extra = {}) => ({ id: `r${version}`, version, title: "Článek", authorId: "u1", authorName: "Jana", activeFrom: "2026-09-20T10:00:00Z", activeTo: null, isCurrent: false, isPublished: false, lastPublishedAt: null, ...extra });
afterEach(() => { vi.clearAllMocks(); document.body.innerHTML = ""; });

const publication = (extra = {}) => ({ mode: null, spaceMode: "Manual", effective: "Manual", publishedVersion: 2, publishedAt: "2026-11-03T13:05:00Z", publishedBy: "Jana", publishedAutomatically: false, unpublishedChanges: false, canChange: true, ...extra });
const checked = (wrapper: { get: (selector: string) => { attributes: (name: string) => string | undefined } }) => wrapper.get("[data-publication-choice] [aria-checked=true]").attributes("data-value");
const publicationApi = (state = publication()) => ({ get: vi.fn(async () => state as never), set: vi.fn(async (_: string, mode: string | null) => ({ ...state, mode, effective: mode ?? state.spaceMode }) as never) });

const verification = (over = {}) => ({ state: "expiring", stewardId: "u1", stewardName: "Jana Malá", verifiedAt: "2026-11-03T08:00:00Z", verifiedUntil: "2027-05-03T08:00:00Z", verifiedVersion: 1, changedSinceVerification: true, intervalMonths: null, spaceIntervalMonths: 6, ...over });
describe("PageSidePanel Diskuse", () => {
  it("shows the page's thread (the one a board has) with the page's own comments and view session", async () => {
    const api = { page: vi.fn(async () => ({ items: [], hasMore: false, count: 0, canPost: true })), post: vi.fn(), edit: vi.fn(), remove: vi.fn(), counts: vi.fn(async () => ({})) };
    const views = { open: vi.fn(() => ({ watch: () => () => {}, viewport() {}, presence() {}, close() {} })) };
    const wrapper = mount(PageSidePanel, { props: { ...base, tab: "comments", commentApi: api, views }, attachTo: document.body });
    await vi.waitFor(() => expect(wrapper.find("[data-comment-thread]").exists()).toBe(true));
    await flushPromises();
    expect(api.page).toHaveBeenCalledWith("p1");
    expect(views.open).toHaveBeenCalledWith("page:p1");
    expect(wrapper.find("[data-comment-composer]").exists()).toBe(true);
    wrapper.unmount();
  });
});

describe("PageSidePanel", () => {
  it("shows the owner, the verification and the 90-day readership, opens the dialog and lets an editor change the interval (5.2)", async () => {
    const verificationApi = { change: vi.fn(async (_: string, change: object) => verification(change) as never), report: vi.fn() };
    const wrapper = mount(PageSidePanel, { props: { ...base, tab: "outline", connections, publicationApi: publicationApi(), verification: verification() as never, canEdit: true, verificationApi, readers: 12 } });
    await flushPromises();
    expect(wrapper.get("[data-verification-steward]").text()).toBe("Jana Malá");
    expect(wrapper.get("[data-verification-row]").text()).toContain("do 3. 5. 2027");
    expect(wrapper.get("[data-verification-row]").text()).toContain("Změněno po ověření (v. 1 → 2)");
    expect(wrapper.get("[data-verification-readers]").text()).toBe("12 čtenářů za 90 dní");
    await wrapper.get("[data-verification-readers]").trigger("click");
    expect(wrapper.emitted("openReadership")).toHaveLength(1);
    await wrapper.get("[data-verification-steward-open]").trigger("click");
    expect(wrapper.emitted("openVerification")).toHaveLength(1);
    expect(wrapper.get("[data-page-verification-interval] [aria-checked=true]").attributes("data-value")).toBe("inherit");
    expect(wrapper.get("[data-page-verification-interval] [data-value=inherit]").text()).toBe("Podle prostoru (6 měsíců)");
    await wrapper.get("[data-page-verification-interval] [data-value='12']").trigger("click");
    await flushPromises();
    expect(verificationApi.change).toHaveBeenCalledWith("p1", { intervalMonths: 12 });
    await wrapper.setProps({ verification: verification({ intervalMonths: 12 }) as never });
    await wrapper.get("[data-page-verification-interval] [data-value=inherit]").trigger("click");
    await flushPromises();
    expect(verificationApi.change).toHaveBeenLastCalledWith("p1", { inheritInterval: true });
    expect(wrapper.emitted("verification")![0]![0]).toMatchObject({ intervalMonths: 12 });
  });

  it("shows a reader only the facts, and nothing of verification while it is off", async () => {
    const reader = mount(PageSidePanel, { props: { ...base, tab: "outline", connections, publicationApi: publicationApi(), verification: verification({ stewardName: null }) as never, readers: 1 } });
    await flushPromises();
    expect(reader.get("[data-verification-steward]").text()).toBe("bez garanta");
    expect(reader.find("[data-page-verification-interval]").exists()).toBe(false);
    expect(reader.get("[data-verification-readers]").text()).toBe("1 čtenář za 90 dní");
    const off = mount(PageSidePanel, { props: { ...base, tab: "outline", connections, publicationApi: publicationApi(), verification: verification({ state: "off", spaceIntervalMonths: null }) as never, readers: undefined } });
    await flushPromises();
    expect(off.find("[data-verification-steward]").exists()).toBe(false);
    expect(off.find("[data-verification-readers]").exists()).toBe(false);
  });

  it("shows the publication, lets an editor change it and shows a reader only the state", async () => {
    const api = publicationApi();
    const wrapper = mount(PageSidePanel, { props: { ...base, tab: "outline", connections, publicationApi: api } });
    expect(wrapper.find("[data-page-publication]").text()).toContain("Načítání publikace");
    await flushPromises();
    // The editor reads the mode from the choice alone (F23: said once).
    expect(wrapper.find("[data-publication-effective]").exists()).toBe(false);
    expect(checked(wrapper)).toBe("inherit");
    expect(wrapper.get("[data-publication-last]").text()).toContain("Publikováno");
    expect(wrapper.get("[data-publication-choice] [data-value=inherit]").text()).toBe("Podle prostoru (Ručně)");
    await wrapper.get("[data-publication-choice] [data-value=Automatic]").trigger("click");
    await flushPromises();
    expect(api.set).toHaveBeenCalledWith("p1", "Automatic");
    expect(checked(wrapper)).toBe("Automatic");

    const reader = mount(PageSidePanel, { props: { ...base, tab: "outline", connections, publicationApi: publicationApi(publication({ canChange: false, publishedAt: null, unpublishedChanges: true })) } });
    await flushPromises();
    expect(reader.find("[data-publication-choice]").exists()).toBe(false);
    expect(reader.get("[data-publication-last]").text()).toBe("Ještě nepublikováno");
    expect(reader.get("[data-publication-effective]").text()).toBe("Ručně (podle prostoru)");
    // „Verze“ says the unpublished changes; the publication does not say them a second time.
    expect(reader.find("[data-publication-changes]").exists()).toBe(false);
  });

  it("keeps the last publication on screen across a page switch until the new one arrives, unchangeable meanwhile (dock CLS)", async () => {
    let answer!: (value: never) => void;
    const api = publicationApi();
    const wrapper = mount(PageSidePanel, { props: { ...base, tab: "outline", connections, publicationApi: api } });
    await flushPromises();
    api.get.mockReturnValueOnce(new Promise(resolve => { answer = resolve; }));
    await wrapper.setProps({ pageId: "p2" });
    await flushPromises();
    expect(wrapper.find("[data-page-publication]").text()).not.toContain("Načítání publikace");
    expect(checked(wrapper)).toBe("inherit");
    expect(wrapper.get("[data-publication-choice] [data-value=Automatic]").attributes("disabled")).toBeDefined();
    answer(publication({ effective: "Automatic", mode: "Automatic" }) as never);
    await flushPromises();
    expect(checked(wrapper)).toBe("Automatic");
    expect(wrapper.get("[data-publication-choice] [data-value=Automatic]").attributes("disabled")).toBeUndefined();
  });

  it("shows the outline, the connections and the page facts on Obsah", async () => {
    const wrapper = mount(PageSidePanel, { props: { ...base, tab: "outline", connections, publicationApi: publicationApi() } });
    await flushPromises();
    expect(wrapper.find("[data-connections-list]").text()).toContain("Odkazující");
    expect(wrapper.find("[data-connections-list]").text()).toContain("Odkazuje sem");
    expect(wrapper.get(".page-side-facts").text()).toContain("3 · koncept, čtenáři vidí verzi 2");
  });

  it("summarises the latest versions and opens the full history", async () => {
    const items = [revision(9, { isCurrent: true, changeSummary: "Nová sekce" }), ...[8, 7, 6, 5, 4, 3].map((v) => revision(v, { isPublished: v === 7 }))];
    const history = { list: vi.fn(async (): Promise<PageHistory> => ({ pageId: "p1", currentVersion: 9, items, nextBeforeVersion: null })) };
    const wrapper = mount(PageSidePanel, { props: { ...base, tab: "history", history, connections } });
    await flushPromises();
    expect(history.list).toHaveBeenCalledWith("p1", null, expect.any(AbortSignal));
    const rows = wrapper.findAll(".page-side-revision");
    expect(rows).toHaveLength(6);
    expect(rows[0]!.text()).toContain("Verze 9");
    expect(rows[0]!.text()).toContain("aktuální");
    expect(rows[0]!.text()).toContain("Nová sekce");
    expect(rows[2]!.text()).toContain("publikovaná");
    await wrapper.get("[data-open-history]").trigger("click");
    await rows[1]!.trigger("click");
    expect(wrapper.emitted("openHistory")).toHaveLength(2);
    expect(wrapper.emitted("openHistory")![1]).toEqual([items[1]]);
  });

  it("summarises access for a manager and only the own access for a reader", async () => {
    const manager: PageAccess = { spaceId: "s1", spaceName: "Tým", isPublished: true, canManage: true, myAccess: {} as PageAccess["myAccess"], items: [], hasMore: false,
      mode: "custom", counts: { manage: 1, edit: 2, read: 4, hidden: 0, loseOnReset: 0, downgradeOnReset: 0, defaultGrants: 0, losing: [] }, groups: [{ groupId: "g", name: "Vývoj", memberCount: 3, role: "Writer", sources: [] }], shareLinks: { active: 2, nextExpiry: null } };
    const sharing = { pageAccess: vi.fn(async () => manager) };
    const wrapper = mount(PageSidePanel, { props: { ...base, tab: "access", sharing, connections } });
    await flushPromises();
    const text = wrapper.text();
    expect(text).toContain("Vlastní přístup");
    expect(text).toContain("7 lidí");
    expect(text).toContain("Vývoj");
    expect(text).toContain("2 aktivní anonymní");
    await wrapper.get("[data-open-access]").trigger("click");
    expect(wrapper.emitted("openAccess")).toHaveLength(1);

    const reader = { pageAccess: vi.fn(async (): Promise<PageAccess> => ({ ...manager, canManage: false, mode: null, counts: null, groups: null, shareLinks: null,
      myAccess: { userId: "u", displayName: "Čtenář", username: "c", role: "Reader", isWikiAdmin: false, canReadPublished: true, canReadDraft: false, canEdit: false, sources: [] } })) };
    const own = mount(PageSidePanel, { props: { ...base, tab: "access", sharing: reader, connections } });
    await flushPromises();
    expect(own.text()).toContain("Můžete číst publikovanou verzi.");
    expect(own.get("[data-open-access]").text()).toBe("Zobrazit můj přístup");
  });

  it("waits with the deferred loading line, keeps the way on, and lets the answer arrive in the same box", async () => {
    let answer!: (value: PageHistory) => void;
    const history = { list: vi.fn(() => new Promise<PageHistory>((resolve) => { answer = resolve; })) };
    const wrapper = mount(PageSidePanel, { props: { ...base, tab: "history", history, connections } });
    await flushPromises();
    // LoadingNote fades in only after the defer, so a fast answer never flashes „Načítání historie…“.
    const note = wrapper.get(".page-side-result .loading-note");
    expect(note.text()).toBe("Načítání historie…");
    expect(note.attributes("role")).toBe("status");
    expect(wrapper.find("[data-open-history]").exists()).toBe(true);
    answer({ pageId: "p1", currentVersion: 2, items: [revision(2, { isCurrent: true })], nextBeforeVersion: null });
    await flushPromises();
    expect(wrapper.find(".loading-note").exists()).toBe(false);
    expect(wrapper.get(".page-side-result .page-side-revision").text()).toContain("Verze 2");
  });

  it("reports a failed load in place", async () => {
    const history = { list: vi.fn(async () => { throw new Error("Server neodpovídá"); }) };
    const wrapper = mount(PageSidePanel, { props: { ...base, tab: "history", history, connections } });
    await flushPromises();
    expect(wrapper.get("[role=alert]").text()).toContain("Server neodpovídá");
    expect(wrapper.find("[data-open-history]").exists()).toBe(true);
  });
});
