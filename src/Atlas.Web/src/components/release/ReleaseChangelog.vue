<script setup lang="ts">
import { computed } from "vue";
import { groupReleases, type ReleaseNote } from "../../whatsNew/releaseNotes";
import SettingsPage from "../settings/SettingsPage.vue";
import ReleaseEntry from "./ReleaseEntry.vue";
import t from "../../i18n/components-release";

/**
 * The full changelog (Co je nového, `#/whats-new`): every named version from the notes, newest first, one ReleaseEntry each with its
 * patch releases beneath it, on the settings page layout. The page to return to at any time (palette, Profil); ReleaseSplash is the
 * one-off welcome after an update. `current`: the release this server runs; `splash`: replay a named release's splash.
 */
const props = defineProps<{ notes: readonly ReleaseNote[]; current?: string; dark?: boolean; accent?: string }>();
const emit = defineEmits<{ splash: [note: ReleaseNote] }>();
const groups = computed(() => groupReleases(props.notes));
</script>

<template>
  <div class="release-changelog settings-ui">
    <SettingsPage :title="t.changelog.title" icon="scroll-text" :description="t.changelog.description" :back="{ href: '#/', label: t.changelog.back }">
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
</style>
