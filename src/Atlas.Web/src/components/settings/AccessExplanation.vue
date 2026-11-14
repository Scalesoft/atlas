<script setup lang="ts">
import t from "../../i18n/settings-grants";
import { computed } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import AccessLevelBadge from "./AccessLevelBadge.vue";
import StatusBadge from "./StatusBadge.vue";
import type { AccessPath, ChoiceOption } from "./types";

/**
 * "Proč má přístup": every path from a person to a target, as a sentence and as a chain
 * person → group/space administrators/role → target, with the one level each path gives and until
 * when. The highest level wins; a level includes every level below it.
 */
const props = withDefaults(
  defineProps<{
    subject: { name: string; kind: "user" | "group" };
    target: { name: string; icon?: string };
    paths: AccessPath[];
    /** The ordered level options (lowest first) the paths refer to. */
    options: ChoiceOption[];
    /** Operation conditions that apply on every path, e.g. audit and fresh sign-in. */
    conditions?: string[];
  }>(),
  { conditions: () => [] },
);

const rank = (level: string) => props.options.findIndex((option) => option.value === level);
const levelLabel = (level: string) => props.options[rank(level)]?.label ?? level;
const viaIcon: Record<AccessPath["via"], string> = { direct: "account", group: "users", spaceAdmin: "book-open", role: "shield-check" };

function sentence(path: AccessPath) {
  const name = props.subject.name;
  const until = path.until ? t.explanation.until(path.until) : "";
  if (path.via === "spaceAdmin" || path.via === "role") return t.explanation.automatic(name, path.scope, levelLabel(path.level), path.label);
  return path.via === "direct"
    ? t.explanation.direct(name, levelLabel(path.level), path.scope, until)
    : t.explanation.viaGroup(name, path.label, levelLabel(path.level), path.scope, until);
}
const summary = computed(() => {
  if (!props.paths.length) return t.explanation.none(props.subject.name, props.target.name);
  const best = props.paths.reduce((top, path) => (rank(path.level) > rank(top.level) ? path : top));
  const lower = props.options.slice(0, Math.max(0, rank(best.level))).map((option) => option.label);
  return t.explanation.summary(props.subject.name, levelLabel(best.level), lower.length ? t.list(lower) : undefined, props.paths.length);
});
</script>

<template>
  <div class="access-explanation" role="group" :aria-label="t.explanation.label(subject.name, target.name)">
    <p class="access-summary"><AtlasIcon name="user-check" :size="17" />{{ summary }}</p>
    <ol v-if="paths.length" class="access-paths">
      <li v-for="(path, position) in paths" :key="`${path.via}-${path.label}-${path.scope}`" :style="{ '--i': position }">
        <div class="access-chain" aria-hidden="true">
          <span class="access-node"><AtlasIcon :name="subject.kind === 'group' ? 'users' : 'account'" :size="15" />{{ subject.name }}</span>
          <AtlasIcon name="arrow-right" :size="14" class="access-arrow" />
          <span v-if="path.via !== 'direct'" class="access-node is-via"><AtlasIcon :name="viaIcon[path.via]" :size="15" />{{ path.label }}</span>
          <AtlasIcon v-if="path.via !== 'direct'" name="arrow-right" :size="14" class="access-arrow" />
          <span class="access-node is-target"><AtlasIcon :name="target.icon ?? 'lock'" :size="15" />{{ target.name }}</span>
        </div>
        <p class="access-sentence">{{ sentence(path) }}</p>
        <div class="access-level">
          <AccessLevelBadge :options="options" :value="path.level" :tone="path.locked ? 'neutral' : 'accent'" />
          <StatusBadge v-if="path.locked" tone="neutral" icon="lock" size="sm" :label="t.explanation.automaticBadge" />
          <StatusBadge v-if="path.until" tone="info" icon="calendar" size="sm" :label="t.explanation.untilBadge(path.until)" />
        </div>
        <p v-if="path.note" class="access-note">{{ path.note }}</p>
      </li>
    </ol>
    <ul v-if="conditions.length" class="access-conditions">
      <li v-for="condition in conditions" :key="condition"><AtlasIcon name="scroll-text" :size="14" />{{ condition }}</li>
    </ul>
    <slot />
  </div>
</template>

<style scoped>
.access-explanation { display: grid; gap: 12px; min-width: 0; }
.access-summary { display: flex; align-items: flex-start; gap: 8px; margin: 0; font-size: 14px; font-weight: 600; line-height: 1.5; }
.access-summary > svg { flex-shrink: 0; margin-top: 2px; color: var(--accent); }
.access-paths { display: grid; gap: 10px; margin: 0; padding: 0; list-style: none; }
.access-paths > li {
  display: grid;
  gap: 8px;
  padding: 12px 14px;
  border: 1px solid var(--line);
  border-radius: var(--st-radius-sm);
  background: color-mix(in srgb, var(--canvas) 50%, var(--surface));
  animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) both;
  animation-delay: calc(var(--i) * var(--st-stagger));
}
.access-chain { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
.access-node { display: inline-flex; align-items: center; gap: 6px; padding: 4px 10px; border-radius: 999px; font-size: 12.5px; font-weight: 600; background: var(--surface); border: 1px solid var(--line); }
.access-node.is-via { color: var(--accent); background: var(--accent-soft); border-color: transparent; }
.access-node.is-target { color: var(--text); }
.access-arrow { color: var(--muted); }
.access-sentence { margin: 0; font-size: 13.5px; line-height: 1.55; }
.access-level { display: flex; flex-wrap: wrap; gap: 6px; }
.access-note { margin: 0; color: var(--muted); font-size: 12.5px; }
.access-conditions { display: grid; gap: 4px; margin: 0; padding: 0; list-style: none; color: var(--muted); font-size: 12.5px; }
.access-conditions li { display: flex; align-items: center; gap: 6px; }
</style>
