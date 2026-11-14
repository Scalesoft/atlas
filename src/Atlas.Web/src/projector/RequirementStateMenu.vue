<script setup lang="ts">
import { nextTick, onMounted, ref, watch } from "vue";
import AtlasIcon from "../components/AtlasIcon.vue";
import InlineReveal from "../components/settings/InlineReveal.vue";
import t from "../i18n/projector";
import type { PartyRow } from "./partySuggestions";
import PartyInput from "./PartyInput.vue";
import { stateIcons } from "./requirementLook";
import { requirementStateLabel } from "./requirementSummary";
import { requirementStates, type RequirementState } from "./requirementTypes";

/**
 * The state chip's quick pick, as rows of the editor's context menu (`md-context-menu`, whose arrows walk the rows): one click per
 * state. „Čeká na vstup“ asks on whom in a panel of the editor's fields at the same chip (`asking`), so the summary can name them;
 * „Zpět“ there comes back to the rows, on „Čeká na vstup“. „Hotovo“ while prerequisites are missing asks once, in a line under the
 * rows rather than a dialog (U4-6): „Ještě chybí 3 prerekvizity — označit přesto?“. „Vlastní…“ (plan 3.4 §4) asks for the block's own
 * word in the same place; the state under it stays (neřešeno becomes rozpracováno) and counts, a default state clears the word.
 */
const props = defineProps<{
  state: RequirementState; party: string; partyContactId?: string; pick: (state: RequirementState, party?: string, partyContactId?: string) => void; close: () => void;
  /** The block's own word for its state, "" for none; `label` writes it with the state it counts as. */
  stateLabel?: string; label?: (text: string, state: RequirementState) => void;
  /** The party question instead of the rows. */
  asking?: boolean;
  /** Opens the party question; `back` returns to the rows. */
  ask?: () => void; back?: () => void;
  /** The row to focus when the rows come back. */
  focus?: RequirementState;
  /** How many prerequisites the block still waits for: „hotovo“ asks first, in a line under the rows (U4-6). */
  missing?: number;
  /** Opens on the „hotovo“ question (the heading's checkbox, requirementHeadingView.ts). */
  askDone?: boolean;
  /** Known names for the party (partySource.ts). */
  suggest?: (text: string, signal: AbortSignal) => Promise<PartyRow[]>;
}>();
const party = ref(props.party);
const contactId = ref(props.partyContactId ?? "");
const form = ref<HTMLFormElement>();
const menu = ref<HTMLElement>();
const confirmDone = ref<HTMLButtonElement>();
const asked = ref(!!props.askDone && !!props.missing);
const custom = ref(false);
const word = ref(props.stateLabel ?? "");
const wordInput = ref<HTMLInputElement>();
// A word over „neřešeno“ means work has started (plan 3.4 §4: „ověřit“ is rozpracováno).
const countsAs = props.state === "planned" ? "in_progress" : props.state;
watch(wordInput, input => { input?.focus(); input?.select(); });
function backToRows() {
  custom.value = false;
  void nextTick(() => menu.value?.querySelector<HTMLButtonElement>("[data-state-option=custom]")?.focus());
}
function saveWord() {
  // No word goes back to the state's own name and leaves the state as it is.
  const text = word.value.trim();
  props.label?.(text, text ? countsAs : props.state);
  props.close();
}
// The question takes the focus once it is there, so Enter confirms and Esc closes the menu as before.
watch(confirmDone, button => button?.focus());

function choose(state: RequirementState) {
  if (state === "waiting_for_input" && props.ask) { props.ask(); return; }
  if (state === "done" && props.missing && props.state !== "done" && !asked.value) {
    asked.value = true;
    return;
  }
  props.pick(state);
  props.close();
}
function confirm() {
  props.pick("waiting_for_input", party.value.trim(), contactId.value);
  props.close();
}
onMounted(() => {
  if (props.asking) form.value?.querySelector<HTMLInputElement>("[data-requirement-party-input]")?.select();
  else if (asked.value) confirmDone.value?.focus();
  else if (props.focus) menu.value?.querySelector<HTMLButtonElement>(`[data-state-option=${props.focus}]`)?.focus();
});
</script>

<template>
  <form v-if="asking" ref="form" class="md-context-fields" data-requirement-party @submit.prevent="confirm">
    <label class="md-context-field md-context-field-wide">{{ t.stateMenu.waitingOn }}
      <PartyInput v-if="suggest" v-model="party" v-model:contact-id="contactId" :suggest="suggest" :placeholder="t.stateMenu.partyExample" :label="t.stateMenu.waitingOn" />
      <input v-else v-model="party" class="md-input" maxlength="200" :placeholder="t.stateMenu.partyExample" data-requirement-party-input />
    </label>
    <div class="md-context-field-wide requirement-actions">
      <button type="button" class="md-btn" @click="back?.()">{{ t.stateMenu.back }}</button>
      <button type="submit" class="md-btn-primary">{{ requirementStateLabel("waiting_for_input").replace(/^./, letter => letter.toUpperCase()) }}</button>
    </div>
  </form>
  <form v-else-if="custom" class="md-context-fields" data-requirement-state-label @submit.prevent="saveWord">
    <label class="md-context-field md-context-field-wide">{{ t.stateMenu.customState }}
      <input ref="wordInput" v-model="word" class="md-input" maxlength="60" :placeholder="t.stateMenu.customExample" data-requirement-state-label-input />
    </label>
    <p class="md-context-field-wide requirement-counts-as">{{ t.stateMenu.countsAs(requirementStateLabel(countsAs)) }}</p>
    <div class="md-context-field-wide requirement-actions">
      <button type="button" class="md-btn" @click="backToRows">{{ t.stateMenu.back }}</button>
      <button type="submit" class="md-btn-primary">{{ t.stateMenu.save }}</button>
    </div>
  </form>
  <div v-else ref="menu" class="requirement-menu">
    <button v-for="option in requirementStates" :key="option" type="button" role="menuitemradio" class="md-context-menu-item" :aria-checked="option === state && !stateLabel"
      :data-state-option="option" @click="choose(option)">
      <AtlasIcon :name="stateIcons[option]" :size="16" />{{ requirementStateLabel(option) }}
    </button>
    <button v-if="label" type="button" role="menuitemradio" class="md-context-menu-item" :aria-checked="!!stateLabel" data-state-option="custom" @click="custom = true">
      <AtlasIcon name="pencil" :size="16" />{{ stateLabel ? t.stateMenu.customNamed(stateLabel) : t.stateMenu.custom }}
    </button>
    <InlineReveal :open="asked">
      <p class="requirement-done-hint" role="status" data-requirement-done-hint>
        {{ t.stateMenu.stillMissing(missing ?? 0) }}
        <button ref="confirmDone" type="button" class="md-btn" @click="asked = false; pick('done'); close()">{{ t.stateMenu.markAnyway }}</button>
      </p>
    </InlineReveal>
  </div>
</template>

<style scoped>
.requirement-menu { display: grid; gap: 1px; }
.requirement-done-hint { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 8px; margin: 4px 0 0; padding: 8px 10px 4px;
  border-top: 1px solid var(--line); color: var(--muted, var(--text-dim)); font-size: 12.5px; line-height: 1.4; }
.requirement-actions { display: flex; justify-content: flex-end; gap: 6px; }
.requirement-counts-as { margin: 0; color: var(--muted, var(--text-dim)); font-size: 12.5px; }
</style>
