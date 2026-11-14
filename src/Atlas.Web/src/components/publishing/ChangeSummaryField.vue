<script setup lang="ts">
import { computed, onBeforeUnmount, ref, toValue, useId, watch, type ComponentPublicInstance } from "vue";
import AiGlyph from "../ai/AiGlyph.vue";
import AiProvenance from "../ai/AiProvenance.vue";
import SuggestionChip from "../settings/SuggestionChip.vue";
import { Button } from "../ui/button";
import { runAiAction, aiUnavailableText } from "../../ai/aiAction";
import type { ChangeSummaryProposal } from "../../ai/writingApi";
import { reportAiInteraction } from "../../ai/interactions";
import { glow } from "../../motion/aiMotion";
import SettingsRow from "../settings/SettingsRow.vue";
import { Input } from "../ui/input";
import { fieldElement } from "../../lib/templateRef";
import { CHANGE_SUMMARY_MAX, normalizeChangeSummary } from "../../publishing/publication";
import t from "../../i18n/components-publishing";

/**
 * The optional "Popis změny" of a publication or a draft approval: one line, at most 500 characters.
 * The field only shows the text, the counter and the error it is given; the rule itself lives in
 * `normalizeChangeSummary` and the caller decides what an invalid value allows. Enter does not insert
 * anything (it is a single-line input) and asks the caller to submit.
 */
const props = withDefaults(defineProps<{
  modelValue: string;
  /** Validation or server error; shown under the field and announced when it appears. */
  error?: string;
  label?: string;
  help?: string;
  disabled?: boolean;
  suggest?: (signal: AbortSignal) => Promise<ChangeSummaryProposal | null>;
  /** What the document was when a proposal was made; a getter spares the caller a render on every keystroke. */
  contextKey?: string | (() => string);
  recordInteraction?: (outcome: "shown" | "applied" | "dismissed") => void;
}>(), { error: undefined, label: () => t.summary.label, help: () => t.summary.help, disabled: false, suggest: undefined, contextKey: undefined });
const emit = defineEmits<{ "update:modelValue": [value: string]; submit: [] }>();

const input = ref<ComponentPublicInstance>();
const proposal=ref<ChangeSummaryProposal|null>(null), suggesting=ref(false), proposalNotice=ref(""), applied=ref(false);
const interaction=(outcome:"shown"|"applied"|"dismissed")=>props.recordInteraction ? props.recordInteraction(outcome) : reportAiInteraction("publish.change-summary","change-summary",outcome);
let captured="", epoch=0, controller:AbortController|undefined;
const stale=computed(()=>!!proposal.value && !applied.value && (props.modelValue!==captured || proposal.value.contextKey!==toValue(props.contextKey)));
async function propose(){
  if(!props.suggest||suggesting.value)return;
  const mine=++epoch, request=controller=new AbortController();captured=props.modelValue;proposal.value=null;proposalNotice.value="";applied.value=false;suggesting.value=true;
  try{const result=await runAiAction(()=>props.suggest!(request.signal));if(mine!==epoch||request.signal.aborted)return;
    if(result.state==="unavailable")proposalNotice.value=result.text;
    else if(result.value && !normalizeChangeSummary(result.value.text).error){proposal.value=result.value;interaction("shown");}
    else if(!result.value)proposalNotice.value=t.summary.pageChanged;
  }catch{if(mine===epoch)proposalNotice.value=t.summary.proposalFailed;}
  finally{if(mine===epoch){suggesting.value=false;controller=undefined;}}
}
function apply(){if(!proposal.value||stale.value)return;applied.value=true;interaction("applied");emit("update:modelValue",proposal.value.text);const el=fieldElement(input.value);if(el)glow(el);}
function discard(){if(proposal.value)interaction("dismissed");proposal.value=null;proposalNotice.value="";}
watch(()=>props.suggest,value=>{if(value)return;const pending=suggesting.value;epoch++;controller?.abort();controller=undefined;suggesting.value=false;proposal.value=null;if(pending)proposalNotice.value=aiUnavailableText();});
onBeforeUnmount(()=>{epoch++;controller?.abort();});
const counterId = `change-summary-${useId()}-count`;
const length = computed(() => props.modelValue.trim().length);
function focus() { fieldElement(input.value)?.focus(); }
function enter(event: KeyboardEvent) {
  // Enter confirming a composition (IME) is not a request to submit.
  if (event.isComposing) return;
  event.preventDefault();
  emit("submit");
}
defineExpose({ focus });
</script>

<template>
  <SettingsRow :label="label" :help="help" :error="error" stacked class="change-summary">
    <template #default="{ id, describedBy, invalid }">
      <div class="change-summary-entry">
        <div class="change-summary-field">
          <Input
            :id="id"
            ref="input"
            name="changeSummary"
            type="text"
            autocomplete="off"
            :model-value="modelValue"
            :disabled="disabled"
            :aria-invalid="invalid"
            :aria-describedby="[describedBy, counterId].filter(Boolean).join(' ')"
            @update:model-value="emit('update:modelValue', String($event))"
            @keydown.enter="enter"
          />
          <span :id="counterId" class="change-summary-count" :data-over="length > CHANGE_SUMMARY_MAX || undefined">{{ length }} / {{ CHANGE_SUMMARY_MAX }}</span>
        </div>
        <Button v-if="suggest" type="button" size="sm" variant="ghost" :disabled="disabled || suggesting" :aria-busy="suggesting || undefined" data-change-summary-suggest @click="propose"><AiGlyph :size="15" :working="suggesting" />{{ t.summary.suggest }}</Button>
      </div>
      <SuggestionChip v-if="proposal && suggest" layout="panel" :label="t.summary.proposalLabel" :state="applied ? 'applied' : stale ? 'stale' : 'idle'" @apply="apply" @dismiss="discard"><template #glyph><AiGlyph :size="15" /></template><p>{{ proposal.text }}</p><AiProvenance :provenance="proposal.provenance" :sources="[]" /></SuggestionChip>
      <p v-if="proposalNotice" role="status" class="change-summary-notice">{{ proposalNotice }}</p>
    </template>
  </SettingsRow>
</template>

<style scoped>
.change-summary { padding: 0; border-top: 0; }
.change-summary-entry { display: flex; align-items: flex-start; gap: 8px; min-width: 0; }
.change-summary-field { display: grid; gap: 4px; min-width: 0; flex: 1; }
.change-summary-entry > button { flex: none; }
.change-summary-notice { margin: 0; color: var(--muted); font-size: 12.5px; }
.change-summary-count { justify-self: end; color: var(--muted); font-size: 12px; font-variant-numeric: tabular-nums; }
.change-summary-count[data-over] { color: var(--error); font-weight: 600; }
</style>
