<script setup lang="ts">
import t from "../../i18n/settings-shell";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { foldText } from "../../lib/foldText";
import { useSearchKeys } from "../../lib/searchKeys";
import { listKeys } from "../../keyboard/navigation";
import { motionMs } from "../../motion/aiMotion";
import { disclosureHooks } from "./disclosure";
import SettingsNav from "./SettingsNav.vue";
import { useOptionalSettingsRoute } from "./useSettingsRoute";
import type { SettingsNavGroup, SettingsNavItem } from "./types";
import "./settings.css";
import "./settings-tailwind.css";

/**
 * One frame for every settings level (space and global): heading, a rail of sections on wide
 * screens (on narrow ones the sections are in the application drawer), content on the right. Width is measured on the
 * shell itself (container query), so it adapts to the application sidebar as well as to the viewport.
 */
const props = withDefaults(
  defineProps<{
    title: string;
    subtitle?: string;
    eyebrow?: string;
    icon?: string;
    groups: SettingsNavGroup[];
    navLabel: string;
    /** Return false to stop a section change (unsaved changes); the shell then leaves the link alone. */
    guard?: (item: SettingsNavItem) => boolean;
    /** Id of the page heading, for a shell that moves focus to it after a navigation (the profile). */
    titleId?: string;
    /** The current section when the application shell owns the address (the profile); otherwise the settings router says. */
    currentPath?: string;
  }>(),
  { subtitle: undefined, eyebrow: undefined, icon: "settings", guard: undefined, titleId: undefined, currentPath: undefined },
);
const router = useOptionalSettingsRoute();
/** The application rail hosts the sections when it offers a place for them (refresh 2.5); the gallery and tests keep the inner rail. */
const railTarget = ref<string>();
onMounted(() => { if (document.getElementById("atlas-settings-rail")) railTarget.value = "#atlas-settings-rail"; });
/** Below 1025 px the application rail is a closed drawer, so the surface keeps its own header and its actions (the way
 * back) there; the sections are in that drawer, like everywhere else in the app. */
const narrowQuery = window.matchMedia?.("(max-width: 1024px)");
const narrow = ref(narrowQuery?.matches ?? false);
const narrowChanged = () => { narrow.value = narrowQuery?.matches ?? false; };
onMounted(() => narrowQuery?.addEventListener?.("change", narrowChanged));
onBeforeUnmount(() => narrowQuery?.removeEventListener?.("change", narrowChanged));
/**
 * Switching the theme or the accent (the profile previews it live) fades the colours of the application shell
 * (refresh-theme.css §1), but the cards and the text inside it were repainted in one frame, and a card fading to dark
 * under text that is already light would even be unreadable halfway. While the change runs, the shell lets every colour
 * inside it ease over the same token (`data-theming`, settings.css), and only then: a permanent colour transition on every
 * card and line of text would also slow down the hover and state changes that must stay fast.
 *
 * It is a transition on every element, so it is for a page of a size where that costs nothing: measured on the profile (117 elements)
 * the switch runs at 16 ms a frame as without it, on the people table (1,080 elements) its first frame took 333 ms and the frames
 * averaged 34 ms against 17 (and a quarter of the speed of the machine made it 2.5 s). A long page changes its colours at once,
 * as it did, and the fade is left to the application shell around it.
 */
const THEMING_MAX_ELEMENTS = 450;
const shell = ref<HTMLElement>();
let themeObserver: MutationObserver | undefined;
let themeTimer: ReturnType<typeof setTimeout> | undefined;
onMounted(() => {
  if (typeof MutationObserver !== "function") return;
  themeObserver = new MutationObserver(() => {
    const node = shell.value;
    if (!node || node.getElementsByTagName("*").length > THEMING_MAX_ELEMENTS) return;
    node.setAttribute("data-theming", "");
    clearTimeout(themeTimer);
    themeTimer = setTimeout(() => node.removeAttribute("data-theming"), motionMs(node, "--atlas-motion-slow") * 1.5);
  });
  themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ["data-mode", "data-accent"] });
});
onBeforeUnmount(() => { themeObserver?.disconnect(); clearTimeout(themeTimer); });
/** „Hledat sekci“ narrows the sections by their names and group names, without diacritics. */
const query = ref("");
const visibleGroups = computed(() => {
  const needle = foldText(query.value.trim());
  if (!needle) return props.groups;
  return props.groups
    .map((group) => ({ ...group, items: foldText(group.label ?? "").includes(needle) ? group.items : group.items.filter((item) => foldText(item.label).includes(needle)) }))
    .filter((group) => group.items.length);
});

/**
 * „Hledat sekci“: once something is typed the first section found is preselected (`data-first` on its link, set here since the links
 * are the nav's), Enter opens it, ↓ steps onto it (keyboard pass), the first Esc empties the search and the next one goes on to the
 * application (P3, P8). Tab goes on to the links.
 */
const rail = ref<HTMLElement>();
const firstItem = computed(() => (query.value.trim() ? visibleGroups.value[0]?.items[0] : undefined));
const linkOf = (item: SettingsNavItem | undefined) => (item ? rail.value?.querySelector<HTMLElement>(`nav a[data-path="${CSS.escape(item.path)}"]`) : null);
function markFirst() {
  rail.value?.querySelectorAll("[data-first]").forEach((link) => link.removeAttribute("data-first"));
  linkOf(firstItem.value)?.setAttribute("data-first", "");
}
watch(firstItem, () => void nextTick(markFirst), { flush: "post" });
const keys = useSearchKeys({ text: () => query.value, clear: () => { query.value = ""; }, tab: false,
  take: () => { const link = linkOf(firstItem.value); if (!link) return false; link.click(); } });
function railSearchKeys(event: KeyboardEvent) {
  if (keys.onKeydown(event) || event.key !== "ArrowDown") return;
  const first = rail.value?.querySelector<HTMLElement>("nav a[href]");
  if (!first) return;
  event.preventDefault();
  first.focus();
}
function navigate(item: SettingsNavItem, event: MouseEvent) {
  if (item.disabled || (props.guard && !props.guard(item))) event.preventDefault();
}
</script>

<template>
  <div ref="shell" class="settings-shell settings-ui" :data-rail-outside="railTarget ? 'true' : undefined">
    <Teleport v-if="railTarget" :to="railTarget">
      <div ref="rail" class="settings-ui settings-rail-outside" @keydown="listKeys">
        <label class="settings-rail-search">
          <span class="st-sr-only">{{ t.shell.searchSections }}</span>
          <AtlasIcon name="search" :size="15" />
          <input v-model="query" type="search" :placeholder="t.shell.searchSections" autocomplete="off" @keydown="railSearchKeys" />
        </label>
        <SettingsNav :groups="visibleGroups" :label="navLabel" :current-path="currentPath" @navigate="navigate" />
        <Transition name="st-fade"><p v-if="!visibleGroups.length" class="settings-rail-empty">{{ t.shell.noMatch }}</p></Transition>
        <slot name="rail-footer" />
      </div>
    </Teleport>
    <header class="settings-shell-header">
      <span class="st-icon-tile settings-shell-icon"><AtlasIcon :name="icon" :size="19" /></span>
      <div class="settings-shell-heading">
        <h1 :id="titleId" :tabindex="titleId ? -1 : undefined">{{ title }}</h1>
        <!-- Scope and purpose on one quiet line: the content below matters more than the header. -->
        <p v-if="eyebrow || subtitle" class="settings-shell-subtitle"><span v-if="eyebrow" class="settings-shell-eyebrow">{{ eyebrow }}</span><template v-if="eyebrow && subtitle"> · </template>{{ subtitle }}</p>
      </div>
      <!-- With the sections in the application rail, the rail carries the way back as well, and a visually hidden
           header must not hold anything focusable. -->
      <div v-if="$slots.actions && (!railTarget || narrow)" class="settings-shell-actions"><slot name="actions" /></div>
    </header>

    <div class="settings-shell-body">
      <aside v-if="!railTarget" class="settings-shell-rail" @keydown="listKeys">
        <SettingsNav :groups="groups" :label="navLabel" :current-path="currentPath" @navigate="navigate" />
        <slot name="rail-footer" />
      </aside>
      <div class="settings-shell-content">
        <!-- The result line unfolds with its margin and folds away again (the disclosure of a field's error), so the page
             below glides instead of jumping by the banner's height. A new message replaces the old one inside the wrapper. -->
        <Transition :css="false" v-bind="disclosureHooks"><div v-if="$slots.notice" class="settings-shell-notice"><slot name="notice" /></div></Transition>
        <slot />
      </div>
    </div>

  </div>
</template>

<style scoped>
/* The section Enter opens, while „Hledat sekci“ has the focus (P8). */
.settings-rail-outside:has(.settings-rail-search input:focus) :deep(.settings-nav-link[data-first]:not([aria-current="page"])) { background: var(--state-active-row-bg); box-shadow: var(--state-active-row-mark); }
.settings-shell {
  container: settings-shell / inline-size;
  width: 100%;
  max-width: none;
  min-width: 0;
  margin: 0;
  padding: 20px 24px 40px;
}
.settings-shell-header {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 16px;
  padding-bottom: 14px;
  border-bottom: 1px solid color-mix(in srgb, var(--line) 70%, transparent);
  animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out);
}
.settings-shell-icon { width: 38px; height: 38px; border-radius: var(--st-radius-sm); }
.settings-shell-heading { flex: 1; min-width: 0; }
.settings-shell-heading h1 { margin: 0; font-size: 20px; font-weight: 650; letter-spacing: -.02em; line-height: 1.25; }
.settings-shell-eyebrow { color: var(--text); font-weight: 600; }
.settings-shell-subtitle { margin: 2px 0 0; color: var(--muted); font-size: 12.5px; line-height: 1.45; }
.settings-shell-actions { display: flex; flex-wrap: wrap; gap: 8px; justify-content: flex-end; }

.settings-shell-body { display: block; }
.settings-shell-rail { display: none; }
.settings-shell-content { position: relative; min-width: 0; }
/* The banner's own bottom margin stays inside the wrapper, so the unfold measures and animates it too. While it unfolds
   the banner only fades: its rise would be clipped by the growing wrapper. */
.settings-shell-notice { display: flow-root; }
.settings-shell-notice > :deep(.result-banner) { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both; }

@container settings-shell (min-width: 780px) {
  .settings-shell-body { display: grid; grid-template-columns: 232px minmax(0, 1fr); gap: 24px; align-items: start; }
  .settings-shell-rail { display: grid; gap: 18px; position: sticky; top: calc(var(--atlas-topbar-height, 52px) + 12px); padding-top: 2px; }
}
/* The sections live in the application rail on a desktop, which also carries the scope and the way back, so the
   surface heading stays only for assistive technology there. Below 1025 px the rail is a closed drawer: the heading
   with its actions shows again and the sections are in the drawer. */
/* Centred in the workspace with the same gutter as every page frame, but capped by the settings measure
   (--st-frame-max, settings.css) and not by the 1600 px of a grid of cards: a settings page is rows of a label and a
   control and lists, and at 1,500 px a text field or a label 800 px from its control stops being readable. */
.settings-shell[data-rail-outside] { max-width: var(--st-frame-max, 1280px); margin-inline: auto; padding: 28px var(--atlas-page-gutter, 40px) 48px; }
.settings-shell[data-rail-outside] .settings-shell-body { display: block; }
@media (min-width: 1025px) {
  .settings-shell[data-rail-outside] .settings-shell-header {
    position: absolute; width: 1px; height: 1px; margin: -1px; padding: 0; overflow: hidden; clip-path: inset(50%); border: 0; white-space: nowrap;
  }
}
@media (max-width: 1024px) {
  .settings-shell[data-rail-outside] { padding: 20px 20px 48px; }
}
@media (max-width: 680px) {
  .settings-shell { padding: 16px 16px 72px; }
  .settings-shell-header { align-items: flex-start; flex-wrap: wrap; }
  /* Actions go below the heading on a phone so the title and subtitle keep the full width. */
  .settings-shell-actions { flex-basis: 100%; justify-content: flex-start; }
  .settings-shell-heading h1 { font-size: 19px; }
  .settings-shell-icon { width: 40px; height: 40px; }
}
</style>

<style>
/* The rail's sections (refresh 2.5) come with this lazy shell, not with the eager refresh.css. */
.atlas .settings-rail-outside:not(.md-editor):not(.md-editor *) { display: grid; gap: 10px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.atlas .settings-rail-search:not(.md-editor):not(.md-editor *) {
  display: flex;
  align-items: center;
  gap: 8px;
  height: 34px;
  margin: 0 0 2px;
  padding: 0 10px;
  border: 1px solid var(--input-line);
  border-radius: var(--radius-md);
  background: var(--surface);
  color: var(--muted);
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out);
}
.atlas .settings-rail-search:focus-within:not(.md-editor):not(.md-editor *) { outline: var(--state-focus-ring); outline-offset: var(--state-focus-offset); }
.atlas .settings-rail-search input:not(.md-editor):not(.md-editor *) { flex: 1; min-width: 0; border: 0; outline: 0; background: transparent; color: var(--text); font: inherit; font-size: 13px; }
.atlas .settings-rail-empty:not(.md-editor):not(.md-editor *) { margin: 4px 12px; color: var(--muted); font-size: 13px; }
.atlas .settings-sidebar-rail .settings-nav:not(.md-editor):not(.md-editor *) { gap: 10px; }
.atlas .settings-sidebar-rail .settings-nav-heading:not(.md-editor):not(.md-editor *) { margin: 0 0 2px; padding: 6px 8px 0; font-size: 11px; letter-spacing: 0.06em; }
.atlas .settings-sidebar-rail .settings-nav-group ul:not(.md-editor):not(.md-editor *) { gap: 1px; }
.atlas .settings-sidebar-rail .settings-nav-link:not(.md-editor):not(.md-editor *) {
  min-height: 30px;
  padding: 0 8px;
  gap: 10px;
  border-radius: 8px;
  color: var(--text);
  font-size: 13.5px;
  font-weight: 500;
}
@media (hover: hover) and (pointer: fine) { .atlas .settings-sidebar-rail .settings-nav-link:hover:not(.md-editor):not(.md-editor *) { background: var(--refresh-glass); } }
.atlas .settings-sidebar-rail .settings-nav-link[aria-current="page"]:not(.md-editor):not(.md-editor *) { background: var(--navigation-selected-bg); color: var(--text); font-weight: 650; }
.atlas .settings-sidebar-rail .settings-nav-link[aria-current="page"] .settings-nav-icon:not(.md-editor):not(.md-editor *) { color: var(--accent); }
/* The icon column of the rail is 18 px wide, as in the wiki navigation (the icon at the row's 8 px inset), so the labels
   start on the same line (46 px) after the switch and the icons stand under the headings above them. */
.atlas .settings-sidebar-rail .settings-nav-icon:not(.md-editor):not(.md-editor *) { width: 18px; color: var(--muted); }
.atlas .settings-sidebar-rail .profile-rail-action:not(.md-editor):not(.md-editor *) { justify-content: flex-start; width: 100%; margin-top: 4px; color: var(--muted); }
.atlas .settings-sidebar-rail > .settings-rail-outside:has([data-profile-logout]):not(.md-editor):not(.md-editor *) { flex: 1 0 auto; display: flex; flex-direction: column; }
.atlas .settings-sidebar-rail .settings-rail-outside > .profile-rail-action:not(.md-editor):not(.md-editor *) {
  min-height: 36px;
  margin-top: auto;
  padding-top: 8px;
  border-top: 1px solid var(--refresh-soft-line);
  border-radius: 0;
}

</style>
