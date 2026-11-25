<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from "vue";
import { animateAtlas, motionMs, scrollBehavior } from "../../motion/aiMotion";
import { useAssistant, type ScopeChoice } from "../../assistant/useAssistant";
import t from "../../i18n/assistant-panel";
import a from "../../i18n/assistant";
import { wikiTasks, type WikiTask } from "../../assistant/assistantEntries";
import ConversationList from "./ConversationList.vue";
import ConversationThread from "./ConversationThread.vue";
import AssistantComposer from "./AssistantComposer.vue";
import AtlasIcon from "../AtlasIcon.vue";
import AiGlyph from "../ai/AiGlyph.vue";
import LoadingNote from "../settings/LoadingNote.vue";
import OptionPills from "../settings/OptionPills.vue";
import AssistantToolsInfo, { type ToolGroup } from "./AssistantToolsInfo.vue";
import AiEffortPicker from "../ai/AiEffortPicker.vue";
import { EFFORT_STEPS } from "../../ai/answerEffort";
import { Button } from "../ui/button";
import { Switch } from "../ui/switch";
import { useAiCapabilities } from "../../composables/useAiCapabilities";
import { vScrollEdges } from "../../lib/scrollEdges";

/**
 * The Assistant (plan phase 3 §15; refresh 2.5 board „Asistent – vstupy a průběh“), the content of the non-modal
 * WorkspaceSidePanel: the context of the next question (the scope as pills, a selection taken from the page as a chip), the
 * thread, the composer with the answer effort picker. An empty conversation greets with the large glyph and offers questions that fit the context; a
 * suggestion only fills the field, the person sends it. Moje konverzace replace the thread while open. It composes existing
 * Atlas parts (OptionPills, SettingsList, AiAnswer, ConfirmDialog) and owns no state: that is `useAssistant`. The page beside
 * it stays usable; a citation opens the page in the main area and leaves this panel open.
 *
 * - The first Escape stops a running answer, the next one (WorkspaceSidePanel) closes the panel.
 * - Outage (AI-25): with an exchange in flight the panel stays, the answer place holds the neutral sentence and the composer
 *   is gone. Without one the panel is closed by the state, not here.
 */
/** `agent` and `arrange` override the capabilities of the same names: the gallery shows the panel with and without the right (no server there); `toolGroups` is the sample list of „Co umí“ then. */
const props = withDefaults(defineProps<{ agent?: boolean; arrange?: boolean; toolGroups?: ToolGroup[] }>(), { agent: undefined, arrange: undefined, toolGroups: undefined });
const assistant = useAssistant();
const { capabilities } = useAiCapabilities();
const hasAgent = computed(() => props.agent ?? capabilities.value.agent);
/** With tools the agent reads what it needs, so the effort (how many wiki passages an answer gets) has nothing to say (D14). */
/** The switch is there whenever the agent is, also without answers from the wiki (no index): tools run only once the user turns them on (D14a, AD5-3). */
const agentOn = computed(() => hasAgent.value && assistant.tools.value);
const { items, context } = assistant;
const scroller = ref<HTMLElement>();
const composer = ref<InstanceType<typeof AssistantComposer>>();

const pills = computed(() => assistant.scopeOptions.value.map((value) => ({
  value, label: value === "page" ? a.scope.page : value === "space" ? (context.value.spaceName ?? a.scope.spaceDefault) : a.scope.wikiShort,
})));
const hasConversation = computed(() => items.value.length > 0);
const readOnlyText = computed(() => assistant.readOnly.value === "outdated" ? a.notice.outdated : a.notice.unavailableConversation);
const selectionPreview = computed(() => {
  const text = assistant.selection.value?.replace(/\s+/g, " ") ?? "";
  if (text.length <= 28) return text;
  const cut = text.slice(0, 27);
  return `${(cut.lastIndexOf(" ") > 12 ? cut.slice(0, cut.lastIndexOf(" ")) : cut).trimEnd()}…`;
});
/** Questions that fit what is on screen. A finished one (the page) can be sent as it is; an open one ends with a space to go on. */
const suggestions = computed(() => {
  if (assistant.scopeChoice.value === "page" && context.value.pageId) return t.panel.pageQuestions;
  if (assistant.scopeChoice.value === "space" && context.value.spaceName) return t.panel.spaceQuestions(context.value.spaceName);
  return t.panel.wikiQuestions;
});
/** With tools on, the wiki tasks of the screen (icons, tags, contacts, the menu) lead; one fills the field with its instruction. */
const tasks = computed(() => agentOn.value ? wikiTasks(context.value, props.arrange ?? capabilities.value.arrangePages) : []);

function escape(event: KeyboardEvent) {
  if (event.isComposing || !assistant.pending.value) return;
  // The first Escape only stops the answer; WorkspaceSidePanel never sees it.
  event.preventDefault();
  event.stopPropagation();
  assistant.stop();
}

async function toBottom() {
  await nextTick();
  const el = scroller.value;
  if (el && typeof el.scrollTo === "function") el.scrollTo({ top: el.scrollHeight, behavior: scrollBehavior(el) });
}
watch(() => items.value.length, toBottom);
watch(() => assistant.view.value, (view) => { if (view === "thread") void nextTick(() => composer.value?.focus()); });
watch(() => assistant.selection.value, (text) => { if (text) void nextTick(() => composer.value?.focus(true)); });
onMounted(() => { void toBottom(); });

function tryOther(question: string) {
  assistant.draft.value = question;
  void nextTick(() => composer.value?.focus());
}
function suggest(text: string) {
  assistant.draft.value = text;
  void nextTick(() => composer.value?.focus(true));
}
function suggestTask(task: WikiTask) {
  assistant.chooseScope(task.scope);
  suggest(task.prompt);
}
function askAgain(question: string) { void assistant.ask(question); }
/** Nothing found: the same question in the whole wiki, or read deeper (Do hloubky, then Naplno). Both stay chosen afterwards. */
const deeper = computed(() => {
  const index = EFFORT_STEPS.findIndex((step) => step.value === assistant.effort.value);
  return index < 3 ? EFFORT_STEPS[3]! : index === 3 ? EFFORT_STEPS[4]! : null;
});
function widen(question: string) { assistant.chooseScope("wiki"); void assistant.ask(question); }
function deepen(question: string) {
  if (!deeper.value) return;
  assistant.effort.value = deeper.value.value;
  void assistant.ask(question);
}
/** The greeting leaves when the first question arrives: it is taken out of the flow at once (the thread takes its place and
 *  rises in) and fades up and away over it, instead of vanishing in the frame the question appears. */
function introLeave(element: Element, done: () => void) {
  const el = element as HTMLElement;
  if (!el.classList.contains("assistant-intro")) { done(); return; }
  Object.assign(el.style, { position: "absolute", top: `${el.offsetTop}px`, left: `${el.offsetLeft}px`, width: `${el.offsetWidth}px`, pointerEvents: "none" });
  el.setAttribute("aria-hidden", "true");
  const leaving = animateAtlas(el, [{ opacity: 1, transform: "none" }, { opacity: 0, transform: "translateY(calc(var(--atlas-rise) * -1.5)) scale(.97)" }], { duration: motionMs(el, "--atlas-motion-base"), fill: "forwards" });
  if (leaving) void leaving.finished.then(done, done); else done();
}
function onOpenConversation(id: string) { void assistant.continueFrom(id); }
</script>

<template>
  <div class="assistant-panel" data-assistant-panel @keydown.esc="escape">
    <div :key="assistant.view.value" class="assistant-view atlas-view-enter">
    <ConversationList
      v-if="assistant.view.value === 'list'"
      :items="assistant.list.value.items"
      :loading="assistant.list.value.loading"
      :error="assistant.list.value.error"
      :has-more="!!assistant.list.value.next"
      @open="onOpenConversation"
      @more="assistant.loadList(true)"
      @retry="assistant.loadList()"
      @back="assistant.showThread()"
    />
    <template v-else>
      <div class="assistant-context" data-assistant-context>
        <span class="assistant-context-label" aria-hidden="true">{{ t.panel.context }}</span>
        <OptionPills :model-value="assistant.scopeChoice.value" :label="t.panel.scopeLabel" hide-label :options="pills" @update:model-value="(value) => assistant.chooseScope(value as ScopeChoice)" />
        <Transition name="assistant-chip">
          <span v-if="assistant.selection.value" class="assistant-selection" data-assistant-selection :title="assistant.selection.value">
            {{ assistant.selectionTarget.value === "Výběr" ? t.panel.selection : assistant.selectionTarget.value }}: „{{ selectionPreview }}“
            <button type="button" class="assistant-selection-remove" :aria-label="t.panel.removeSelection" @click="assistant.attachSelection(null)"><AtlasIcon name="close" :size="12" /></button>
          </span>
        </Transition>
      </div>
      <div ref="scroller" v-scroll-edges class="assistant-scroll" data-assistant-scroll>
        <Transition :css="false" @leave="introLeave">
        <p v-if="assistant.loadError.value" class="assistant-note atlas-view-enter" role="status">{{ assistant.loadError.value }} <Button type="button" variant="link" size="sm" @click="assistant.newConversation()">{{ t.panel.startNew }}</Button></p>
        <LoadingNote v-else-if="assistant.loading.value" class="assistant-note" :label="t.panel.loading" />
        <div v-else-if="!hasConversation" class="assistant-intro atlas-stagger" data-assistant-intro>
          <AiGlyph size="xl" />
          <p class="assistant-intro-title">{{ t.panel.introTitle }}</p>
          <p class="assistant-intro-text">{{ assistant.selection.value ? t.panel.introSelection : t.panel.intro }}</p>
          <ul v-if="assistant.canCompose.value && !assistant.selection.value && (tasks.length || assistant.canAnswer.value)" class="assistant-suggestions atlas-stagger" :aria-label="tasks.length ? t.panel.suggestionsAll : t.panel.suggestions">
            <li v-for="task in tasks" :key="task.id"><button type="button" class="assistant-suggestion assistant-task" data-assistant-task @click="suggestTask(task)"><AiGlyph :size="15" state="ready" />{{ task.label }}</button></li>
            <li v-for="text in assistant.canAnswer.value ? suggestions : []" :key="text"><button type="button" class="assistant-suggestion" data-assistant-suggestion @click="suggest(text)">{{ text.endsWith(' ') ? `${text.trimEnd()}…` : text }}</button></li>
          </ul>
          <p class="assistant-intro-shortcut" data-assistant-shortcut><kbd>Ctrl J</kbd> {{ t.panel.shortcut }}</p>
        </div>
        </Transition>
        <ConversationThread
          v-if="hasConversation"
          :items="items"
          :context="context"
          @stop="assistant.stop()"
          @retry="(key) => assistant.retry(key)"
          @ask-again="askAgain"
          @try-other="tryOther"
          @widen="widen"
          @deepen="deepen"
          :deeper-label="deeper ? (deeper.value === 'deep' ? t.panel.searchDeep : t.panel.searchMax) : null"
          @open-source="assistant.openSource"
          @open-editor="assistant.openEditor"
          @proposal="(key, proposal) => assistant.setProposal(key, proposal)"
          @rate="(key, rating) => assistant.rate(key, rating)"
          @feedback="(key, feedback) => assistant.sendReason(key, feedback)"
        />
      </div>
      <footer class="assistant-foot">
        <p v-if="assistant.readOnly.value" class="assistant-note" role="status" data-assistant-readonly>{{ readOnlyText }} · <Button type="button" variant="link" size="sm" @click="assistant.newConversation()">{{ t.panel.startNew }}</Button></p>
        <AssistantComposer v-else-if="assistant.canCompose.value" ref="composer" v-model="assistant.draft.value" :busy="assistant.pending.value" :notice="assistant.notice.value" :recall="assistant.lastQuestions.value" :maxlength="assistant.questionLimit.value" :placeholder="assistant.selection.value ? t.panel.selectionPlaceholder : undefined" @send="assistant.ask(assistant.draft.value)">
          <template #actions>
            <label v-if="hasAgent" class="assistant-tools" data-assistant-tools :title="t.panel.toolsHint"><Switch v-model="assistant.tools.value" />{{ t.panel.tools }}</label>
            <AssistantToolsInfo v-if="hasAgent" :groups="props.toolGroups" />
            <span :title="agentOn ? t.panel.toolsOn : undefined" data-assistant-effort><AiEffortPicker v-model="assistant.effort.value" :disabled="agentOn" /></span>
          </template>
        </AssistantComposer>
      </footer>
    </template>
    </div>
  </div>
</template>

<style scoped>
.assistant-tools { display: inline-flex; align-items: center; gap: 6px; margin-right: 4px; color: var(--muted); font-size: 12px; font-weight: 550; cursor: pointer; }
.assistant-panel { display: flex; flex-direction: column; height: 100%; min-height: 0; }
.assistant-view { display: flex; flex-direction: column; flex: 1; min-height: 0; }
.assistant-context { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; padding: 12px 16px 0; font-size: 12px; }
.assistant-context-label { color: var(--muted); }
/* The scope pills as the board's context chips: small, square-ish, the chosen one tinted. */
.assistant-context :deep(.option-pills-group) { gap: 6px; }
/* The pills' own label stays for assistive technology only (the visible word is „Kontext“). */
.assistant-context :deep(.option-pills-label) { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
.assistant-context :deep(.option-pill) { min-height: 24px; height: 24px; padding: 0 8px; border-radius: 7px; font-size: 12px; font-weight: 500; }
.assistant-context :deep(.option-pill[data-state="checked"]) { border-color: var(--accent); background: var(--accent-soft); color: var(--text); }
.assistant-context :deep(.option-pill span) { max-width: 16ch; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.assistant-selection { display: inline-flex; align-items: center; gap: 4px; max-width: 100%; height: 24px; padding: 0 4px 0 8px; border: 1px solid var(--ai-line-strong); border-radius: 7px; background: var(--ai-wash); color: var(--text); font-weight: 500; white-space: nowrap; }
.assistant-selection-remove { display: grid; place-items: center; width: 18px; height: 18px; padding: 0; border: 0; border-radius: 5px; background: transparent; color: var(--muted); cursor: pointer; }
@media (hover: hover) and (pointer: fine) { .assistant-selection-remove:hover { background: var(--hover); color: var(--text); } }
.assistant-selection-remove:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
.assistant-scroll { position: relative; flex: 1; min-height: 0; overflow: auto; padding: 14px 16px; }
/* The thread runs under the context pills and above the composer, with nothing between them: a line cut off at an edge faded
   to the nothing instead of being sliced mid-glyph (the rails of the sidebar do the same, refresh.css §2; lib/scrollEdges.ts
   marks the edges, the lengths are registered in motion.css). At rest the 14 px paddings are not faded. */
.assistant-scroll {
  mask-image: linear-gradient(to bottom, transparent, #000 var(--atlas-scroll-fade-top), #000 calc(100% - var(--atlas-scroll-fade-bottom)), transparent);
  transition: --atlas-scroll-fade-top var(--atlas-motion-fast) var(--atlas-ease-out), --atlas-scroll-fade-bottom var(--atlas-motion-fast) var(--atlas-ease-out);
  scroll-padding-block: 14px;
}
.assistant-scroll[data-more-above] { --atlas-scroll-fade-top: 14px; }
.assistant-scroll[data-more-below] { --atlas-scroll-fade-bottom: 14px; }
.assistant-foot { padding: 10px 16px 14px; }
.assistant-foot:empty { display: none; }
.assistant-note { margin: 0 0 10px; color: var(--muted); font-size: 13px; line-height: 1.55; }
/* The selection taken from the page arrives as a chip and leaves the same way; the pills beside it do not move meanwhile. */
.assistant-chip-enter-active, .assistant-chip-leave-active { transition: opacity var(--atlas-motion-base) var(--atlas-ease-out), transform var(--atlas-motion-base) var(--atlas-ease-out); }
.assistant-chip-leave-active { transition-duration: var(--atlas-motion-fast); }
.assistant-chip-enter-from, .assistant-chip-leave-to { opacity: 0; transform: scale(.92); }
/* No side padding of its own: the suggestions take the panel's 16 px gutter like the context pills and the composer
   (they sat 8 px further in on both sides); the centred title and text keep their measure. */
.assistant-intro { display: grid; justify-items: center; gap: 6px; padding: 28px 0 8px; text-align: center; }
.assistant-intro :deep(.ai-glyph) { margin-bottom: 8px; }
.assistant-intro-title { margin: 0; font-size: 17px; font-weight: 650; }
.assistant-intro-text { max-width: 30ch; margin: 0; color: var(--muted); font-size: 13px; line-height: 1.5; text-wrap: balance; }
/* Said once, quietly, under the suggestions of an empty conversation; a touch screen has no shortcut to offer. */
.assistant-intro-shortcut { margin: 10px 0 0; color: var(--muted); font-size: 12px; opacity: .8; }
.assistant-intro-shortcut kbd { font-size: 11px; }
@media (pointer: coarse), (max-width: 680px) { .assistant-intro-shortcut { display: none; } }
.assistant-suggestions { display: grid; gap: 6px; width: 100%; margin: 14px 0 0; padding: 0; list-style: none; --st-stagger: 60ms; }
.assistant-suggestion { width: 100%; padding: 9px 12px; border: 1px solid var(--refresh-soft-line, var(--line)); border-radius: 10px; background: var(--surface); color: var(--text); font: inherit; font-size: 13px; text-align: left; cursor: pointer; transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out), transform var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .assistant-suggestion:hover { border-color: var(--ai-line-strong); background: var(--ai-wash); } }
.assistant-suggestion:active { transform: scale(var(--atlas-press)); }
.assistant-suggestion:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
/* A task changes the wiki (as a proposal): marked with the AI glyph, so it reads as a command, not a question. */
.assistant-task { display: flex; align-items: center; gap: 8px; }
.assistant-task :deep(.ai-glyph) { flex: none; }
</style>
