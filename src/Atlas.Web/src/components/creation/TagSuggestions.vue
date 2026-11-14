<script setup lang="ts">
import {computed,onBeforeUnmount,ref,watch} from "vue";
import type {PageTag} from "../../pageTags";
import type {AiProvenanceView} from "../../ai/assistantApi";
import {runAiAction,aiUnavailableText} from "../../ai/aiAction";
import {reportAiInteraction} from "../../ai/interactions";
import AiGlyph from "../ai/AiGlyph.vue";
import AiProvenance from "../ai/AiProvenance.vue";
import SuggestionChip from "../settings/SuggestionChip.vue";
import {Button} from "../ui/button";
import t from "../../i18n/components-creation";
const props=defineProps<{recordInteraction?: (outcome:"shown"|"applied"|"dismissed")=>void;contextKey:string;request?: (signal:AbortSignal)=>Promise<{tags:PageTag[];provenance:AiProvenanceView}>}>();
const emit=defineEmits<{apply:[tags:PageTag[]]}>();
const proposal=ref<{tags:PageTag[];provenance:AiProvenanceView}|null>(null),busy=ref(false),notice=ref(""),applied=ref(false);
const interaction=(outcome:"shown"|"applied"|"dismissed")=>props.recordInteraction ? props.recordInteraction(outcome) : reportAiInteraction("page.tag-suggest","tags",outcome);
let epoch=0,controller:AbortController|undefined,captured="";
const stale=computed(()=>proposal.value && captured!==props.contextKey && !applied.value);
function clear(){epoch++;controller?.abort();controller=undefined;proposal.value=null;busy.value=false;notice.value="";}
async function suggest(){if(!props.request||busy.value)return;clear();captured=props.contextKey;applied.value=false;const mine=epoch,request=controller=new AbortController();busy.value=true;
try{const result=await runAiAction(()=>props.request!(request.signal));if(mine!==epoch||request.signal.aborted)return;
if(result.state==="unavailable")notice.value=result.text;else if(result.value.tags.length){proposal.value=result.value;interaction("shown");}else notice.value=t.suggestions.tagsNone;
}catch{if(mine===epoch)notice.value=t.suggestions.tagsFailed;}finally{if(mine===epoch){busy.value=false;controller=undefined;}}}
function apply(){if(!proposal.value||stale.value)return;applied.value=true;emit("apply",proposal.value.tags);interaction("applied");}
function dismiss(){if(proposal.value)interaction("dismissed");clear();}
watch(()=>props.request,value=>{if(value)return;const pending=busy.value;clear();if(pending)notice.value=aiUnavailableText();});
onBeforeUnmount(clear);
</script>
<template><div v-if="request || notice" class="tag-suggestions"><Button v-if="request" type="button" variant="ghost" size="sm" :disabled="busy" data-tag-suggest @click="suggest"><AiGlyph :size="15" :working="busy" />{{busy?t.suggestions.tagsBusy:t.suggestions.tagsSuggest}}</Button><SuggestionChip v-if="proposal && request" layout="panel" :label="t.suggestions.tagsChip" :state="applied?'applied':stale?'stale':'idle'" @apply="apply" @dismiss="dismiss"><template #glyph><AiGlyph :size="15" /></template><span v-for="tag in proposal.tags" :key="tag.id" class="tag-suggestion-value">#{{tag.name}}</span><AiProvenance :provenance="proposal.provenance" :sources="[]" /></SuggestionChip><p v-if="notice" role="status">{{notice}}</p></div></template>
<style scoped>.tag-suggestions {display:grid;gap:8px;justify-items:start;} .tag-suggestion-value {display:inline-block;margin:0 8px 8px 0;color:var(--accent);} .tag-suggestions p {margin:0;color:var(--muted);font-size:12.5px;}</style>
