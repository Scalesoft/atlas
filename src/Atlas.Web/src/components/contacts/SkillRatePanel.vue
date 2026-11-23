<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import t from "../../i18n/contacts-ui";
import { errorMessage } from "../../api";
import { createDebouncedLookup } from "../../lib/debouncedLookup";
import { foldText } from "../../lib/foldText";
import { useSearchKeys } from "../../lib/searchKeys";
import { skillApi as defaultApi, shownLevel, type ContactSkill, type ContactSkills, type SkillApi, type SkillVisibility, type TopicRef } from "../../contacts/skillApi";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import OptionPills from "../settings/OptionPills.vue";
import { Button } from "../ui/button";
import { Input } from "../ui/input";

/**
 * Rating what a person knows (skills spec §2.2, §7), opened from the person's card („Ohodnotit“, „Přidat znalost“): the person's topics with
 * the reader's own level, then one row of fields: the topic (an existing one first, Enter takes the first; a new one only when nothing
 * matches), the level 1–5, who sees it and how. A topic of the list fills the fields to change or remove the rating. Saved explicitly;
 * the list answers with the new summary. Why not ContactSkills: that shows the skills, this edits the reader's rating. `api` is the gallery's.
 */
const props = defineProps<{ contactId: string; topic?: string; api?: Pick<SkillApi, "contact" | "rate" | "topics">; close?: () => void }>();
const emit = defineEmits<{ saved: [skills: ContactSkills] }>();
const api = computed(() => props.api ?? defaultApi);

const state = ref<ContactSkills>();
const error = ref(""), note = ref(""), saving = ref(false);
const topic = ref(props.topic ?? ""), level = ref("3"), visibility = ref<SkillVisibility>("shared"), how = ref("");
const suggestions = ref<TopicRef[]>([]), searching = ref(false), picked = ref<string | undefined>(props.topic);
const field = ref<InstanceType<typeof Input>>();

onMounted(async () => {
  try { state.value = await api.value.contact(props.contactId); }
  catch (failure) { error.value = errorMessage(failure); }
  if (props.topic) focusLevel(); else (field.value?.$el as HTMLInputElement | undefined)?.focus?.({ preventScroll: true });
});
const levels = computed(() => t.skills.levels.map((label, index) => ({ value: String(index + 1), label: `${index + 1} ${label}` })));
const visibilities = computed(() => state.value?.self
  ? [{ value: "shared", label: t.skills.visibilitySelfShared }, { value: "private", label: t.skills.visibilityPrivate }]
  : [{ value: "shared", label: t.skills.visibilityShared }, { value: "signed", label: t.skills.visibilitySigned }, { value: "private", label: t.skills.visibilityPrivate }]);

// The topics as typed: existing ones first (by name, alias or a similar spelling); the typed text is a new topic only when none is the same.
const lookup = createDebouncedLookup<TopicRef[]>({
  find: async (text, signal) => (await api.value.topics(text.trim(), signal)).items,
  onResult: items => { suggestions.value = items.slice(0, 5); searching.value = false; },
  empty: () => [],
  delayMs: 150,
  minLength: 1,
});
watch(topic, text => { if (picked.value === text) return; picked.value = undefined; searching.value = !!text.trim(); lookup.update(text); });
onBeforeUnmount(lookup.cancel);
const sameAsTyped = computed(() => suggestions.value.some(item => foldText(item.name) === foldText(topic.value.trim()) || (item.matched && foldText(item.matched) === foldText(topic.value.trim()))));
const offered = computed(() => (picked.value ? [] : suggestions.value));
const root = ref<HTMLFormElement>();
/** The level next, so 1–5 and Enter finish the rating from the keyboard. */
function focusLevel() { void nextTick(() => root.value?.querySelector<HTMLElement>('[data-skill-level] [role="radio"][data-state="checked"]')?.focus({ preventScroll: true })); }
function take(name: string) { picked.value = name; topic.value = name; suggestions.value = []; focusLevel(); }
// Enter takes the first topic offered; with none, the typed one as a new topic (its row is then the only one, below the similar ones).
const keys = useSearchKeys({ text: () => topic.value, clear: () => { topic.value = ""; }, waiting: () => searching.value, tab: false,
  take: () => { const first = offered.value[0]; if (first) { take(first.name); return true; } if (topic.value.trim()) { take(topic.value.trim()); return true; } return false; } });
/** Outside the text fields: 1–5 set the level, Enter on a choice saves. */
function shortcuts(event: KeyboardEvent) {
  const target = event.target as HTMLElement;
  if (target.closest("input, textarea") || event.ctrlKey || event.metaKey || event.altKey) return;
  if (/^[1-5]$/.test(event.key)) { event.preventDefault(); level.value = event.key; focusLevel(); }
  else if (event.key === "Enter" && target.getAttribute("role") === "radio") { event.preventDefault(); void save(); }
}

/** A topic of the list: the fields take its name and the reader's own rating to change or remove. */
function edit(skill: ContactSkill) {
  take(skill.name);
  level.value = String(skill.mine ?? Math.round(shownLevel(skill) ?? 3));
  visibility.value = skill.myVisibility ?? "shared";
  how.value = skill.myNote ?? "";
}
const mine = computed(() => state.value?.skills.find(skill => foldText(skill.name) === foldText(topic.value.trim()) && skill.mine != null));

async function save(remove = false) {
  if (!topic.value.trim() || saving.value) return;
  saving.value = true; error.value = ""; note.value = "";
  try {
    state.value = await api.value.rate(props.contactId, { topic: topic.value.trim(), level: remove ? null : Number(level.value), visibility: visibility.value, note: how.value.trim() || null });
    note.value = t.skills.saved;
    emit("saved", state.value);
    topic.value = ""; how.value = ""; picked.value = undefined; suggestions.value = [];
    (field.value?.$el as HTMLInputElement | undefined)?.focus?.();
  } catch (failure) { error.value = errorMessage(failure); }
  finally { saving.value = false; }
}
</script>

<template>
  <form ref="root" class="skill-rate" data-skill-rate @submit.prevent="save()" @keydown="shortcuts">
    <LoadingSkeleton v-if="!state && !error" :rows="2" />
    <template v-if="state">
      <ul v-if="state.skills.length" class="skill-rate-list atlas-stagger" :aria-label="t.skills.title">
        <li v-for="skill in state.skills" :key="skill.tagId">
          <button type="button" class="skill-chip skill-rate-chip" :aria-pressed="foldText(skill.name) === foldText(topic)" @click="edit(skill)">
            <AtlasIcon name="hash" :size="13" />{{ skill.name }}
            <span class="skill-dots" aria-hidden="true"><i v-for="n in 5" :key="n" :class="{ on: n <= Math.round(shownLevel(skill) ?? 0) }" /></span>
            <small v-if="skill.mine != null">{{ t.skills.yours(skill.mine) }}</small>
          </button>
        </li>
      </ul>
      <p v-else class="skill-rate-hint">{{ t.skills.empty }}</p>
      <!-- One's own card: topics of pages one wrote, a suggestion only; a click puts it in the field, nothing is saved by itself. -->
      <div v-if="state.suggested.length" class="skill-rate-list skill-rate-suggested">
        <span class="skill-rate-hint">{{ t.skills.suggested }}</span>
        <button v-for="item in state.suggested" :key="item.id" type="button" class="skill-rate-suggestion" :title="t.skills.suggestedAdd(item.name)" @click="take(item.name)">
          <AtlasIcon name="plus" :size="13" />{{ item.name }}<small>{{ t.skills.pages(item.people) }}</small>
        </button>
      </div>
    </template>

    <label class="skill-rate-field">
      <span>{{ t.skills.topic }}</span>
      <Input ref="field" v-model="topic" :placeholder="t.skills.topicPlaceholder" autocomplete="off" data-skill-topic @keydown="keys.onKeydown($event)" />
    </label>
    <ul v-if="offered.length || (topic.trim() && !searching && !sameAsTyped && !picked)" class="skill-rate-options" role="listbox" :aria-label="t.skills.topic">
      <li v-for="(item, index) in offered" :key="item.id" role="option" :aria-selected="index === 0">
        <button type="button" :data-first="index === 0 || undefined" @mousedown.prevent @click="take(item.name)">
          <span class="skill-chip"><AtlasIcon name="hash" :size="13" />{{ item.name }}</span>
          <small>{{ [item.matched ? t.skills.topicAlias(item.matched) : "", item.people ? t.skills.people(item.people) : ""].filter(Boolean).join(" · ") }}</small>
        </button>
      </li>
      <li v-if="topic.trim() && !searching && !sameAsTyped && !picked" role="option" :aria-selected="!offered.length">
        <button type="button" class="skill-rate-new" @mousedown.prevent @click="take(topic.trim())"><AtlasIcon name="plus" :size="14" />{{ t.skills.topicNew(topic.trim()) }}</button>
      </li>
    </ul>
    <OptionPills v-model="level" :options="levels" :label="t.skills.level" data-skill-level />
    <p class="skill-rate-hint">{{ t.skills.keys }}</p>
    <OptionPills v-model="visibility" :options="visibilities" :label="t.skills.visibility" />
    <label class="skill-rate-field">
      <span>{{ t.skills.note }}</span>
      <Input v-model="how" :placeholder="t.skills.notePlaceholder" maxlength="200" />
    </label>
    <p class="skill-rate-hint">{{ t.skills.anonymity }}</p>
    <p v-if="error" class="skill-rate-error" role="alert">{{ error }}</p>
    <p v-else-if="note" class="skill-rate-hint" role="status">{{ note }}</p>
    <div class="skill-rate-actions">
      <Button v-if="mine" type="button" variant="ghost" :disabled="saving" @click="save(true)">{{ t.skills.remove }}</Button>
      <Button type="submit" :disabled="saving || !topic.trim()" data-skill-save>{{ t.skills.save }}</Button>
    </div>
  </form>
</template>

<style scoped>
.skill-rate { --tag-ink: var(--accent); display: grid; gap: 12px; min-width: 0; font-size: 13px; }
.skill-rate-list { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin: 0; padding: 0; list-style: none; }
/* The page tag's chip (page-tags.css), drawn here too: the panel opens in the body, outside the shell's styles. */
.skill-chip { display: inline-flex; align-items: center; gap: 5px; min-width: 0; max-width: 100%; min-height: 27px; padding: 3px 9px;
  border: 1px solid color-mix(in srgb, var(--tag-ink) 19%, transparent); border-radius: 7px; background: color-mix(in srgb, var(--tag-ink) 9%, var(--surface));
  color: var(--tag-ink); font: inherit; font-size: 12px; font-weight: 600; line-height: 1.4; overflow-wrap: anywhere; }
.skill-rate-chip { cursor: pointer; transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.skill-rate-chip[aria-pressed="true"] { border-color: color-mix(in srgb, var(--tag-ink) 60%, transparent); }
.skill-rate-chip small { color: var(--muted); font-weight: 500; }
.skill-rate-chip:focus-visible, .skill-rate-options button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.skill-dots { display: inline-flex; gap: 2px; }
.skill-dots i { width: 5px; height: 5px; border-radius: 999px; background: color-mix(in srgb, var(--tag-ink) 22%, transparent); }
.skill-dots i.on { background: var(--tag-ink); }
.skill-rate-field { display: grid; gap: 6px; }
.skill-rate-field > span { font-size: 12.5px; font-weight: 650; color: var(--muted); }
.skill-rate-options { display: grid; gap: 2px; margin: -6px 0 0; padding: 4px; list-style: none; border: 1px solid var(--line); border-radius: 10px; background: var(--surface);
  animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
.skill-rate-options button { display: flex; width: 100%; align-items: center; justify-content: space-between; gap: 8px; padding: 4px 6px; border: 0; border-radius: 7px; background: transparent;
  color: var(--text); font: inherit; text-align: left; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.skill-rate-options button[data-first] { background: var(--state-active-row-bg, var(--hover)); }
.skill-rate-options small { color: var(--muted); }
.skill-rate-new { color: var(--muted) !important; }
@media (hover: hover) and (pointer: fine) { .skill-rate-options button:hover, .skill-rate-chip:hover { background: var(--hover); } }
.skill-rate-suggestion { display: inline-flex; align-items: center; gap: 4px; min-height: 27px; padding: 3px 8px; border: 1px dashed var(--line); border-radius: 7px; background: transparent;
  color: var(--muted); font: inherit; font-size: 12px; cursor: pointer; transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
.skill-rate-suggestion small { font-size: 11px; }
@media (hover: hover) and (pointer: fine) { .skill-rate-suggestion:hover { border-color: var(--accent); color: var(--accent); } }
.skill-rate-hint { margin: 0; color: var(--muted); font-size: 12px; }
.skill-rate-error { margin: 0; color: var(--error); font-size: 12.5px; }
.skill-rate-actions { display: flex; justify-content: flex-end; gap: 8px; }
</style>

<style>
/* The reader's panel it opens in (openVueReaderPopover): wide enough for the levels, never wider than the screen. */
.md-context-popover.skill-rate-popover { width: min(440px, calc(100vw - 24px)); }
</style>
