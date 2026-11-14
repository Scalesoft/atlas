<script setup lang="ts">
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { reactive } from "vue";
import AtlasIcon from "../components/AtlasIcon.vue";
import PagePreview from "../components/boards/preview/PagePreview.vue";
import t from "../i18n/projector";
import { collapseHeight, revealHeight } from "../motion/aiMotion";
import { pageBlockLink, pageLink } from "../pageLinks";
import { deploymentMap, orderSection, progress, rollup, type DeploymentBlock, type DeploymentCategory, type DeploymentModule, type DeploymentProduct, type DeploymentService } from "./deploymentMap";
import { requirementName } from "./requirementModel";
import { requirementStateLabel, stateText } from "./requirementSummary";
import "./requirements.css";

/**
 * A deployment's product or service on a board (plan 3.4 §5): a page card with the display `product` (anchor: the product heading's
 * id) or `service` (anchor: the slug of the service's name, deploymentMap.serviceAnchor), drawn Projector's way from the page,
 * live: PagePreview loads it with the reader's rights, follows its changes and shows the skeleton meanwhile.
 * - Product: its infrastructure (the blocks right under its heading) as tags, then its categories (foldable, the reader's own view)
 *   with a row per module: state, name, done/total.
 * - The order's own section (deploymentMap.orderSection: contract, schedule, accesses…) is no system: its product card is the board's
 *   summary, the whole page's progress and state over that section's requirements as tags, tinted apart from the systems.
 * - Service: its state, its inputs as tags, each once whichever product uses it, and the products that share it.
 * A module opens the page at its heading and a tag at its block, where the requirement is written (comments, PM actions).
 * Choose it over the page preview („Náhled“) when the board should show where the deployment stands rather than the page's text;
 * „Poskládat tabuli“ of the requirements overview lays both out with their connectors (composeDeploymentBoard.ts).
 * The preview's `load` and `live` pass through to PagePreview (the gallery's sample page).
 */
const props = defineProps<{ pageId: string; display: "product" | "service"; anchor: string }>();

/** `page`: every block of the page and how many products follow, on the card of the order's own section. */
type Shown = { product: DeploymentProduct; page?: { blocks: DeploymentBlock[]; products: number } } | { service: DeploymentService };
function select(value: MarkdownRichValue): Shown | null {
  const map = deploymentMap(value.document as Parameters<typeof deploymentMap>[0]);
  if (props.display === "product") {
    const product = map.products.find(entry => entry.anchor === props.anchor);
    return product ? { product, ...(orderSection(map) === product && { page: { blocks: map.products.flatMap(entry => entry.blocks), products: map.products.length - 1 } }) } : null;
  }
  const service = map.services.find(entry => entry.anchor === props.anchor);
  return service ? { service } : null;
}
// The slot's value as a list of none or one, so the template names it once.
const productIn = (shown: unknown) => { const { product, page } = shown as Partial<Extract<Shown, { product: unknown }>>; return product ? [{ product, page }] : []; };
const serviceIn = (shown: unknown) => { const service = (shown as Partial<{ service: DeploymentService }>).service; return service ? [service] : []; };

const done = (blocks: readonly DeploymentBlock[]) => { const { done: finished, total } = progress(blocks); return `${finished}/${total}`; };
const modules = (product: DeploymentProduct) => product.categories.reduce((sum, category) => sum + category.modules.length, 0);
const tagTitle = (block: DeploymentBlock) => [`${requirementName(block)} — ${stateText(block.attrs)}`, block.attrs.party].filter(Boolean).join(" · ");
// What the tone and the dots say, for a screen reader too.
const moduleLabel = (module: DeploymentModule) => `${module.name} — ${requirementStateLabel(rollup(module.blocks))}, ${t.board.done(done(module.blocks))}`;
const categoryLabel = (category: DeploymentCategory) =>
  `${category.name} — ${requirementStateLabel(rollup(category.modules.flatMap(module => module.blocks)))}, ${t.counts.modules(category.modules.length)}`;

// Folded categories: the reader's own view of this card, never stored.
const folded = reactive(new Set<string>());
const toggle = (name: string) => (folded.has(name) ? folded.delete(name) : folded.add(name));
const settle = (animation: Animation | null, end: () => void) => { if (animation) animation.finished.then(end, end); else end(); };
const unfold = (el: Element, end: () => void) => settle(revealHeight(el as HTMLElement), end);
const fold = (el: Element, end: () => void) => settle(collapseHeight(el as HTMLElement), end);
</script>

<template>
  <PagePreview :page-id="pageId" :select="select" :missing="display === 'product' ? t.board.productGone : t.board.serviceGone">
    <template #default="{ shown }">
      <div v-for="{ product, page } in productIn(shown)" :key="product.anchor" class="rqcard dcard" :class="{ 'dcard-order': page }" data-deployment-card="product" :data-deployment-order="page ? '' : undefined">
        <header class="dcard-head">
          <span class="dcard-icon"><AtlasIcon :name="page ? 'clipboard-list' : 'server'" :size="16" /></span>
          <span class="dcard-title"><strong>{{ product.name }}</strong>
            <small v-if="page">{{ t.counts.products(page.products) }} · {{ t.board.overall(done(page.blocks)) }}</small>
            <small v-else>{{ t.counts.modules(modules(product)) }} · {{ t.board.done(done(product.blocks)) }}</small></span>
          <span class="dcard-pill" :data-tone="rollup(page?.blocks ?? product.blocks)">{{ requirementStateLabel(rollup(page?.blocks ?? product.blocks)) }}</span>
        </header>
        <ul v-if="product.base.length" class="dcard-tags" aria-label="Infrastruktura">
          <li v-for="block in product.base" :key="block.id"><a class="dcard-tag" :data-tone="block.attrs.state" :href="pageBlockLink(pageId, block.id)" :title="tagTitle(block)" :aria-label="tagTitle(block)">{{ requirementName(block) }}</a></li>
        </ul>
        <section v-for="category in product.categories" :key="category.name" class="dcard-cat" data-deployment-category>
          <button type="button" class="dcard-cat-head" :aria-expanded="!folded.has(category.name)" :aria-label="categoryLabel(category)" @click="toggle(category.name)">
            <AtlasIcon name="chevron-down" :size="14" class="dcard-chevron" />
            <span class="dcard-cat-name">{{ category.name }}</span>
            <span class="dcard-dots" aria-hidden="true"><i v-for="module in category.modules" :key="module.anchor" class="dcard-dot" :data-tone="rollup(module.blocks)" /></span>
            <span class="dcard-count">{{ category.modules.length }}</span>
          </button>
          <Transition :css="false" @enter="unfold" @leave="fold">
            <ul v-if="!folded.has(category.name)" class="dcard-mods">
              <li v-for="module in category.modules" :key="module.anchor">
                <a class="dcard-mod" :data-tone="rollup(module.blocks)" :href="pageLink(pageId, module.anchor)" :title="moduleLabel(module)" :aria-label="moduleLabel(module)" data-deployment-module>
                  <i class="dcard-dot" aria-hidden="true" /><span class="dcard-mod-name">{{ module.name }}</span><span class="dcard-badge">{{ done(module.blocks) }}</span>
                </a>
              </li>
            </ul>
          </Transition>
        </section>
      </div>
      <div v-for="service in serviceIn(shown)" :key="service.anchor" class="rqcard dcard dcard-service" data-deployment-card="service">
        <header class="dcard-head">
          <span class="dcard-icon"><AtlasIcon name="plug" :size="16" /></span>
          <span class="dcard-title"><strong>{{ service.name }}</strong>
            <small>{{ t.counts.inputs(service.inputs.length) }} · {{ t.board.done(done(service.inputs)) }}</small></span>
          <span class="dcard-pill" :data-tone="rollup(service.inputs)">{{ requirementStateLabel(rollup(service.inputs)) }}</span>
        </header>
        <ul class="dcard-tags" aria-label="Vstupy">
          <li v-for="block in service.inputs" :key="block.id"><a class="dcard-tag" :data-tone="block.attrs.state" :href="pageBlockLink(pageId, block.id)" :title="tagTitle(block)" :aria-label="tagTitle(block)">{{ requirementName(block) }}</a></li>
        </ul>
        <p v-if="service.products.length > 1" class="dcard-shared"><AtlasIcon name="link" :size="12" />{{ service.products.join(t.board.and) }} — {{ t.board.filledOnce }}</p>
      </div>
    </template>
  </PagePreview>
</template>

<style scoped>
/* Projector's deployment diagram in Atlas's tokens: the state's tone (requirements.css, data-tone) on dots, pills, tags and badges. */
.dcard { display: grid; align-content: start; gap: 8px; min-width: 0; font-size: 13px; }
/* The order's summary is no system: its whole card is tinted (the card's room is PagePreview's scroller). */
:global(.page-preview-scroll:has(> .dcard-order)) { background: color-mix(in oklab, var(--accent) 7%, var(--surface)); }
.dcard-order .dcard-icon { border-radius: 50%; }
.dcard-head { display: flex; align-items: center; gap: 10px; min-width: 0; }
.dcard-icon { display: grid; flex: none; place-items: center; width: 30px; height: 30px; border: 1px solid color-mix(in oklab, var(--accent) 30%, var(--line)); border-radius: 8px;
  background: color-mix(in oklab, var(--accent) 8%, var(--surface)); color: var(--accent); }
.dcard-title { display: grid; flex: 1; min-width: 0; }
.dcard-title strong { overflow: hidden; font-size: 14px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.dcard-title small { color: var(--muted); font-size: 11.5px; font-variant-numeric: tabular-nums; }
.dcard-pill { flex: none; padding: 1px 8px; border-radius: 999px; background: color-mix(in oklab, var(--tone) 14%, var(--surface)); color: color-mix(in oklab, var(--tone) 85%, var(--text));
  font-size: 12px; font-weight: 600; white-space: nowrap; }
.dcard-tags { display: flex; flex-wrap: wrap; gap: 4px; margin: 0; padding: 0; list-style: none; }
.dcard-tag { display: block; max-width: 220px; overflow: hidden; padding: 1px 7px; border: 1px solid color-mix(in oklab, var(--tone) 35%, transparent); border-radius: 6px;
  background: color-mix(in oklab, var(--tone) 10%, var(--surface)); color: var(--text); font-size: 11.5px; text-decoration: none; text-overflow: ellipsis; white-space: nowrap; }
.dcard-tag[data-tone="planned"] { border-style: dashed; background: var(--surface); }
.dcard-cat { border: 1px solid var(--line); border-radius: 9px; background: var(--hover); }
.dcard-cat-head { display: flex; align-items: center; gap: 7px; width: 100%; padding: 6px 9px; border: 0; border-radius: 9px; background: transparent; color: var(--text); font: inherit; cursor: pointer; }
.dcard-chevron { flex: none; color: var(--muted); transition: rotate var(--atlas-motion-fast) var(--atlas-ease-out); }
.dcard-cat-head[aria-expanded="false"] .dcard-chevron { rotate: -90deg; }
.dcard-cat-name { flex: 1; min-width: 0; overflow: hidden; font-size: 12.5px; font-weight: 600; text-align: left; text-overflow: ellipsis; white-space: nowrap; }
.dcard-dots { display: inline-flex; gap: 3px; }
.dcard-dot { flex: none; width: 8px; height: 8px; border-radius: 50%; background: var(--tone); }
.dcard-count { padding: 0 7px; border: 1px solid var(--line); border-radius: 999px; background: var(--surface); color: var(--muted); font-size: 11px; font-variant-numeric: tabular-nums; }
.dcard-mods { display: grid; gap: 4px; margin: 0; padding: 0 7px 7px; overflow: hidden; list-style: none; }
.dcard-mod { display: flex; align-items: center; gap: 8px; padding: 6px 9px; border: 1px solid var(--line); border-radius: 7px; background: var(--surface); color: var(--text); text-decoration: none;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
.dcard-mod:hover { border-color: var(--accent); box-shadow: 0 0 0 2px color-mix(in oklab, var(--accent) 15%, transparent); }
.dcard-mod-name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.dcard-badge { padding: 0 7px; border: 1px solid color-mix(in oklab, var(--tone) 30%, transparent); border-radius: 999px; background: color-mix(in oklab, var(--tone) 12%, var(--surface));
  color: color-mix(in oklab, var(--tone) 85%, var(--text)); font-size: 11px; font-variant-numeric: tabular-nums; }
.dcard-shared { display: flex; align-items: center; gap: 4px; margin: 0; color: var(--muted); font-size: 11.5px; }
:is(.dcard-tag, .dcard-mod, .dcard-cat-head):focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
</style>
