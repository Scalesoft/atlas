<script setup lang="ts">
import { computed, nextTick, onMounted, ref, useId, watch } from "vue";
import CommandIcon from "../CommandIcon.vue";
import { useOptionalSettingsRoute } from "./useSettingsRoute";
import type { SettingsNavGroup, SettingsNavItem } from "./types";

// Real navigation: links with aria-current, grouped under headings, one Tab stop per link.
// The shell may intercept a click (unsaved changes) through `navigate`.
// Links and group headings match the app sidebar (nav-link, section heading), so settings
// read as the same navigation family. A zero count is left out: "0" next to every empty
// section was noise, and a problem always carries a non-zero count with a tone.
const props = defineProps<{ groups: SettingsNavGroup[]; label: string; currentPath?: string }>();
const emit = defineEmits<{ navigate: [item: SettingsNavItem, event: MouseEvent] }>();
const router = useOptionalSettingsRoute();
const uid = useId();

function href(item: SettingsNavItem) {
  return router ? router.href(item.path) : `#${item.path}`;
}
function active(item: SettingsNavItem) {
  const current = props.currentPath ?? router?.route.value.path ?? "";
  return item.exact ? current === item.path : current === item.path || current.startsWith(`${item.path}/`);
}
function countText(item: SettingsNavItem) {
  return item.countLabel ? `${item.count}, ${item.countLabel}` : String(item.count);
}

// The rail scrolls (the application rail, the sheet), so on a short window the current section can sit below its visible part,
// under the fade. It is brought into view when the nav arrives (a deep link, a reload, coming from elsewhere) and when the current
// section changes; the reader scrolling the rail is never undone, since nothing else moves it. The scroll is the rail's own and is not
// animated (a scrollIntoView could scroll the page too), and it takes no focus.
const root = ref<HTMLElement>();
const currentPath = computed(() => props.groups.flatMap((group) => group.items).find(active)?.path);
function scrollingAncestor(node: HTMLElement) {
  for (let element = node.parentElement; element; element = element.parentElement) {
    const overflow = getComputedStyle(element).overflowY;
    if (overflow === "auto" || overflow === "scroll") return element;
  }
  return null;
}
function revealCurrent() {
  const row = root.value?.querySelector<HTMLElement>('.settings-nav-link[aria-current="page"]');
  const box = row ? scrollingAncestor(row) : null;
  if (!row || !box) return;
  const style = getComputedStyle(box);
  const above = Number.parseFloat(style.scrollPaddingTop) || 0;
  const below = Number.parseFloat(style.scrollPaddingBottom) || 0;
  const rect = row.getBoundingClientRect();
  const view = box.getBoundingClientRect();
  if (rect.top < view.top + above) box.scrollTop -= view.top + above - rect.top;
  else if (rect.bottom > view.bottom - below) box.scrollTop += rect.bottom - (view.bottom - below);
}
onMounted(() => void nextTick(revealCurrent));
watch(currentPath, () => void nextTick(revealCurrent));
</script>

<template>
  <!-- „Hledat sekci“ narrows the groups and their links: what drops out fades while the rest glides up, and what comes back
       fades in, instead of the rail rebuilding itself in one frame on every keystroke. -->
  <nav ref="root" class="settings-nav" :aria-label="label">
    <TransitionGroup name="settings-nav-shift">
    <div v-for="group in groups" :key="group.id" class="settings-nav-group">
      <h2 v-if="group.label" :id="`${uid}-${group.id}`" class="settings-nav-heading">{{ group.label }}</h2>
      <ul :aria-labelledby="group.label ? `${uid}-${group.id}` : undefined">
        <TransitionGroup name="settings-nav-shift">
        <li v-for="item in group.items" :key="item.path">
          <a
            class="settings-nav-link"
            :href="href(item)"
            :aria-current="active(item) ? 'page' : undefined"
            :aria-disabled="item.disabled || undefined"
            :data-path="item.path"
            @click="emit('navigate', item, $event)"
          >
            <span class="settings-nav-icon"><CommandIcon :name="item.icon" :size="17" /></span>
            <span class="settings-nav-label">{{ item.label }}</span>
            <!-- Refresh 2.5 rail: a problem is a dot of its tone, an ordinary count or a state a quiet word on the right. -->
            <span v-if="item.count && (item.tone === 'warning' || item.tone === 'danger')" class="settings-nav-count settings-nav-alert" :data-tone="item.tone">
              <span class="settings-nav-dot" aria-hidden="true" />
              <span class="st-sr-only">{{ countText(item) }}</span>
            </span>
            <span v-else-if="item.count" class="settings-nav-count" :data-tone="item.tone">
              <span aria-hidden="true">{{ item.count }}</span>
              <span class="st-sr-only">{{ countText(item) }}</span>
            </span>
            <span v-if="item.note" class="settings-nav-note">{{ item.note }}</span>
          </a>
        </li>
        </TransitionGroup>
      </ul>
    </div>
    </TransitionGroup>
  </nav>
</template>

<style scoped>
.settings-nav { position: relative; display: grid; gap: 14px; }
.settings-nav-group ul { position: relative; list-style: none; margin: 0; padding: 0; display: grid; gap: 2px; }
.settings-nav-shift-enter-active { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both; }
/* A leaving link or group keeps its place but not its room, so what follows glides up while it fades. */
.settings-nav-shift-leave-active { position: absolute; inset-inline: 0; animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-in) both; pointer-events: none; }
.settings-nav-shift-move { transition: transform var(--atlas-motion-base) var(--atlas-ease-out); }
@media (prefers-reduced-motion: reduce) { .settings-nav-shift-move { transition-property: opacity; } }
[data-atlas-reduced-motion] .settings-nav-shift-move { transition-property: opacity; }
.settings-nav-heading {
  margin: 0 0 6px;
  /* The heading starts where the icons of the rows start: the row's 10 px and the 2.5 px a 17 px icon leaves in its 22 px column. */
  padding: 0 12.5px;
  color: var(--muted);
  font-size: 10px;
  font-weight: 700;
  letter-spacing: .09em;
  text-transform: uppercase;
}
.settings-nav-link {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: var(--control-h);
  padding: 8px 10px;
  border-radius: var(--radius-sm);
  color: var(--muted);
  text-decoration: none;
  font-size: 13px;
  font-weight: 550;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast);
}
@media (hover: hover) and (pointer: fine) {
  .settings-nav-link:hover { background: var(--hover); color: var(--text); }
}
.settings-nav-link[aria-disabled="true"] { opacity: .55; cursor: not-allowed; }
.settings-nav-link:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; --focus-offset: 1px; }
.settings-nav-link[aria-current="page"] { background: var(--navigation-selected-bg); color: var(--accent); }
.settings-nav-icon { display: grid; place-items: center; width: 22px; color: var(--muted); transition: color var(--atlas-motion-fast); }
.settings-nav-link[aria-current="page"] .settings-nav-icon { color: var(--accent); }
.settings-nav-label { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.settings-nav-count, .settings-nav-note {
  flex-shrink: 0;
  color: var(--muted);
  font-size: 11.5px;
  font-weight: 500;
  font-variant-numeric: tabular-nums;
}
.settings-nav-count[data-tone="success"] { color: var(--success); }
.settings-nav-alert { display: inline-flex; align-items: center; justify-content: center; width: 20px; flex-shrink: 0; }
.settings-nav-dot { width: 8px; height: 8px; border-radius: 50%; background: var(--warning); box-shadow: 0 0 0 3px color-mix(in srgb, var(--warning) 16%, transparent); }
.settings-nav-alert[data-tone="danger"] .settings-nav-dot { background: var(--error); box-shadow: 0 0 0 3px color-mix(in srgb, var(--error) 16%, transparent); }
</style>
