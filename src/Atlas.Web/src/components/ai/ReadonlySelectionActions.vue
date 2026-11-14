<script setup lang="ts">
import {computed,onBeforeUnmount,onMounted} from "vue";
import {Popover,PopoverAnchor,PopoverContent} from "../ui/popover";
import {Button} from "../ui/button";
import AiGlyph from "./AiGlyph.vue";
import AtlasIcon from "../AtlasIcon.vue";
import CopyPageLink from "../CopyPageLink.vue";
import {aiIntentAvailable,aiText} from "../../assist/aiIntents";
import {useToast} from "../../composables/useToast";
import aiQuota from "../../i18n/ai-quota";

/**
 * The toolbar under text selected on a read page (refresh 2.5, board „Asistent – vstupy a průběh“): one quiet bar on the popover
 * surface (as every menu, in light and dark) that starts from the selection, below it — the browser's own selection menu (Edge's mini menu) takes the space above. It leads with Kopírovat; with the assistant available the AI actions follow, all backed by the one assistant capability:
 * Zeptat se (the panel opens with the selection attached to the next question), Vysvětlit and Shrnout (a new conversation
 * about the page asks at once). Then the actions that need no model: Kopírovat odkaz na sekci and Kde se to ještě zmiňuje
 * (the search with the selection). Focus stays in the text; the bar never takes it on its own.
 * P11: the AI actions are the reading intents of aiIntents.ts and follow its one rule and one limit: `text` is null when the selection
 * holds only an element, a secret or more than the limit, and the AI part is then not shown at all (no notice) while the other
 * actions stay. A selection inside a heading points at its section (`section`): the AI actions take the whole section, and the bar
 * names it in visible text before them (a title is invisible on touch and to the keyboard).
 */
export interface ReadonlySelection {text:string|null;searchText?:string|null;section?:string;anchorRect:DOMRect;sectionHref?:string;}
const props=withDefaults(defineProps<{selection:ReadonlySelection|null;enabled:boolean;explainEnabled?:boolean}>(),{explainEnabled:true});
const emit=defineEmits<{ask:[text:string];explain:[text:string];summarize:[text:string];find:[text:string];close:[]}>();
const reference=computed(()=>props.selection ? {getBoundingClientRect:()=>props.selection!.anchorRect} : undefined);
// Every button is a reading intent: the one rule makes them all depend on canAnswer (`explainEnabled`).
const sendable=computed(()=>aiIntentAvailable("ask",{editing:false,canAnswer:props.explainEnabled,transform:false}) ? aiText(props.selection?.text) : null);
// The gallery passes only `text`; the page passes `searchText`, null for a secret (nothing to search for).
const findText=computed(()=>props.selection?.searchText === undefined ? props.selection?.text ?? null : props.selection.searchText);
const sectionUrl=computed(()=>props.selection?.sectionHref ? new URL(props.selection.sectionHref,location.origin+location.pathname).href : undefined);
const shown=computed(()=>props.enabled && !!props.selection && !!(sendable.value || findText.value || sectionUrl.value));
const about=(verb:string)=>aiQuota.selection.about(verb,props.selection?.section);
// Kopírovat (J5.4, the one bar of a reader: the editor's own reader bubble stays off): as the browser copies, the copied formatting
// and the selection kept; the selected text when the browser refuses.
const toaster=useToast();
async function copy(){
  let done=false;
  try{done=document.execCommand("copy");}catch{/* refused */}
  const text=document.getSelection()?.toString();
  if(!done&&text){try{await navigator.clipboard.writeText(text);done=true;}catch{/* refused too */}}
  if(done)toaster.success(aiQuota.selection.copied,{id:"readonly-copy"}); else toaster.error(aiQuota.selection.copyFailed);
}
// J5.3 as in the editor's bubble: Alt+F10 from the page into the bar (read text takes no focus), arrows, Home and End in it, Esc back.
const barButtons=()=>[...document.querySelectorAll<HTMLButtonElement>("[data-readonly-actions] button:not(:disabled)")];
let returnFocus:Element|null=null;
function onPageKeydown(event:KeyboardEvent){
  if(event.key!=="F10"||!event.altKey||event.ctrlKey||event.metaKey||event.shiftKey||event.defaultPrevented||!shown.value)return;
  const first=barButtons()[0];
  if(!first)return;
  event.preventDefault();returnFocus=document.activeElement;first.focus();
}
function onBarKeydown(event:KeyboardEvent){
  const buttons=barButtons(),index=buttons.indexOf(event.target as HTMLButtonElement);
  if(index<0)return;
  const next=event.key==="ArrowRight"?(index+1)%buttons.length:event.key==="ArrowLeft"?(index-1+buttons.length)%buttons.length:event.key==="Home"?0:event.key==="End"?buttons.length-1:-1;
  if(next<0)return;
  event.preventDefault();buttons[next]!.focus();
}
// Esc (the popover's own dismissal, which closes the bar) keeps the selection; from inside the bar the focus goes back where it was.
function onEscape(){
  const from=returnFocus;returnFocus=null;
  if(!document.querySelector("[data-readonly-actions]")?.contains(document.activeElement))return;
  if(from instanceof HTMLElement&&from.isConnected&&from!==document.body)from.focus();else (document.activeElement as HTMLElement).blur();
}
onMounted(()=>document.addEventListener("keydown",onPageKeydown));
onBeforeUnmount(()=>document.removeEventListener("keydown",onPageKeydown));
</script>
<template><Popover :open="shown" @update:open="value=>{if(!value)emit('close')}"><PopoverAnchor :reference="reference" /><PopoverContent v-if="shown && selection" role="toolbar" :aria-label="aiQuota.selection.toolbar" side="bottom" align="start" :side-offset="8" class="readonly-selection-actions w-auto" data-readonly-actions @open-auto-focus.prevent @close-auto-focus.prevent @escape-key-down="onEscape" @keydown="onBarKeydown">
  <Button type="button" variant="ghost" size="sm" data-readonly-copy @mousedown.prevent @click="copy"><AtlasIcon name="copy" :size="14" />{{ aiQuota.selection.copy }}</Button>
  <template v-if="sendable">
    <span class="readonly-selection-glyph"><AiGlyph state="suggest" :size="15" /></span>
    <span v-if="selection.section" class="readonly-selection-target" data-readonly-target>{{ aiQuota.selection.section(selection.section) }}</span>
    <Button type="button" variant="ghost" size="sm" class="readonly-selection-primary" :title="about(aiQuota.selection.askVerb)" data-readonly-ask @click="emit('ask',sendable)">{{ aiQuota.selection.ask }}</Button>
    <Button type="button" variant="ghost" size="sm" :title="about(aiQuota.selection.explainVerb)" data-readonly-explain @click="emit('explain',sendable)">{{ aiQuota.selection.explain }}</Button>
    <Button type="button" variant="ghost" size="sm" :title="about(aiQuota.selection.summarizeVerb)" data-readonly-summarize @click="emit('summarize',sendable)">{{ aiQuota.selection.summarize }}</Button>
    <span v-if="sectionUrl || findText" class="readonly-selection-divider" aria-hidden="true" />
  </template>
  <CopyPageLink v-if="sectionUrl" :href="sectionUrl" :label="aiQuota.selection.copySectionLink" text />
  <Button v-if="findText" type="button" variant="ghost" size="sm" data-readonly-find @click="emit('find',findText)"><AtlasIcon name="search" :size="14" />{{ aiQuota.selection.find }}</Button>
</PopoverContent></Popover></template>
<style>
/* Portalled out of the page, so not scoped. The popover's own surface, line, radius and shadow (--raised in dark, palettes.css);
   the labels in the text colour, icons muted, the AI part marked only by its sparkle in the AI ink and a faint wash on Zeptat se. */
.readonly-selection-actions { display:flex; flex-wrap:wrap; align-items:center; gap:2px; max-width:calc(100vw - 24px); min-height:42px; padding:4px; box-shadow:var(--shadow-menu); }
.readonly-selection-actions button { height:var(--control-h-sm); gap:6px; padding:0 10px; border-radius:var(--radius-sm); color:var(--text); background:transparent; font-weight:500; }
.readonly-selection-actions button > svg { color:var(--muted); }
@media (hover: hover) and (pointer: fine) { .readonly-selection-actions button:hover:not(:disabled) { background:var(--hover); color:var(--text); } }
.readonly-selection-actions button:focus-visible { background:var(--hover); outline:var(--focus-ring); outline-offset:-2px; }
.readonly-selection-actions .readonly-selection-primary { background:var(--ai-wash); font-weight:600; }
@media (hover: hover) and (pointer: fine) { .readonly-selection-actions .readonly-selection-primary:hover:not(:disabled) { background:var(--ai-wash-strong); } }
.readonly-selection-glyph { display:inline-flex; align-items:center; align-self:center; height:20px; margin-left:4px; padding:0 6px 0 10px; border-left:1px solid var(--line); }
.readonly-selection-target { max-width:14em; padding:0 6px 0 0; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-size:12px; color:var(--muted); }
.readonly-selection-divider { width:1px; height:20px; margin:0 4px; background:var(--line); }
</style>
