<script setup lang="ts">
import { computed, nextTick, ref, useId, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { accessApi } from "../../../accessApi";
import GrantValidityFields from "../GrantValidityFields.vue";
import { validityError, validitySummary } from "../grantValidity";
import InlineReveal from "../InlineReveal.vue";
import OptionPills from "../OptionPills.vue";
import { locale } from "../../../i18n";
import t from "../../../i18n/settings-access";
import SearchableChoiceList from "../SearchableChoiceList.vue";
import type { ChoiceOption } from "../types";
import AccessChangeDialogs from "./AccessChangeDialogs.vue";
import { memberCount } from "./accessView";
import { levelOptions } from "./accessKinds";
import { targetName, useAccessChanges } from "./accessChanges";
import type { AccessCandidate, AccessLevel } from "./accessModel";
import { useCandidateSearch } from "./useCandidateSearch";

/**
 * Share one item in one compact form (progressive disclosure, CLAUDE.md): who (people and groups searched together as typed, the
 * signed-in owner left out, the first result preselected), what they may do (the kind's levels as pills, the lowest preselected) and,
 * only on demand, until when (GrantValidityFields, end only). Enter in the search picks the person and the focus goes to the level;
 * Enter there, or Sdílet, shares; a live sentence says what will happen. Who already has access is shown but not offered again
 * (`taken`, "kind:id"): the list beneath changes or removes it. A share goes through PUT /api/access/resources/{kind}/{id}/grants
 * like every grant (useAccessChanges). Choose it over ResourceGrantFlow (the five-step GrantFlow) for one item with two plain levels
 * and no fresh sign-in, such as a personal note; resources with a staircase, a start date or sign-in rules keep the flow.
 */
const props = withDefaults(defineProps<{ kind: string; id: string; title: string; taken?: string[]; api?: Pick<typeof accessApi, "candidates" | "grant" | "revoke"> }>(),
  { taken: () => [], api: () => accessApi });
const emit = defineEmits<{ shared: [message: string] }>();
const uid = useId();
const root = ref<HTMLElement>();

const levels = computed(() => levelOptions(props.kind));
const level = ref<string>(levels.value[0]?.value ?? "read");
const query = ref("");
const picked = ref<string | null>(null);
const limited = ref(false);
const expiresAt = ref("");
const validity = computed(() => (limited.value ? validityError("", expiresAt.value) : ""));

// People (the signed-in owner left out) and groups together.
const { results: found, searching, find } = useCandidateSearch<AccessCandidate>({
  delayMs: 200, failed: (message) => { changes.error.value = message; }, found: () => { changes.error.value = ""; },
});
watch(query, (value) => find(value, async (term) => {
  const [people, groups] = await Promise.all([props.api.candidates("user", term, true), props.api.candidates("group", term)]);
  return [...people, ...groups];
}));
const key = (candidate: Pick<AccessCandidate, "kind" | "id">) => `${candidate.kind}:${candidate.id}`;
const options = computed<ChoiceOption[]>(() => found.value.map((candidate) => {
  const taken = props.taken.includes(key(candidate));
  return {
    value: key(candidate), label: candidate.name, icon: candidate.kind === "group" ? "users" : "account", tone: candidate.kind === "group" ? undefined : "neutral",
    description: candidate.kind === "group" ? t.share.group(memberCount(Number(candidate.detail ?? 0))) : candidate.detail ? `@${candidate.detail}` : undefined,
    disabled: taken, disabledReason: taken ? t.share.alreadyHas : undefined,
  };
}));
const subject = computed(() => found.value.find((candidate) => key(candidate) === picked.value));
// A pick that the next search no longer offers is dropped, so it cannot be shared unseen.
watch(options, (list) => { if (picked.value && !list.some((option) => option.value === picked.value && !option.disabled)) picked.value = null; });

const levelLabel = computed(() => levels.value.find((option) => option.value === level.value)?.label.toLocaleLowerCase(locale) ?? "");
const summary = computed(() => {
  if (!subject.value) return "";
  const until = limited.value && expiresAt.value ? validitySummary("", expiresAt.value).toLocaleLowerCase(locale) : t.share.unlimited;
  return `${subject.value.name} · ${levelLabel.value} · ${until}`;
});

function chosen() {
  // Enter (or a click) picked a person or group: the level comes next.
  void nextTick(() => root.value?.querySelector<HTMLElement>('[data-share-level] [role="radio"][data-state="checked"]')?.focus());
}
const changes = useAccessChanges({ api: props.api, reload: async () => undefined, done: (message) => finish(message) });
function share() {
  const who = subject.value;
  if (!who || validity.value || changes.busy.value) return;
  void changes.grant({
    target: { kind: props.kind, id: props.id, name: targetName(props.kind, props.title) },
    subject: { kind: who.kind, id: who.id, name: who.name },
    level: level.value as AccessLevel,
    previous: null,
    startAt: null,
    expiresAt: limited.value && expiresAt.value ? new Date(expiresAt.value).toISOString() : null,
    expectedVersion: null,
  });
}
function finish(message: string) {
  query.value = "";
  picked.value = null;
  level.value = levels.value[0]?.value ?? "read";
  limited.value = false;
  expiresAt.value = "";
  emit("shared", message);
  // Ready for the next person.
  void nextTick(() => root.value?.querySelector<HTMLInputElement>("input[type=search]")?.focus());
}
/** Enter anywhere past the search shares what the summary says (the search's own Enter picks). */
function enter(event: KeyboardEvent) {
  const target = event.target as HTMLElement;
  // A level pill (a radio) takes Enter itself only to keep it from submitting; here it shares.
  if (event.isComposing || (event.defaultPrevented && !target.matches("[role=radio]"))) return;
  if (target.matches("input[type=search], button:not([role=radio]), input[type=datetime-local]")) return;
  if (!subject.value) return;
  event.preventDefault();
  share();
}
function toggleLimit() {
  limited.value = !limited.value;
  if (!limited.value) expiresAt.value = "";
}
defineExpose({ focus: () => root.value?.querySelector<HTMLInputElement>("input[type=search]")?.focus() });
</script>

<template>
  <div ref="root" class="share-form" data-share-form @keydown.enter="enter">
    <SearchableChoiceList
      v-model="picked"
      v-model:query="query"
      remote
      :loading="searching"
      :options="options"
      :label="t.share.who"
      :placeholder="t.share.placeholder"
      :empty-text="!query.trim() ? t.share.startTyping : searching ? t.share.searching : t.share.nobody"
      :max-height="220"
      data-share-recipient
      @pick="chosen"
    />
    <div class="share-form-row">
      <OptionPills v-model="level" :options="levels" :label="t.share.level" data-share-level />
      <button type="button" class="share-form-limit st-link" :aria-expanded="limited" :aria-controls="`${uid}-validity`" data-share-limit @click="toggleLimit">
        <AtlasIcon name="calendar" :size="14" />{{ limited ? t.share.removeLimit : t.share.limit }}
      </button>
    </div>
    <InlineReveal :id="`${uid}-validity`" :open="limited">
      <GrantValidityFields v-if="limited" v-model:expires-at="expiresAt" hide-start :label="t.share.validity" hide-label :error="validity" />
    </InlineReveal>
    <div class="share-form-actions">
      <p class="share-form-summary" role="status" data-share-summary>
        <template v-if="summary"><AtlasIcon name="circle-check" :size="15" />{{ summary }}</template>
        <template v-else>{{ t.share.choose }}</template>
      </p>
      <Button type="button" :disabled="!subject || !!validity || changes.busy.value" :aria-busy="changes.busy.value" data-share-submit @click="share">
        <AtlasIcon name="users" :size="15" />{{ t.share.share }}
      </Button>
    </div>
    <p v-if="changes.error.value || changes.conflict.value" class="share-form-error" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ changes.error.value || changes.conflict.value }}</p>
    <AccessChangeDialogs :changes="changes" />
  </div>
</template>

<style scoped>
.share-form { display: grid; gap: 12px; min-width: 0; }
.share-form-row { display: flex; flex-wrap: wrap; align-items: flex-end; justify-content: space-between; gap: 10px; }
.share-form-limit { display: inline-flex; align-items: center; gap: 6px; padding: 4px 2px; border: 0; background: none; font: inherit; font-size: 13px; cursor: pointer; }
.share-form-actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 10px; }
.share-form-summary { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--muted); font-size: 13px; }
.share-form-summary > svg { color: var(--success); }
.share-form-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
</style>
