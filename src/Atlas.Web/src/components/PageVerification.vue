<script setup lang="ts">
import t from "../i18n/verification";
import { computed, nextTick, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import { errorMessage } from "../api";
import { formatDate } from "../formatDate";
import { pageAccounts, type PagePerson } from "../contacts/contactSources";
import { pageVerificationApi, verificationIcon, verificationLabel, verificationShown, verificationTone, type PageVerification, type PageVerificationApi, type VerificationChange } from "../verification/pageVerification";
import AtlasIcon from "./AtlasIcon.vue";
import { Button } from "./ui/button";
import { Input } from "./ui/input";
import { Popover, PopoverContent, PopoverTrigger } from "./ui/popover";
import { Textarea } from "./ui/textarea";
import SearchableChoiceList from "./settings/SearchableChoiceList.vue";
import StatusBadge from "./settings/StatusBadge.vue";
import type { ChoiceOption } from "./settings/types";

/**
 * „Ověřeno“ at the page title (5.2, plan §3.1): the status chip of the page (StatusBadge, as the catalog badge beside it) as the trigger of a small
 * popover. It draws from the page's own data at once (the bundle), never blocks reading and adds no bar above the text. In the popover: who is the
 * owner, who verified when, „Změněno po ověření“; an editor verifies (V), verifies until a date, or changes the owner (the people search: first result
 * preselected, Enter picks); a reader reports the page as outdated. Every change is `PUT /pages/{id}/verification`, whose answer replaces the state.
 */
const props = withDefaults(defineProps<{
  pageId: string;
  verification: PageVerification | null | undefined;
  canEdit: boolean;
  isPublished: boolean;
  api?: PageVerificationApi;
  /** The people an owner may be (the page's accounts); the gallery passes local ones. */
  people?: (pageId: string, text: string, signal: AbortSignal) => Promise<PagePerson[]>;
}>(), { api: () => pageVerificationApi, people: (pageId: string, text: string, signal: AbortSignal) => pageAccounts(pageId)(text, signal) });
const emit = defineEmits<{ change: [value: PageVerification]; openHistory: [] }>();

type View = "main" | "steward" | "date" | "report";
const open = defineModel<boolean>("open", { default: false });
const view = ref<View>("main");
const busy = ref(false);
const error = ref("");
const reported = ref(false);
const note = ref("");
const until = ref("");
const root = ref<HTMLElement>();

const value = computed(() => (verificationShown(props.verification) ? props.verification : undefined));
const label = computed(() => (value.value ? verificationLabel(value.value) : ""));
const tooltip = computed(() => {
  const v = value.value;
  return v?.verifiedUntil ? t.tooltip(formatDate(v.verifiedUntil), v.stewardName) : t.tooltipNone;
});
const tomorrow = () => new Date(Date.now() + 86_400_000).toISOString().slice(0, 10);

async function run(change: VerificationChange, back = true) {
  if (busy.value) return;
  busy.value = true;
  error.value = "";
  try {
    emit("change", await props.api.change(props.pageId, change));
    if (back) show("main");
  } catch (failure) {
    error.value = errorMessage(failure) || t.failed;
  } finally {
    busy.value = false;
  }
}
const verify = () => { if (props.canEdit && props.isPublished) void run({ verify: true }); };

async function show(next: View) {
  view.value = next;
  error.value = "";
  await nextTick();
  root.value?.querySelector<HTMLElement>("[data-step-focus], input, textarea, button:not([disabled])")?.focus();
}
watch(open, (isOpen) => { if (!isOpen) { view.value = "main"; error.value = ""; reported.value = false; note.value = ""; } });
watch(() => props.pageId, () => { open.value = false; });

/** V verifies, as the popover's hint says; not while a field has the keys. */
function keydown(event: KeyboardEvent) {
  if (event.key.toLowerCase() !== "v" || event.ctrlKey || event.metaKey || event.altKey || view.value !== "main") return;
  if (event.target instanceof HTMLElement && event.target.closest("input, textarea")) return;
  event.preventDefault();
  verify();
}

// Změnit garanta: the page's accounts, searched remotely; the first result is preselected, Enter picks it.
const query = ref("");
const found = shallowRef<ChoiceOption[]>([]);
const searching = ref(false);
let search: AbortController | undefined;
let timer: ReturnType<typeof setTimeout> | undefined;
async function lookup() {
  search?.abort();
  const controller = (search = new AbortController());
  searching.value = true;
  try {
    const people = await props.people(props.pageId, query.value.trim(), controller.signal);
    if (!controller.signal.aborted) found.value = people.map((person) => ({ value: person.id, label: person.name, description: person.username ?? person.email ?? undefined, icon: "user" }));
  } catch {
    if (!controller.signal.aborted) found.value = [];
  } finally {
    if (search === controller) searching.value = false;
  }
}
watch([query, view], () => { if (view.value !== "steward") return; clearTimeout(timer); timer = setTimeout(() => void lookup(), query.value ? 180 : 0); });
onBeforeUnmount(() => { search?.abort(); clearTimeout(timer); });
const pickSteward = (id: string) => void run({ stewardId: id });

function openUntil() {
  until.value = value.value?.verifiedUntil?.slice(0, 10) && value.value.verifiedUntil.slice(0, 10) > tomorrow() ? value.value.verifiedUntil.slice(0, 10) : tomorrow();
  void show("date");
}
async function sendReport() {
  if (busy.value) return;
  busy.value = true;
  error.value = "";
  try {
    await props.api.report(props.pageId, note.value);
    reported.value = true;
    note.value = "";
  } catch (failure) {
    error.value = errorMessage(failure) || t.failed;
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <Popover v-if="value" v-model:open="open">
    <PopoverTrigger as-child>
      <button type="button" class="page-verification-chip" :title="tooltip" :aria-label="t.chipLabel(label)" :data-state="value.state" data-page-verification-chip>
        <StatusBadge :tone="verificationTone(value.state)" :icon="verificationIcon(value.state)" :label="label" size="sm" />
      </button>
    </PopoverTrigger>
    <PopoverContent class="page-verification-pop" align="start" :side-offset="6" data-page-verification-popover @keydown="keydown">
      <div :key="view" ref="root" class="page-verification-view" :aria-label="t.popoverTitle" role="group">
        <template v-if="view === 'main'">
          <dl class="page-verification-facts">
            <dt>{{ t.steward }}</dt><dd data-verification-steward>{{ value.stewardName ?? t.noSteward }}</dd>
            <dt>{{ t.verifiedBy }}</dt><dd data-verification-by>{{ value.verifiedAt ? t.verifiedOn(value.verifiedByName ?? null, formatDate(value.verifiedAt), !!value.verifiedViaAi) : t.neverVerified }}</dd>
            <template v-if="value.verifiedUntil"><dt>{{ t.validUntil }}</dt><dd>{{ formatDate(value.verifiedUntil) }}</dd></template>
          </dl>
          <p v-if="value.changedSinceVerification" class="page-verification-changed" data-verification-changed>
            <AtlasIcon name="history" :size="14" />{{ t.changed }}
            <button type="button" class="page-verification-link" @click="open = false; emit('openHistory')">{{ t.compare }}</button>
          </p>
          <div v-if="canEdit" class="page-verification-actions">
            <Button size="sm" type="button" :disabled="busy || !isPublished" data-step-focus data-verification-verify @click="verify"><AtlasIcon name="shield-check" :size="15" />{{ t.verify }}<kbd>V</kbd></Button>
            <Button variant="outline" size="sm" type="button" :disabled="busy || !isPublished" data-verification-until @click="openUntil"><AtlasIcon name="calendar" :size="15" />{{ t.verifyUntil }}</Button>
            <Button variant="outline" size="sm" type="button" :disabled="busy" data-verification-steward-change @click="show('steward')"><AtlasIcon name="user" :size="15" />{{ t.changeSteward }}</Button>
          </div>
          <p v-if="canEdit && !isPublished" class="page-verification-note">{{ t.needsPublication }}</p>
          <div v-else-if="!canEdit" class="page-verification-actions">
            <Button variant="outline" size="sm" type="button" data-step-focus data-verification-report @click="show('report')"><AtlasIcon name="megaphone" :size="15" />{{ t.report }}</Button>
          </div>
        </template>

        <template v-else-if="view === 'steward'">
          <SearchableChoiceList v-model:query="query" remote :loading="searching" :options="found" :model-value="value.stewardId ?? ''" :label="t.changeSteward" :placeholder="t.stewardSearch" :empty-text="t.stewardEmpty" :max-height="220" @pick="pickSteward" />
          <div class="page-verification-actions">
            <Button variant="ghost" size="sm" type="button" @click="show('main')"><AtlasIcon name="back" :size="15" />{{ t.back }}</Button>
            <Button v-if="value.stewardId" variant="ghost" size="sm" type="button" :disabled="busy" data-verification-steward-clear @click="run({ stewardId: null })">{{ t.stewardNone }}</Button>
          </div>
        </template>

        <form v-else-if="view === 'date'" class="page-verification-form" @submit.prevent="run({ verify: true, until })">
          <label class="page-verification-field"><span>{{ t.verifyUntilField }}</span><Input v-model="until" type="date" :min="tomorrow()" required data-step-focus data-verification-date /></label>
          <div class="page-verification-actions">
            <Button variant="ghost" size="sm" type="button" @click="show('main')"><AtlasIcon name="back" :size="15" />{{ t.back }}</Button>
            <Button size="sm" type="submit" :disabled="busy || !until" data-verification-date-confirm><AtlasIcon name="shield-check" :size="15" />{{ t.verifyUntilConfirm }}</Button>
          </div>
        </form>

        <form v-else class="page-verification-form" @submit.prevent="sendReport">
          <p v-if="reported" class="page-verification-note" role="status" data-verification-reported><AtlasIcon name="circle-check" :size="15" />{{ t.reportSent }}</p>
          <template v-else>
            <label class="page-verification-field"><span>{{ t.reportNote }}</span><Textarea v-model="note" maxlength="500" rows="3" data-step-focus data-verification-note /></label>
            <div class="page-verification-actions">
              <Button variant="ghost" size="sm" type="button" @click="show('main')"><AtlasIcon name="back" :size="15" />{{ t.back }}</Button>
              <Button size="sm" type="submit" :disabled="busy" data-verification-report-send><AtlasIcon name="megaphone" :size="15" />{{ t.reportSend }}</Button>
            </div>
          </template>
        </form>
        <p v-if="error" class="notice error" role="alert">{{ error }}</p>
      </div>
    </PopoverContent>
  </Popover>
</template>

<style scoped>
/* The trigger only hosts the badge: no frame of its own, the focus ring around the badge, a press as every control (--atlas-press). */
.page-verification-chip { display: inline-flex; padding: 0; border: 0; border-radius: 7px; background: transparent; color: inherit; font: inherit; cursor: pointer; transition: transform var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .page-verification-chip:hover { box-shadow: 0 0 0 1px var(--line); } }
.page-verification-chip:active { transform: scale(var(--atlas-press)); }
.page-verification-chip:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
</style>

<style>
/* The popover is portalled out of this component, so its rules cannot be scoped. */
.page-verification-pop { width: 320px; padding: 14px; }
.page-verification-view { display: grid; gap: 12px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.page-verification-facts { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 6px 12px; margin: 0; font-size: 13px; }
.page-verification-facts dt { color: var(--muted); }
.page-verification-facts dd { margin: 0; overflow-wrap: anywhere; }
.page-verification-changed { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin: 0; color: var(--warning); font-size: 13px; }
.page-verification-link { padding: 0; border: 0; background: none; color: var(--accent); font: inherit; text-decoration: underline; cursor: pointer; }
.page-verification-link:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 3px; }
.page-verification-actions { display: flex; flex-wrap: wrap; gap: 6px; }
.page-verification-actions kbd { margin-left: 4px; padding: 0 4px; border-radius: 4px; background: color-mix(in srgb, currentColor 18%, transparent); font: 600 11px var(--font-mono, ui-monospace, monospace); }
.page-verification-note { display: flex; align-items: flex-start; gap: 8px; margin: 0; color: var(--muted); font-size: 13px; line-height: 1.45; }
.page-verification-form { display: grid; gap: 10px; }
.page-verification-field { display: grid; gap: 4px; font-size: 13px; color: var(--muted); }
</style>
