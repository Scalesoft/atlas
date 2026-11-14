<script setup lang="ts">
import t from "../../../i18n/settings-people";
import { computed } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { formatDateTime, formatShortDate } from "../../../formatDate";
import EmptyState from "../EmptyState.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import type { StatusTone } from "../types";
import { apiChannelInfo, apiChannels, apiLevelInfo, levelOfScopes } from "../../../apiTokenRights";
import type { AdminApiToken } from "../access/accessModel";
import { expiringSoon } from "../access/accessView";

/**
 * Personal API tokens as the wiki administrator sees them (GET /api/admin/api-tokens): the valid ones with their scope
 * (Čtení, Zápis), channels (REST API, MCP), owner, issue date and end, each revocable (`revoke`, the page confirms); revoked and expired ones collapsed below.
 * Store-free. A token never acts beyond its owner's current access, so it has no access of its own to change here.
 */
const props = withDefaults(defineProps<{ tokens: AdminApiToken[]; showUser?: boolean; busy?: boolean; userHref?: (userId: string) => string | undefined; now?: number }>(), {
  showUser: true,
  busy: false,
  userHref: undefined,
  now: () => Date.now(),
});
const emit = defineEmits<{ revoke: [token: AdminApiToken] }>();

const valid = (token: AdminApiToken) => !token.revokedAt && new Date(token.expiresAt).getTime() > props.now;
const active = computed(() => props.tokens.filter(valid).sort((a, b) => a.expiresAt.localeCompare(b.expiresAt)));
const inactive = computed(() => props.tokens.filter((token) => !valid(token)).sort((a, b) => (b.revokedAt ?? b.expiresAt).localeCompare(a.revokedAt ?? a.expiresAt)));
const levelTone = { none: "neutral", read: "neutral", write: "accent" } as const;
const scope = (token: AdminApiToken): { tone: StatusTone; icon: string; label: string } => {
  const level = levelOfScopes(token.scopes);
  return { tone: levelTone[level], icon: apiLevelInfo[level].icon, label: apiLevelInfo[level].label };
};
// Token use is not recorded, so the row says when the token was issued, never when it was last used.
const usage = (token: AdminApiToken) => t.tokens.issued(formatShortDate(token.createdAt));
const detail = (token: AdminApiToken) => [props.showUser ? token.userName : undefined, usage(token)].filter(Boolean).join(" · ");
</script>

<template>
  <div class="api-token-list">
    <SettingsSection :title="showUser ? t.tokens.validTitle : t.tokens.personalTitle" icon="key-round" :count="active.length" :description="t.tokens.description">
      <SettingsList v-if="active.length" :label="t.tokens.validList">
        <SettingsListRow v-for="token in active" :key="token.id" :title="token.name" icon="key-round" icon-tone="neutral" :data-api-token="token.id">
          <template #description>
            <template v-if="showUser"><a v-if="userHref?.(token.userId)" class="st-link" :href="userHref(token.userId)">{{ token.userName }}</a><template v-else>{{ token.userName }}</template> · </template>{{ usage(token) }}
          </template>
          <template #meta>
            <StatusBadge v-bind="scope(token)" size="sm" data-token-scope />
            <StatusBadge v-for="channel in apiChannels(token.channels)" :key="channel" tone="neutral" :icon="apiChannelInfo[channel].icon" :label="apiChannelInfo[channel].label" size="sm" data-token-channel />
            <StatusBadge :tone="expiringSoon(token.expiresAt, now) ? 'warning' : 'info'" :icon="expiringSoon(token.expiresAt, now) ? 'hourglass' : 'calendar'" :label="t.tokens.until(formatShortDate(token.expiresAt))" :title="t.tokens.validUntil(formatDateTime(token.expiresAt))" size="sm" />
          </template>
          <template #actions>
            <Button variant="ghost" size="sm" type="button" :disabled="busy" :aria-label="t.tokens.revokeToken(token.name)" data-token-revoke @click="emit('revoke', token)"><AtlasIcon name="ban" :size="15" />{{ t.tokens.revoke }}</Button>
          </template>
        </SettingsListRow>
      </SettingsList>
      <EmptyState v-else compact icon="key-round" :title="t.tokens.emptyTitle" :text="showUser ? t.tokens.emptyAll : t.tokens.emptyOne" />
    </SettingsSection>
    <SettingsSection v-if="inactive.length" :title="t.tokens.invalidTitle" icon="ban" :count="inactive.length" advanced :summary="t.tokens.invalidSummary(inactive.length)">
      <SettingsList :label="t.tokens.invalidList">
        <SettingsListRow v-for="token in inactive" :key="token.id" :title="token.name" :description="detail(token)" icon="key-round" icon-tone="neutral" :data-api-token="token.id">
          <template #meta>
            <StatusBadge v-if="token.revokedAt" tone="neutral" icon="ban" :label="t.tokens.revoked(formatShortDate(token.revokedAt))" size="sm" />
            <StatusBadge v-else tone="neutral" icon="hourglass" :label="t.tokens.expiredOn(formatShortDate(token.expiresAt))" size="sm" />
          </template>
        </SettingsListRow>
      </SettingsList>
    </SettingsSection>
  </div>
</template>

<style scoped>
.api-token-list { display: grid; gap: 16px; min-width: 0; }
</style>
