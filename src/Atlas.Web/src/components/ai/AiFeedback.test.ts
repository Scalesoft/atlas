import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import AiFeedback from "./AiFeedback.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });
const menu = () => document.querySelector("[data-ai-feedback-menu]");
const reason = (code: string) => document.querySelector<HTMLButtonElement>(`[data-reason='${code}']`)!;

it("rates up in one click and asks a reason only for thumbs down", async () => {
  wrapper = mount(AiFeedback, { attachTo: document.body });
  await wrapper.get("[data-ai-feedback-up]").trigger("click");
  await flushPromises();
  expect(wrapper.emitted("rate")).toEqual([["up"]]);
  expect(wrapper.get("[data-ai-feedback-up]").attributes("aria-pressed")).toBe("true");
  // no thank-you, no menu
  expect(menu()).toBeNull();
  expect(document.body.textContent).not.toMatch(/děkujeme|díky/i);

  await wrapper.get("[data-ai-feedback-down]").trigger("click");
  await flushPromises();
  expect(wrapper.emitted("rate")).toEqual([["up"], ["down"]]);
  expect(wrapper.get("[data-ai-feedback-up]").attributes("aria-pressed")).toBe("false");
  expect(menu()).not.toBeNull();
  // anchored menu without a title and without a close button
  expect(menu()!.querySelector("h1, h2, h3, h4, [data-slot='popover-close']")).toBeNull();
  expect([...document.querySelectorAll("[role='radio']")].map((r) => r.textContent)).toEqual(["Nepřesné", "Chybí informace", "Špatné zdroje", "Nevhodný návrh", "Jiné"]);

  reason("wrong_sources").click();
  await flushPromises();
  const note = document.querySelector<HTMLTextAreaElement>("[data-ai-feedback-note]")!;
  expect(note.getAttribute("maxlength")).toBe("500");
  note.value = "  Chybí odkaz na směrnici.  ";
  note.dispatchEvent(new Event("input"));
  await flushPromises();
  document.querySelector<HTMLButtonElement>("[data-ai-feedback-send]")!.click();
  await flushPromises();
  expect(wrapper.emitted("feedback")).toEqual([[{ rating: "down", reason: "wrong_sources", note: "Chybí odkaz na směrnici." }]]);
  expect(menu()).toBeNull();
  expect(wrapper.get("[data-ai-feedback-down]").attributes("aria-pressed")).toBe("true");
});

it("confirms a new rating where the eye is: the pressed thumb settles in once, and a restored or repeated rating does not", async () => {
  const animate = vi.fn(() => ({ finished: Promise.resolve() }));
  Object.defineProperty(SVGElement.prototype, "animate", { configurable: true, writable: true, value: animate });
  try {
    wrapper = mount(AiFeedback, { props: { rating: "up" }, attachTo: document.body });
    expect(animate).not.toHaveBeenCalled(); // a saved rating is not news
    await wrapper.get("[data-ai-feedback-up]").trigger("click");
    expect(animate).not.toHaveBeenCalled(); // already up
    await wrapper.get("[data-ai-feedback-down]").trigger("click");
    expect(animate).toHaveBeenCalledTimes(1);
    const thumb = animate.mock.contexts[0] as unknown as Element;
    expect(thumb.closest("[data-ai-feedback-down]")).not.toBeNull();
    await wrapper.get("[data-ai-feedback-down]").trigger("click"); // only closes the menu
    expect(animate).toHaveBeenCalledTimes(1);
  } finally {
    Reflect.deleteProperty(SVGElement.prototype, "animate");
  }
});

it("restores a saved rating", async () => {
  wrapper = mount(AiFeedback, { props: { rating: "up" }, attachTo: document.body });
  expect(wrapper.get("[data-ai-feedback-up]").attributes("aria-pressed")).toBe("true");
});
