import { afterEach, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, nextTick, ref } from "vue";
import CollaboratorAvatars from "../components/CollaboratorAvatars.vue";
import type { ObjectListener } from "../live/viewSession";
import { keepPeers, pagePresenceHeartbeatMs, usePageViewPresence } from "./usePageViewPresence";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); });

function fakeViews() {
  const watched = new Map<string, ObjectListener>();
  const view = { watch: vi.fn((id: string, listener: ObjectListener) => { watched.set(id, listener); return () => watched.delete(id); }), viewport: vi.fn(), presence: vi.fn(), close: vi.fn() };
  return { views: { open: vi.fn(() => view) }, view, watched };
}

it("tells the page's views while it is edited, and shows who works at the page through another view", async () => {
  vi.useFakeTimers();
  const { views, view, watched } = fakeViews();
  const edited = ref<string | undefined>("P1");
  wrapper = mount(defineComponent(() => {
    const elsewhere = usePageViewPresence(() => edited.value, views);
    return () => h(CollaboratorAvatars, { peers: elsewhere.value });
  }), { attachTo: document.body });
  expect(views.open).toHaveBeenCalledWith("page:p1");
  expect(view.presence).toHaveBeenLastCalledWith("p1", { editing: true });
  await vi.advanceTimersByTimeAsync(pagePresenceHeartbeatMs);
  expect(view.presence).toHaveBeenCalledTimes(2);

  const at = (userId: string, displayName: string, via: string | null) =>
    watched.get("p1")!.presence!({ objectId: "p1", userId, displayName, color: "#2a9d8f", payload: { editing: true }, via });
  at("u2", "Jana", "board:b1");
  at("u3", "Petr", "page:p1");
  at("u4", "Eva", null);
  await nextTick();
  expect(wrapper.findAll(".collaborator-avatar").map(avatar => avatar.attributes("aria-label"))).toEqual(["Jana upravuje přes tabuli", "Eva upravuje jinde"]);

  edited.value = undefined;
  await nextTick();
  expect(view.presence).toHaveBeenLastCalledWith("p1", { editing: false });
  expect(view.close).toHaveBeenCalledTimes(1);
  await vi.advanceTimersByTimeAsync(pagePresenceHeartbeatMs * 3);
  expect(view.presence).toHaveBeenCalledTimes(3);
});

it("keeps the avatars' list while a status republishes the same peers, so they do not redraw and measure (perf guard)", () => {
  const ada = { clientId: 1, id: "u1", name: "Ada", color: "#d946ef", cursorAvailable: true };
  const shown = keepPeers(undefined, [ada]);
  expect(keepPeers(shown, [{ ...ada }])).toBe(shown);
  const moved = [{ ...ada, cursorAvailable: false }];
  expect(keepPeers(shown, moved)).toBe(moved);
  const joined = [ada, { ...ada, clientId: 2, id: "u2", name: "Petr" }];
  expect(keepPeers(shown, joined)).toBe(joined);
});
