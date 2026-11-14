import type { MarkdownPlugin } from "@atlas/markdown/core";
import { h, render } from "vue";
import { watchOverviewLevels, type OverviewJson } from "./overviewKinds";
import OverviewReadCard from "./OverviewReadCard.vue";

/**
 * The overviews of a page that is read (the static rendering): each kind's read card in place of the block's line, and the reader's
 * level of the kinds shown on the page's components (`pageId` keeps it per page, as in the editor). An overview another plugin has
 * drawn already (the requirements' read view, renderRequirements, runs before this one) keeps its card: only a block that still
 * holds its bare line is taken. One level watcher per kind and page root, however many overviews of the kind the page holds.
 */
export function createOverviewReading(pageId: () => string | undefined): MarkdownPlugin {
  return {
    id: "atlas.overview-reading", apiVersion: 1, profiles: ["rich"],
    create: () => ({
      onRender({ container, document }) {
        const cards = [...container.querySelectorAll<HTMLElement>("[data-overview][data-kind]")].filter(element => !element.childElementCount);
        if (!cards.length) return;
        const scope = pageId() ?? "", kinds = new Set(cards.map(element => element.dataset.kind ?? ""));
        const stop = watchOverviewLevels(container, scope, kind => kinds.has(kind));
        cards.forEach(element => { element.textContent = ""; render(h(OverviewReadCard, { kind: element.dataset.kind ?? "", scope, document: document as OverviewJson }), element); });
        return () => { stop(); cards.forEach(element => render(null, element)); };
      },
    }),
  };
}
