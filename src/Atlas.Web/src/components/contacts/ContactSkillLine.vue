<script setup lang="ts">
import { onMounted, ref, useId } from "vue";
import t from "../../i18n/contacts-ui";
import { topicUrl } from "../../contacts/contactLinking";
import { shownLevel, skillApi as defaultApi, type ContactSkill, type SkillApi } from "../../contacts/skillApi";
import AtlasIcon from "../AtlasIcon.vue";
import InlineReveal from "../settings/InlineReveal.vue";
import SkillRatePanel from "./SkillRatePanel.vue";
import SkillBars from "./SkillBars.vue";

/**
 * „Umí“ of one person where Atlas shows the person in Vue (the page's contact editor, the contact's detail in the dock): the reader card's
 * row (contacts plugin, readerSkills) for the places the plugin does not draw. Loaded after the contact, kept hidden until it answers, so
 * nothing jumps; a topic leads to who else knows it, Ohodnotit reveals the rating beneath (InlineReveal: the line sits in a panel already,
 * a second popover anchored in it would close with it). Nothing for a person without topics but
 * Přidat znalost. Why not SkillRatePanel: that edits; this is the one line a person's card has. `api` is the gallery's.
 */
/** `bars`: the person's overview (SkillBars) in place of the chips, where there is room for it (the dock's detail, the graph's person). */
const props = withDefaults(defineProps<{ contactId: string; limit?: number; bars?: boolean; name?: string; api?: Pick<SkillApi, "contact" | "rate" | "topics"> }>(), { limit: 6, bars: false, name: "", api: undefined });
const skills = ref<ContactSkill[] | null>(null);
onMounted(async () => {
  try { skills.value = (await (props.api ?? defaultApi).contact(props.contactId)).skills; }
  catch { skills.value = null; /* The card stays without the line. */ }
});
const dots = (skill: ContactSkill) => Math.round(shownLevel(skill) ?? 0);
const rating = ref(false);
const panelId = useId();
</script>

<template>
  <div v-if="skills" class="contact-skill-block">
  <div class="contact-skill-line" data-contact-skills>
    <span class="contact-skill-line-title"><AtlasIcon name="lightbulb" :size="14" />{{ t.skills.title }}</span>
    <a v-for="skill in bars ? [] : skills.slice(0, limit)" :key="skill.tagId" class="contact-skill-line-chip" :href="topicUrl(skill.name)" :title="skill.selfNote ?? undefined">
      {{ skill.name }}<span v-if="dots(skill)" class="contact-skill-line-dots" aria-hidden="true"><i v-for="n in 5" :key="n" :class="{ on: n <= dots(skill) }" /></span>
    </a>
    <span v-if="!bars && skills.length > limit" class="contact-skill-line-more">+{{ skills.length - limit }}</span>
    <button type="button" class="contact-skill-line-rate" :aria-expanded="rating" :aria-controls="panelId" @click="rating = !rating">
      {{ skills.length ? t.skills.rateShort : t.skills.addSkill }}
    </button>
  </div>
  <SkillBars v-if="bars" class="contact-skill-bars" :skills="skills" :name="name" />
  <InlineReveal :id="panelId" v-model:open="rating">
    <div class="contact-skill-rating"><SkillRatePanel :contact-id="contactId" :api="api" @saved="skills = $event.skills" /></div>
  </InlineReveal>
  </div>
</template>

<style scoped>
.contact-skill-block { display: grid; min-width: 0; }
.contact-skill-rating { padding-top: 10px; }
.contact-skill-bars { padding-top: 8px; }
.contact-skill-line { --tag-ink: var(--accent); display: flex; flex-wrap: wrap; align-items: center; gap: 5px; min-width: 0; font-size: 12px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.contact-skill-line-title { display: inline-flex; align-items: center; gap: 5px; margin-right: 2px; color: var(--muted); }
.contact-skill-line-chip, .contact-skill-line-more { display: inline-flex; align-items: center; gap: 6px; min-width: 0; max-width: 100%; padding: 2px 8px; border: 1px solid color-mix(in srgb, var(--tag-ink) 22%, transparent);
  border-radius: 7px; background: color-mix(in srgb, var(--tag-ink) 8%, var(--surface)); color: var(--tag-ink); font-weight: 600; line-height: 1.5; text-decoration: none;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.contact-skill-line-more { border-style: dashed; background: transparent; color: var(--muted); font-weight: 500; }
.contact-skill-line-dots { display: inline-flex; gap: 2px; }
.contact-skill-line-dots i { width: 5px; height: 5px; border-radius: 999px; background: color-mix(in srgb, var(--tag-ink) 22%, transparent); }
.contact-skill-line-dots i.on { background: var(--tag-ink); }
.contact-skill-line-rate { margin-left: auto; padding: 0 4px; border: 0; border-radius: 5px; background: transparent; color: var(--accent); font: inherit; cursor: pointer; }
@media (hover: hover) and (pointer: fine) {
  .contact-skill-line-chip:hover { border-color: color-mix(in srgb, var(--tag-ink) 55%, transparent); background: color-mix(in srgb, var(--tag-ink) 15%, var(--surface)); }
  .contact-skill-line-rate:hover { text-decoration: underline; }
}
.contact-skill-line-chip:focus-visible, .contact-skill-line-rate:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
</style>
