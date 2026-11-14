import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import type { SpaceCover } from "../../spaceCover";
import SpaceCoverImage from "./SpaceCoverImage.vue";

const cover = (value: Partial<SpaceCover>): SpaceCover => ({ source: "Auto", catalogKey: null, version: null, tinted: true, ...value });
const image = (value?: SpaceCover, src?: string) => mount(SpaceCoverImage, { props: { space: { id: "space-1", icon: "server", color: "teal-2", cover: value }, src } });

describe("SpaceCoverImage", () => {
  it("draws a drawing and a tinted upload in the space's colours, an untinted upload as it is, and the pattern where there is no image", () => {
    for (const drawing of [cover({ source: "Catalog", catalogKey: "it-backup" }), cover({ catalogKey: "it-backup" })]) {
      const wrapper = image(drawing);
      expect(wrapper.attributes("data-cover")).toBe("tinted");
      expect(wrapper.get("img.space-cover-image").attributes("src")).toContain("it-backup.svg");
      expect(wrapper.find(".space-cover-tint").exists()).toBe(true);
    }
    const upload = image(cover({ source: "Upload", version: "a1" }));
    expect(upload.attributes("data-cover")).toBe("tinted");
    expect(upload.get("img.space-cover-image").attributes("src")).toBe("/api/spaces/space-1/cover?v=a1");
    const photo = image(cover({ source: "Upload", version: "a1", tinted: false }));
    expect(photo.attributes("data-cover")).toBe("photo");
    expect(photo.find(".space-cover-tint").exists()).toBe(false);
    for (const none of [cover({ source: "None" }), cover({}), cover({ source: "Catalog", catalogKey: "no-such-drawing" }), undefined]) {
      const wrapper = image(none);
      expect(wrapper.attributes()).toMatchObject({ "data-cover": "pattern", "data-color": "teal-2", "aria-hidden": "true" });
      expect(wrapper.find("img").exists()).toBe(false);
      expect(wrapper.find(".space-cover-mark").exists()).toBe(true);
    }
  });

  it("falls back to the pattern when the image fails to load, and shows a given src instead of the stored image", async () => {
    const broken = image(cover({ source: "Catalog", catalogKey: "it-backup" }));
    await broken.get("img").trigger("error");
    expect(broken.attributes("data-cover")).toBe("pattern");
    expect(broken.find("img").exists()).toBe(false);
    // The file being uploaded, or a drawing previewed before it is saved.
    const given = image(cover({ source: "None" }), "blob:chosen-file");
    expect(given.attributes("data-cover")).toBe("tinted");
    expect(given.get("img").attributes()).toMatchObject({ src: "blob:chosen-file", loading: "lazy" });
  });
});
