import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { nextTick, ref } from "vue";
import PartyInput from "./PartyInput.vue";

let wrapper: VueWrapper | undefined;
beforeEach(() => vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] }));
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; vi.useRealTimers(); });

const rows = [{ id: "a", value: "Jan Novák (Modelsoft)", description: "Dva lidé tohoto jména" }, { id: "b", value: "Jan Novák (AB)", description: "Dva lidé tohoto jména" }];
function show() {
  const value = ref("");
  wrapper = mount(PartyInput, { attachTo: document.body, props: { modelValue: value.value, "onUpdate:modelValue": (next: string) => { value.value = next; void wrapper!.setProps({ modelValue: next }); },
    suggest: vi.fn(async () => rows) } });
  return value;
}
const input = () => wrapper!.get("input");
async function type(text: string) { await input().setValue(text); await vi.advanceTimersByTimeAsync(200); await nextTick(); }

it("offers the known names as they are typed; Enter takes the first, the arrows another", async () => {
  const value = show();
  await type("novak");
  expect(input().attributes("aria-expanded")).toBe("true");
  await input().trigger("keydown", { key: "ArrowDown" });
  await input().trigger("keydown", { key: "Enter" });
  expect(value.value).toBe("Jan Novák (AB)");
  expect(input().attributes("aria-expanded")).toBe("false");
});

it("saves the form around with the one Enter that picks (3.5.1 B9), not with Tab", async () => {
  const value = show();
  const form = document.createElement("form"), submit = vi.fn((event: Event) => event.preventDefault());
  form.addEventListener("submit", submit);
  document.body.append(form);
  form.append(wrapper!.element);
  await wrapper!.unmount();
  const inside = ref("");
  wrapper = mount(PartyInput, { attachTo: form, props: { modelValue: "", "onUpdate:modelValue": (next: string) => { inside.value = next; void wrapper!.setProps({ modelValue: next }); }, suggest: vi.fn(async () => rows) } });
  await type("novak");
  await input().trigger("keydown", { key: "Tab" });
  await nextTick();
  expect(submit).not.toHaveBeenCalled();
  await type("novak2");
  await input().trigger("keydown", { key: "Enter" });
  await nextTick();
  expect(inside.value).toBe("Jan Novák (Modelsoft)");
  expect(submit).toHaveBeenCalledTimes(1);
  expect(value.value).toBe("");
});

it("takes the highlighted name on Tab and lets Esc close the list, keeping what was typed", async () => {
  const value = show();
  await type("nov");
  await input().trigger("keydown", { key: "Escape" });
  expect(value.value).toBe("nov");
  expect(input().attributes("aria-expanded")).toBe("false");
  await type("novak");
  await input().trigger("keydown", { key: "Tab" });
  expect(value.value).toBe("Jan Novák (Modelsoft)");
});

// PR2-1 (UX cycle 2): „senc“ + Enter typed faster than the people lookup answers saved „senc“ as the party; now the Enter waits for the answer.
it("lets an Enter typed before the suggestions came take the first name when they do, and saves what was typed when there is none", async () => {
  const value = ref("");
  let answer: (found: typeof rows) => void = () => undefined;
  const submit = vi.fn((event: Event) => event.preventDefault());
  wrapper = mount({ components: { PartyInput }, data: () => ({ text: "" }), methods: { submit },
    template: `<form @submit="submit"><PartyInput v-model="text" :suggest="suggest" /></form>`, setup: () => ({ suggest: () => new Promise<typeof rows>(resolve => { answer = resolve; }) }) }, { attachTo: document.body });
  const field = wrapper.get("input");
  await field.setValue("novak");
  await vi.advanceTimersByTimeAsync(200);
  const enter = new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true });
  field.element.dispatchEvent(enter);
  expect(enter.defaultPrevented).toBe(true); // not the form's submit with „novak“
  expect(submit).not.toHaveBeenCalled();
  answer(rows);
  await vi.advanceTimersByTimeAsync(0);
  await nextTick();
  expect((field.element as HTMLInputElement).value).toBe("Jan Novák (Modelsoft)");
  expect(submit).toHaveBeenCalledTimes(1); // the one Enter picked and saved (3.5.1 B9)
  void value;
  // No known name: the waiting Enter saves what was typed.
  await field.setValue("nikdo");
  await vi.advanceTimersByTimeAsync(200);
  field.element.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
  answer([]);
  await vi.advanceTimersByTimeAsync(0);
  await nextTick();
  expect(submit).toHaveBeenCalledTimes(2);
});

it("teaches a picked directory person what was typed once the form applies the party; a cancelled form, a page's name or an older answer teach nothing", async () => {
  const learn = vi.fn(), submit = vi.fn((event: Event) => event.preventDefault());
  const martin = { id: "obj:m", value: "Marek Musil", description: "Vývoj", contactId: "c-m" }, michal = { id: "obj:x", value: "Michal Macek", description: "Provoz", contactId: "c-x" };
  const suggest = async (text: string) => (text === "MM" ? [michal] : [martin, { id: "party:x", value: "MMu tým", description: "Na této stránce" }]);
  // No v-model: jsdom flushes no microtasks between the input's listeners, so a parent's model would reach the lookup a key late.
  wrapper = mount({ components: { PartyInput }, methods: { submit },
    template: `<form @submit="submit"><PartyInput model-value="" :suggest="suggest" :learn="learn" /></form>`, setup: () => ({ suggest, learn }) }, { attachTo: document.body });
  const field = wrapper.get("input"), typed = () => (field.element as HTMLInputElement).value;
  const apply = () => wrapper!.get("form").element.dispatchEvent(new Event("submit", { cancelable: true }));
  const typeIn = async (text: string) => { await field.setValue(text); await vi.advanceTimersByTimeAsync(200); await nextTick(); };
  await typeIn("MMu");
  await field.trigger("keydown", { key: "Tab" });
  expect(typed()).toBe("Marek Musil");
  // Picked, not applied: Esc or „Zrušit“ now would leave the contact as it was.
  expect(learn).not.toHaveBeenCalled();
  apply();
  expect(learn).toHaveBeenCalledExactlyOnceWith({ contact: { name: "Marek Musil" }, objectId: "c-m" }, "MMu");
  learn.mockClear();
  await typeIn("MMu");
  await field.trigger("keydown", { key: "ArrowDown" });
  await field.trigger("keydown", { key: "Tab" });
  expect(typed()).toBe("MMu tým");
  apply();
  // „MM“ listed Michal; „u“ and Tab before the new answer take that older row, which teaches nobody „MMu“.
  await typeIn("MM");
  await field.setValue("MMu");
  await field.trigger("keydown", { key: "Tab" });
  expect(typed()).toBe("Michal Macek");
  apply();
  expect(learn).not.toHaveBeenCalled();
});

it("↓ with the list closed only offers the names: the party and its contact stay, so leaving the panel saves nothing", async () => {
  const party = vi.fn(), contact = vi.fn();
  wrapper = mount(PartyInput, { attachTo: document.body, props: { modelValue: "Jan Novák (AB)", contactId: "c-1", "onUpdate:modelValue": party, "onUpdate:contactId": contact,
    suggest: vi.fn(async () => rows) } });
  await input().trigger("keydown", { key: "ArrowDown" });
  await vi.advanceTimersByTimeAsync(200);
  await nextTick();
  expect(input().attributes("aria-expanded")).toBe("true");
  expect(party).not.toHaveBeenCalled();
  expect(contact).not.toHaveBeenCalled();
});

it("shows the known names at once (P17) and lets the answer refine them", async () => {
  let answer!: (found: typeof rows) => void;
  const suggest = Object.assign(vi.fn(() => new Promise<typeof rows>(resolve => { answer = resolve; })), { cached: vi.fn((text: string) => rows.filter(row => !text || row.value.includes("Modelsoft"))) });
  wrapper = mount(PartyInput, { attachTo: document.body, props: { modelValue: "", suggest } });
  const options = () => [...document.querySelectorAll("[data-testid=requirement-party-list] [role=option]")].map(option => option.textContent);
  expect(options()).toHaveLength(2);
  await input().setValue("mod");
  expect(input().attributes("aria-expanded")).toBe("true");
  expect(options()).toEqual([expect.stringContaining("Jan Novák (Modelsoft)")]);
  await vi.advanceTimersByTimeAsync(200);
  answer(rows);
  await vi.advanceTimersByTimeAsync(0);
  await nextTick();
  expect(options()).toHaveLength(2);
});
