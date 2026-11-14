<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, toValue, useId, watch, type ComponentPublicInstance } from "vue";
import type {ChangeSummaryProposal} from "../../ai/writingApi";
import { hostElement } from "../../lib/templateRef";
import { errorMessage } from "../../api";
import { animateAtlas, dropFromTrigger, motionMs, motionScale, popStatus, prefersReducedMotion, REDUCED_MOTION_PREVIEW } from "../../motion/aiMotion";
import type { RevealTarget } from "../../editor/revealTarget";
import { CHECK_FOOTER_GROW_MS, checkRowSchedule, unfoldIn } from "../../publishing/findingsMotion";
import { normalizeChangeSummary, orderPublicationChecks, type PublicationCheck, type PublicationCheckRun } from "../../publishing/publication";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import t from "../../i18n/components-publishing";
import { Popover, PopoverAnchor, PopoverContent } from "../ui/popover";
import { Tooltip, TooltipContent, TooltipTrigger } from "../ui/tooltip";
import ChangeSummaryField from "./ChangeSummaryField.vue";
import PublishFindings from "./PublishFindings.vue";

/**
 * Publikovat as a split button (plan §5, B1). The main part publishes at once, as it always did; the
 * arrow "Publikovat s popisem změny…" opens an anchored, non-modal panel "Před publikací" with the
 * optional change summary and the buttons Publikovat / Zpět k úpravám. Without the arrow nothing new
 * appears. The panel is the one the publication check (package E) fills through the `findings` slot.
 *
 * A page saves itself (Atlas 3.6-B); publishing stays a deliberate step, offered while the saved version is not published yet.
 *
 * The control does not know how a page is saved or published: `prepare` and `publish` are the host's.
 * `publish` resolves `true` when the page was published, `false` when it was not and the host already
 * said why (the panel then stays), and rejects with the error to show inside an open panel.
 *
 * The check before publishing (package E, plan §9): with `check`, Publikovat first saves (`prepare`),
 * then runs the check on the saved page. Nothing found publishes at once, exactly as without it. A
 * finding opens the panel (or fills the open one) with the findings, the change summary and "Publikovat
 * i tak" for a warning; confirming then publishes without checking again. "Zobrazit" and "Zpět k
 * úpravám" (Escape) close the panel and ask the host to reveal the place (`reveal`) in the editor.
 * The check never disables publishing.
 *
 * The panel is also where the opposite of publishing lives: with `canDiscard`, "Zahodit změny…" closes it and
 * asks the host (`discard`) to show what would be thrown away and confirm it.
 */
const props = withDefaults(defineProps<{
  /** A getter keeps a host whose readiness changes with every keystroke (the shared session's sync) from rendering again. */
  disabled?: boolean | (() => boolean);
  suggestSummary?: (signal: AbortSignal) => Promise<ChangeSummaryProposal | null>;
  summaryContextKey?: string | (() => string);
  summaryInteraction?: (outcome:"shown"|"applied"|"dismissed")=>void;
  /** The host is saving or publishing; the control is inert and busy. */
  pending?: boolean;
  /** The host has reached publication after saving and checks. */
  publishing?: boolean;
  /** Saves the draft and settles open details before publishing; `false` = do not publish. */
  prepare?: () => Promise<boolean>;
  publish: (summary: string | null) => Promise<boolean | void>;
  /** Findings of the publication check. A warning turns the confirmation into "Publikovat i tak". */
  checks?: PublicationCheck[];
  /** The element that pops with "Publikováno" once the page is published. */
  statusSelector?: string;
  /** The publication check, run after `prepare` on the saved page; `null` when there is nothing to check. */
  check?: () => PublicationCheckRun | null | undefined;
  /** The main button's label. */
  label?: string;
  /** One sentence on what the main button does, as its tooltip and description (the draft bar: whom it shows the draft to). */
  description?: string;
  /** Quiet (secondary) while something else on the bar is the one primary action (a locked page's Upravit). */
  quiet?: boolean;
  /** The page has a published version to return to: the panel offers "Zahodit změny…". */
  canDiscard?: boolean;
}>(), { label: () => t.control.publish, description: undefined, disabled: false, pending: false, publishing: false, prepare: undefined, checks: () => [], statusSelector: ".document-status", check: undefined });
const emit = defineEmits<{
  opened: []; closed: []; published: [summary: string | null];
  /** "Zobrazit", "Zpět k úpravám": the host shows the place in the editor and focuses it. */
  reveal: [target: RevealTarget];
  /** "Zahodit změny…": the host shows what is discarded and confirms. */
  discard: [];
}>();

defineSlots<{
  /** Findings of the publication check, above the change summary. */
  findings?: (props: { checks: PublicationCheck[] }) => unknown;
}>();

// `rendered` keeps the panel in the DOM while it drops back to the button; `shown` is what the user sees.
const rendered = ref(false);
const shown = ref(false);
const summary = ref("");
const serverError = ref("");
const busy = ref(false);
const body = ref<HTMLElement>();
const group = ref<HTMLElement>();
const arrow = ref<ComponentPublicInstance>();
const panelId = `publish-panel-${useId()}`;
const field = ref<InstanceType<typeof ChangeSummaryField>>();
const normalized = computed(() => normalizeChangeSummary(summary.value));
const fieldError = computed(() => normalized.value.error ?? (serverError.value || undefined));
// The button answers at once: while the host still saves before publishing it already says it publishes.
const publishingNow = computed(() => props.publishing || busy.value);
const inert = computed(() => toValue(props.disabled) || props.pending || busy.value);
// Findings of the check run for this panel; `checking` while the server part still runs beside them.
const found = ref<PublicationCheck[]>([]);
const checking = ref(false);
/** The author has seen this panel's findings: confirming publishes without checking again. */
const inspected = ref(false);
const shownChecks = computed(() => orderPublicationChecks([...props.checks, ...found.value]));
const hasFindings = computed(() => shownChecks.value.length > 0 || checking.value);
const confirmLabel = computed(() => (shownChecks.value.some(check => check.severity === "warning") ? t.control.publishAnyway : t.control.publish));
watch(summary, () => { serverError.value = ""; });
// Why the panel opened (focus goes to the field, or to the first action of the findings); a reveal keeps focus in the editor.
let openedFor: "arrow" | "findings" = "arrow";
let keepFocus = false;
let checkRun = 0;
let active = true;
let operationRun = 0;
let presenceRun = 0;
let panelMotion: Animation | null = null;
onBeforeUnmount(() => { active = false; checkRun++; presenceRun++; panelMotion?.cancel(); });

function movePanel(panel: HTMLElement, direction: "in" | "out") {
  // Rapid reversals continue from the visible frame, including a half-finished entrance.
  const frame = panelMotion ? { opacity: getComputedStyle(panel).opacity, transform: getComputedStyle(panel).transform } : null;
  panelMotion?.cancel();
  panelMotion = null;
  if (direction === "in") { panel.removeAttribute("inert"); panel.removeAttribute("aria-hidden"); }
  else { panel.setAttribute("inert", ""); panel.setAttribute("aria-hidden", "true"); }
  const motion = frame
    ? animateAtlas(panel, [frame, direction === "in" ? { opacity: 1, transform: "none" } : { opacity: 0, transform: "translateY(-6px) scale(.98)" }], { duration: motionMs(panel, "--atlas-motion-base"), fill: "both" })
    : dropFromTrigger(panel, direction);
  panelMotion = motion;
  if (motion) void motion.finished.then(() => { if (panelMotion === motion) { panelMotion = null; motion.cancel(); } }, () => {});
  return motion;
}

/** The popover surface that drops: the panel content, which Reka portals out of this component. */
function panelElement() { return body.value?.closest<HTMLElement>("[data-slot=popover-content]") ?? null; }
/**
 * The panel is portalled out of the page, so a motion preview around the button (the gallery's "Zpomalit 3×" and
 * "Omezený pohyb") is carried onto it when it opens, before its rows mount. The application sets neither.
 */
const preview = ref<Record<string, string>>({});
const previewStyle = computed(() => (preview.value.scale ? { "--atlas-motion-scale": preview.value.scale } : undefined));
const previewAttrs = computed(() => (preview.value.reduced ? { [REDUCED_MOTION_PREVIEW]: "" } : {}));
function readMotionPreview() {
  const trigger = group.value;
  const scale = trigger ? motionScale(trigger) : 1;
  preview.value = {
    ...(scale !== 1 ? { scale: String(scale) } : {}),
    ...(trigger?.closest(`[${REDUCED_MOTION_PREVIEW}]`) ? { reduced: "true" } : {}),
  };
}
function open(reason: "arrow" | "findings" = "arrow") {
  if (shown.value || toValue(props.disabled) || (reason === "arrow" && inert.value)) return;
  const reopening = rendered.value;
  const presence = ++presenceRun;
  openedFor = reason;
  keepFocus = false;
  readMotionPreview();
  rendered.value = true;
  shown.value = true;
  emit("opened");
  void nextTick(() => {
    const panel = panelElement();
    if (!panel || !shown.value || presence !== presenceRun) return;
    movePanel(panel, "in");
    if (reopening) focusInitial();
    // Prototype moment 6: the rows come first, then the change summary and the buttons unfold.
    if (reason === "findings") revealFooter(shownChecks.value.length + (checking.value ? 1 : 0));
  });
}
function revealFooter(rows: number) {
  const footer = body.value?.querySelector<HTMLElement>(".publish-panel-footer");
  if (footer && !prefersReducedMotion(footer)) unfoldIn(footer, checkRowSchedule(rows).footerAt, CHECK_FOOTER_GROW_MS);
}
/** Closes the panel: it drops back towards the button, then leaves the DOM. The typed summary stays. */
async function close(options: { returnFocus?: boolean } = {}) {
  if (!shown.value) return;
  if (options.returnFocus === false) keepFocus = true;
  shown.value = false;
  const presence = ++presenceRun;
  operationRun++;
  // A server answer that arrives after this belongs to a panel nobody looks at any more.
  checkRun++;
  checking.value = false;
  const panel = panelElement();
  const motion = panel ? movePanel(panel, "out") : null;
  if (motion) await motion.finished.catch(() => {});
  if (!active || shown.value || presence !== presenceRun) return; // opened again meanwhile
  rendered.value = false;
  found.value = [];
  inspected.value = false;
  emit("closed");
}
function toggle() { if (shown.value) void close(); else open(); }
/** A press on the control itself is the arrow's own toggle, not an outside dismissal. */
function onInteractOutside(event: Event) {
  const target = (event as CustomEvent<{ originalEvent?: Event }>).detail?.originalEvent?.target ?? event.target;
  if (target instanceof Node && group.value?.contains(target)) event.preventDefault();
}
function onOpenChange(value: boolean) {
  if (value) open();
  else void close();
}

/**
 * Runs the check on the saved page. `true` when it found something: the panel shows it and the author decides.
 * Local findings show at once while the server part still runs; without them the server part decides, within
 * its budget (it never rejects).
 */
async function inspect(): Promise<boolean> {
  const run = props.check?.();
  if (!run) return false;
  const token = ++checkRun;
  if (!run.local.length) {
    const server = await run.server;
    if (token !== checkRun || !server.length) return false;
    showFindings([...server], false);
    return true;
  }
  showFindings([...run.local], true);
  void run.server.then((server) => {
    if (token !== checkRun) return;
    found.value = [...found.value, ...server];
    checking.value = false;
  });
  return true;
}
function showFindings(list: PublicationCheck[], pending: boolean) {
  found.value = list;
  checking.value = pending;
  inspected.value = true;
  if (!shown.value) { open("findings"); return; }
  // The open panel of the arrow: its rows grow in; focus moves to them, as the result of the click.
  void nextTick(focusFirstAction);
}

/** One publication, from the main button (no summary) or from the panel. */
async function perform(text: string | null) {
  if (busy.value || inert.value) return;
  const operation = ++operationRun;
  const current = () => active && operation === operationRun;
  busy.value = true;
  serverError.value = "";
  try {
    if (!inspected.value) {
      if (props.prepare && !(await props.prepare())) return;
      if (!current() || await inspect() || !current()) return;
    }
    if (!current() || (await props.publish(text)) === false || !current()) return;
    summary.value = "";
    emit("published", text);
    const wasOpen = shown.value;
    if (wasOpen) await close();
    // The status text has changed by now; the new status pops in.
    await nextTick();
    if (!active) return;
    const status = document.querySelector(props.statusSelector);
    if (status) popStatus(status);
  } catch (failure) {
    // With the panel closed the host already shows the failure in its own status.
    if (shown.value) serverError.value = errorMessage(failure);
  } finally {
    busy.value = false;
  }
}
function publishFromPanel() {
  if (normalized.value.error) return;
  void perform(normalized.value.value);
}
function focusField() { field.value?.focus(); }
function focusFirstAction() {
  const action = body.value?.querySelector<HTMLElement>(".publish-findings button, .publish-findings a[href]");
  if (action) action.focus();
  else focusField();
}
function focusInitial() {
  if (openedFor === "findings") focusFirstAction();
  else focusField();
}
function onCloseAutoFocus() {
  // After "Zobrazit" the editor has the focus at the revealed place (Reka may call this more than once; the next
  // opening resets it).
  if (keepFocus) return;
  hostElement(arrow.value)?.focus();
}
function revealCheck(check: PublicationCheck) {
  if (!check.target) return;
  keepFocus = true;
  emit("reveal", check.target);
  void close({ returnFocus: false });
}
/** "Zpět k úpravám" (and Escape): with findings, back to the first one; otherwise back to the button. */
function back() {
  const first = shownChecks.value.find(check => check.target);
  if (first) revealCheck(first);
  else void close();
}
function onEscape(event: KeyboardEvent) {
  if (!shownChecks.value.some(check => check.target)) return;
  event.preventDefault();
  back();
}

/** "1 kontrola", "3 kontroly", "5 kontrol". */
function checkCount(count: number) {
  return t.control.checkCount(count);
}

// `publish`: the main button's own path (prepare, check, publish), for the palette command and the chip „Nepublikované změny“ (keyboard pass).
defineExpose({ open, close, publish: () => perform(null) });
</script>

<template>
  <Popover :open="rendered" @update:open="onOpenChange">
    <PopoverAnchor as-child>
      <span ref="group" class="publish-control" role="group" :aria-label="t.control.group" :aria-busy="pending || busy || undefined">
        <Tooltip :disabled="!description">
          <TooltipTrigger as-child>
            <Button :variant="quiet ? 'secondary' : 'default'" class="publish-main publish-button" :disabled="inert" :aria-busy="pending || busy || publishing" :aria-description="description" @click="perform(null)">
              <AtlasIcon v-if="publishingNow" name="loader-circle" :size="15" class="atlas-spinner" />
              {{ publishingNow ? t.control.publishing : label }}
            </Button>
          </TooltipTrigger>
          <TooltipContent>{{ description }}</TooltipContent>
        </Tooltip>
        <Button
          ref="arrow" :variant="quiet ? 'secondary' : 'default'" class="publish-arrow" type="button" :disabled="inert"
          :aria-label="t.control.withSummary" :title="t.control.withSummary"
          aria-haspopup="dialog" :aria-expanded="shown" :aria-controls="shown ? panelId : undefined"
          @click="toggle"
        >
          <AtlasIcon name="down" :size="14" />
        </Button>
      </span>
    </PopoverAnchor>
    <PopoverContent
      :id="panelId"
      role="dialog"
      :aria-label="t.control.before"
      :style="previewStyle"
      v-bind="previewAttrs"
      align="end"
      :side-offset="6"
      :collision-padding="16"
      class="publish-panel w-[min(420px,calc(100vw-2rem))]"
      @open-auto-focus.prevent="focusInitial"
      @close-auto-focus.prevent="onCloseAutoFocus"
      @escape-key-down="onEscape"
      @interact-outside="onInteractOutside"
    >
      <div ref="body" class="publish-panel-body">
        <p class="publish-panel-title"><span>{{ t.control.before }}</span><small v-if="shownChecks.length">{{ checkCount(shownChecks.length) }}</small></p>
        <slot name="findings" :checks="shownChecks">
          <PublishFindings v-if="hasFindings" :checks="shownChecks" :running="checking" @reveal="revealCheck" />
        </slot>
        <div class="publish-panel-footer">
          <ChangeSummaryField ref="field" v-model="summary" :error="fieldError" :disabled="busy" :suggest="suggestSummary" :context-key="summaryContextKey" :record-interaction="summaryInteraction" @submit="publishFromPanel" />
          <div class="publish-panel-actions">
            <Button v-if="canDiscard" variant="ghost" type="button" class="publish-discard" :disabled="inert" @click="close(); emit('discard')">{{ t.control.discard }}</Button>
            <Button variant="outline" type="button" @click="back()">{{ t.control.backToEdit }}</Button>
            <Button variant="default" type="button" :disabled="inert || !!normalized.error" :aria-busy="publishing" @click="publishFromPanel">
              <AtlasIcon v-if="publishing" name="loader-circle" :size="15" class="atlas-spinner" />
              {{ publishing ? t.control.publishing : confirmLabel }}
            </Button>
          </div>
        </div>
      </div>
    </PopoverContent>
  </Popover>
</template>

<style>
/* The split button: two halves of one control. */
.publish-control { display: inline-flex; align-items: stretch; }
.publish-control .publish-main { border-top-right-radius: 0; border-bottom-right-radius: 0; }
.publish-control .publish-arrow {
  width: 30px; padding-inline: 0;
  border-top-left-radius: 0; border-bottom-left-radius: 0;
  border-left: 1px solid color-mix(in srgb, var(--primary-foreground, #fff) 35%, transparent);
}
/* The arrow turns with the panel it opens, in the panel's own time (as the page outline's chevron). */
.publish-control .publish-arrow > svg { transition: rotate var(--atlas-motion-base) var(--atlas-ease-out); }
.publish-control .publish-arrow[aria-expanded="true"] > svg { rotate: 180deg; }
[data-atlas-reduced-motion] .publish-control .publish-arrow > svg { transition: none; }
@media (prefers-reduced-motion: reduce) { .publish-control .publish-arrow > svg { transition: none; } }
/* The drop is a Web Animation from the button (dropFromTrigger); the stock fade/zoom/slide and the
   shared popover fade stay out of its way. */
.publish-panel { animation: none !important; }
.publish-panel-body { display: grid; gap: 14px; }
/* Board 04: „Před publikací“ with the number of checks, the checks as quiet cards, then the change summary. */
.publish-panel-title { display: flex; align-items: baseline; gap: 8px; margin: 0; padding: 2px 2px 0; font-size: 15px; font-weight: 600; }
.publish-panel-title > span { flex: 1; }
.publish-panel-title > small { color: var(--muted); font-size: 12px; font-weight: 400; }
.publish-panel-footer { display: grid; gap: 12px; }
.publish-panel-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 8px; }
.publish-panel-actions .publish-discard { margin-right: auto; color: var(--error); }
</style>
