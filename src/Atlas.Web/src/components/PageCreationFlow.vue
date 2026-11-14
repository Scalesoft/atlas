<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { pickOnEnter } from "../lib/pickEnter";
import { api, errorMessage, post, type Appearance, type Page, type Space } from "../api";
import { useAiCapabilities } from "../composables/useAiCapabilities";
import { useCreationSuggestions } from "../composables/useCreationSuggestions";
import { useSimilarPages } from "../composables/useSimilarPages";
import { emptyDocument, readDocument } from "../document";
import { createPageTemplateContent, pageTemplates, projectorTemplate } from "../pageTemplates";
import { pageTemplateApi, type PageTemplateSummary } from "../pageTemplateApi";
import { boardApi } from "../boards/boardApi";
import CatalogPicker from "../projector/CatalogPicker.vue";
import { projectorApi } from "../projector/projectorApi";
import t from "../i18n/components-creation";
import { invalidateLexicon } from "../linking/lexicon";
import { useDeploymentDraft } from "../projector/useDeploymentDraft";
import type { TreePage } from "../treeMove";
import AtlasIcon from "./AtlasIcon.vue";
import DocumentEditor from "./DocumentEditor.vue";
import { flyValue } from "./creation/flyValue";
import PlacementSuggestion from "./creation/PlacementSuggestion.vue";
import TemplateSuggestion from "./creation/TemplateSuggestion.vue";
import PageLocationPicker from "./PageLocationPicker.vue";
import SimilarPagesSuggestion from "./SimilarPagesSuggestion.vue";
import ChoiceCards from "./settings/ChoiceCards.vue";
import InlineReveal from "./settings/InlineReveal.vue";
import ErrorState from "./settings/ErrorState.vue";
import SettingsPage from "./settings/SettingsPage.vue";
import SettingsRow from "./settings/SettingsRow.vue";
import SettingsSection from "./settings/SettingsSection.vue";
import ProgressiveFlow from "./settings/ProgressiveFlow.vue";
import LoadingRegion from "./LoadingRegion.vue";
import type { ChoiceOption, FlowStep } from "./settings/types";
import { Input } from "./ui/input";
// A workspace view outside the settings shell: it brings the design system's styles itself.
import "./settings/settings.css";
import "./settings/settings-tailwind.css";

// `asBoard` (module Boards): a board is named and placed in the tree the way a page is, the same step without the content step
// (a board starts empty; board templates come later, spec §6 P3), and the created board is reported by its id.
// `projector` (module Projector): a deployment page from the catalog is one of the starting contents (U2-4). Chosen, the name step
// unfolds the catalog's modules under the name and the place (progressive disclosure, owner J2); `deployment` („Nové nasazení“ of
// the page menu) is that one screen alone.
const props = withDefaults(defineProps<{ space: Space; pages: TreePage[]; initialParentId: string; dark: boolean; accent: Appearance["accent"]; asTemplate?: boolean; asBoard?: boolean; initialTitle?: string; projector?: boolean; deployment?: boolean }>(), { asTemplate: false, asBoard: false, initialTitle: "", projector: false, deployment: false });
const emit = defineEmits<{ created: [page: Page]; createdBoard: [boardId: string]; cancel: []; dirty: [value: boolean]; busy: [value: boolean] }>();
const single = computed(() => props.asBoard || props.deployment);
const step = ref(single.value ? "details" : "template");
const selected = ref(props.deployment ? projectorTemplate.value : "blank");
const title = ref(props.initialTitle);
const parentId = ref(props.initialParentId);
const spaceTemplates = ref<PageTemplateSummary[]>([]);
const presets = ref(new Map<string, number>());
const listLoading = ref(true);
const listError = ref("");
const previewLoading = ref(false);
const previewError = ref("");
const preview = ref<{ contentJson: string; markdown: string }>({ contentJson: JSON.stringify(emptyDocument()), markdown: "" });
// The editor remounts only when new content arrives, so the previous preview stays in place while the next one loads.
const previewKey = ref("");
const submissionError = ref("");
const submitting = ref(false);
let generation = 0;
let listRequest = 0;
let mounted = true;

const builtIn = computed(() => [...pageTemplates, ...(props.projector ? [projectorTemplate] : [])]);
const isBuiltIn = (id: string) => builtIn.value.some(template => template.value === id);
const options = computed<ChoiceOption[]>(() => [
  ...builtIn.value.map(template => ({ value: template.value, label: template.label, description: template.description, icon: template.value === projectorTemplate.value ? "list-checks" : "page" })),
  // A template with requirement blocks is a deployment preset (M4a): the page starts with them reset, as a copy of a deployment does.
  ...spaceTemplates.value.map(template => {
    const requirements = presets.value.get(template.id);
    return requirements === undefined ? { value: template.id, label: template.title, description: t.flow.spaceTemplate, icon: "book-open" }
      : { value: template.id, label: template.title, description: t.flow.preset(requirements), icon: "list-checks" };
  }),
]);
const currentName = computed(() => options.value.find(option => option.value === selected.value)?.label ?? t.flow.templateUnavailable);
const validSelection = computed(() => isBuiltIn(selected.value) || spaceTemplates.value.some(item => item.id === selected.value));
const previewValue = computed(() => { try { return readDocument(preview.value.contentJson); } catch { return emptyDocument(); } });
const isDeployment = computed(() => props.projector && selected.value === projectorTemplate.value);
const draft = useDeploymentDraft(() => props.space.id, () => isDeployment.value);
// The deployment's name comes from the customer, the page it goes under („KBN — Portál pacienta“ → „KBN — nasazení“), until typed over.
const nameTyped = ref(!!props.initialTitle);
const customerName = (id: string) => props.pages.find(page => page.id === id)?.title.split(" — ")[0]?.trim();
watch([isDeployment, parentId], ([deployment, parent]) => {
  if (!deployment || nameTyped.value) return;
  const customer = customerName(parent);
  title.value = customer ? `${customer} — nasazení` : "";
}, { immediate: true });
const draftReady = computed(() => !isDeployment.value || (draft.status.value !== "loading" && !("refused" in draft.plan.value)));
const dirty = computed(() => selected.value !== (props.deployment ? projectorTemplate.value : "blank") || (title.value !== props.initialTitle && (nameTyped.value || !isDeployment.value)) || draft.selected.value.length > 0
  || (!props.asTemplate && parentId.value !== props.initialParentId));
watch(dirty, value => emit("dirty", value));
// P1: pages that resemble the title being typed. A courtesy beside the field: it never feeds `ready`, never delays create(),
// and a template gets none (a template is not a page that competes with the wiki's pages).
const root = ref<HTMLElement>();
const { items: similarItems, dismiss: dismissSimilar } = useSimilarPages({ title, spaceId: computed(() => props.space.id), enabled: computed(() => step.value === "details" && !props.asTemplate && !props.asBoard) });
// ↓ in the title goes to the first similar page (its Enter opens it instead of creating a duplicate); nothing shown, ↓ does nothing.
const similarPanel = ref<{ focusFirst: () => void }>();
function toSimilarPages(event: KeyboardEvent) {
  if (!similarPanel.value || event.isComposing) return;
  event.preventDefault();
  similarPanel.value.focusFirst();
}
function dismissSimilarPages() {
  dismissSimilar();
  // The proposal never took focus; the person returns to where they were, the title.
  void nextTick(() => root.value?.querySelector<HTMLInputElement>('input[name="page-title"]')?.focus());
}
// P2 placement and P3 template: the same kind of courtesy. Neither feeds `ready`, create() never waits for them, a template is
// never given either, and what a person dismissed does not return for the same title. Placement needs the `createSuggestions`
// capability; the dictionary part of P3 works without AI. P3 only while step 1 left the content blank (or the suggestion itself chose it).
const { capabilities } = useAiCapabilities();
const appliedPlacement = ref<string | null>(null);
const appliedTemplate = ref<string | null>(null);
const detailsOpen = computed(() => step.value === "details" && !props.asTemplate && !props.asBoard);
const { placement: placementFound, template: templateFound, dismissPlacement, dismissTemplate } = useCreationSuggestions({
  title,
  spaceId: computed(() => props.space.id),
  placementEnabled: computed(() => detailsOpen.value && capabilities.value.createSuggestions),
  templateEnabled: computed(() => detailsOpen.value && (selected.value === "blank" || selected.value === appliedTemplate.value)),
});
/** `""` is the root of the space; a parent that is not in the tree, or that the person may not create under, is not offered. */
const placementTarget = computed(() => {
  const found = placementFound.value;
  if (!found) return null;
  if (found.parentId === null) return "";
  return props.pages.some(page => page.id === found.parentId && page.canEdit) ? found.parentId : null;
});
const placementState = computed(() => placementTarget.value !== null && appliedPlacement.value === placementTarget.value && parentId.value === placementTarget.value ? "applied" as const : "idle" as const);
const showPlacement = computed(() => placementTarget.value !== null && (placementState.value === "applied" || parentId.value !== placementTarget.value));
const showTemplate = computed(() => !!templateFound.value && spaceTemplates.value.some(item => item.id === templateFound.value!.templateId) && (selected.value === "blank" || selected.value === templateFound.value.templateId));
const templateState = computed(() => selected.value === templateFound.value?.templateId ? "applied" as const : "idle" as const);
function returnFocus() {
  // A suggestion never takes focus; the person returns to where they were, the title.
  void nextTick(() => root.value?.querySelector<HTMLInputElement>('input[name="page-title"]')?.focus());
}
/** The value flies from the chip into the field (flyValue); under reduced motion it only fades in there. */
async function settleInto(from: Element | null, text: string) {
  await nextTick();
  const to = root.value?.querySelector("[data-location-value]");
  if (to) flyValue(from, to, text);
}
function applyPlacement(from: Element | null) {
  const target = placementTarget.value;
  if (target === null) return;
  appliedPlacement.value = target;
  parentId.value = target;
  void settleInto(from, from?.textContent ?? "");
}
function applyTemplate() {
  const found = templateFound.value;
  if (!found) return;
  appliedTemplate.value = found.templateId;
  selected.value = found.templateId;
}
const deploymentStep = computed<FlowStep>(() => ({ id: "details", label: t.flow.deploymentLabel, title: t.flow.deploymentTitle,
  hint: t.flow.deploymentHint,
  icon: "list-checks", manual: true, nextLabel: t.flow.createDeployment, ready: !!title.value.trim() && !submitting.value && draftReady.value }));
const steps = computed<FlowStep[]>(() => props.asBoard ? [
  { id: "details", label: t.flow.nameAndPlace, title: t.flow.boardTitle, hint: t.flow.boardHint, icon: "layout-dashboard", manual: true, nextLabel: t.flow.createBoard, ready: !!title.value.trim() && !submitting.value },
] : props.deployment ? [deploymentStep.value] : [
  { id: "template", label: t.flow.content, title: t.flow.contentTitle, hint: t.flow.contentHint, icon: "layers", summary: currentName.value, ready: validSelection.value && !previewLoading.value && !previewError.value },
  isDeployment.value ? deploymentStep.value
    : { id: "details", label: props.asTemplate ? t.flow.name : t.flow.nameAndPlace, title: props.asTemplate ? t.flow.nameTemplate : t.flow.namePage, hint: props.asTemplate ? t.flow.hintTemplate : t.flow.hintPage, icon: "page", manual: true, nextLabel: props.asTemplate ? t.flow.createTemplate : t.flow.createPage, ready: !!title.value.trim() && !submitting.value },
]);

async function loadTemplates() {
  const current = ++listRequest;
  listLoading.value = true;
  listError.value = "";
  try {
    // The presets only mark templates the list has: without them (no module, an error) they read as plain templates.
    const [items, found] = await Promise.all([pageTemplateApi.list(props.space.id), props.projector ? projectorApi.presets(props.space.id).catch(() => []) : []]);
    if (!mounted || current !== listRequest) return;
    presets.value = new Map((Array.isArray(found) ? found : []).map(preset => [preset.pageId, preset.requirements]));
    spaceTemplates.value = items;
    if (!validSelection.value) {
      previewError.value = t.flow.templateGone;
      preview.value = { contentJson: JSON.stringify(emptyDocument()), markdown: "" };
    }
  } catch (error) {
    if (mounted && current === listRequest) listError.value = errorMessage(error);
  } finally {
    if (mounted && current === listRequest) listLoading.value = false;
  }
}
async function loadPreview(id: string) {
  const current = ++generation;
  previewLoading.value = true;
  previewError.value = "";
  try {
    const content = isBuiltIn(id) ? await createPageTemplateContent(id, { projector: props.projector }) : await api<Page>(`/pages/${encodeURIComponent(id)}`);
    if (!mounted || current !== generation) return;
    readDocument(content.contentJson);
    preview.value = { contentJson: content.contentJson, markdown: "markdown" in content ? content.markdown : "" };
    previewKey.value = id;
  } catch (error) {
    if (mounted && current === generation) { preview.value = { contentJson: JSON.stringify(emptyDocument()), markdown: "" }; previewKey.value = ""; previewError.value = errorMessage(error); }
  } finally {
    if (mounted && current === generation) previewLoading.value = false;
  }
}
watch(selected, id => { submissionError.value = ""; void loadPreview(id); });
watch(() => props.space.id, () => {
  generation++; if (submitting.value) { submitting.value = false; emit("busy", false); } parentId.value = props.initialParentId; selected.value = props.deployment ? projectorTemplate.value : "blank";
  if (!isDeployment.value || nameTyped.value) title.value = props.initialTitle;
  step.value = single.value ? "details" : "template"; spaceTemplates.value = [];
  if (!single.value) { void loadTemplates(); void loadPreview("blank"); }
}, { immediate: true });
onUnmounted(() => { mounted = false; generation++; listRequest++; emit("busy", false); emit("dirty", false); });
// The flow starts at its top, however far the view before it was scrolled.
onMounted(() => { for (let node = root.value?.parentElement; node; node = node.parentElement) node.scrollTop = 0; });
// The caret waits in the name as soon as it can be typed (the content step is ready): typing and Enter create the page.
let nameFocused = false;
watch(step, async value => {
  if (value !== "details" || nameFocused) return;
  nameFocused = true;
  await nextTick();
  root.value?.querySelector<HTMLInputElement>('input[name="page-title"]')?.focus({ preventScroll: true });
}, { immediate: true });
// After the update, not after a timer (P7): the radio group focuses its card on the press, before the click that lands the choice, and its own arrow
// selection is a script click (detail 0) that does not come here, so nothing takes the focus back and no key typed meanwhile goes to the card.
const focusName = () => void nextTick(() => root.value?.querySelector<HTMLInputElement>('input[name="page-title"]')?.focus());
// A deployment's Enter goes on to its modules; their Enter creates it.
const createOnEnter = (event: KeyboardEvent) => pickOnEnter(event, title.value.trim(), () => {
  if (isDeployment.value) root.value?.querySelector<HTMLInputElement>("[data-catalog-query]")?.focus();
  else void create();
});
/** Enter in the modules: what is ticked, or else the highlighted module, and the deployment is created. */
function createWith(_keep: boolean, keys: string[]) {
  draft.selected.value = keys;
  void create();
}
/** The prefilled name is selected on its first focus, so typing replaces it. */
function selectPrefilled(event: FocusEvent) {
  const field = event.target as HTMLInputElement;
  if (!isDeployment.value || nameTyped.value || field.dataset.selected) return;
  field.dataset.selected = "true";
  field.select();
}

async function createBoard() {
  if (submitting.value || !title.value.trim()) return;
  submitting.value = true;
  const request = generation;
  emit("busy", true);
  submissionError.value = "";
  try {
    const board = await boardApi.create(props.space.id, title.value.trim(), parentId.value || null);
    if (!mounted || request !== generation) return;
    submitting.value = false;
    emit("dirty", false);
    emit("busy", false);
    emit("createdBoard", board.id);
  } catch (error) {
    if (mounted && request === generation) { submissionError.value = errorMessage(error); submitting.value = false; emit("busy", false); }
  }
}
/** Esc is Zrušit (keyboard pass), unless a field, list or picker inside used it first (they stop it or prevent it). */
function escape(event: KeyboardEvent) {
  if (event.defaultPrevented || event.isComposing || submitting.value) return;
  event.preventDefault();
  emit("cancel");
}
async function create() {
  if (props.asBoard) return createBoard();
  if (submitting.value || !title.value.trim() || !validSelection.value || previewLoading.value || previewError.value || !draftReady.value) return;
  submitting.value = true;
  const request = generation;
  const activeSpaceId = props.space.id;
  emit("busy", true);
  submissionError.value = "";
  try {
    const templatePageId = isBuiltIn(selected.value) ? undefined : selected.value;
    // A deployment is the catalog's blocks by module (useDeploymentDraft), written with the page in the one request.
    const deployment = isDeployment.value ? await draft.content() : undefined;
    if (!mounted || request !== generation) return;
    const page = await post<Page>("/pages", {
      spaceId: activeSpaceId,
      parentId: props.asTemplate ? null : parentId.value || null,
      title: title.value.trim(),
      contentJson: templatePageId ? JSON.stringify(emptyDocument()) : deployment?.contentJson ?? preview.value.contentJson,
      markdown: templatePageId || deployment ? "" : preview.value.markdown,
      ...(templatePageId ? { templatePageId } : {}),
      ...(props.asTemplate ? { isTemplate: true } : {}),
    });
    if (!mounted || request !== generation) return;
    invalidateLexicon(page.spaceId);
    submitting.value = false;
    title.value = "";
    selected.value = "blank";
    parentId.value = props.initialParentId;
    emit("dirty", false);
    emit("busy", false);
    emit("created", page);
  } catch (error) {
    if (mounted && request === generation) submissionError.value = errorMessage(error);
  } finally {
    if (mounted && request === generation) {
      submitting.value = false;
      if (submissionError.value) emit("busy", false);
    }
  }
}
</script>

<template>
  <div ref="root" class="page-creation-flow settings-ui" @keydown.esc="escape">
    <SettingsPage :title="asBoard ? t.flow.newBoard : asTemplate ? t.flow.newTemplate : deployment ? t.flow.newDeployment : t.flow.newPage" :description="asTemplate ? t.flow.templateDescription(space.name) : t.flow.spaceDescription(space.name)" :icon="asBoard ? 'layout-dashboard' : deployment ? 'list-checks' : 'file-plus'">
      <SettingsSection :label="asBoard ? t.flow.creatingBoard : asTemplate ? t.flow.creatingTemplate : t.flow.creatingPage">
        <ProgressiveFlow v-model="step" :steps="steps" :label="asBoard ? t.flow.newBoard : asTemplate ? t.flow.newTemplate : t.flow.newPage" :busy="submitting" @next="create()" @cancel="emit('cancel')">
          <template v-if="!single" #template>
            <ErrorState v-if="listError" :title="t.flow.templatesError" :text="listError" @retry="loadTemplates" />
            <LoadingRegion :loading="listLoading" :ready="true" note :label="t.flow.templatesLoading">
              <!-- A clicked card (or Enter on one) is the choice made: the caret goes on to the name, which sits below the cards. -->
              <div @click.capture="(event: MouseEvent) => event.detail > 0 && focusName()" @keydown.enter="focusName">
                <ChoiceCards v-model="selected" :options="options" :label="t.flow.defaultContent" hide-label :columns="2" size="hero" :disabled="submitting" />
              </div>
            </LoadingRegion>
            <ErrorState v-if="previewError && !previewLoading" :title="t.flow.previewUnavailable" :text="previewError" @retry="loadPreview(selected)" />
            <!-- An empty page has nothing to preview. -->
            <LoadingRegion v-else-if="selected !== 'blank' && !isDeployment" :loading="previewLoading" :ready="true" note :label="t.flow.previewLoading">
              <p class="template-preview-label"><AtlasIcon name="eye" :size="15" />{{ t.flow.previewLabel }}</p>
              <div class="template-preview" :aria-label="t.flow.previewAria">
                <DocumentEditor :key="previewKey" :value="previewValue" :readonly="true" :dark="dark" :accent="accent" :space-id="space.id" />
              </div>
            </LoadingRegion>
          </template>
          <template #details>
            <SettingsRow v-slot="{ id, describedBy }" :label="asBoard ? t.flow.titleOfBoard : asTemplate ? t.flow.titleOfTemplate : isDeployment ? t.flow.titleOfDeployment : t.flow.titleOfPage" :help="asTemplate ? t.flow.helpTemplate : asBoard ? t.flow.helpBoard : isDeployment ? t.flow.helpDeployment : t.flow.helpPage" stacked>
              <Input :id="id" v-model="title" name="page-title" :aria-describedby="describedBy" maxlength="200" required autocomplete="off" data-step-focus @keydown.enter="createOnEnter" @keydown.down="toSimilarPages" @focus="selectPrefilled" @input="nameTyped = true" />
            </SettingsRow>
            <SimilarPagesSuggestion v-if="!asTemplate && !asBoard && similarItems.length" ref="similarPanel" :items="similarItems" @dismiss="dismissSimilarPages" @create="create()" @back="returnFocus" @open="emit('dirty', false)" />
            <TemplateSuggestion v-if="!asTemplate && !asBoard && showTemplate && templateFound" :suggestion="templateFound" :state="templateState" @apply="applyTemplate" @dismiss="dismissTemplate(); returnFocus()" />
            <SettingsRow v-if="!asTemplate" v-slot="{ labelId }" :label="t.flow.location" :label-for="false" stacked>
              <PageLocationPicker v-model="parentId" :items="pages.map(page => ({ ...page, canEdit: !!page.canEdit }))" :label-id="labelId" :label="t.flow.parentPage" :disabled="submitting" />
              <PlacementSuggestion v-if="showPlacement && placementFound" :suggestion="placementFound" :state="placementState" @apply="applyPlacement" @dismiss="dismissPlacement(); returnFocus()" />
            </SettingsRow>
            <!-- The catalog's modules unfold under the place once the content is a deployment; Enter in the search creates it. -->
            <InlineReveal :open="isDeployment">
              <SettingsRow v-if="isDeployment" :label="t.flow.catalogModules" :label-for="false" :help="t.flow.catalogHelp" stacked>
                <CatalogPicker v-model:selected="draft.selected.value" :definitions="draft.definitions.value" :modules="draft.modules.value" :plan="draft.plan.value" modules-only
                  :label="t.flow.catalogModules" :status="draft.status.value" :error="draft.error.value" :busy="submitting" :autofocus="false"
                  :empty-text="t.flow.catalogEmpty" @submit="createWith" />
              </SettingsRow>
            </InlineReveal>
            <!-- An error unfolds under the form instead of pushing the buttons away at once. -->
            <InlineReveal :open="!!submissionError"><p v-if="submissionError" role="alert" class="template-error">{{ submissionError }}</p></InlineReveal>
          </template>
        </ProgressiveFlow>
      </SettingsSection>
    </SettingsPage>
  </div>
</template>

<style scoped>
.page-creation-flow { width: 100%; max-width: 1080px; min-width: 0; margin: 0 auto; padding: 20px var(--atlas-page-gutter, 24px) 40px; }
@media (max-width: 680px) { .page-creation-flow { padding: 16px 16px 72px; } }
.template-preview-label { display: flex; align-items: center; gap: 6px; margin: 4px 0 8px; color: var(--muted); font-size: 12.5px; font-weight: 600; }
.template-preview { max-height: 360px; overflow: auto; border: 1px solid var(--line); border-radius: var(--st-radius); padding: 12px; }
.template-error { color: var(--error); }
</style>
