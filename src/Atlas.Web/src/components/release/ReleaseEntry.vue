<script setup lang="ts">
// The release codename's display face, loaded only with the What's new chunk.
import "@fontsource-variable/fraunces/index.css";
import { computed } from "vue";
import { MarkdownRenderer } from "@atlas/markdown/vue";
import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/themes/ocean.css";
import "@atlas/markdown/themes/forest.css";
import "@atlas/markdown/themes/midnight.css";
import { firstPoint, points, type ReleaseNote } from "../../whatsNew/releaseNotes";
import { releaseLabel } from "../../whatsNew/notice";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import SettingsSection from "../settings/SettingsSection.vue";
import ReleaseMark from "./ReleaseMark.vue";
import t from "../../i18n/components-release";

/**
 * One named version in the changelog (ReleaseChangelog), driven only by its notes: a settings section with the notes drawn by the
 * editor's read-only renderer. A named release (codename and mark) gets its codename in the heading, its mark faded out behind it
 * and a soft glow. Its patch releases (`patches`, newest first) follow beneath it, smaller: a row each with the version and date,
 * folded when the patch is long (over three points). `current`: the release this server runs („Aktuální verze“). „Přehrát úvod“ or a
 * click on a named release's codename asks to replay its splash (`splash`; WhatsNewPage opens it in a dialog).
 */
const props = defineProps<{ note: ReleaseNote; patches?: readonly ReleaseNote[]; current?: string; dark?: boolean; accent?: string }>();
const emit = defineEmits<{ splash: [] }>();
const label = computed(() => `Atlas ${releaseLabel(props.note.version)}`);
const theme = computed(() => props.dark ? "md-theme-midnight" : props.accent === "green" ? "md-theme-forest" : "");
/** The patch's date stands in its row, so its text starts with the points. */
const undated = (text: string) => text.replace(/^\*[^*\n]+\*\s*/, "");
</script>

<template>
  <SettingsSection class="release-entry" :title="label" :data-release="note.version" :data-named="note.mark ? '' : undefined">
    <template v-if="note.codename || current === note.version" #title-extra>
      <button v-if="note.codename && note.mark" type="button" class="release-entry-codename" :title="t.entry.replay(note.codename)"
        data-release-replay @click="emit('splash')"><span class="release-entry-dash"> — </span>{{ note.codename }}</button>
      <span v-else-if="note.codename" class="release-entry-codename"><span class="release-entry-dash"> — </span>{{ note.codename }}</span>
      <span v-if="current === note.version" class="release-entry-current">{{ t.entry.current }}</span>
      <Button v-if="note.codename && note.mark" type="button" variant="ghost" size="sm" class="release-entry-play" data-release-play
        :aria-label="t.entry.replay(note.codename)" @click="emit('splash')">{{ t.entry.play }}</Button>
    </template>
    <ReleaseMark v-if="note.mark" :mark="note.mark" variant="watermark" class="release-entry-mark" />
    <MarkdownRenderer :value="note.text" profile="markdown-safe" :ariaLabel="t.entry.whatsNewIn(label, note.codename)" :class="theme" />
    <div v-if="patches?.length" class="release-entry-patches" role="group" :aria-label="t.entry.patchesOf(label)">
      <details v-for="patch in patches" :key="patch.version" class="release-entry-patch" :data-release="patch.version" :open="points(patch).length <= 3">
        <summary>
          <AtlasIcon name="down" :size="14" class="release-entry-patch-chevron" />
          <h4 class="release-entry-patch-title">Atlas {{ patch.version }}</h4>
          <span v-if="patch.date" class="release-entry-patch-date">{{ patch.date }}</span>
          <span v-if="current === patch.version" class="release-entry-current">{{ t.entry.current }}</span>
          <span class="release-entry-patch-preview">{{ firstPoint(patch) }}</span>
        </summary>
        <div class="release-entry-patch-body">
          <MarkdownRenderer :value="undated(patch.text)" profile="markdown-safe" :ariaLabel="t.entry.whatsNewIn(`Atlas ${patch.version}`)" :class="theme" />
        </div>
      </details>
    </div>
  </SettingsSection>
</template>

<style scoped>
.release-entry-codename { font-family: "Fraunces Variable", Georgia, "Times New Roman", serif; font-weight: 600; background: linear-gradient(100deg, #7058de, #4c78e5 52%, #159daa); -webkit-background-clip: text; background-clip: text; color: transparent; }
.release-entry-dash { color: var(--muted); -webkit-text-fill-color: var(--muted); }
/* No underline: background-clip: text paints a decoration with the gradient too. Pointed at, the codename only brightens. */
button.release-entry-codename { padding: 0; border: 0; border-radius: 4px; font-size: inherit; line-height: inherit;
  transition: filter var(--atlas-motion-base) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { button.release-entry-codename:hover { filter: brightness(1.25) saturate(1.2); } }
:global(html[data-mode="dark"] .release-entry-codename) { background-image: linear-gradient(100deg, #bd9cff, #89b5ff 52%, #63dfd3); }
.release-entry-play { margin-left: auto; }
.release-entry-current { margin-left: 8px; padding: 1px 8px; border-radius: 999px; background: var(--accent-soft, color-mix(in srgb, var(--accent) 14%, transparent)); color: var(--accent); font-size: 12px; font-weight: 600; vertical-align: middle; }
/* A named release: the mark stands behind the heading's right end and fades out towards the text; the card clips it. */
.release-entry[data-named] { position: relative; overflow: clip; isolation: isolate;
  background: radial-gradient(50% 90% at 100% 0%, color-mix(in srgb, #7058de 12%, transparent), transparent 70%), var(--surface); }
.release-entry-mark { position: absolute; z-index: -1; right: -28px; top: -32px; }
/* Its patch releases: smaller rows on a thin rule beneath the notes, subordinate to the named version. A patch's single section
   heading (Opravy) is left out; several stay, small. */
.release-entry-patches { display: grid; gap: 2px; margin: 0 var(--st-pad-x, 16px) 8px; padding-left: 10px; border-left: 2px solid var(--st-line-soft, var(--line)); }
.release-entry-patch > summary { display: flex; flex-wrap: wrap; align-items: baseline; gap: 2px 8px; padding: 5px 6px; border-radius: 6px; list-style: none; cursor: pointer; color: var(--muted); font-size: 13px; }
.release-entry-patch > summary::-webkit-details-marker { display: none; }
.release-entry-patch > summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
@media (hover: hover) and (pointer: fine) { .release-entry-patch > summary:hover { background: color-mix(in srgb, var(--hover) 45%, transparent); } }
.release-entry-patch-chevron { align-self: center; flex-shrink: 0; rotate: -90deg; transition: rotate var(--atlas-motion-base) var(--atlas-ease-out); }
.release-entry-patch[open] .release-entry-patch-chevron { rotate: 0deg; }
@media (prefers-reduced-motion: reduce) { .release-entry-patch-chevron { transition: none; } }
.release-entry-patch-title { margin: 0; color: var(--text); font-size: 13.5px; font-weight: 600; }
.release-entry-patch-date { font-variant-numeric: tabular-nums; }
.release-entry-patch-preview { flex: 1 1 12ch; min-width: 0; contain: inline-size; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.release-entry-patch[open] .release-entry-patch-preview { display: none; }
.release-entry-patch .release-entry-current { margin-left: 0; font-size: 11px; }
.release-entry-patch-body .md-renderer { padding: 0 0 4px 26px; font-size: 13.5px; }
.release-entry-patch-body :deep(.md-renderer h3:only-of-type) { display: none; }
.release-entry-patch-body :deep(.md-renderer h3) { margin-block: 6px 2px; font-size: 13px; }
.release-entry-patch-body :deep(.md-renderer :is(p, ul)) { margin-block: 4px; }
/* The renderer is a .md-editor surface with the editor's default sans; the page keeps the host's (as HistoryRevisionPreview). */
.md-renderer { --font-sans: "DM Sans", system-ui, sans-serif; padding: 4px var(--st-pad-x, 16px) 8px; background: transparent; }
@media (max-width: 520px) { .release-entry-mark.release-mark[data-variant] { width: 170px; height: 170px; right: -24px; top: -20px; } }
</style>
