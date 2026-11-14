import { afterEach, describe, expect, it } from "vitest";
import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import SettingsShell from "./SettingsShell.vue";
import type { SettingsNavGroup } from "./types";

enableAutoUnmount(afterEach);

const groups: SettingsNavGroup[] = [{ id: "a", items: [{ path: "one", label: "Jedna", icon: "layout-dashboard" }] }];
const mountShell = (content: string) => mount(SettingsShell, { props: { title: "Nastavení", groups, navLabel: "Nastavení", currentPath: "one" }, slots: { default: content }, attachTo: document.body });

afterEach(() => { delete document.documentElement.dataset.mode; });

describe("SettingsShell eases the colours inside it when the theme changes", () => {
  it("marks itself with data-theming for the length of the fade when the mode or the accent changes", async () => {
    const wrapper = mountShell("<p>stránka</p>");
    expect(wrapper.get(".settings-shell").attributes("data-theming")).toBeUndefined();
    document.documentElement.dataset.mode = "dark";
    await flushPromises();
    expect(wrapper.get(".settings-shell").attributes("data-theming")).toBe("");
  });

  it("leaves a long page alone: easing the colours of a thousand elements costs the first frame more than the fade is worth", async () => {
    // Measured on the people table (1,080 elements): the switch took 333 ms in its first frame (at a quarter of the speed 2.5 s) and
    // averaged 34 ms a frame against 17 without it; the profile (117 elements) costs nothing.
    const wrapper = mountShell(`<div>${"<span>řádek</span>".repeat(700)}</div>`);
    document.documentElement.dataset.mode = "dark";
    await flushPromises();
    expect(wrapper.get(".settings-shell").attributes("data-theming")).toBeUndefined();
  });
});

describe("the rail from the keyboard (keyboard pass)", () => {
  const many: SettingsNavGroup[] = [{ id: "a", items: [
    { path: "people", label: "Lidé", icon: "users" }, { path: "groups", label: "Skupiny", icon: "users" }, { path: "ai", label: "AI", icon: "ai" },
  ] }];
  it("opens the first section „Hledat sekci“ leaves with Enter, steps onto it with ↓ and walks the sections with the arrows", async () => {
    const rail = document.createElement("div");
    rail.id = "atlas-settings-rail";
    document.body.append(rail);
    const wrapper = mount(SettingsShell, { props: { title: "Správa wiki", groups: many, navLabel: "Správa wiki", currentPath: "people" }, slots: { default: "<p>obsah</p>" }, attachTo: document.body });
    await flushPromises();
    const search = rail.querySelector<HTMLInputElement>('input[type="search"]')!;
    search.value = "sku"; search.dispatchEvent(new Event("input"));
    await flushPromises();
    const opened: string[] = [];
    rail.addEventListener("click", event => { opened.push((event.target as Element).closest("a")?.textContent?.trim() ?? ""); event.preventDefault(); });
    // The section Enter opens is marked while the search is typed in, and the first Esc empties the search (P3, P8).
    expect([...rail.querySelectorAll("[data-first]")].map(link => link.textContent?.trim())).toEqual(["Skupiny"]);
    search.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    expect(opened).toEqual(["Skupiny"]);
    const escape = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
    search.dispatchEvent(escape);
    await flushPromises();
    expect(search.value).toBe("");
    expect(escape.defaultPrevented).toBe(true);
    expect(rail.querySelectorAll("[data-first]")).toHaveLength(0);
    search.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    expect(opened).toEqual(["Skupiny"]);
    search.value = ""; search.dispatchEvent(new Event("input"));
    await flushPromises();
    search.focus();
    search.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true, cancelable: true }));
    expect(document.activeElement?.textContent).toContain("Lidé");
    document.activeElement!.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true, cancelable: true }));
    expect(document.activeElement?.textContent).toContain("Skupiny");
    wrapper.unmount();
    rail.remove();
  });
});
