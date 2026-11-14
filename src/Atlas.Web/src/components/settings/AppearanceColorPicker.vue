<script setup lang="ts">
import t from "../../i18n/settings-appearance";
import { computed, ref, useId } from "vue";
import { RadioGroupItem, RadioGroupRoot } from "reka-ui";
import AtlasIcon from "../AtlasIcon.vue";
import InlineReveal from "./InlineReveal.vue";
import { appearanceColorKey, appearanceColors, appearanceHues, appearanceTones } from "../../appearance";

/**
 * The colour of the appearance catalog (appearance.ts) in the order the spaces' AppearancePicker introduced: ten quick colours of
 * the soft tone, the whole palette (12 hues × 5 tones, tone labels in front of the rows) under Další barvy a tóny. One component for
 * every place that picks from that palette: the appearance of a space and of a page icon (AppearancePicker, `show-label`) and the
 * „Pozadí“ of a board's editor component (`compact`, popover). A stored colour outside the quick ten opens the palette. Quick and
 * palette are radio groups (arrow keys, Home/End, roving focus); the parent saves. `leading` adds options in front of the quick
 * colours inside the same group (the board's „Bez pozadí“, `white`, `black`), drawn as none, surface and text-colour swatches.
 * The swatches hold `icon` when given (the icon being coloured), else they are plain tiles.
 */
const props = withDefaults(
  defineProps<{ disabled?: boolean; label?: string; showLabel?: boolean; icon?: string; compact?: boolean; leading?: readonly { value: string; label: string }[] }>(),
  { disabled: false, label: undefined, showLabel: false, icon: undefined, compact: false, leading: () => [] },
);
const color = defineModel<string>({ required: true });
const uid = useId();

/** The quick colours: the soft tone (2) of ten hues. */
const quickHueIds = ["blue", "teal", "green", "amber", "orange", "red", "pink", "violet", "indigo", "slate"];
const quickColors = quickHueIds.map((hue) => appearanceColors.find((item) => item.id === `${hue}-2`)!);

const colorKey = computed(() => appearanceColorKey(color.value));
// A stored colour outside the ten opens the palette, so the current choice is always in sight.
const moreColors = ref(!!colorKey.value && !quickColors.some((item) => item.id === colorKey.value) && !props.leading.some((item) => item.value === colorKey.value));

const rows = computed(() => appearanceTones.map((tone) => ({ tone, colors: appearanceColors.filter((item) => item.tone === tone.id) })));
const pickColor = (value: unknown) => { color.value = String(value); };
// Labelled by the visible heading, or by `label` where there is none.
const labelling = computed(() => (props.showLabel ? { "aria-labelledby": `${uid}-colors` } : { "aria-label": props.label ?? t.colors.label }));
</script>

<template>
  <section class="appearance-block appearance-color-picker" :data-compact="compact || undefined" :aria-labelledby="showLabel ? `${uid}-colors` : undefined">
    <div v-if="showLabel" class="appearance-block-head">
      <h4 :id="`${uid}-colors`">{{ label ?? t.colors.label }}</h4>
    </div>
    <RadioGroupRoot :model-value="colorKey" class="appearance-quick" v-bind="labelling" :disabled="disabled" loop data-appearance-quick-colors @update:model-value="pickColor">
      <RadioGroupItem
        v-for="item in leading"
        :key="item.value"
        :value="item.value"
        class="appearance-swatch is-quick is-leading"
        :data-swatch="item.value"
        :data-value="item.value"
        :aria-label="item.label"
        :title="item.label"
      />
      <RadioGroupItem
        v-for="item in quickColors"
        :key="item.id"
        :value="item.id"
        class="appearance-swatch is-quick"
        :data-color="item.id"
        :data-value="item.id"
        :aria-label="item.label"
        :title="item.label"
      >
        <AtlasIcon v-if="icon" :name="icon" :size="compact ? 14 : 17" />
      </RadioGroupItem>
    </RadioGroupRoot>
    <div class="appearance-more">
      <button
        type="button"
        class="appearance-disclosure"
        :aria-expanded="moreColors"
        :aria-controls="`${uid}-palette`"
        :disabled="disabled"
        data-appearance-more-colors
        @click="moreColors = !moreColors"
      ><AtlasIcon name="down" :size="14" class="appearance-disclosure-chevron" />{{ moreColors ? t.colors.fewer : t.colors.more(appearanceColors.length) }}</button>
      <InlineReveal :id="`${uid}-palette`" v-model:open="moreColors">
        <div class="appearance-palette-panel">
          <p class="appearance-palette-note">{{ t.colors.note(appearanceHues.length, appearanceTones.length) }}</p>
          <RadioGroupRoot :model-value="colorKey" class="appearance-palette" :aria-label="t.colors.all" :disabled="disabled" loop data-appearance-colors @update:model-value="pickColor">
            <div v-for="row in rows" :key="row.tone.id" class="appearance-palette-row" role="presentation">
              <span class="appearance-tone" aria-hidden="true">{{ row.tone.label }}</span>
              <RadioGroupItem
                v-for="item in row.colors"
                :key="item.id"
                :value="item.id"
                class="appearance-swatch"
                :data-color="item.id"
                :data-value="item.id"
                :aria-label="item.label"
                :title="item.label"
              >
                <AtlasIcon v-if="icon" :name="icon" :size="compact ? 12 : 15" />
              </RadioGroupItem>
            </div>
          </RadioGroupRoot>
          <p class="appearance-hues" aria-hidden="true"><span /><span v-for="hue in appearanceHues" :key="hue.id">{{ hue.label }}</span></p>
        </div>
      </InlineReveal>
    </div>
  </section>
</template>

<style scoped src="./appearanceBlock.css"></style>
<style scoped>
.appearance-swatch:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.appearance-swatch[data-disabled] { cursor: not-allowed; }

.appearance-quick { display: grid; grid-template-columns: repeat(10, minmax(0, 1fr)); gap: 8px; }
.appearance-swatch {
  position: relative; display: grid; place-items: center; height: 36px; min-width: 0; padding: 0;
  border: 0; border-radius: 9px; cursor: pointer;
  color: var(--appearance-fg); background: var(--appearance-bg);
  transition: transform var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out);
}
.appearance-swatch.is-quick { height: 40px; border-radius: 10px; }
[data-variant="ink"] .appearance-swatch { color: var(--appearance-ink); background: color-mix(in srgb, var(--appearance-ink) 14%, var(--surface)); }
/* Leading options have no hue: none is the crossed-out tile, white lies on the surface with a hairline, black is the text colour. */
.appearance-swatch.is-leading { color: var(--muted); background: var(--surface); box-shadow: inset 0 0 0 1px var(--line); }
.appearance-swatch[data-swatch="none"] { background: linear-gradient(to top right, transparent calc(50% - 1px), var(--muted) calc(50% - 1px), var(--muted) calc(50% + 1px), transparent calc(50% + 1px)), var(--surface); }
.appearance-swatch[data-swatch="black"] { background: var(--text); box-shadow: none; }
@media (hover: hover) and (pointer: fine) {
  .appearance-swatch:hover:not([data-disabled]):not([data-state="checked"]) { box-shadow: inset 0 0 0 2px var(--appearance-fg, var(--muted)); }
}
.appearance-swatch:active:not([data-disabled]) { transform: scale(var(--atlas-press)); }
.appearance-swatch[data-state="checked"] { box-shadow: 0 0 0 2px var(--surface), 0 0 0 4px var(--text); animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out); }

.appearance-palette-panel { display: grid; gap: 8px; padding: 12px; border: 1px solid color-mix(in srgb, var(--line) 50%, var(--surface)); border-radius: 12px; background: color-mix(in srgb, var(--line) 7%, var(--surface)); }
.appearance-palette-note { margin: 0; color: var(--muted); font-size: 12px; }
.appearance-palette { display: grid; gap: 6px; }
.appearance-palette-row, .appearance-hues { display: grid; grid-template-columns: 76px repeat(12, minmax(0, 1fr)); align-items: center; gap: 6px; }
.appearance-tone { color: var(--muted); font-size: 12px; }
.appearance-hues { margin: 0; color: var(--muted); font-size: 10px; text-align: center; }
.appearance-hues > span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

/* A popover (the board's Pozadí): the same tiles at a fixed, smaller size, thirteen in a row (3 leading + 10 quick). */
/* A popover's own padding (10px) and the 8px it keeps from a phone's edges. */
.appearance-color-picker[data-compact] { width: min(368px, calc(100vw - 36px)); }
[data-compact] .appearance-quick { grid-template-columns: repeat(13, minmax(0, 1fr)); gap: 4px; }
/* min-height: a popover's controls are 40 px tall on touch (the editor's controls.css); these tiles keep their size. */
[data-compact] .appearance-swatch, [data-compact] .appearance-swatch.is-quick { height: 26px; min-height: 0; border-radius: 7px; }
/* The checked ring stays inside the 4 px gap: the full one (2 + 2 px) would touch the next tile. */
[data-compact] .appearance-swatch[data-state="checked"] { box-shadow: 0 0 0 1px var(--surface), 0 0 0 3px var(--text); }
[data-compact] .appearance-palette-panel { gap: 6px; padding: 8px; }
[data-compact] .appearance-palette { gap: 4px; }
[data-compact] .appearance-palette-row { grid-template-columns: 56px repeat(12, minmax(0, 1fr)); gap: 4px; }
[data-compact] .appearance-tone { font-size: 11px; }
[data-compact] .appearance-palette-note, [data-compact] .appearance-hues { display: none; }

@container appearance-picker (max-width: 640px) {
  .appearance-quick { grid-template-columns: repeat(5, minmax(0, 1fr)); }
}
@container appearance-picker (max-width: 560px) {
  .appearance-palette-row { grid-template-columns: repeat(6, minmax(0, 1fr)); }
  .appearance-tone { grid-column: 1 / -1; }
  .appearance-hues { display: none; }
}
</style>
