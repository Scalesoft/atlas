import { computed, onScopeDispose, ref, shallowRef, watch, type Ref } from "vue";
import { runAnswerTurn, type AnswerTurnState } from "./answerTurn";
import { completeMessage, preparePageSummary, sendFeedback, type AiFeedbackBody, type AiMessageView, type AiSourceView } from "./assistantApi";
export interface PageSummaryApi { preparePageSummary: typeof preparePageSummary; completeMessage: typeof completeMessage; sendFeedback: typeof sendFeedback; }
export function usePageSummary(page: Ref<{id:string;version:number}|null|undefined>, enabled: Ref<boolean>, api: PageSummaryApi = {preparePageSummary,completeMessage,sendFeedback}) {
  const state=ref<AnswerTurnState>("canceled"), sources=ref<AiSourceView[]>([]), message=shallowRef<AiMessageView|null>(null);
  const truncated=ref(false), rating=ref<"up"|"down"|null>(null), visible=ref(false);
  const pending=computed(()=>state.value==="retrieving"||state.value==="preparing");
  let epoch=0, controller:AbortController|undefined;
  function close() { epoch++; controller?.abort(); controller=undefined; visible.value=false; state.value="canceled"; sources.value=[]; message.value=null; truncated.value=false; rating.value=null; }
  watch(()=>[page.value?.id,page.value?.version],close,{flush:"sync"});
  watch(enabled,value=>{if(value)return;const working=pending.value;close();if(working){visible.value=true;state.value="unavailable";}},{flush:"sync"});
  async function ask() {
    if(!page.value||!enabled.value||controller)return;
    close();visible.value=true;const mine=epoch, request=controller=new AbortController();
    await runAnswerTurn({prepare:signal=>api.preparePageSummary(page.value!.id,signal),complete:api.completeMessage,signal:request.signal,onUpdate:update=>{
      if(mine!==epoch)return;state.value=update.state;sources.value=update.sources;truncated.value=update.truncated??false;if(update.message)message.value=update.message;
    }});
    if(controller===request)controller=undefined;
  }
  async function feedback(body:AiFeedbackBody) { if(!message.value)return;const before=rating.value;rating.value=body.rating;if(body.rating==="down"&&!body.reason)return;try{await api.sendFeedback(message.value.id,body);}catch{rating.value=before;} }
  onScopeDispose(close);
  return{state,sources,message,truncated,rating,visible,pending,ask,close,stop:()=>controller?.abort(),feedback};
}
