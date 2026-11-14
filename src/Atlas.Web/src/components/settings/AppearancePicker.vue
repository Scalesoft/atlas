<script setup lang="ts">
import { formatNumber } from "../../i18n";
import t from "../../i18n/settings-appearance";
import { computed, nextTick, ref, shallowRef, useId, watch, type Component } from "vue";
import { RadioGroupItem, RadioGroupRoot } from "reka-ui";
import AppearanceColorPicker from "./AppearanceColorPicker.vue";
import AtlasIcon from "../AtlasIcon.vue";
import ErrorState from "./ErrorState.vue";
import InlineReveal from "./InlineReveal.vue";
import LoadingNote from "./LoadingNote.vue";
import { appearanceColorLabel } from "../../appearance";
import { appearanceIconLabel, appearanceIcons, quickAppearanceIconIds } from "./appearanceIcons";
import { iconPackCount, iconPackLabel, loadIconPackCatalog, loadIconPackMarkup, prefetchIconPackMarkup } from "../../iconPack";
import { loadChunk } from "../../lazyComponent";

/**
 * Icon and colour of a space or a page icon from the one appearance catalog (appearance.ts), shown step by
 * step (refresh 2.5, board 11): 28 common icons and 10 quick colours first; every icon, the 87 of the catalog
 * and the icon pack (iconPack.ts), opens under Další ikony with a search and categories (AppearanceIconBrowser,
 * loaded with the pack only then) and the full palette (12 hues × 5 tones) under Další barvy a tóny, so nobody
 * wades through every option before asking for it. A choice outside the quick sets stays visible: the current
 * icon joins the common ones and a stored colour outside the ten opens the palette. The preview on top shows
 * the result where it will appear: a tile with the sidebar row (`variant="tile"`, spaces) or an icon on the
 * page (`variant="ink"`, page icons); `title` turns it into the head of a section. The quick choices and the
 * palette are radio groups (arrow keys, Home/End); the parent saves. A single short choice stays OptionPills.
 */
const props = withDefaults(
  defineProps<{ name?: string; description?: string; variant?: "tile" | "ink"; disabled?: boolean; iconsLabel?: string; title?: string }>(),
  { name: "", description: "", variant: "tile", disabled: false, iconsLabel: undefined, title: undefined },
);
const icon = defineModel<string>("icon", { required: true });
const color = defineModel<string>("color", { required: true });
const uid = useId();

const quickIcons = computed<{ id: string; label: string }[]>(() => {
  const common = quickAppearanceIconIds.map((id) => appearanceIcons.find((item) => item.id === id)!).filter(Boolean);
  if (!icon.value || common.some((item) => item.id === icon.value)) return common;
  // The current icon takes the first place, so the grid keeps its two rows. One outside the 87 (the icon pack, a
  // retired or imported one) stays visible and selectable too, so saving other fields keeps it.
  const current = appearanceIcons.find((item) => item.id === icon.value) ?? { id: icon.value, label: iconPackLabel(icon.value) ?? t.picker.current };
  return [current, ...common.slice(0, -1)];
});
// The name of an icon of the pack is in its catalog: asked for only when the current icon is not one of the 87.
watch(icon, (value) => {
  if (value && !appearanceIcons.some((item) => item.id === value)) loadIconPackCatalog().catch(() => {});
}, { immediate: true });

const root = ref<HTMLElement>();
const iconTotal = formatNumber(appearanceIcons.length + iconPackCount);
const moreIcons = ref(false);
/** AppearanceIconBrowser, once it, the pack's catalog and its drawings have arrived. */
const browser = shallowRef<Component>();
const browserFailed = ref(false);
const browserBusy = ref(false);
let browserRequest: Promise<void> | undefined;
/**
 * Pointing at or focusing the disclosure warms the three chunks early. It is a hint: plain imports whose failure is ignored,
 * so it neither flags the build as outdated (loadChunk does after its retry) nor leaves a failure behind; opening asks again.
 */
function prefetchBrowser() {
  if (browser.value) return;
  for (const chunk of [import("./AppearanceIconBrowser.vue"), import("../../assets/icon-pack/catalog.json")]) chunk.catch(() => {});
  prefetchIconPackMarkup();
}
/** One request at a time; a failed one may be asked for again. */
function loadBrowser() {
  if (browser.value) return Promise.resolve();
  if (!browserRequest) {
    browserBusy.value = true;
    browserRequest = Promise.all([loadChunk(() => import("./AppearanceIconBrowser.vue")), loadIconPackCatalog(), loadIconPackMarkup()])
      .then(([module]) => { browser.value = module.default; }, () => { browserFailed.value = true; })
      .finally(() => { browserRequest = undefined; browserBusy.value = false; });
  }
  return browserRequest;
}
async function showBrowser() {
  const held = document.activeElement;
  await loadBrowser();
  await nextTick();
  // A slow load must not pull the focus out of another field; after a retry its button is gone and the focus is nowhere.
  if (browser.value && moreIcons.value && (document.activeElement === held || document.activeElement === document.body)) {
    root.value?.querySelector<HTMLElement>("[data-appearance-search]")?.focus();
  }
}
function toggleMoreIcons() {
  moreIcons.value = !moreIcons.value;
  if (!moreIcons.value) return;
  browserFailed.value = false;
  void showBrowser();
}

// Nothing chosen yet (a page without an icon): previews show a plain document, no icon is selected.
const shownIcon = computed(() => icon.value || "file-text");
const iconName = computed(() => (icon.value ? iconPackLabel(icon.value) ?? appearanceIconLabel(icon.value) : t.picker.none));
/** "Fialová, jemná": the hue and tone as one phrase, after the icon's name. */
const colorPhrase = computed(() => appearanceColorLabel(color.value).replace(" · ", ", "));
</script>

<template>
  <div ref="root" class="appearance-picker" :data-variant="variant" :aria-disabled="disabled || undefined">
    <div class="appearance-preview" role="img" :aria-label="t.picker.preview(iconName, appearanceColorLabel(color))" data-appearance-preview>
      <span :key="`${icon}:${color}`" class="appearance-preview-mark" :data-color="color" :data-empty="!icon || undefined"><AtlasIcon :name="shownIcon" :size="variant === 'tile' ? 24 : 26" /></span>
      <span class="appearance-preview-text">
        <strong>{{ title ?? (name.trim() || (variant === "tile" ? t.picker.spaceName : t.picker.pageName)) }}</strong>
        <small v-if="description && !title" class="appearance-preview-description">{{ description }}</small>
        <small>{{ iconName }} · {{ colorPhrase }}</small>
      </span>
      <!-- Where it shows first: the row of the space in the sidebar. -->
      <span v-if="variant === 'tile'" class="appearance-preview-row" aria-hidden="true">
        <span class="appearance-preview-row-icon" :data-color="color"><AtlasIcon :name="shownIcon" :size="12" /></span>
        <span class="appearance-preview-row-name" :title="name.trim() || undefined">{{ name.trim() || t.picker.spaceName }}</span>
      </span>
    </div>

    <section class="appearance-block" :aria-labelledby="`${uid}-icons`">
      <div class="appearance-block-head">
        <h4 :id="`${uid}-icons`">{{ iconsLabel ?? t.picker.icon }}</h4>
      </div>
      <RadioGroupRoot v-model="icon" class="appearance-icons" :aria-labelledby="`${uid}-icons`" :disabled="disabled" loop data-appearance-icons>
        <RadioGroupItem
          v-for="item in quickIcons"
          :key="item.id"
          :value="item.id"
          class="appearance-icon"
          :data-value="item.id"
          :data-color="icon === item.id ? color : undefined"
          :aria-label="item.label"
          :title="item.label"
        >
          <AtlasIcon :name="item.id" :size="18" />
        </RadioGroupItem>
      </RadioGroupRoot>
      <div class="appearance-more">
      <button
        type="button"
        class="appearance-disclosure"
        :aria-expanded="moreIcons"
        :aria-controls="`${uid}-more-icons`"
        :disabled="disabled"
        data-appearance-more-icons
        @pointerenter="prefetchBrowser"
        @focus="prefetchBrowser"
        @click="toggleMoreIcons"
      ><AtlasIcon name="down" :size="14" class="appearance-disclosure-chevron" />{{ moreIcons ? t.picker.fewer : t.picker.more(iconTotal) }}</button>
      <InlineReveal :id="`${uid}-more-icons`" :open="moreIcons">
        <component :is="browser" v-if="browser" v-model="icon" :color="color" :variant="variant" :disabled="disabled" />
        <ErrorState
          v-else-if="browserFailed"
          :title="t.picker.failedTitle"
          :text="t.picker.failedText"
          :busy="browserBusy"
          @retry="showBrowser"
        />
        <LoadingNote v-else :label="t.picker.loading" />
      </InlineReveal>
      </div>
    </section>

    <AppearanceColorPicker v-model="color" :label="t.picker.color" show-label :icon="shownIcon" :disabled="disabled" />
  </div>
</template>

<style scoped src="./appearanceBlock.css"></style>
<style scoped>
.appearance-picker { display: grid; gap: 18px; min-width: 0; container: appearance-picker / inline-size; }
.appearance-picker[aria-disabled] { opacity: .6; }

/* The head (board 11): the tile, what is chosen, and the row it will make in the sidebar. */
.appearance-preview { display: flex; align-items: center; gap: 14px; min-width: 0; }
.appearance-preview-mark { display: grid; place-items: center; flex-shrink: 0; width: 48px; height: 48px; border-radius: 13px; color: var(--appearance-fg); background: var(--appearance-bg); animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out) both; }
[data-variant="ink"] .appearance-preview-mark { color: var(--appearance-ink); background: var(--surface); border: 1px solid var(--line); }
.appearance-preview-mark[data-empty] { color: var(--muted); }
.appearance-preview-text { display: grid; flex: 1; gap: 2px; min-width: 0; }
.appearance-preview-text strong { font-size: 15px; font-weight: 600; overflow-wrap: anywhere; }
.appearance-preview-text small { color: var(--muted); font-size: 13px; }
.appearance-preview-description { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.appearance-preview-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: 0 1 300px;
  min-width: 0;
  height: 36px;
  padding: 0 10px;
  border: 1px solid var(--line);
  border-radius: 10px;
  background: var(--sidebar-fill, var(--sidebar, var(--hover)));
  font-size: 13px;
}
.appearance-preview-row-icon { display: grid; place-items: center; flex: none; width: 22px; height: 22px; border-radius: 6px; color: var(--appearance-fg); background: var(--appearance-bg); }
.appearance-preview-row-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

.appearance-icons { display: grid; grid-template-columns: repeat(14, minmax(0, 1fr)); gap: 4px; min-width: 0; }
.appearance-icon {
  display: grid; place-items: center; height: 38px; min-width: 0; padding: 0;
  border: 1px solid transparent; border-radius: 9px;
  background: transparent; color: var(--muted); cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out), border-color var(--atlas-motion-fast) var(--atlas-ease-out), transform var(--atlas-motion-fast) var(--atlas-ease-out);
}
@media (hover: hover) and (pointer: fine) {
  .appearance-icon:hover:not([data-disabled]) { background: var(--hover); color: var(--text); }
}
.appearance-icon:active:not([data-disabled]) { transform: scale(var(--atlas-press)); }
.appearance-icon:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.appearance-icon[data-state="checked"] { color: var(--appearance-fg); background: var(--appearance-bg); animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out); }
[data-variant="ink"] .appearance-icon[data-state="checked"] { color: var(--appearance-ink); background: color-mix(in srgb, var(--appearance-ink) 12%, var(--surface)); }
.appearance-icon[data-disabled] { cursor: not-allowed; }

@container appearance-picker (max-width: 640px) {
  .appearance-icons { grid-template-columns: repeat(7, minmax(0, 1fr)); }
  .appearance-preview-row { display: none; }
}
</style>
