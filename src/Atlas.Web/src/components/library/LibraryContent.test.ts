import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { TooltipProvider } from "../ui/tooltip";
import type { Space } from "../../api";
import LibraryContent from "./LibraryContent.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });
const props: InstanceType<typeof LibraryContent>["$props"] = { view: "spaces", userId: "u1", spaces: [] as Space[], canCreate: true, canManage: false, pendingIds: new Set<string>(), limit: 200, refreshKey: 0, personalRefreshKey: 0, notificationSignal: 0, spacesLoaded: false };
const render = (extra: Record<string, unknown> = {}) =>
  (wrapper = mount(defineComponent({ setup: () => () => h(TooltipProvider, null, () => h(LibraryContent, { ...props, ...extra })) }), { attachTo: document.body }));

it("shows a placeholder, not the empty state, until the first list of spaces has arrived", async () => {
  const view = render();
  await flushPromises();
  expect(view.find('[role="status"]').exists()).toBe(true);
  expect(view.find("[data-space-empty]").exists()).toBe(false);
});

it("says there are no spaces only once the list has loaded empty", async () => {
  const view = render({ spacesLoaded: true });
  await import("../SpaceDirectory.vue");
  await flushPromises(); await flushPromises();
  expect(view.find("[data-space-empty]").exists()).toBe(true);
});
