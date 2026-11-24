<script setup lang="ts">
import { computed } from "vue";
import { groupReleases, type ReleaseNote } from "../../whatsNew/releaseNotes";
import { releaseLabel } from "../../whatsNew/notice";
import SettingsPage from "../settings/SettingsPage.vue";
import ReleaseEntry from "./ReleaseEntry.vue";
import ReleaseMark from "./ReleaseMark.vue";
import t from "../../i18n/components-release";

/**
 * The full changelog (Co je nového, `#/whats-new`): every named version from the notes, newest first, one ReleaseEntry each with its
 * patch releases beneath it, on the settings page layout. The page to return to at any time (palette, Profil); ReleaseSplash is the
 * one-off welcome after an update. `current`: the release this server runs; `splash`: replay a named release's splash.
 * On top, the release gallery (owner, 5.1): a tile per named release with its mark, a click replays its splash with its demos.
 */
const props = defineProps<{ notes: readonly ReleaseNote[]; current?: string; dark?: boolean; accent?: string }>();
const emit = defineEmits<{ splash: [note: ReleaseNote] }>();
const groups = computed(() => groupReleases(props.notes));
const named = computed(() => groups.value.map(group => group.release).filter(note => note.codename && note.mark));
</script>

<template>
  <div class="release-changelog settings-ui">
    <SettingsPage :title="t.changelog.title" icon="scroll-text" :description="t.changelog.description" :back="{ href: '#/', label: t.changelog.back }">
      <nav v-if="named.length" class="release-gallery" :aria-label="t.changelog.gallery">
        <button v-for="note in named" :key="note.version" type="button" class="release-gallery-tile" :data-release-tile="note.version"
          :title="t.entry.replay(note.codename!)" @click="emit('splash', note)">
          <ReleaseMark :mark="note.mark!" variant="tile" />
          <span class="release-gallery-codename">{{ note.codename }}</span>
          <span class="release-gallery-version">{{ releaseLabel(note.version) }}</span>
        </button>
      </nav>
      <div class="release-changelog-list atlas-stagger">
        <!-- The keyboard lands on the newest release (keyboard/navigation.ts landFocus). -->
        <ReleaseEntry v-for="({ release, patches }, index) in groups" :key="release.version" :note="release" :patches="patches" :current="current"
          :dark="dark" :accent="accent" :data-focus-landing="index === 0 || undefined" @splash="emit('splash', release)" />
      </div>
    </SettingsPage>
  </div>
</template>

<style scoped>
.release-changelog { max-width: 760px; margin: 0 auto; padding: 20px var(--atlas-page-gutter, 16px) 40px; }
.release-changelog-list { display: flex; flex-direction: column; gap: 12px; }
/* One row of tiles that scrolls sideways on a narrow screen; the marks play their intro as the page opens. */
.release-gallery { display: flex; gap: 10px; margin-bottom: 16px; padding: 2px 2px 8px; overflow-x: auto; scroll-snap-type: x proximity; }
.release-gallery-tile { display: grid; justify-items: center; gap: 2px; flex: none; width: 128px; padding: 10px 8px; border: 1px solid var(--line);
  border-radius: 12px; background: var(--surface); color: var(--text); scroll-snap-align: start; cursor: pointer;
  transition: translate var(--atlas-motion-base) var(--atlas-ease-out), border-color var(--atlas-motion-base) var(--atlas-ease-out); }
.release-gallery-tile:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
@media (hover: hover) and (pointer: fine) { .release-gallery-tile:hover { translate: 0 -2px; border-color: var(--accent); } }
@media (prefers-reduced-motion: reduce) { .release-gallery-tile { transition: none; } }
.release-gallery-codename { font-family: "Fraunces Variable", Georgia, "Times New Roman", serif; font-weight: 600; font-size: 14px; text-align: center; line-height: 1.2; }
.release-gallery-version { color: var(--muted); font-size: 12px; font-variant-numeric: tabular-nums; }
</style>
