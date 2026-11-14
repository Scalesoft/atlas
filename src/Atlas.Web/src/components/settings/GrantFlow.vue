<script setup lang="ts">
import { locale } from "../../i18n";
import shell from "../../i18n/settings-shell";
import t from "../../i18n/settings-flow";
import { computed, nextTick, ref, watch } from "vue";
import AccessLevelBadge from "./AccessLevelBadge.vue";
import AccessLevelPicker from "./AccessLevelPicker.vue";
import ChoiceCards from "./ChoiceCards.vue";
import ConfirmWithReauth from "./ConfirmWithReauth.vue";
import GrantValidityFields from "./GrantValidityFields.vue";
import { validityError, validitySummary } from "./grantValidity";
import SearchableChoiceList from "./SearchableChoiceList.vue";
import ProgressiveFlow from "./ProgressiveFlow.vue";
import SummaryList from "./SummaryList.vue";
import type { ChoiceOption, FlowStep, GrantDraft, GrantSubject, SummaryItem } from "./types";
import type { ReauthTarget } from "../../protection";

/**
 * Give someone access, one decision per step (the space access flow):
 * 1 Komu (Uživatel/Skupina) · 2 Kdo (searchable tiles) · 3 Úroveň (AccessLevelPicker: a higher
 * level includes the lower ones) · 4 Platnost (GrantValidityFields: optional Od and Do, presets) · 5 Souhrn, with a fresh sign-in when
 * `requiresReauth` says the grant needs one. The same flow serves vault levels and page or space
 * roles; it gets the levels as options and emits the draft. `search` reports what is typed in the
 * recipient step, for a parent that loads the matching people and groups from the server.
 */
const props = withDefaults(
  defineProps<{
    subjects: GrantSubject[];
    /** Ordered access levels, lowest first. */
    levels: ChoiceOption[];
    target: string;
    requiresReauth?: (draft: GrantDraft) => boolean;
    /** The fresh sign-in of the summary is bound to this scope (a vault or secret its own). */
    reauthTarget?: ReauthTarget;
    busy?: boolean;
    /** The owner searches the server (`search`) and passes the matches as `subjects`; nothing is listed before typing. */
    remote?: boolean;
    /** A refusal of the save, said at the summary. */
    saveError?: string;
    /** `remote`: the owner is still searching for what was typed; the results shown are the previous ones (show fast). */
    searching?: boolean;
  }>(),
  { requiresReauth: undefined, reauthTarget: () => ({}), busy: false, remote: false, saveError: undefined, searching: false },
);
const emit = defineEmits<{ save: [draft: GrantDraft, proof: string | null]; cancel: []; search: [query: string, kind: "user" | "group"] }>();

// The target reads mid-sentence elsewhere ("k cíli prostor Produkt"); as the flow's caption it starts with a capital,
// as the vault wizard's "Prostor Produkt" does.
const g = t.grant;
const caption = computed(() => props.target.charAt(0).toLocaleUpperCase(locale) + props.target.slice(1));
const step = ref("kind");
const kind = ref<"user" | "group" | null>(null);
const subjectId = ref<string | null>(null);
const level = ref<string | null>(null);
const startAt = ref("");
const expiresAt = ref("");
const error = computed(() => validityError(startAt.value, expiresAt.value));
const recipientQuery = ref("");
watch(recipientQuery, (value) => emit("search", value.trim(), kind.value ?? "user"));

const subject = computed(() => props.subjects.find((item) => item.id === subjectId.value));
const subjectOptions = computed<ChoiceOption[]>(() =>
  props.subjects
    .filter((item) => item.kind === kind.value)
    .map((item) => ({ value: item.id, label: item.name, description: item.detail, icon: item.kind === "group" ? "users" : "account", tone: item.kind === "group" ? undefined : "neutral" })),
);
const draft = computed<GrantDraft>(() => ({
  kind: kind.value ?? "user",
  subjectId: subjectId.value ?? "",
  role: level.value ?? "",
  startAt: startAt.value,
  expiresAt: expiresAt.value,
}));
const needsReauth = computed(() => !!level.value && !!props.requiresReauth?.(draft.value));
const levelOption = computed(() => props.levels.find((item) => item.value === level.value));
const validityText = computed(() => validitySummary(startAt.value, expiresAt.value));
function validateValidity() {
  return !error.value;
}

const steps = computed<FlowStep[]>(() => [
  { id: "kind", label: g.kind.label, icon: "users", title: g.kind.title, hint: g.kind.hint, summary: kind.value === "group" ? g.kind.group : kind.value === "user" ? g.kind.user : undefined, ready: !!kind.value },
  { id: "recipient", label: g.recipient.label, icon: kind.value === "group" ? "users" : "account", title: kind.value === "group" ? g.recipient.groupTitle : g.recipient.userTitle, hint: g.recipient.hint, summary: subject.value?.name, ready: !!subject.value && subject.value.kind === kind.value },
  { id: "level", label: g.level.label, icon: "key-round", title: g.level.title(kind.value === "group"), hint: g.level.hint(props.target), summary: levelOption.value?.label, ready: !!levelOption.value },
  { id: "validity", label: g.validity.label, icon: "calendar", title: g.validity.title, hint: g.validity.hint, summary: startAt.value || expiresAt.value ? g.validity.limited : g.validity.unlimited, ready: !error.value, validate: validateValidity },
  {
    id: "summary",
    label: shell.summary,
    icon: "circle-check",
    title: g.review.title,
    hint: needsReauth.value ? g.review.hintReauth : g.review.hint,
    nextLabel: g.review.save,
    nextIcon: "check",
    hideNext: needsReauth.value,
  },
]);
const summary = computed<SummaryItem[]>(() => [
  { key: "subject", icon: kind.value === "group" ? "users" : "account", label: kind.value === "group" ? g.kind.group : g.kind.user, value: subject.value?.name },
  { key: "target", icon: "lock", label: g.review.target, value: props.target },
  { key: "level", icon: levelOption.value?.icon ?? "key-round", label: g.level.label },
  { key: "validity", icon: "calendar", label: "Platnost", value: validityText.value },
]);

const kindOptions: ChoiceOption[] = [
  { value: "user", label: g.kind.user, description: g.kind.userDescription, icon: "account" },
  { value: "group", label: g.kind.group, description: g.kind.groupDescription, icon: "users" },
];

const root = ref<HTMLElement>();
/** Keyboard-first (UX1-4): a choice that reveals the next step hands it the focus and brings it into view. */
function focusStep(id: string, selector: string) {
  // Two ticks: the flow reveals the step, then its InlineReveal renders it.
  void nextTick().then(nextTick).then(() => {
    const section = root.value?.querySelector<HTMLElement>(`[data-step="${id}"]`);
    section?.scrollIntoView?.({ block: "nearest" });
    section?.querySelector<HTMLElement>(selector)?.focus({ preventScroll: true });
  });
}
function selectKind(value: string | string[] | null) {
  const next = value === "group" ? "group" : "user";
  const first = !kind.value;
  if (kind.value !== next) {
    subjectId.value = null;
    recipientQuery.value = "";
  }
  kind.value = next;
  // Only the first choice moves on: arrows that change it later stay on the tiles.
  if (first) focusStep("recipient", "input[type=search]");
}
function picked() {
  focusStep("level", '[role="radio"][data-state="checked"], [role="radio"]');
}
const canSave = computed(() => !!kind.value && !!subject.value && subject.value.kind === kind.value && !!levelOption.value && !error.value);
function save(proof: string | null = null) {
  if (props.busy || !canSave.value) return;
  emit("save", draft.value, proof);
}
function onNext(current: FlowStep) {
  if (current.id === "summary" && !needsReauth.value) save();
}
</script>

<template>
  <div ref="root" class="grant-flow">
    <ProgressiveFlow autofocus v-model="step" :steps="steps" :label="g.add" :context="caption" :busy="busy" @next="onNext" @cancel="emit('cancel')">
      <template #kind>
        <ChoiceCards :model-value="kind" :options="kindOptions" :label="g.kind.label" hide-label :columns="2" @update:model-value="selectKind" />
      </template>
      <template #recipient>
        <SearchableChoiceList
          v-model="subjectId"
          v-model:query="recipientQuery"
          :options="subjectOptions"
          :label="kind === 'group' ? g.recipient.groups : g.recipient.users"
          :placeholder="kind === 'group' ? g.recipient.searchGroup : g.recipient.searchUser"
          :remote="remote"
          :loading="searching"
          :empty-text="remote && !recipientQuery.trim() ? (kind === 'group' ? g.recipient.typeGroup : g.recipient.typeUser) : searching ? g.recipient.searching : g.recipient.none"
          @pick="picked"
        />
      </template>
      <template #level>
        <AccessLevelPicker v-model="level" :options="levels" :label="g.level.access" hide-label />
      </template>
      <template #validity>
        <GrantValidityFields v-model:start-at="startAt" v-model:expires-at="expiresAt" :label="g.validity.label" hide-label :error="error" />
      </template>
      <template #summary>
        <SummaryList :items="summary" :label="g.review.summary">
          <template #value-level><AccessLevelBadge v-if="level" :options="levels" :value="level" /></template>
        </SummaryList>
        <ConfirmWithReauth v-if="needsReauth" :key="JSON.stringify([draft, canSave])" :disabled="!canSave" :confirm-label="g.review.save" :busy="busy" :target="reauthTarget" :error="saveError" :cancel-label="shell.cancel" @confirm="save" @cancel="emit('cancel')" />
        <p v-else-if="saveError" class="grant-flow-error" role="alert">{{ saveError }}</p>
      </template>
    </ProgressiveFlow>
  </div>
</template>

<style scoped>
.grant-flow { min-width: 0; }
.grant-flow-error { margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
</style>
