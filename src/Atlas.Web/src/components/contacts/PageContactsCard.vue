<script setup lang="ts">
import t from "../../i18n/contacts-ui";
import type { Editor } from "@tiptap/core";
import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";
import { computed, onBeforeUnmount, shallowRef, toRaw } from "vue";
import { contactEmails, downloadContacts, pageContacts } from "../../contacts/pageContacts";
import { useToast } from "../../composables/useToast";
import type { OverviewCardAction, OverviewHost } from "../../overview/overviewKinds";
import OverviewActions from "../../overview/OverviewActions.vue";
import OverviewLevelSlider from "../../overview/OverviewLevelSlider.vue";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import "../settings/settings.css";

/**
 * The card of the contacts overview (contacts/contactOverview.ts), laid out as every overview card: its name and the level of the
 * contact cards (S Jména / M Karty / L Plné karty, the reader's own choice, OverviewLevelSlider) in its head, the people by organization,
 * the reader's actions on all of them in its foot — one vCard file, the e-mails to paste, the kind's `actions`. In the editor it follows
 * the document (`editor`); a read page or a board gives it the people (`contacts`) and its id (`pageId`). Without a page (an editor
 * without a host) the levels have no root to act on, so no switch. Why not ContactOverview: that one
 * lists the people of a space or the wiki from the server; this one only the page's own, also in a draft nobody published.
 */
const props = defineProps<{ editor?: Editor; host?: OverviewHost; contacts?: readonly MarkdownContact[]; pageId?: string; actions?: readonly OverviewCardAction[] }>();
const editor = props.editor && toRaw(props.editor);
const live = shallowRef(editor ? pageContacts(editor.state.doc) : []);
// A new list only when the people changed: typing elsewhere on the page redraws nothing (the contacts are the nodes' own objects).
const refresh = () => { const next = pageContacts(editor!.state.doc); if (next.length !== live.value.length || next.some((person, at) => person !== live.value[at])) live.value = next; };
editor?.on("update", refresh);
onBeforeUnmount(() => editor?.off("update", refresh));

const people = computed(() => props.contacts ?? live.value);
const page = computed(() => props.pageId ?? props.host?.pageId() ?? "");

const groups = computed(() => {
  const found: { organization: string; people: MarkdownContact[] }[] = [];
  for (const person of people.value) {
    const organization = person.organization ?? "";
    if (found.at(-1)?.organization === organization) found.at(-1)!.people.push(person); else found.push({ organization, people: [person] });
  }
  return found;
});
const emails = computed(() => contactEmails(people.value));
const toaster = useToast();
async function copyEmails() {
  try { await navigator.clipboard.writeText(emails.value); toaster.success(t.pageCard.copied); }
  catch { toaster.error(t.pageCard.copyFailed); }
}
</script>

<template>
  <section class="page-contacts settings-ui" :class="{ 'is-editor': editor }" contenteditable="false" :aria-label="t.pageCard.label" data-page-contacts>
    <header class="page-contacts-head">
      <span class="page-contacts-label"><AtlasIcon name="users" :size="15" />{{ t.pageCard.heading(people.length) }}</span>
      <OverviewLevelSlider v-if="people.length && page" kind="contacts" :scope="page" data-page-contacts-level />
    </header>
    <p v-if="!people.length" class="page-contacts-empty">{{ t.pageCard.empty }}</p>
    <div v-for="group in groups" :key="group.organization" class="page-contacts-group">
      <h4>{{ group.organization || t.pageCard.noOrganization }}</h4>
      <ul>
        <li v-for="person in group.people" :key="person.objectId ?? `${person.name}|${person.emails?.[0] ?? ''}`" data-page-contact>
          <strong>{{ person.name }}</strong><span v-if="person.title"> · {{ person.title }}</span>
          <a v-if="person.emails?.[0]" :href="`mailto:${person.emails[0]}`">{{ person.emails[0] }}</a>
        </li>
      </ul>
    </div>
    <footer v-if="people.length" class="page-contacts-actions">
      <Button variant="outline" size="sm" type="button" data-page-contacts-vcf @click="downloadContacts(people)"><AtlasIcon name="download" :size="14" />{{ t.pageCard.download }}</Button>
      <Button v-if="emails" variant="outline" size="sm" type="button" data-page-contacts-emails @click="copyEmails"><AtlasIcon name="copy" :size="14" />{{ t.pageCard.copyEmails }}</Button>
      <OverviewActions :actions="actions" />
    </footer>
  </section>
</template>

<style scoped>
.page-contacts { display: grid; gap: .75rem; margin: .75rem 0 1.25rem; padding: 1rem 1.125rem; border: 1px solid var(--line); border-radius: var(--atlas-radius-lg, 12px);
  background: var(--paper, var(--surface)); box-shadow: 0 1px 2px color-mix(in srgb, var(--text) 5%, transparent); font-size: .875rem; line-height: 1.45; }
/* In the editor the card is no text of the page; read, its names copy. */
.page-contacts.is-editor { user-select: none; }
.page-contacts-head { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: .5rem .75rem; }
.page-contacts-label { display: inline-flex; align-items: center; gap: .375rem; font-weight: 600; color: var(--muted, var(--text-dim)); }
.page-contacts-empty { margin: 0; color: var(--muted, var(--text-dim)); }
.page-contacts-group h4 { margin: 0 0 .25rem; font-size: .75rem; font-weight: 600; letter-spacing: .02em; color: var(--muted, var(--text-dim)); }
.page-contacts-group ul { display: grid; gap: .125rem; margin: 0; padding: 0; list-style: none; }
.page-contacts-group li { display: flex; flex-wrap: wrap; align-items: baseline; gap: 0 .375rem; overflow-wrap: anywhere; }
.page-contacts-group a { margin-left: auto; color: var(--accent-strong, var(--accent)); text-decoration: none; user-select: text; }
.page-contacts-group a:hover { text-decoration: underline; }
.page-contacts-actions { display: flex; flex-wrap: wrap; gap: .375rem; padding-top: .75rem; border-top: 1px solid color-mix(in srgb, var(--line) 70%, transparent); }
</style>
