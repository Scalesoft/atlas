import {onScopeDispose,ref,watch,type Ref} from "vue";
import {runAiAction} from "../ai/aiAction";
import {checkSpelling,proposeChangeSummary,type ChangeSummaryProposal} from "../ai/writingApi";
import type {PublicationCheck} from "./publication";
interface Source {page:Ref<{id:string;version:number}|null|undefined>;checkAllowed:Ref<boolean>;summaryAllowed:Ref<boolean>;prepare:()=>Promise<boolean>;snapshot:()=>string;contextKey:Ref<string>;}
export function usePublishAssistant(source:Source){
  const findings=ref<PublicationCheck[]>([]);let controller:AbortController|undefined,epoch=0,lastKey="";
  function reset(){epoch++;controller?.abort();controller=undefined;findings.value=[];lastKey="";}
  watch(()=>[source.page.value?.id,source.page.value?.version,source.checkAllowed.value],reset,{flush:"sync"});
  function startSpelling(){
    const page=source.page.value,key=source.contextKey.value;if(!page||!source.checkAllowed.value||lastKey===key)return;
    reset();lastKey=key;const mine=epoch,request=controller=new AbortController(),snapshot=source.snapshot();
    void runAiAction(()=>checkSpelling(page.id,page.version,request.signal)).then(result=>{
      if(mine!==epoch||request.signal.aborted||result.state!=="done"||!source.checkAllowed.value||source.snapshot()!==snapshot)return;
      findings.value=result.value.issues.map((issue,index)=>({id:`spelling:${page.id}:${page.version}:${index}`,kind:"spelling",severity:"info",text:`Pravopis: „${issue.original}“ → „${issue.correction}“`,provenance:result.value.provenance}));
    }).catch(()=>{}).finally(()=>{if(controller===request)controller=undefined;});
  }
  async function suggestSummary(signal:AbortSignal):Promise<ChangeSummaryProposal|null>{
    if(!source.summaryAllowed.value||!(await source.prepare())||signal.aborted)return null;
    const page=source.page.value;if(!page)return null;const contextKey=source.contextKey.value,snapshot=source.snapshot();
    const result=await proposeChangeSummary(page.id,page.version,signal);
    if(signal.aborted||!source.summaryAllowed.value||contextKey!==source.contextKey.value||snapshot!==source.snapshot())return null;
    return {...result,contextKey};
  }
  onScopeDispose(reset);return{findings,startSpelling,suggestSummary};
}
