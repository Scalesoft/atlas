<script setup lang="ts">
import { ref } from "vue";
import { errorMessage } from "../api";
import AtlasIcon from "../components/AtlasIcon.vue";
import { Button } from "../components/ui/button";
import { useToast } from "../composables/useToast";
import type { OverviewCardAction } from "./overviewKinds";

/**
 * The bulk actions of an overview in its card's foot (every kind the same: the requirements', the contacts', the issues' cards).
 * An action is busy until it ends (a second click does not run it twice); its failure ends in a toast. While it runs the buttons
 * are aria-disabled, not disabled, so the keyboard stays on the one pressed.
 */
defineProps<{ actions?: readonly OverviewCardAction[] }>();
const toast = useToast();
const busy = ref<string>();
async function run(action: OverviewCardAction) {
  if (busy.value) return;
  busy.value = action.id;
  try { await action.run(); } catch (failure) { toast.error(`${action.label}: ${errorMessage(failure)}`); } finally { busy.value = undefined; }
}
</script>

<template>
  <span v-if="actions?.length" class="overview-actions">
    <Button v-for="action in actions" :key="action.id" variant="outline" size="sm" type="button" :data-overview-action="action.id"
      class="aria-disabled:opacity-50" :aria-disabled="!!busy || undefined" :aria-busy="busy === action.id || undefined" @click="run(action)">
      <AtlasIcon v-if="busy === action.id" name="loader-circle" :size="14" class="atlas-spinner" /><AtlasIcon v-else-if="action.icon" :name="action.icon" :size="14" />{{ action.label }}
    </Button>
  </span>
</template>

<style scoped>
.overview-actions { display: inline-flex; flex-wrap: wrap; gap: .375rem; }
/* The card's foot pill, as the view's Řadit and Stav beside it (FilterMenu): in the editor the outline variant's border colour token is
   not set, so the border went black (F27). */
.overview-actions > button { min-height: 32px; height: auto; padding: 4px 10px; border: 1px solid var(--line); border-radius: 999px; background: var(--surface); color: var(--text); font-size: 13px; font-weight: 400; }
@media (hover: hover) and (pointer: fine) { .overview-actions > button:hover { border-color: color-mix(in srgb, var(--accent) 45%, var(--line)); background: var(--surface); } }
</style>
