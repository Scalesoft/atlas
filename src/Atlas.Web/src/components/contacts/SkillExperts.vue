<script setup lang="ts">
import { computed, onBeforeUnmount, ref, useId, watch } from "vue";
import t from "../../i18n/contacts-ui";
import { errorMessage } from "../../api";
import { contactTypeId } from "../../contacts/contactData";
import { createDebouncedLookup } from "../../lib/debouncedLookup";
import { objectApi } from "../../objects/objectApi";
import { buildShellHashEncoded } from "../../shellRoute";
import { skillApi as defaultApi, shownLevel, type Expert, type ExpertList, type SkillApi, type TaggedPage, type TopicLinkKind, type TopicRef } from "../../contacts/skillApi";
import { rateSkills, topicUrl } from "../../contacts/contactLinking";
import { useToast } from "../../composables/useToast";
import { tagTone } from "../../pageTags";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import ConfirmDialog from "../settings/ConfirmDialog.vue";
import InlineReveal from "../settings/InlineReveal.vue";
import OptionPills from "../settings/OptionPills.vue";
import SearchableTags from "../settings/SearchableTags.vue";
import { Button } from "../ui/button";
import ContactCard from "./ContactCard.vue";
import SkillLevelDots from "./SkillLevelDots.vue";
import TopicField from "./TopicField.vue";
import "../../page-tags.css";

/**
 * The topic's dashboard in Knihovna → Kontakty (skills spec §7): the directory's search is also a topic's, so above the contacts found by
 * their fields stands what the wiki knows of the topic the search names (by name, alias or a similar spelling): who knows it, the best
 * first, each with why (level, who says so, how); how the levels spread; the broader topics, its parts and related topics, set right here
 * (Vazba: TopicField and the kind); the pages carrying it; Přidat sebe and Přidat lidi (several people at one level, SearchableTags). Its row
 * keeps the catalog tidy where people see it: other spellings and „Možná totéž“ with Sloučit. A topic named anywhere (a chip, the graph)
 * opens here through `#/library/contacts?q=`, so there is no screen of its own. Nothing shows for a text that is no topic. Why not
 * ContactOverview: that lists the people of the pages; this ranks people by a topic. `api` and `people` are the gallery's.
 */
const props = defineProps<{ query: string; api?: Pick<SkillApi, "experts" | "merge" | "contact" | "rate" | "topics" | "link" | "pages">;
  people?: { id: string; name: string; organization?: string }[] }>();
const emit = defineEmits<{ found: [people: number] }>();
const api = computed(() => props.api ?? defaultApi);
const toaster = useToast();
const uid = useId();
const list = ref<ExpertList | null>(null);
const pages = ref<TaggedPage[] | null>(null);
const merging = ref<TopicRef | null>(null), mergeBusy = ref(false), mergeError = ref("");
/** A topic is being looked up (the debounce and the answer): a sized skeleton holds its place (show fast). */
const pending = ref(false);

const lookup = createDebouncedLookup<ExpertList | null>({
  find: (text, signal) => api.value.experts(text.trim(), signal),
  onResult: value => {
    pending.value = false;
    list.value = value;
    emit("found", value?.topic ? value.people.length : 0);
    pages.value = null;
    const id = value?.topic?.id;
    if (id && value!.people.length) void api.value.pages(id).then(items => { if (list.value?.topic?.id === id) pages.value = items; }).catch(() => { pages.value = []; });
  },
  empty: () => null,
  delayMs: 250,
  minLength: 2,
});
watch(() => props.query, text => { pending.value = text.trim().length >= 2; lookup.update(text); }, { immediate: true });
onBeforeUnmount(lookup.cancel);
const reload = () => lookup.update(props.query);
// Hundreds of people: a page of cards at a time.
const pageSize = 12;
const peopleShown = ref(pageSize);
watch(() => props.query, () => { peopleShown.value = pageSize; });
const shownPeople = computed(() => (list.value?.people ?? []).slice(0, peopleShown.value));
const hiddenPeople = computed(() => Math.max(0, (list.value?.people.length ?? 0) - peopleShown.value));

const topic = computed(() => (list.value?.topic && list.value.people.length ? list.value.topic : null));
const about = computed(() => [topic.value?.aliases.length ? t.skills.alsoKnown(topic.value.aliases.join(", ")) : "", t.skills.people(list.value?.people.length ?? 0)].filter(Boolean).join(" · "));
/** The reader's own contact when it is not among the people yet: „Přidat sebe“ rates it in this topic. */
const self = computed(() => (list.value?.selfContactId && !list.value.people.some(person => person.contactId === list.value!.selfContactId) ? list.value.selfContactId : null));
/** How the levels the reader may see spread over 1–5 (people without a number are left out). */
const spread = computed(() => {
  const counts = [0, 0, 0, 0, 0];
  for (const person of list.value?.people ?? []) { const level = person.skill ? shownLevel(person.skill) : null; if (level) counts[Math.min(5, Math.max(1, Math.round(level))) - 1]!++; }
  const most = Math.max(1, ...counts);
  return counts.map((count, index) => ({ level: index + 1, count, share: count / most }));
});
const hasSpread = computed(() => spread.value.some(entry => entry.count));
/** Why the person is here; `cardLevel`: the card says the level itself. */
function why(expert: Expert, cardLevel = false) {
  const skill = expert.skill;
  if (!skill) return t.skills.pages(expert.evidence);
  const level = shownLevel(skill);
  return [expert.via ? t.skills.via(expert.via) : "", level && !cardLevel ? t.skills.levels[Math.round(level) - 1] : "",
    skill.peers ? t.skills.confirmedShort(skill.peers, skill.peersAtLeast) : skill.fewPeers ? t.skills.fewShort : skill.self != null ? t.skills.selfShort : "",
    skill.selfNote ?? "", skill.myNote ?? "", ...skill.raters.map(rater => rater.note ?? ""), expert.evidence ? t.skills.pages(expert.evidence) : ""]
    .filter((part, index, parts) => part && parts.indexOf(part) === index).join(" · ");
}
async function merge() {
  if (!merging.value || !topic.value) return;
  mergeBusy.value = true; mergeError.value = "";
  try {
    const into = await api.value.merge(merging.value.id, topic.value.id);
    toaster.success(t.skills.merged(into.name));
    merging.value = null;
    reload();
  } catch (failure) { mergeError.value = errorMessage(failure); }
  finally { mergeBusy.value = false; }
}

// Vazba: is part of / related, to an existing topic.
const linking = ref(false), linkKind = ref<TopicLinkKind>("part_of"), linkText = ref(""), linkError = ref("");
const linkKinds = computed(() => [{ value: "part_of", label: t.skills.linkPartOf }, { value: "related", label: t.skills.linkRelated }]);
async function link(other: TopicRef | null) {
  if (!other || !topic.value) return;
  linkError.value = "";
  try { await api.value.link(topic.value.id, other.id, linkKind.value); linkText.value = ""; linking.value = false; reload(); }
  catch (failure) { linkError.value = errorMessage(failure); }
}
async function unlink(other: TopicRef, kind: TopicLinkKind, narrower = false) {
  if (!topic.value) return;
  try { await (narrower ? api.value.link(other.id, topic.value.id, kind, true) : api.value.link(topic.value.id, other.id, kind, true)); reload(); }
  catch (failure) { toaster.error(errorMessage(failure)); }
}
const relations = computed(() => topic.value ? [
  { title: t.skills.partOfTitle, items: topic.value.parents, kind: "part_of" as const, narrower: false },
  { title: t.skills.childrenTitle, items: topic.value.children, kind: "part_of" as const, narrower: true },
  { title: t.skills.relatedTitle, items: topic.value.related ?? [], kind: "related" as const, narrower: false },
].filter(group => group.items.length) : []);

// Přidat lidi: several people of the directory at one level, those of the topic's organizations first.
const adding = ref(false), picked = ref<string[]>([]), addLevel = ref("3"), addBusy = ref(false), addError = ref("");
const directory = ref<{ id: string; name: string; organization?: string }[]>([]);
watch(adding, async open => {
  if (!open || directory.value.length || props.people) return;
  try {
    const listing = await objectApi.list({ type: contactTypeId, scope: "wiki" });
    directory.value = listing.items.map(item => ({ id: item.id, name: item.title, organization: (item.data?.organization as string | undefined) ?? undefined }));
  } catch (failure) { addError.value = errorMessage(failure); }
});
const candidates = computed(() => {
  const known = new Set(list.value?.people.map(person => person.contactId));
  const organizations = new Set(list.value?.people.map(person => person.contact.organization).filter(Boolean));
  return (props.people ?? directory.value).filter(person => !known.has(person.id))
    .sort((a, b) => Number(organizations.has(b.organization)) - Number(organizations.has(a.organization)) || a.name.localeCompare(b.name))
    .map(person => ({ value: person.id, label: person.name, description: person.organization }));
});
const levels = computed(() => t.skills.levels.map((label, index) => ({ value: String(index + 1), label: `${index + 1} ${label}` })));
async function addPeople() {
  if (!topic.value || !picked.value.length) return;
  addBusy.value = true; addError.value = "";
  try {
    for (const id of picked.value) await api.value.rate(id, { topic: topic.value.name, level: Number(addLevel.value), visibility: "signed" });
    toaster.success(t.skills.added(picked.value.length));
    picked.value = []; adding.value = false; reload();
  } catch (failure) { addError.value = errorMessage(failure); }
  finally { addBusy.value = false; }
}
const pageUrl = (page: TaggedPage) => buildShellHashEncoded({ view: "page", pageId: page.id });
</script>

<template>
  <section v-if="topic" class="skill-experts tags-ui" :aria-label="t.skills.whoKnows(topic.name)" data-skill-experts>
    <header class="skill-experts-head">
      <span class="tag-chip" :data-tone="tagTone(topic.name)"><AtlasIcon name="hash" :size="13" />{{ topic.name }}</span>
      <span class="skill-experts-about">{{ about }}</span>
      <span class="skill-experts-actions">
        <Button v-if="self" size="sm" variant="outline" type="button" data-skill-add-self @click="rateSkills(self, $event.currentTarget as HTMLElement, props.api, topic.name)">{{ t.skills.addSelf }}</Button>
        <Button size="sm" variant="outline" type="button" :aria-expanded="adding" :aria-controls="`${uid}-add`" data-skill-add-people @click="adding = !adding">
          <AtlasIcon name="users" :size="14" />{{ t.skills.addPeople }}</Button>
      </span>
      <span v-if="list?.similar.length" class="skill-experts-similar">{{ t.skills.similar }}
        <Button v-for="item in list.similar" :key="item.id" size="sm" variant="ghost" type="button" :title="t.skills.mergeConfirm(item.name, topic.name)" data-skill-merge @click="merging = item">
          {{ item.name }} → {{ t.skills.merge }}
        </Button>
      </span>
    </header>
    <InlineReveal :id="`${uid}-add`" v-model:open="adding">
      <div class="skill-experts-add">
        <SearchableTags v-model="picked" :options="candidates" :label="t.skills.addPeopleLabel" :placeholder="t.skills.addPeoplePlaceholder" :idle="8" />
        <OptionPills v-model="addLevel" :options="levels" :label="t.skills.level" />
        <p v-if="addError" class="skill-experts-error" role="alert">{{ addError }}</p>
        <div><Button type="button" :disabled="!picked.length || addBusy" data-skill-add-save @click="addPeople">{{ t.skills.addPeopleSave(picked.length) }}</Button></div>
      </div>
    </InlineReveal>

    <div class="skill-experts-body">
      <div class="skill-experts-people">
        <ul class="skill-experts-items atlas-stagger">
          <li v-for="expert in shownPeople" :key="expert.contactId" class="skill-person" :data-skill-expert="expert.contactId">
            <ContactCard :contact="expert.contact" />
            <div class="skill-person-level">
              <SkillLevelDots v-if="expert.skill && shownLevel(expert.skill)" :level="shownLevel(expert.skill) ?? 0" :size="8" :data-tone="tagTone(topic.name)" />
              <strong v-if="expert.skill && shownLevel(expert.skill)">{{ t.skills.levels[Math.round(shownLevel(expert.skill) ?? 1) - 1] }}</strong>
              <Button variant="ghost" size="sm" type="button" class="skill-experts-rate" @click="rateSkills(expert.contactId, $event.currentTarget as HTMLElement, props.api, topic.name)">{{ t.skills.rateShort }}</Button>
            </div>
            <p class="skill-experts-why">{{ why(expert, true) }}</p>
          </li>
        </ul>
        <Button v-if="hiddenPeople" variant="outline" size="sm" type="button" class="skill-experts-more" data-skill-more-people @click="peopleShown += pageSize">{{ t.skills.showMore(hiddenPeople) }}</Button>
      </div>

      <aside class="skill-experts-side">
        <section v-if="hasSpread" class="skill-experts-panel">
          <h3>{{ t.skills.levelsTitle }}</h3>
          <div class="skill-spread" role="img" :aria-label="spread.map(entry => t.skills.levelCount(t.skills.levels[entry.level - 1]!, entry.count)).join(', ')">
            <span v-for="entry in spread" :key="entry.level" class="skill-spread-column" :title="t.skills.levelCount(t.skills.levels[entry.level - 1]!, entry.count)">
              <span class="skill-spread-bar" :style="{ '--share': entry.share }" /><small>{{ entry.level }}</small>
            </span>
          </div>
        </section>
        <section class="skill-experts-panel">
          <h3>{{ t.skills.linksTitle }}</h3>
          <div v-for="group in relations" :key="group.title" class="skill-experts-relations">
            <span class="skill-experts-relation-title">{{ group.title }}</span>
            <span v-for="item in group.items" :key="item.id" class="tag-chip skill-experts-relation" :data-tone="tagTone(item.name)">
              <a :href="topicUrl(item.name)"><AtlasIcon name="hash" :size="12" />{{ item.name }}</a>
              <button type="button" class="tag-remove" :aria-label="t.skills.removeLink(item.name)" @click="unlink(item, group.kind, group.narrower)"><AtlasIcon name="close" :size="13" /></button>
            </span>
          </div>
          <div><Button variant="ghost" size="sm" type="button" :aria-expanded="linking" :aria-controls="`${uid}-link`" data-skill-link @click="linking = !linking"><AtlasIcon name="plus" :size="14" />{{ t.skills.addLink }}</Button></div>
          <InlineReveal :id="`${uid}-link`" v-model:open="linking">
            <div class="skill-experts-link">
              <OptionPills v-model="linkKind" :options="linkKinds" :label="t.skills.linkKind" hide-label />
              <TopicField v-model="linkText" :label="t.skills.linkTopic" :allow-new="false" :exclude="[topic.id]" :api="api" @take="(_name, other) => link(other)" />
              <p v-if="linkError" class="skill-experts-error" role="alert">{{ linkError }}</p>
            </div>
          </InlineReveal>
        </section>
        <section class="skill-experts-panel">
          <h3>{{ t.skills.pagesTitle }}</h3>
          <ul v-if="pages?.length" class="skill-experts-pages">
            <li v-for="page in pages" :key="page.id"><a :href="pageUrl(page)"><AtlasIcon name="page" :size="13" /><span>{{ page.title }}</span></a><small>{{ page.spaceName }}</small></li>
          </ul>
          <p v-else-if="pages" class="skill-experts-muted">{{ t.skills.noPages }}</p>
        </section>
      </aside>
    </div>
    <ConfirmDialog :open="!!merging" :title="t.skills.merge" :confirm-label="t.skills.merge" :busy="mergeBusy" :error="mergeError" @confirm="merge" @cancel="merging = null">
      <p>{{ merging && t.skills.mergeConfirm(merging.name, topic.name) }}</p>
    </ConfirmDialog>
  </section>
  <LoadingSkeleton v-else-if="pending" class="skill-experts-pending" :rows="2" row-height="64px" :label="t.skills.loading" />
</template>

<style scoped>
.skill-experts { display: grid; gap: 10px; container-type: inline-size; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.skill-experts-head { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; font-size: 13px; }
.skill-experts-about { color: var(--muted); }
.skill-experts-actions { display: inline-flex; flex-wrap: wrap; gap: 6px; margin-left: auto; }
.skill-experts-similar { display: flex; flex-basis: 100%; flex-wrap: wrap; align-items: center; justify-content: flex-end; gap: 4px; color: var(--muted); }
.skill-experts-add { display: grid; gap: 10px; padding: 12px 14px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); }
.skill-experts-link { display: grid; gap: 10px; padding-top: 4px; }
.skill-experts-body { display: grid; grid-template-columns: minmax(0, 1fr) minmax(220px, 280px); align-items: start; gap: 12px; }
@container (max-width: 640px) { .skill-experts-body { grid-template-columns: minmax(0, 1fr); } .skill-experts-actions { margin-left: 0; } .skill-experts-similar { justify-content: flex-start; } }
/* The people as the board shows a contact (boardCard.css): a card with the whole contact, its level and why. */
.skill-experts-people { display: grid; gap: 10px; min-width: 0; }
.skill-experts-items { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 260px), 1fr)); gap: 10px; margin: 0; padding: 0; list-style: none; }
.skill-person { display: flex; flex-direction: column; gap: 8px; min-width: 0; padding: 12px 14px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface);
  box-shadow: var(--refresh-card-shadow); content-visibility: auto; contain-intrinsic-size: auto 150px;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .skill-person:hover { border-color: color-mix(in srgb, var(--accent) 35%, var(--line)); box-shadow: var(--refresh-card-shadow-hover); } }
.skill-person-level { display: flex; align-items: center; gap: 8px; min-width: 0; padding-top: 8px; border-top: 1px solid color-mix(in srgb, var(--line) 60%, transparent); font-size: 13px; }
.skill-person-level strong { font-weight: 650; }
.skill-experts-rate { margin-left: auto; color: var(--muted); }
.skill-experts-why { margin: 0; color: var(--muted); font-size: 12.5px; }
.skill-experts-why:empty { display: none; }
.skill-experts-more { justify-self: start; }
.skill-experts-side { display: grid; gap: 10px; min-width: 0; }
.skill-experts-panel { display: grid; gap: 8px; min-width: 0; padding: 12px 14px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); }
.skill-experts-panel > h3 { margin: 0; color: var(--muted); font-size: 12px; font-weight: 650; }
/* The levels' spread: five columns, their height the share of the most frequent level. */
.skill-spread { display: grid; grid-template-columns: repeat(5, 1fr); align-items: end; gap: 6px; height: 64px; }
.skill-spread-column { display: grid; grid-template-rows: 1fr auto; justify-items: center; gap: 2px; height: 100%; }
.skill-spread-column small { color: var(--muted); font-size: 11px; }
.skill-spread-bar { align-self: end; width: 100%; height: calc(var(--share) * 100%); min-height: 2px; border-radius: 4px 4px 2px 2px; background: var(--accent); transform-origin: bottom;
  animation: atlas-bar-rise var(--atlas-motion-slow) var(--atlas-ease-out) both; }
@media (prefers-reduced-motion: reduce) { .skill-spread-bar { animation: none; } }
:global([data-atlas-reduced-motion]) .skill-spread-bar { animation: none; }
.skill-experts-relations { display: flex; flex-wrap: wrap; align-items: center; gap: 5px; font-size: 12.5px; }
.skill-experts-relation-title { width: 100%; color: var(--muted); font-size: 11.5px; }
.skill-experts-relation a { display: inline-flex; align-items: center; gap: 4px; color: inherit; text-decoration: none; }
.skill-experts-pages { display: grid; gap: 4px; margin: 0; padding: 0; list-style: none; font-size: 13px; }
.skill-experts-pages li { display: grid; min-width: 0; }
.skill-experts-pages a { display: inline-flex; align-items: center; gap: 6px; min-width: 0; color: var(--accent); text-decoration: none; }
.skill-experts-pages a > span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.skill-experts-pages small, .skill-experts-muted { margin: 0; color: var(--muted); font-size: 12px; }
@media (hover: hover) and (pointer: fine) { .skill-experts-pages a:hover { text-decoration: underline; } }
.skill-experts-error { margin: 0; color: var(--error); font-size: 12.5px; }
</style>
