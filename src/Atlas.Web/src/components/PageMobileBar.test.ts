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

  it("offers the page's Diskuse with its comment count on a phone, the unread ones accented", async () => {
    const bar = mount(PageMobileBar, { props: { discussion: 3 } });
    const discussion = bar.get("[data-page-mobile-discussion]");
    expect(discussion.text()).toContain("Diskuse");
    expect(discussion.attributes("aria-label")).toBe("Diskuse, komentářů: 3");
    expect(discussion.get(".page-mobile-count").text()).toBe("3");
    await discussion.trigger("click");
    expect(bar.emitted("discussion")).toHaveLength(1);
    expect(discussion.get(".page-mobile-count").attributes("data-fresh")).toBeUndefined();
    // A colleague's new comment: the unread count, accented, as on the top bar's button.
    await bar.setProps({ discussion: 12, unread: 1 });
    expect(bar.get(".page-mobile-count").text()).toBe("1");
    expect(bar.get(".page-mobile-count").attributes("data-fresh")).toBeDefined();
    expect(bar.get("[data-page-mobile-discussion]").attributes("aria-label")).toBe("Diskuse, komentářů: 12, nepřečtených: 1");
    await bar.setProps({ discussion: 0, unread: 0 });
    expect(bar.get("[data-page-mobile-discussion]").attributes("aria-label")).toBe("Diskuse");
  });
});
