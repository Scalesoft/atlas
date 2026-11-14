<script setup lang="ts">
import { computed } from "vue";
import AtlasIcon from "../../../AtlasIcon.vue";
import { Button } from "../../../ui/button";
import ResultBanner from "../../ResultBanner.vue";
import SettingsSection from "../../SettingsSection.vue";
import { disclosureHooks } from "../../disclosure";
import { useSettingsRoute } from "../../useSettingsRoute";
import { errorCodeText, profileStateView, type ProfileKind } from "./aiAdmin";
import { useAiAdminHealth } from "./useAiAdminHealth";
import t from "../../../../i18n/settings-ai";

const router = useSettingsRoute();
const { overview, failures, warnings, error, retrying, result, refresh, retry, dismissResult } = useAiAdminHealth();
const labels: Record<ProfileKind, string> = t.health.labels;
const showAlert = computed(() => !!error.value || (!!overview.value?.enabled && (!!failures.value.length || !!warnings.value.length || (result.value && !result.value.ok))));
const resultMessage = computed(() => result.value?.ok
  ? t.health.works(labels[result.value.kind])
  : "");
const failedTestText = (kind: ProfileKind) => result.value?.kind === kind && !result.value.ok
  ? t.health.failedPrefix + " " + (result.value.code ? errorCodeText(result.value.code) : t.health.retryHint)
  : "";
</script>

<template>
  <div class="ai-health-status" data-ai-health-status>
    <ResultBanner v-if="result?.ok" :message="resultMessage" @dismiss="dismissResult" />
    <Transition name="st-step">
      <SettingsSection v-if="showAlert" :title="t.health.title" icon="triangle-alert" role="alert" data-ai-health-alert>
        <p v-if="error" class="st-note">{{ error }}</p>
        <p v-if="failures.length" class="st-note">{{ t.health.someFailed }}</p>
        <p v-if="failures.length || (result && !result.ok)" class="st-note">{{ t.health.costNote }}</p>
        <div v-for="profile in failures" :key="profile.kind" class="ai-health-profile" :data-ai-health-profile="profile.kind">
          <strong>{{ labels[profile.kind] }}</strong>
          <p v-if="profile.useChatProfile" class="st-note">{{ t.health.usesChat }}</p>
          <p class="st-note">{{ profileStateView(profile).label }}<template v-if="profile.lastErrorCode"> · {{ errorCodeText(profile.lastErrorCode) }}</template></p>
          <Transition :css="false" v-bind="disclosureHooks"><p v-if="failedTestText(profile.kind)" class="st-note" role="status">{{ failedTestText(profile.kind) }}</p></Transition>
          <div class="st-actions">
            <Button type="button" variant="outline" size="sm" :disabled="!!retrying" :aria-busy="retrying === profile.kind" :data-ai-health-retry="profile.kind" @click="retry(profile.kind)">
              <AtlasIcon :name="retrying === profile.kind ? 'loader-circle' : 'refresh'" :size="15" :class="{ 'atlas-spinner': retrying === profile.kind }" />
              {{ retrying === profile.kind ? t.health.verifying : t.health.verifyAgain }}
            </Button>
            <Button as-child variant="ghost" size="sm"><a :href="router.href('general/modules/ai/' + (profile.useChatProfile ? 'chat' : profile.kind))">{{ t.health.editConnection }}</a></Button>
          </div>
        </div>
        <!-- A warning, as the backups' one: the profile works, so no retry, only the way to fix it. -->
        <div v-for="profile in warnings" :key="'warning-' + profile.kind" class="ai-health-profile" :data-ai-health-warning="profile.kind">
          <strong>{{ labels[profile.kind] }}</strong>
          <p class="ai-health-warning"><AtlasIcon name="triangle-alert" :size="15" />{{ profile.warning }}</p>
          <div class="st-actions">
            <Button as-child variant="ghost" size="sm"><a :href="router.href('general/modules/ai/' + profile.kind)">{{ t.health.editModel }}</a></Button>
          </div>
        </div>
        <div v-if="error" class="st-actions">
          <Button type="button" variant="outline" size="sm" @click="refresh">{{ t.health.reload }}</Button>
        </div>
        <div v-if="result && !result.ok && !failures.length && overview?.enabled" class="ai-health-profile" role="status">
          <strong>{{ labels[result.kind] }}</strong>
          <p class="st-note">{{ t.health.failedPrefix }} {{ result.code ? errorCodeText(result.code) : t.health.retryHint }}</p>
          <div class="st-actions">
            <Button type="button" variant="outline" size="sm" :disabled="!!retrying" :aria-busy="retrying === result.kind" :data-ai-health-retry="result.kind" @click="retry(result.kind)"><AtlasIcon :name="retrying === result.kind ? 'loader-circle' : 'refresh'" :size="15" :class="{ 'atlas-spinner': retrying === result.kind }" />{{ retrying === result.kind ? t.health.verifying : t.health.verifyAgain }}</Button>
            <Button as-child variant="ghost" size="sm"><a :href="router.href('general/modules/ai/' + result.kind)">{{ t.health.editConnection }}</a></Button>
          </div>
        </div>
      </SettingsSection>
    </Transition>
  </div>
</template>

<style scoped>
.ai-health-status { min-width: 0; }
.ai-health-status :deep([data-ai-health-alert]) { border-color: color-mix(in srgb, var(--warning) 45%, var(--line)); background: color-mix(in srgb, var(--warning-bg) 35%, var(--surface)); }
/* A column of flex items, not a grid: the failed-test line unfolds and gives the gap above it back (settings/disclosure.ts). */
/* The body's gap already separates the blocks: a padding below each one doubled it under the last block (28 px to the card's
   edge) and above the first (32 px under the notes). Only the space after a dividing line is the block's own. */
.ai-health-profile { display: flex; flex-direction: column; gap: 6px; padding: 12px 0 0; border-top: 1px solid var(--line); }
.ai-health-profile:first-of-type { padding-top: 4px; border-top: 0; }
.ai-health-profile strong { font-size: 13.5px; }
.ai-health-profile p { margin: 0; }
.ai-health-profile .st-actions { margin-top: 2px; }
.ai-health-warning { display: flex; align-items: flex-start; gap: 6px; color: var(--text); font-size: 12.5px; line-height: 1.5; }
.ai-health-warning > svg { flex-shrink: 0; margin-top: 2px; color: var(--warning); }
</style>
