<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import t from "../../i18n/contacts-ui";
import { errorMessage } from "../../api";
import { createDebouncedLookup } from "../../lib/debouncedLookup";
import { skillApi as defaultApi, shownLevel, type Expert, type ExpertList, type SkillApi, type TopicRef } from "../../contacts/skillApi";
import { rateSkills } from "../../contacts/contactLinking";
import { useToast } from "../../composables/useToast";
import { tagTone } from "../../pageTags";
import AtlasIcon from "../AtlasIcon.vue";
import ConfirmDialog from "../settings/ConfirmDialog.vue";
import { Button } from "../ui/button";
import ContactCard from "./ContactCard.vue";
import "../../page-tags.css";

/**
 * „Kdo umí X“ in Knihovna → Kontakty (skills spec §7): the directory's search is also a topic's, so above the contacts found by their
 * fields stand the people who know the topic it names (by name, alias or a similar spelling, narrower topics included), the best first,
 * each with why: the level, who says so and how. The topic's row keeps the catalog tidy where people see it: its other spellings, the
 * broader topic and „Možná totéž“ with Sloučit. Nothing shows for a text that is no topic. Why not ContactOverview: that lists the
 * people of the pages; this ranks people by a topic. `api` is the gallery's.
 */
const props = defineProps<{ query: string; api?: Pick<SkillApi, "experts" | "merge" | "contact" | "rate" | "topics"> }>();
const emit = defineEmits<{ found: [people: number] }>();
const api = computed(() => props.api ?? defaultApi);
const toaster = useToast();
const list = ref<ExpertList | null>(null);
const merging = ref<TopicRef | null>(null), mergeBusy = ref(false), mergeError = ref("");

const lookup = createDebouncedLookup<ExpertList | null>({
  find: (text, signal) => api.value.experts(text.trim(), signal),
  onResult: value => { list.value = value; emit("found", value?.topic ? value.people.length : 0); },
  empty: () => null,
  delayMs: 250,
  minLength: 2,
});
watch(() => props.query, text => lookup.update(text), { immediate: true });
onBeforeUnmount(lookup.cancel);

const topic = computed(() => (list.value?.topic && list.value.people.length ? list.value.topic : null));
/** The reader's own contact when it is not among the people yet: „Přidat sebe“ rates it in this topic. */
const self = computed(() => (list.value?.selfContactId && !list.value.people.some(person => person.contactId === list.value!.selfContactId) ? list.value.selfContactId : null));
const about = computed(() => [topic.value?.aliases.length ? t.skills.alsoKnown(topic.value.aliases.join(", ")) : "",
  topic.value?.parents.length ? t.skills.partOf(topic.value.parents.map(x => x.name).join(", ")) : "", t.skills.people(list.value?.people.length ?? 0)].filter(Boolean).join(" · "));
function why(expert: Expert) {
  const skill = expert.skill;
  if (!skill) return t.skills.pages(expert.evidence);
  const level = shownLevel(skill);
  return [expert.via ? t.skills.via(expert.via) : "", level ? t.skills.levels[Math.round(level) - 1] : "",
    skill.peers ? t.skills.confirmedShort(skill.peers) : skill.fewPeers ? t.skills.fewShort : skill.self != null ? t.skills.selfShort : "",
    skill.selfNote ?? "", skill.myNote ?? "", ...skill.raters.map(rater => rater.note ?? "").filter(Boolean), expert.evidence ? t.skills.pages(expert.evidence) : ""].filter(Boolean).join(" · ");
}
async function merge() {
  if (!merging.value || !topic.value) return;
  mergeBusy.value = true; mergeError.value = "";
  try {
    const into = await api.value.merge(merging.value.id, topic.value.id);
    toaster.success(t.skills.merged(into.name));
    merging.value = null;
    lookup.update(props.query);
  } catch (failure) { mergeError.value = errorMessage(failure); }
  finally { mergeBusy.value = false; }
}
</script>

<template>
  <section v-if="topic" class="skill-experts tags-ui" :aria-label="t.skills.whoKnows(topic.name)" data-skill-experts>
    <header class="skill-experts-head">
      <span class="tag-chip" :data-tone="tagTone(topic.name)"><AtlasIcon name="hash" :size="13" />{{ topic.name }}</span>
      <span class="skill-experts-about">{{ about }}</span>
      <Button v-if="self" size="sm" variant="outline" type="button" data-skill-add-self @click="rateSkills(self, $event.currentTarget as HTMLElement, props.api, topic.name)">{{ t.skills.addSelf }}</Button>
      <span v-if="list?.similar.length" class="skill-experts-similar">{{ t.skills.similar }}
        <Button v-for="item in list.similar" :key="item.id" size="sm" variant="outline" type="button" :title="t.skills.mergeConfirm(item.name, topic.name)" data-skill-merge @click="merging = item">
          {{ item.name }} → {{ t.skills.merge }}
        </Button>
      </span>
    </header>
    <ul class="skill-experts-items atlas-stagger">
      <li v-for="expert in list!.people" :key="expert.contactId" :data-skill-expert="expert.contactId">
        <ContactCard :contact="expert.contact" compact />
        <p class="skill-experts-why">
          <span v-if="expert.skill && shownLevel(expert.skill)" class="skill-dots" :data-tone="tagTone(topic.name)" aria-hidden="true"><i v-for="n in 5" :key="n" :class="{ on: n <= Math.round(shownLevel(expert.skill) ?? 0) }" /></span>
          <span>{{ why(expert) }}</span>
        </p>
        <Button variant="ghost" size="sm" type="button" class="skill-experts-rate" @click="rateSkills(expert.contactId, $event.currentTarget as HTMLElement, props.api, topic.name)">{{ t.skills.rateShort }}</Button>
      </li>
    </ul>
    <ConfirmDialog :open="!!merging" :title="t.skills.merge" :confirm-label="t.skills.merge" :busy="mergeBusy" :error="mergeError" @confirm="merge" @cancel="merging = null">
      <p>{{ merging && t.skills.mergeConfirm(merging.name, topic.name) }}</p>
    </ConfirmDialog>
  </section>
</template>

<style scoped>
.skill-experts { display: grid; gap: 8px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.skill-experts-head { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; font-size: 13px; }
.skill-experts-about { color: var(--muted); }
.skill-experts-similar { display: inline-flex; flex-wrap: wrap; align-items: center; gap: 6px; margin-left: auto; color: var(--muted); }
.skill-experts-items { margin: 0; padding: 0; list-style: none; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); overflow: hidden; }
.skill-experts-items > li { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: start; gap: 4px 8px; padding: 10px 12px 10px 16px; }
.skill-experts-items > li + li { border-top: 1px solid color-mix(in srgb, var(--line) 50%, var(--surface)); }
.skill-experts-why { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; grid-column: 1; margin: 0 0 0 40px; color: var(--muted); font-size: 12.5px; }
.skill-experts-rate { grid-column: 2; grid-row: 1; color: var(--muted); }
.skill-dots { display: inline-flex; gap: 2px; }
.skill-dots i { width: 6px; height: 6px; border-radius: 999px; background: color-mix(in srgb, var(--tag-ink) 22%, transparent); }
.skill-dots i.on { background: var(--tag-ink); }
@container (max-width: 520px) { .skill-experts-similar { margin-left: 0; } .skill-experts-why { margin-left: 0; } }
</style>
