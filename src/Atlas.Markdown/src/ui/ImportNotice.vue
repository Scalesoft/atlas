<script setup lang="ts">
/**
 * Non-blocking report of constructs a paste or `.md` drop kept as literal text. It floats in the
 * editor's message stack and is announced once as a status. It never hides by itself: a timer cannot
 * tell whether a keyboard or screen reader user has reached it yet (WCAG 2.2.1). The close button
 * dismisses it, and the next import with literal text replaces it.
 */
import { computed } from 'vue'
import type { MarkdownImportDegradedPart } from '../core'
import { CloseIcon } from './icons'
import { degradedKindLabels, degradedNoticeTitle } from './importNotice'
import { markdownStrings } from './strings'

const props = defineProps<{ parts: readonly MarkdownImportDegradedPart[] }>()
const emit = defineEmits<{ dismiss: [] }>()
const title = computed(() => degradedNoticeTitle(props.parts.reduce((sum, part) => sum + part.count, 0)))
</script>

<template>
  <div class="md-drop-status md-import-notice" role="status" aria-live="polite" data-testid="markdown-import-notice">
    <details>
      <summary>{{ title }}</summary>
      <ul>
        <li v-for="part in parts" :key="part.kind">
          <span class="md-import-notice-kind">{{ degradedKindLabels[part.kind] ?? part.kind }}<template v-if="part.count > 1"> ({{ part.count }}×)</template>:</span>{{ ' ' }}<code>{{ part.excerpt }}</code>
        </li>
      </ul>
    </details>
    <button type="button" class="md-import-notice-dismiss" :aria-label="markdownStrings.close" @click="emit('dismiss')"><CloseIcon /></button>
  </div>
</template>

<style>
.md-import-notice { display: flex; align-items: flex-start; gap: 8px; }
.md-import-notice details { min-width: 0; flex: 1; }
.md-import-notice summary { cursor: pointer; color: var(--text, #1f2933); }
.md-import-notice summary:focus-visible, .md-import-notice-dismiss:focus-visible { outline: var(--focus-ring, 2px solid #0e7490); outline-offset: var(--focus-offset, 2px); border-radius: var(--radius-sm, 4px); }
.md-import-notice ul { margin: 6px 0 0; padding: 0; list-style: none; display: grid; gap: 4px; max-height: 40vh; overflow: auto; }
.md-import-notice li { display: flex; flex-wrap: wrap; gap: 2px 8px; align-items: baseline; }
.md-import-notice-kind { font-weight: 600; }
.md-import-notice code { overflow-wrap: anywhere; font-size: 12px; color: var(--text-dim, #64748b); }
.md-import-notice-dismiss {
  flex: none; display: inline-grid; place-items: center; width: 24px; height: 24px; margin: -3px -5px 0 0; padding: 0;
  border: 0; border-radius: var(--radius-sm, 6px); background: transparent; color: var(--text-dim, #64748b); cursor: pointer;
}
.md-import-notice-dismiss:hover { background: var(--accent-soft, rgb(14 56 66 / 6%)); color: var(--text, #1f2933); }
.md-import-notice-dismiss svg { width: var(--icon-sm, 16px); height: var(--icon-sm, 16px); }
</style>
