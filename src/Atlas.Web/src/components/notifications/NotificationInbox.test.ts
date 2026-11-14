import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { nextTick, ref } from "vue";
import NotificationInbox from "./NotificationInbox.vue";
import { inboxOpened, unreadNotifications, useUnreadNotifications } from "../../notifications";
import { groupByDay, notificationLink, type NotificationApi, type NotificationItem } from "./notificationInbox";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

const now = new Date("2026-10-29T12:00:00Z");
const item = (id: string, updatedAt: string, read = false, extra: Partial<NotificationItem> = {}): NotificationItem => ({
  id, kind: "comment.mention", subjectId: `page-${id}`, subjectType: "atlas.page", subjectTitle: `Stránka ${id}`, spaceId: "s", spaceName: "Vývoj",
  anchor: null, actorId: "a", actorName: "Jana Nováková", count: 1, createdAt: updatedAt, updatedAt, read, ...extra,
});
function fakeApi(items: NotificationItem[]) {
  const state = { items };
  const unread = () => state.items.filter(entry => !entry.read).length;
  const api = {
    list: vi.fn(async () => ({ items: state.items, next: null, unread: unread() })),
    unread: vi.fn(async () => ({ count: unread() })),
    markRead: vi.fn(async (id: string) => { state.items = state.items.map(entry => (entry.id === id ? { ...entry, read: true } : entry)); return { marked: 1, unread: unread() }; }),
    markAllRead: vi.fn(async () => { const marked = unread(); state.items = state.items.map(entry => ({ ...entry, read: true })); return { marked, unread: 0 }; }),
    preferences: vi.fn(async () => ({ items: [{ kind: "comment.mention", enabled: true }, { kind: "page.mention", enabled: false }, { kind: "item.shared", enabled: true }] })),
    setPreference: vi.fn(async (kind: string, enabled: boolean) => ({ kind, enabled })),
  } satisfies NotificationApi;
  return { api, state };
}

describe("notification inbox", () => {
  it("groups by day, marks the unread, lands focus on the first unread and opens a row at its anchor marking it read", async () => {
    const { api } = fakeApi([
      item("a", "2026-10-29T10:00:00Z", true),
      item("b", "2026-10-29T09:00:00Z", false, { anchor: "postup", count: 3 }),
      item("c", "2026-10-28T09:00:00Z", false, { subjectType: "atlas.board" }),
      item("d", "2026-10-01T09:00:00Z", true, { subjectType: "atlas.contact" }),
      item("e", "2026-10-01T08:00:00Z", true, { kind: "item.unshared", subjectType: null, subjectTitle: "", spaceName: null }),
    ]);
    wrapper = mount(NotificationInbox, { props: { api, now }, attachTo: document.body });
    await flushPromises(); await nextTick();

    expect(wrapper.findAll(".notification-group-label").map(label => label.text())).toEqual(["Dnes", "Včera", "Starší"]);
    expect(wrapper.findAll("[data-unread]").map(row => row.attributes("data-notification"))).toEqual(["b", "c"]);
    expect(document.activeElement?.getAttribute("data-notification")).toBe("b");
    expect(wrapper.get('[data-notification="b"]').attributes("href")).toContain("heading=postup");
    expect(wrapper.get('[data-notification="c"]').attributes("href")).toContain("#/boards/page-c");
    // A type the shell opens by no address is a button that only marks it read.
    expect(wrapper.get('[data-notification="d"]').element.tagName).toBe("BUTTON");
    expect(wrapper.get('[data-notification="b"]').text()).toContain("3×");
    // An ended share names no item and opens nothing.
    expect(wrapper.get('[data-notification="e"]').text()).toContain("Položka, ke které už nemáte přístup");
    expect(wrapper.get('[data-notification="e"]').element.tagName).toBe("BUTTON");
    expect(wrapper.emitted("unread")?.at(-1)).toEqual([2]);

    // ↓ walks the rows.
    await wrapper.get('[data-notification="b"]').trigger("keydown", { key: "ArrowDown" });
    expect(document.activeElement?.getAttribute("data-notification")).toBe("c");

    await wrapper.get('[data-notification="b"]').trigger("click");
    await flushPromises();
    expect(api.markRead).toHaveBeenCalledWith("b");
    expect(wrapper.find('[data-notification="b"][data-unread]').exists()).toBe(false);
    expect(wrapper.emitted("unread")?.at(-1)).toEqual([1]);
  });

  it("marks everything read, shows the empty state, and lets a new event rise in on the live signal", async () => {
    const { api, state } = fakeApi([item("a", "2026-10-29T10:00:00Z")]);
    wrapper = mount(NotificationInbox, { props: { api, now, refreshKey: 0 }, attachTo: document.body });
    await flushPromises();
    (wrapper.get("[data-notifications-read-all]").element as HTMLElement).focus();
    await wrapper.get("[data-notifications-read-all]").trigger("click");
    await flushPromises();
    expect(api.markAllRead).toHaveBeenCalled();
    expect(wrapper.findAll("[data-unread]")).toHaveLength(0);
    expect(wrapper.get("[data-notifications-read-all]").attributes("disabled")).toBeDefined();
    // The disabled button gives its focus to the first row instead of dropping it on the page.
    expect(document.activeElement?.getAttribute("data-notification")).toBe("a");

    state.items = [item("new", "2026-10-29T11:00:00Z"), ...state.items];
    await wrapper.setProps({ refreshKey: 1 });
    await flushPromises();
    expect(wrapper.get('li:has([data-notification="new"])').attributes("data-arrived")).toBeDefined();
    expect(wrapper.find('li:has([data-notification="a"])').attributes("data-arrived")).toBeUndefined();

    wrapper.unmount();
    wrapper = mount(NotificationInbox, { props: { api: { ...api, list: vi.fn(async () => ({ items: [], next: null, unread: 0 })) }, now } });
    await flushPromises();
    expect(wrapper.text()).toContain("Žádná oznámení");
  });

  it("shows a sized skeleton while loading and switches a kind off in its settings", async () => {
    const { api } = fakeApi([item("a", "2026-10-29T10:00:00Z")]);
    let release!: () => void;
    const list = api.list;
    api.list = vi.fn(() => new Promise(resolve => { release = () => resolve(list()); })) as typeof api.list;
    wrapper = mount(NotificationInbox, { props: { api, now } });
    await nextTick();
    expect(wrapper.find(".loading-skeleton").exists()).toBe(true);
    release(); await flushPromises();
    expect(wrapper.find(".loading-skeleton").exists()).toBe(false);

    await wrapper.get(".notification-settings-toggle").trigger("click");
    await flushPromises();
    expect(api.preferences).toHaveBeenCalledTimes(1);
    await wrapper.get('[data-preference="comment.mention"]').trigger("click");
    await flushPromises();
    expect(api.setPreference).toHaveBeenCalledWith("comment.mention", false);
  });
});

describe("notification helpers", () => {
  it("links pages at their heading, boards by id and a failed job to its page", () => {
    expect(notificationLink({ subjectId: "p", subjectType: "atlas.page", anchor: "h" })).toContain("#/pages/p?heading=h");
    expect(notificationLink({ subjectId: "b", subjectType: "atlas.board", anchor: null })).toContain("#/boards/b");
    expect(notificationLink({ subjectId: "c", subjectType: "atlas.contact", anchor: null })).toBeUndefined();
    expect(notificationLink({ subjectId: "n", subjectType: null, anchor: null })).toBeUndefined();
    // A failed job opens its page in Správa wiki; its id is the anchor.
    expect(notificationLink({ subjectId: "j", subjectType: "atlas.job", anchor: "backups.zip" })).toBe("#/settings/general/jobs/backups.zip");
  });

  it("groups consecutive items by day", () => {
    expect(groupByDay([item("a", "2026-10-29T08:00:00Z"), item("b", "2026-10-25T08:00:00Z"), item("c", "2026-10-26T08:00:00Z")], now)
      .map(group => [group.label, group.items.length])).toEqual([["Dnes", 1], ["Tento týden", 2]]);
  });

  it("keeps the badge current over the account's live channel, reading the count once per signal", async () => {
    const handlers: { message?: (event: string, payload: unknown) => void; subscribed?: () => void } = {};
    const live = { route: vi.fn(), subscribe: vi.fn((_name: string, listener: typeof handlers) => { Object.assign(handlers, listener); return { close: vi.fn(), presence: vi.fn(), join: vi.fn() }; }) };
    let count = 2;
    const api = { unread: vi.fn(async () => ({ count })) };
    let state: ReturnType<typeof useUnreadNotifications> | undefined;
    const userId = ref<string | undefined>("u1");
    wrapper = mount({ setup() { state = useUnreadNotifications(userId, { api, live: () => live as never, idle: work => work() }); return () => null; } });
    await flushPromises();
    expect(live.subscribe).toHaveBeenCalledWith("user:u1", expect.anything());
    // The count is read once the channel is subscribed, not also before it.
    expect(api.unread).not.toHaveBeenCalled();
    handlers.subscribed?.();
    await flushPromises();
    expect(api.unread).toHaveBeenCalledTimes(1);
    expect(state!.unread.value).toBe(2);
    expect(unreadNotifications.value).toBe(2);
    count = 5;
    handlers.message?.("NotificationsChanged", { channel: "user:u1" });
    await flushPromises();
    expect(state!.signal.value).toBe(1);
    expect(state!.unread.value).toBe(5);
    // Another event of the channel (a security event) is not the inbox's.
    handlers.message?.("secretAccessRevoked", { secretId: "s" });
    expect(state!.signal.value).toBe(1);

    // An open inbox reloads its list with the signal and reports the count itself: the badge reads nothing more.
    const close = inboxOpened();
    handlers.message?.("NotificationsChanged", { channel: "user:u1" });
    await flushPromises();
    expect([state!.signal.value, api.unread.mock.calls.length]).toEqual([2, 2]);
    close();
    close();
    handlers.message?.("NotificationsChanged", { channel: "user:u1" });
    await flushPromises();
    expect(api.unread).toHaveBeenCalledTimes(3);
  });

  it("subscribes the account's channel again after the server revoked it, and not after the account signed out (ARCH3-7)", async () => {
    const handlers: { revoked?: () => void; subscribed?: () => void } = {};
    const live = { route: vi.fn(), subscribe: vi.fn((_name: string, listener: typeof handlers) => { Object.assign(handlers, listener); return { close: vi.fn(), presence: vi.fn(), join: vi.fn() }; }) };
    const api = { unread: vi.fn(async () => ({ count: 1 })) };
    const userId = ref<string | undefined>("u1");
    wrapper = mount({ setup() { useUnreadNotifications(userId, { api, live: () => live as never, idle: work => work() }); return () => null; } });
    await flushPromises();
    expect(live.subscribe).toHaveBeenCalledTimes(1);
    const revoked = handlers.revoked!;
    revoked();
    expect(live.subscribe).toHaveBeenCalledTimes(2);
    expect(live.subscribe).toHaveBeenLastCalledWith("user:u1", expect.anything());
    handlers.subscribed?.();
    await flushPromises();
    expect(api.unread).toHaveBeenCalledTimes(1);
    userId.value = undefined;
    await flushPromises();
    handlers.revoked!();
    expect(live.subscribe).toHaveBeenCalledTimes(2);
  });
});
