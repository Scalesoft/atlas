import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import { draftWording } from "../../publishing/pagePublication";
import PagePublicationState from "./PagePublicationState.vue";

const publishedAt = new Date(new Date().getFullYear(), 10, 5, 9).toISOString();

describe("draftWording", () => {
  it("names the version readers read, before and after its date is known, and a page never published", () => {
    expect(draftWording(true, publishedAt)).toEqual({
      label: "Koncept – čtenáři vidí verzi z 5. 11.",
      publishHint: "Zveřejní koncept čtenářům; do té doby vidí verzi z 5. 11.",
      publishedLabel: "Publikovaná verze z 5. 11. – takhle stránku vidí čtenáři",
    });
    expect(draftWording(true).label).toBe("Koncept – čtenáři vidí publikovanou verzi");
    expect(draftWording(false, null).label).toBe("Koncept – zatím nepublikováno, čtenáři ji nevidí");
  });
});

describe("PagePublicationState", () => {
  it("says the draft with the automatic publication's rule and keeps the full sentence for the tooltip", () => {
    const wrapper = mount(PagePublicationState, { props: { state: "draft", published: true, publishedAt, automatic: true } });
    expect(wrapper.get(".page-publication-full").text()).toBe("Koncept – čtenáři vidí verzi z 5. 11. · sám se zveřejní po 30 min bez úprav");
    expect(wrapper.attributes("title")).toBe("Koncept – čtenáři vidí verzi z 5. 11.; sám se zveřejní po 30 min bez úprav");
    expect(wrapper.find(".page-publication-switch").exists()).toBe(false);
  });

  it("switches Koncept | Publikovaná with pressed buttons and returns with Zpět ke konceptu", async () => {
    const wrapper = mount(PagePublicationState, { props: { state: "draft", published: true, publishedAt, canViewPublished: true, viewingPublished: false } });
    const [draft, published] = wrapper.findAll(".page-publication-switch button");
    expect([draft!.attributes("aria-pressed"), published!.attributes("aria-pressed")]).toEqual(["true", "false"]);
    await published!.trigger("click");
    expect(wrapper.emitted("update:viewingPublished")).toEqual([[true]]);
    await wrapper.setProps({ viewingPublished: true });
    expect(wrapper.attributes("data-publication-state")).toBe("published-view");
    expect(wrapper.get(".page-publication-full").text()).toBe("Publikovaná verze z 5. 11. – takhle stránku vidí čtenáři");
    await wrapper.get("[data-publication-back]").trigger("click");
    expect(wrapper.emitted("update:viewingPublished")).toEqual([[true], [false]]);
  });

  it("says Živě for a live page", () => {
    const wrapper = mount(PagePublicationState, { props: { state: "live", published: true } });
    expect(wrapper.get('[data-publication-state="live"]').text()).toBe("Živě");
  });
});
