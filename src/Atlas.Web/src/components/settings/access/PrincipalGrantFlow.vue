<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { locale } from "../../../i18n";
import t from "../../../i18n/settings-access";
import AccessLevelBadge from "../AccessLevelBadge.vue";
import AccessLevelPicker from "../AccessLevelPicker.vue";
import ChoiceCards from "../ChoiceCards.vue";
import ConfirmWithReauth from "../ConfirmWithReauth.vue";
import GrantValidityFields from "../GrantValidityFields.vue";
import ProgressiveFlow from "../ProgressiveFlow.vue";
import SearchableChoiceList from "../SearchableChoiceList.vue";
import SummaryList from "../SummaryList.vue";
import { validityError, validitySummary } from "../grantValidity";
import type { ChoiceOption, FlowStep, SummaryItem } from "../types";
import type { ReauthTarget } from "../../../protection";
import { accessKind, allLevelOptions, grantableKinds, levelOptions, type AccessKind } from "./accessKinds";
import type { AccessLevel, AccessResource } from "./accessModel";

/**
 * GrantFlow in the other direction: the person (or group) is fixed, opened from their card, and the target is chosen.
 * K čemu offers every grantable kind as a tile, so a plugin's kind appears without touching the flow; Který searches the
 * resources of that kind on the server (`search`, results come back as `targets`); Úroveň is that kind's staircase;
 * Platnost the optional bounds; Souhrn saves, with a fresh sign-in when `requiresReauth` says the grant needs one.
 */
export interface PrincipalGrantDraft {
  target: AccessResource;
  level: AccessLevel;
  /** datetime-local strings in the browser's zone; empty for none. */
  startAt: string;
  expiresAt: string;
}
const props = withDefaults(
  defineProps<{
    subject: string;
    subjectKind?: "user" | "group";
    /** Search results of the current kind and query. */
    targets: AccessResource[];
    searching?: boolean;
    busy?: boolean;
    /** Defaults to the registry's grantable kinds. */
    kinds?: AccessKind[];
    requiresReauth?: (kind: string, level: AccessLevel) => boolean;
    initial?: { kind?: string; target?: AccessResource; level?: AccessLevel };
    /** A refusal of the save, said at the summary. */
    saveError?: string;
  }>(),
  { subjectKind: "user", searching: false, busy: false, kinds: undefined, requiresReauth: undefined, initial: () => ({}), saveError: undefined },
);
const emit = defineEmits<{ search: [kind: string, query: string]; save: [draft: PrincipalGrantDraft, proof: string | null]; cancel: [] }>();

const step = ref("kind");
const kind = ref<string | null>(props.initial.kind ?? null);
const target = ref<AccessResource | null>(props.initial.target ?? null);
const targetId = ref<string | null>(props.initial.target?.id ?? null);
const level = ref<string | null>(props.initial.level ?? null);
const query = ref("");
const startAt = ref("");
const expiresAt = ref("");
const error = computed(() => validityError(startAt.value, expiresAt.value));

const available = computed(() => props.kinds ?? grantableKinds());
const kindOptions = computed<ChoiceOption[]>(() =>
  available.value.map((item) => ({
    value: item.id,
    label: item.one,
    icon: item.icon,
    badge: item.plugin ?? undefined,
    description: item.parent ? t.flow.inside(accessKind(item.parent).one.toLocaleLowerCase(locale)) : t.flow.wholeResource,
  })),
);
const detail = (item: AccessResource) => [item.spaceName, item.parentTitle, item.note].filter(Boolean).join(" · ") || undefined;
// The chosen target stays listed while a new search runs, so the selection never disappears under the cursor.
const targetOptions = computed<ChoiceOption[]>(() => {
  const list = props.targets.filter((item) => item.kind === kind.value);
  const shown = target.value && !list.some((item) => item.id === target.value!.id) ? [target.value, ...list] : list;
  return shown.map((item) => ({ value: item.id, label: item.title, description: detail(item), icon: accessKind(item.kind).icon }));
});
const levels = computed(() => (kind.value ? levelOptions(kind.value) : []));
const levelOption = computed(() => levels.value.find((item) => item.value === level.value));
const kindName = computed(() => (kind.value ? accessKind(kind.value).one : ""));
const needsReauth = computed(() => !!kind.value && !!level.value && !!props.requiresReauth?.(kind.value, level.value as AccessLevel));
// Every guarded grant binds its fresh sign-in to the global security target (accessChanges.reauthTargetOf).
const reauthTarget = computed<ReauthTarget>(() => ({}));

function selectKind(value: string | string[] | null) {
  const next = typeof value === "string" ? value : null;
  if (next !== kind.value) {
    target.value = null;
    targetId.value = null;
    level.value = null;
    query.value = "";
  }
  kind.value = next;
}
watch(kind, (value) => value && emit("search", value, ""), { immediate: true });
watch(query, (value) => kind.value && emit("search", kind.value, value.trim()));
watch(targetId, (id) => {
  if (!id) return;
  const found = props.targets.find((item) => item.id === id && item.kind === kind.value);
  if (found) target.value = found;
});

const steps = computed<FlowStep[]>(() => [
  { id: "kind", label: t.flow.kindStep, icon: "layers", title: t.flow.kindTitle(props.subject), summary: kindName.value || undefined, ready: !!kind.value },
  { id: "target", label: t.flow.targetStep, icon: kind.value ? accessKind(kind.value).icon : "search", title: t.flow.targetTitle(kindName.value || t.flow.target), hint: t.flow.targetHint, summary: target.value?.title, ready: !!target.value },
  { id: "level", label: t.flow.levelStep, icon: "key-round", title: props.subjectKind === "group" ? t.flow.levelTitleGroup : t.flow.levelTitleUser, hint: t.flow.levelHint, summary: levelOption.value?.label, ready: !!levelOption.value },
  { id: "validity", label: t.flow.validityStep, icon: "calendar", title: t.flow.validityTitle, hint: t.flow.validityHint, summary: startAt.value || expiresAt.value ? t.flow.limited : t.flow.unlimited, ready: !error.value },
  {
    id: "summary",
    label: t.flow.summaryStep,
    icon: "circle-check",
    title: t.flow.summaryTitle,
    hint: needsReauth.value ? t.flow.reauthHint : t.flow.saveHint,
    nextLabel: t.flow.save,
    nextIcon: "check",
    hideNext: needsReauth.value,
  },
]);
const summary = computed<SummaryItem[]>(() => [
  { key: "subject", icon: props.subjectKind === "group" ? "users" : "account", label: props.subjectKind === "group" ? t.flow.group : t.flow.who, value: props.subject },
  { key: "target", icon: kind.value ? accessKind(kind.value).icon : "layers", label: kindName.value || t.flow.target, value: target.value ? [target.value.title, detail(target.value)].filter(Boolean).join(" · ") : undefined },
  { key: "level", icon: "key-round", label: t.flow.levelStep },
  { key: "validity", icon: "calendar", label: t.flow.validityStep, value: validitySummary(startAt.value, expiresAt.value) },
]);
const canSave = computed(() => !!target.value && !!levelOption.value && !error.value);
function save(proof: string | null = null) {
  if (props.busy || !canSave.value || !target.value) return;
  emit("save", { target: target.value, level: level.value as AccessLevel, startAt: startAt.value, expiresAt: expiresAt.value }, proof);
}
function onNext(current: FlowStep) {
  if (current.id === "summary" && !needsReauth.value) save();
}
</script>

<template>
  <ProgressiveFlow autofocus v-model="step" :steps="steps" :label="t.flow.add" :context="subject" :busy="busy" @next="onNext" @cancel="emit('cancel')">
    <template #kind>
      <ChoiceCards :model-value="kind" :options="kindOptions" :label="t.flow.kindStep" hide-label :columns="3" data-grant-kind @update:model-value="selectKind" />
    </template>
    <template #target>
      <SearchableChoiceList
        v-model="targetId"
        v-model:query="query"
        remote
        :options="targetOptions"
        :label="kindName"
        :placeholder="t.flow.searchPlaceholder(kindName.toLocaleLowerCase(locale))"
        :empty-text="searching ? t.flow.searching : query.trim() ? t.flow.nothingMatches : t.flow.nothingToGrant"
        data-grant-target
      />
    </template>
    <template #level>
      <AccessLevelPicker v-model="level" :options="levels" :label="t.flow.levelLabel" hide-label data-grant-level />
    </template>
    <template #validity>
      <GrantValidityFields v-model:start-at="startAt" v-model:expires-at="expiresAt" :label="t.flow.validityStep" hide-label :error="error" />
    </template>
    <template #summary>
      <SummaryList :items="summary" :label="t.flow.summaryLabel">
        <template #value-level><AccessLevelBadge v-if="level" :options="allLevelOptions()" :value="level" tone="level" /></template>
      </SummaryList>
      <ConfirmWithReauth
        v-if="needsReauth"
        :key="JSON.stringify([kind, target?.id, level, startAt, expiresAt])"
        :disabled="!canSave"
        :confirm-label="t.flow.save"
        :busy="busy"
        :target="reauthTarget"
        :error="saveError"
        :cancel-label="t.flow.cancel"
        @confirm="save"
        @cancel="emit('cancel')"
      />
      <p v-else-if="saveError" class="principal-grant-error" role="alert">{{ saveError }}</p>
    </template>
  </ProgressiveFlow>
</template>

<style scoped>
.principal-grant-error { margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
</style>
