import { afterEach, expect, it } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { reactive } from "vue";
import AiQuotaFields from "./AiQuotaFields.vue";
import { emptyQuotas } from "./aiAccess";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); document.body.innerHTML = ""; });

it("retains a custom quota across disclosure choices without applying it to inherited or unlimited modes", async () => {
  const draft = reactive(emptyQuotas());
  wrapper = mount(AiQuotaFields, { props: { draft }, attachTo: document.body });
  const row = wrapper.get('[data-quota="chatAnswerDailyLimit"]');
  expect(row.find("input").exists()).toBe(false);
  await row.get('[data-value="custom"]').trigger("click");
  await row.get("input").setValue("47");
  await row.get('[data-value="inherit"]').trigger("click");
  expect(draft.chatAnswerDailyLimit).toBeNull();
  await row.get('[data-value="custom"]').trigger("click");
  expect((row.get("input").element as HTMLInputElement).value).toBe("47");
  expect(draft.chatAnswerDailyLimit).toBe(47);
  await row.get('[data-value="unlimited"]').trigger("click");
  expect(draft.chatAnswerDailyLimit).toBe(0);
  await row.get('[data-value="custom"]').trigger("click");
  expect(draft.chatAnswerDailyLimit).toBe(47);
  // An unfinished custom value stays unfinished, never silently becoming unlimited or the default 20.
  await row.get("input").setValue("");
  await row.get('[data-value="inherit"]').trigger("click");
  expect(draft.chatAnswerDailyLimit).toBeNull();
  await row.get('[data-value="custom"]').trigger("click");
  expect(draft.chatAnswerDailyLimit).toBe(-1);
  expect(row.get("input").attributes("aria-invalid")).toBe("true");
});
