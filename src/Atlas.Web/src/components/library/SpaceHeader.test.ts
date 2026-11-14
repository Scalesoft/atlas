import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import type { Space } from "../../api";
import SpaceHeader from "./SpaceHeader.vue";

const space: Space = {
  id: "space-1", name: "Zálohy", icon: "server", color: "teal-2", role: "Reader", pinPolicy: "None", pinned: false, pageCount: 3, lastChangedAt: null,
  cover: { source: "Catalog", catalogKey: "it-backup", version: null, tinted: true },
};

describe("SpaceHeader", () => {
  // A space without an image keeps its plain head: the content stays first, no band of pattern above the name.
  it("shows the image of the space above its name only when the space has one", () => {
    const head = mount(SpaceHeader, { props: { space, pages: [] } });
    const cover = head.get(".space-hero.has-cover .space-hero-cover");
    expect(cover.attributes("data-cover")).toBe("tinted");
    expect(cover.get("img").attributes("src")).toContain("it-backup.svg");

    const plain = mount(SpaceHeader, { props: { space: { ...space, cover: { source: "Auto", catalogKey: null, version: null, tinted: true } }, pages: [] } });
    expect(plain.find(".space-hero-cover").exists()).toBe(false);
    expect(plain.get(".space-hero").classes()).not.toContain("has-cover");
  });

  // The pages arrive a moment after the head: until then the head says what the space list knows, not "0 stránek".
  it("counts the pages of the space from the space list until the page list has arrived", async () => {
    const head = mount(SpaceHeader, { props: { space, pages: [], pagesLoaded: false } });
    expect(head.get('[data-space-fact="pages"]').text()).toBe("3 stránky");
    expect(head.find('[data-space-fact="drafts"]').exists()).toBe(false);

    await head.setProps({ pages: [1, 2, 3, 4].map((n) => ({ id: `p${n}`, title: `P${n}`, isPublished: n !== 4 })) as never, pagesLoaded: true });
    expect(head.get('[data-space-fact="pages"]').text()).toBe("4 stránky");
    expect(head.get('[data-space-fact="drafts"]').text()).toBe("1 koncept");

    // A head that is told nothing about loading counts the pages it was given, as before.
    expect(mount(SpaceHeader, { props: { space, pages: [] } }).get('[data-space-fact="pages"]').text()).toBe("0 stránek");
  });
});
