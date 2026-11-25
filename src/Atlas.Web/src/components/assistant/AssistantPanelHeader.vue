<script setup lang="ts">
import { computed, ref } from "vue";
import t from "../../i18n/assistant-panel";
import { useAssistant } from "../../assistant/useAssistant";
import ActionDisclosure from "../ActionDisclosure.vue";
import ActionDisclosureItem from "../ActionDisclosureItem.vue";
import AtlasIcon from "../AtlasIcon.vue";
import AiGlyph from "../ai/AiGlyph.vue";
import ConfirmDialog from "../settings/ConfirmDialog.vue";
import { Button } from "../ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "../ui/tooltip";

/**
 * The header of the Assistant panel (the `header` slot of WorkspaceSidePanel, which adds the close button), as on the board
 * „Asistent – vstupy a průběh“: the AI glyph (size m), „Asistent“, and the two everyday actions as icon buttons, Nová
 * konverzace (+) and Moje konverzace (history). Smazat konverzaci stays one step away in ⋯ behind a confirmation, so it is
 * never a misclick. The glyph is here because the panel is the entry of a model task; it never appears while the capability
 * is off, because then there is no panel.
 */
const assistant = useAssistant();
const glyphState = computed(() => {
  if (assistant.outage.value) return 'unavailable';
  const active = assistant.items.value.find(item => item.kind === 'answer' && (item.state === 'retrieving' || item.state === 'preparing'));
  return active?.kind === 'answer' ? (active.state === 'retrieving' ? 'searching' : 'writing') : 'idle';
});
const confirming = ref(false);
const busy = ref(false);

async function remove() {
  busy.value = true;
  const done = await assistant.deleteCurrent();
  busy.value = false;
  if (done) confirming.value = false;
}
</script>

<template>
  <span class="assistant-header" data-assistant-header>
    <AiGlyph size="m" :state="glyphState" />
    <span class="assistant-header-title">{{ t.header.title }}</span>
    <Tooltip><TooltipTrigger as-child><Button type="button" variant="ghost" size="icon-sm" class="assistant-header-action" :aria-label="t.header.new" data-assistant-new @click="assistant.newConversation()"><AtlasIcon name="plus" :size="16" /></Button></TooltipTrigger><TooltipContent>{{ t.header.new }}</TooltipContent></Tooltip>
    <Tooltip><TooltipTrigger as-child><Button type="button" variant="ghost" size="icon-sm" class="assistant-header-action" :aria-label="t.header.mine" :aria-pressed="assistant.view.value === 'list'" data-assistant-history @click="assistant.view.value === 'list' ? assistant.showThread() : assistant.showList()"><AtlasIcon name="history" :size="16" /></Button></TooltipTrigger><TooltipContent>{{ t.header.mine }}</TooltipContent></Tooltip>
    <ActionDisclosure :label="t.header.more" class="assistant-header-menu">
      <ActionDisclosureItem :disabled="!assistant.conversationId.value" @click="confirming = true"><AtlasIcon name="trash" :size="16" />{{ t.header.delete }}</ActionDisclosureItem>
    </ActionDisclosure>
    <ConfirmDialog :open="confirming" :title="t.header.confirmTitle" :confirm-label="t.header.confirm" confirm-icon="trash" :busy="busy" @confirm="remove" @cancel="confirming = false">
      <p>{{ t.header.confirmText }}</p>
    </ConfirmDialog>
  </span>
</template>

<style scoped>
.assistant-header { display: flex; flex: 1; align-items: center; gap: 4px; min-width: 0; }
.assistant-header > .ai-glyph { margin-right: 4px; }
.assistant-header-title { flex: 1; min-width: 0; overflow: hidden; font-size: 15px; font-weight: 650; text-overflow: ellipsis; white-space: nowrap; }
.assistant-header-action { color: var(--muted); }
.assistant-header-action[aria-pressed="true"] { color: var(--text); }
@media (hover: hover) and (pointer: fine) { .assistant-header-action:hover { color: var(--text); } }
.assistant-header-action[aria-pressed="true"] { background: var(--ai-wash-strong); }
</style>
