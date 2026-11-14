<script setup lang="ts">
import {computed, onBeforeUnmount, useId} from "vue";
import type {AiFeedbackBody,AiSourceView} from "../../ai/assistantApi";
import {usePageSummary,type PageSummaryApi} from "../../ai/usePageSummary";
import AiAnswer from "./AiAnswer.vue";
import AiGlyph from "./AiGlyph.vue";
import {collapseHeight, revealHeight} from "../../motion/aiMotion";
import t from "../../i18n/ai-answer";
import {Button} from "../ui/button";
const props=defineProps<{page:{id:string;version:number};enabled:boolean;canContinue?:boolean;api?:PageSummaryApi}>();
const emit=defineEmits<{"open-source":[source:AiSourceView,newTab:boolean];continue:[conversationId:string]}>();
const summary=usePageSummary(computed(()=>props.page),computed(()=>props.enabled),props.api);
const contentId=useId();
let motion:Animation|null=null;
function cancelMotion(){motion?.cancel();motion=null;}
function transition(element:Element,done:()=>void,opening:boolean){
  cancelMotion();
  const el=element as HTMLElement;
  el.removeAttribute("inert");el.removeAttribute("aria-hidden");
  const animation=opening?revealHeight(el):collapseHeight(el);
  motion=animation;
  if(!animation){done();return;}
  animation.finished.then(()=>{if(motion===animation)motion=null;done();},done);
}
onBeforeUnmount(cancelMotion);
function feedback(body:{rating:"down";reason:string|null;note:string|null}){void summary.feedback({...body,reason:body.reason as AiFeedbackBody["reason"]});}
</script>
<template>
  <div v-if="enabled || summary.visible.value" :key="`${page.id}:${page.version}`" class="page-summary" data-page-summary>
    <div class="page-summary-toolbar"><Button v-if="enabled" type="button" variant="ghost" size="xs" class="page-summary-trigger" :aria-disabled="summary.pending.value || undefined" :aria-expanded="summary.visible.value" :aria-controls="contentId" data-page-summary-start @click="summary.ask()"><AiGlyph size="xs" state="ready" />{{ t.summary.start }}</Button></div>
    <Transition :css="false" @enter="(el,done)=>transition(el,done,true)" @leave="(el,done)=>transition(el,done,false)" @enter-cancelled="cancelMotion" @leave-cancelled="cancelMotion">
      <div v-if="summary.visible.value && summary.state.value !== 'canceled'" :id="contentId" class="page-summary-content">
      <AiAnswer :state="summary.state.value" variant="compact" format="bullets" :title="t.summary.title" task="summary" :sources="summary.sources.value" :sentences="summary.message.value?.sentences" :provenance="summary.message.value?.provenance" :created-at="summary.message.value?.createdAt" :truncated="summary.truncated.value" :rating="summary.rating.value" @stop="summary.close()" @retry="summary.ask()" @ask-again="summary.ask()" @try-other="summary.close()" @open-source="(source,newTab)=>emit('open-source',source,newTab)" @rate="rating=>summary.feedback({rating})" @feedback="feedback">
        <template #actions><Button v-if="canContinue && summary.message.value" size="sm" variant="outline" @click="emit('continue',summary.message.value.conversationId)">{{ t.summary.continue }}</Button><Button size="sm" variant="ghost" @click="summary.close()">{{ t.summary.close }}</Button></template>
      </AiAnswer>
      </div>
    </Transition>
  </div>
</template>
<style scoped>
.page-summary { min-width:0; margin:0 0 8px; }
.page-summary-toolbar { display:flex; justify-content:flex-end; }
.page-summary-trigger { color:var(--muted); font-weight:450; }
.page-summary-trigger[aria-expanded="true"] { color:var(--text); }
/* Busy, it stays focusable (a disabled button drops the keyboard's focus to the page); ask() ignores a second press. */
.page-summary-trigger[aria-disabled="true"] { opacity:.5; pointer-events:none; }
@media (hover: hover) and (pointer: fine) { .page-summary-trigger:hover { color:var(--text); } }
/* The 8 px gap lives inside the box whose height is animated (flow-root keeps the child's margin in it). As the box's own padding it
   could not go below 8 px: the summary opened with an 8 px jump before the unfolding began and left with one at its end. */
.page-summary-content { display:flow-root; }
.page-summary-content > :first-child { margin-top:8px; }
</style>
