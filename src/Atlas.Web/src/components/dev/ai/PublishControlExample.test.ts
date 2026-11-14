import { afterEach, expect, it } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import PublishControlExample from "./PublishControlExample.vue";

let wrapper: ReturnType<typeof mount>;
afterEach(() => { wrapper?.unmount(); document.body.innerHTML = ""; });

it("shows the publish control states and the validation of the change summary with local data", async () => {
  wrapper = mount(PublishControlExample, { attachTo: document.body });
  await flushPromises();
  expect(wrapper.findAll('button[aria-label="Publikovat s popisem změny…"]')).toHaveLength(9);
  // The page saves itself: the bars show the draft (koncept) with what readers see, the lock of the edits and Zveřejnit změny (no Hotovo step any more).
  expect(wrapper.findAll('[data-publication="draft"]')).toHaveLength(4);
  expect(wrapper.text()).toContain("Koncept – čtenáři vidí verzi z");
  expect(wrapper.text()).toContain("Koncept – zatím nepublikováno, čtenáři ji nevidí");
  expect(wrapper.find('[data-publication-state="live"]').text()).toBe("Živě");
  expect(wrapper.findAll(".publish-main").map(button => button.text())).toContain("Zveřejnit změny");
  expect(wrapper.find("[data-edit-lock]").exists()).toBe(true);
  expect(wrapper.findAll(".publish-main").map(button => button.text())).not.toContain("Hotovo");
  expect(wrapper.text()).toContain("Popis změny smí mít jeden řádek a nejvýše 500 znaků.");
  expect(wrapper.text()).toContain("36 / 500");
});
