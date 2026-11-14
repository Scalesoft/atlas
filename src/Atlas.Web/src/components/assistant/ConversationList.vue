<script setup lang="ts">
import type { AiConversationSummary } from "../../ai/assistantApi";
import t from "../../i18n/assistant-panel";
import a from "../../i18n/assistant";
import { formatAgo } from "../../formatDate";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import LoadingNote from "../settings/LoadingNote.vue";
import SettingsList from "../settings/SettingsList.vue";
import SettingsListRow from "../settings/SettingsListRow.vue";
import { Button } from "../ui/button";

/**
 * „Moje konverzace“ (plan §15): the user's own listed conversations, newest first. A conversation that lost a source is listed
 * without a title („Konverzace není dostupná“) and cannot be opened; nothing of its text is shown. Presentational.
 */
defineProps<{ items: readonly AiConversationSummary[]; loading?: boolean; error?: string; hasMore?: boolean }>();
const emit = defineEmits<{ open: [id: string]; more: []; back: []; retry: [] }>();
</script>

<template>
  <section class="conversation-list" :aria-label="t.list.label" data-conversation-list>
    <div class="conversation-list-bar">
      <Button type="button" variant="ghost" size="sm" data-conversation-back @click="emit('back')">{{ t.list.back }}</Button>
    </div>
    <ErrorState v-if="error" :title="error" @retry="emit('retry')" />
    <LoadingNote v-else-if="loading && !items.length" :label="t.list.loading" />
    <EmptyState v-else-if="!items.length" compact icon="info" :title="t.list.empty" :text="t.list.emptyText" />
    <SettingsList v-else :label="t.list.label">
      <SettingsListRow
        v-for="item in items"
        :key="item.id"
        :title="item.available && item.title ? item.title : a.notice.unavailableConversation"
        :description="formatAgo(item.lastActivityAt)"
        icon="quote"
        :data-conversation="item.id"
        :data-available="item.available && !!item.title"
      >
        <template v-if="item.available && item.title" #actions>
          <Button type="button" variant="outline" size="sm" @click="emit('open', item.id)">{{ t.list.open }}</Button>
        </template>
      </SettingsListRow>
    </SettingsList>
    <Button v-if="hasMore && !error" type="button" variant="ghost" size="sm" :disabled="loading" @click="emit('more')">{{ t.list.more }}</Button>
  </section>
</template>

<style scoped>
.conversation-list { display: grid; gap: 10px; padding: 12px; align-content: start; }
</style>
