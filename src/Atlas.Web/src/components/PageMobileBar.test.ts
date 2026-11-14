import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import PageMobileBar from "./PageMobileBar.vue";

describe("PageMobileBar", () => {
  it("offers only the actions the reader has, as one labelled navigation", async () => {
    const reader = mount(PageMobileBar, { props: { favorite: true } });
    expect(reader.get("nav").attributes("aria-label")).toBe("Akce stránky");
    expect(reader.findAll("button").map((button) => button.text())).toEqual(["Číst", "Oblíbené"]);
    expect(reader.get("button[aria-current=page]").text()).toBe("Číst");
    expect(reader.get("button[aria-pressed]").attributes("aria-pressed")).toBe("true");

    const writer = mount(PageMobileBar, { props: { canAsk: true, lockable: true, locked: true } });
    const buttons = writer.findAll("button");
    expect(buttons.map((button) => button.text())).toEqual(["Číst", "Asistent", "Oblíbené", "Odemknout"]);
    // The lock in place of Upravit (3.6-F): pressed while the page is locked, named by what it does.
    expect(writer.get("[data-page-mobile-lock]").attributes()).toMatchObject({ "aria-pressed": "true", "aria-label": "Odemknout úpravy" });
    for (const button of buttons) await button.trigger("click");
    expect(Object.keys(writer.emitted())).toEqual(expect.arrayContaining(["read", "ask", "favorite", "lock"]));
    await writer.setProps({ locked: false });
    expect(writer.get("[data-page-mobile-lock]").attributes("aria-label")).toBe("Zamknout úpravy");
  });
});
