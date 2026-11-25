import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import PageStatusBadge from "./PageStatusBadge.vue";

type Props = { isPublished: boolean; version: number; publishedVersion: number | null };
const draft: Props = { isPublished: false, version: 4, publishedVersion: null };
const behind: Props = { isPublished: true, version: 5, publishedVersion: 4 };
const published: Props = { isPublished: true, version: 4, publishedVersion: 4 };

describe("page status badge", () => {
  it("names the three page states with one vocabulary", () => {
    const states: [Props, string, string, string][] = [
      [draft, "draft", "Koncept", "neutral"],
      [behind, "unpublished", "Nepublikované změny", "warning"],
      [published, "published", "Publikováno", "success"],
    ];
    for (const [props, status, text, tone] of states) {
      const badge = mount(PageStatusBadge, { props });
      expect(badge.get(".status-badge").attributes("data-status")).toBe(status);
      expect(badge.get(".status-badge").attributes("data-tone")).toBe(tone);
      expect(badge.text()).toBe(text);
      badge.unmount();
    }
  });
  it("treats a missing published version as nothing published yet", () => {
    const badge = mount(PageStatusBadge, { props: { isPublished: true, version: 2, publishedVersion: null } });
    expect(badge.get(".status-badge").attributes("data-status")).toBe("unpublished");
    badge.unmount();
  });
  it("leaves the usual published state unmarked on request and still shows what needs attention", () => {
    // The lists of pages (library, space, dashboard) pass hidePublished.
    for (const [props, shown] of [[published, false], [draft, true], [behind, true]] as const) {
      const badge = mount(PageStatusBadge, { props: { ...props, hidePublished: true } });
      expect(badge.find(".status-badge").exists()).toBe(shown);
      badge.unmount();
    }
  });
});
