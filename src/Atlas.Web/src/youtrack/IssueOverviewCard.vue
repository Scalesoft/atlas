<script setup lang="ts">
import t from "../i18n/youtrack";
import type { Editor } from "@tiptap/core";
import { computed, onBeforeUnmount, shallowRef, toRaw } from "vue";
import AtlasIcon from "../components/AtlasIcon.vue";
import OverviewActions from "../overview/OverviewActions.vue";
import type { OverviewCardAction } from "../overview/overviewKinds";
import { documentIssues, type OverviewIssue } from "./issueOverview";
import "../components/settings/settings.css";

/**
 * The card of the issues overview (issueOverview.ts): every YouTrack issue of the page or the board once, its key as a link, its
 * title and its state as stored. In the editor it follows the document (`editor`); a read page or a board gives it the issues.
 * The kind's actions (copy the keys, the CSV) are in its foot, as every overview card's. Why not a YouTrack search: this lists only
 * what the page shows, also in a draft nobody published, with no request to YouTrack.
 */
const props = defineProps<{ editor?: Editor; issues?: readonly OverviewIssue[]; actions?: readonly OverviewCardAction[] }>();
const editor = props.editor && toRaw(props.editor);
const live = shallowRef(editor ? documentIssues(editor.state.doc) : []);
// A new list only when the issues changed: typing elsewhere on the page redraws nothing.
const refresh = () => { const next = documentIssues(editor!.state.doc); if (JSON.stringify(next) !== JSON.stringify(live.value)) live.value = next; };
editor?.on("update", refresh);
onBeforeUnmount(() => editor?.off("update", refresh));
const issues = computed(() => props.issues ?? live.value);
</script>

<template>
  <section class="issue-overview settings-ui" contenteditable="false" :aria-label="t.label" data-issue-overview>
    <header class="issue-overview-head"><span class="issue-overview-label"><AtlasIcon name="list-checks" :size="15" />{{ t.label }} · {{ issues.length }}</span></header>
    <p v-if="!issues.length" class="issue-overview-empty">{{ t.empty }}</p>
    <ul v-else>
      <li v-for="issue in issues" :key="issue.key" data-overview-item data-issue>
        <a v-if="issue.url" :href="issue.url" target="_blank" rel="noopener noreferrer">{{ issue.key }}</a><strong v-else>{{ issue.key }}</strong>
        <span class="issue-overview-title">{{ issue.title }}</span>
        <span v-if="issue.state" class="issue-overview-state">{{ issue.state }}</span>
      </li>
    </ul>
    <footer v-if="issues.length && actions?.length" class="issue-overview-foot"><OverviewActions :actions="actions" /></footer>
  </section>
</template>

<style scoped>
.issue-overview { display: grid; gap: .625rem; margin: .75rem 0 .5rem; padding: 1rem 1.125rem; border: 1px solid var(--line); border-radius: var(--atlas-radius-lg, 12px);
  background: var(--paper, var(--surface)); box-shadow: 0 1px 2px color-mix(in srgb, var(--text) 5%, transparent); font-size: .875rem; line-height: 1.45; user-select: none; }
.issue-overview-head { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: .25rem .625rem; min-height: 1.75rem; }
.issue-overview-label { display: inline-flex; align-items: center; gap: .375rem; font-weight: 600; color: var(--muted, var(--text-dim)); }
.issue-overview-foot { display: flex; padding-top: .75rem; border-top: 1px solid color-mix(in srgb, var(--line) 70%, transparent); }
.issue-overview-empty { margin: 0; color: var(--muted, var(--text-dim)); }
.issue-overview ul { display: grid; gap: .125rem; margin: 0; padding: 0; list-style: none; }
.issue-overview li { display: flex; flex-wrap: wrap; align-items: baseline; gap: 0 .5rem; overflow-wrap: anywhere; }
.issue-overview a { font-weight: 600; color: var(--accent-strong, var(--accent)); text-decoration: none; user-select: text; }
.issue-overview a:hover { text-decoration: underline; }
.issue-overview-title { flex: 1 1 12rem; min-width: 0; }
.issue-overview-state { padding: 0 .5rem; border-radius: 999px; background: var(--hover); color: var(--muted, var(--text-dim)); font-size: .75rem; font-weight: 600; }
</style>
