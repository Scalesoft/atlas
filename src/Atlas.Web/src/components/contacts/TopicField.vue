<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import t from "../../i18n/contacts-ui";
import { createDebouncedLookup } from "../../lib/debouncedLookup";
import { foldText } from "../../lib/foldText";
import { useSearchKeys } from "../../lib/searchKeys";
import { skillApi as defaultApi, type SkillApi, type TopicRef } from "../../contacts/skillApi";
import AtlasIcon from "../AtlasIcon.vue";
import { Input } from "../ui/input";

/**
 * One topic by typing (skills spec §3, §7): the existing topics first — by name, alias or a similar spelling, so another spelling never
 * makes a new one —, the first preselected and Enter takes it; „Nové téma …“ only below them, and with nothing matching Enter takes the
 * typed text as a new topic (`allowNew`). Emits `take` with the name (and the topic when it exists). Used by the rating (SkillRatePanel)
 * and the topic's links (SkillExperts). Why not PageTags: that edits a page's tag set in a dialog; this picks one topic in place.
 */
const props = withDefaults(defineProps<{ label: string; placeholder?: string; allowNew?: boolean; exclude?: readonly string[]; api?: Pick<SkillApi, "topics"> }>(),
  { placeholder: undefined, allowNew: true, exclude: () => [], api: undefined });
const emit = defineEmits<{ take: [name: string, topic: TopicRef | null] }>();
const text = defineModel<string>({ default: "" });
const suggestions = ref<TopicRef[]>([]), searching = ref(false), picked = ref<string | undefined>(text.value || undefined);
const field = ref<InstanceType<typeof Input>>();

const lookup = createDebouncedLookup<TopicRef[]>({
  find: async (value, signal) => (await (props.api ?? defaultApi).topics(value.trim(), signal)).items,
  onResult: items => { suggestions.value = items.filter(item => !props.exclude.includes(item.id)).slice(0, 5); searching.value = false; },
  empty: () => [],
  delayMs: 150,
  minLength: 1,
});
watch(text, value => { if (picked.value === value) return; picked.value = undefined; searching.value = !!value.trim(); lookup.update(value); });
onBeforeUnmount(lookup.cancel);
const typed = computed(() => text.value.trim());
const offered = computed(() => (picked.value ? [] : suggestions.value));
const same = computed(() => suggestions.value.some(item => foldText(item.name) === foldText(typed.value) || (item.matched && foldText(item.matched) === foldText(typed.value))));
const offerNew = computed(() => props.allowNew && !!typed.value && !searching.value && !same.value && !picked.value);
function take(name: string, topic: TopicRef | null) { picked.value = name; text.value = name; suggestions.value = []; emit("take", name, topic); }
const keys = useSearchKeys({ text: () => text.value, clear: () => { text.value = ""; }, waiting: () => searching.value, tab: false,
  take: () => {
    const first = offered.value[0];
    if (first) { take(first.name, first); return true; }
    if (offerNew.value) { take(typed.value, null); return true; }
    return false;
  } });
/** `set`: a topic named from outside (a chip of the list) goes in as taken, without a search. */
defineExpose({ focus: () => (field.value?.$el as HTMLInputElement | undefined)?.focus?.({ preventScroll: true }),
  set: (name: string) => { picked.value = name; text.value = name; suggestions.value = []; } });
</script>

<template>
  <div class="topic-field">
    <label class="topic-field-label">
      <span>{{ label }}</span>
      <Input ref="field" v-model="text" :placeholder="placeholder ?? t.skills.topicPlaceholder" autocomplete="off" data-skill-topic @keydown="keys.onKeydown($event)" />
    </label>
    <ul v-if="offered.length || offerNew" class="topic-field-options" role="listbox" :aria-label="label">
      <li v-for="(item, index) in offered" :key="item.id" role="option" :aria-selected="index === 0">
        <button type="button" :data-first="index === 0 || undefined" @mousedown.prevent @click="take(item.name, item)">
          <span class="topic-field-chip"><AtlasIcon name="hash" :size="13" />{{ item.name }}</span>
          <small>{{ [item.matched ? t.skills.topicAlias(item.matched) : "", item.people ? t.skills.people(item.people) : ""].filter(Boolean).join(" · ") }}</small>
        </button>
      </li>
      <li v-if="offerNew" role="option" :aria-selected="!offered.length">
        <button type="button" class="topic-field-new" :data-first="!offered.length || undefined" @mousedown.prevent @click="take(typed, null)">
          <AtlasIcon name="plus" :size="14" />{{ t.skills.topicNew(typed) }}
        </button>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.topic-field { --tag-ink: var(--accent); display: grid; gap: 6px; min-width: 0; }
.topic-field-label { display: grid; gap: 6px; }
.topic-field-label > span { font-size: 12.5px; font-weight: 650; color: var(--muted); }
/* The page tag's chip (page-tags.css), drawn here too: the field may open in a popover outside the shell's styles. */
.topic-field-chip { display: inline-flex; align-items: center; gap: 5px; min-width: 0; max-width: 100%; min-height: 25px; padding: 2px 8px;
  border: 1px solid color-mix(in srgb, var(--tag-ink) 19%, transparent); border-radius: 7px; background: color-mix(in srgb, var(--tag-ink) 9%, var(--surface));
  color: var(--tag-ink); font-size: 12px; font-weight: 600; line-height: 1.4; }
.topic-field-options { display: grid; gap: 2px; margin: 0; padding: 4px; list-style: none; border: 1px solid var(--line); border-radius: 10px; background: var(--surface);
  animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
.topic-field-options button { display: flex; width: 100%; align-items: center; justify-content: space-between; gap: 8px; padding: 4px 6px; border: 0; border-radius: 7px;
  background: transparent; color: var(--text); font: inherit; font-size: 13px; text-align: left; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.topic-field-options button[data-first] { background: var(--state-active-row-bg, var(--hover)); }
.topic-field-options small { color: var(--muted); }
.topic-field-new { color: var(--muted) !important; }
@media (hover: hover) and (pointer: fine) { .topic-field-options button:hover { background: var(--hover); } }
.topic-field-options button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
</style>
