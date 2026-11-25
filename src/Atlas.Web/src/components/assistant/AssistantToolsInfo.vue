<script setup lang="ts">
import { ref } from "vue";
import { api } from "../../api";
import t from "../../i18n/assistant-panel";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingNote from "../settings/LoadingNote.vue";
import { Popover, PopoverContent, PopoverTrigger } from "../ui/popover";

/**
 * „Co asistent s nástroji umí“: a small info button beside the „S nástroji“ switch; its popover lists the tools the server offers THIS user
 * (GET /api/assistant/tools: the registry of the agent turn), grouped, with the write ones marked. Loaded on first open, so it adds nothing
 * to the panel's start. Hidden when the list cannot be had (the server answers 404 without the right to tools). `groups` is the gallery's
 * sample (no server there).
 */
export interface ToolGroup { group: string; tools: { name: string; description: string; write: boolean }[] }
const props = defineProps<{ groups?: ToolGroup[] }>();
const loaded = ref<ToolGroup[] | undefined>(props.groups);
const failed = ref(false);
const text = t.panel.toolsInfo.groups as Record<string, string[]>;

async function load(open: boolean) {
  if (!open || loaded.value) return;
  try { loaded.value = await api<ToolGroup[]>("/assistant/tools"); } catch { failed.value = true; }
}
</script>

<template>
  <Popover @update:open="load">
    <PopoverTrigger as-child>
      <button type="button" class="tools-info-btn" :aria-label="t.panel.toolsInfo.open" :title="t.panel.toolsInfo.open" data-assistant-tools-info><AtlasIcon name="circle-help" :size="14" /></button>
    </PopoverTrigger>
    <PopoverContent class="tools-info-pop" side="top" align="end" :side-offset="8" data-assistant-tools-list>
      <h3 class="tools-info-title">{{ t.panel.toolsInfo.title }}</h3>
      <p class="tools-info-note">{{ t.panel.toolsInfo.intro }}</p>
      <p v-if="failed" class="tools-info-note" role="status">{{ t.panel.toolsInfo.unavailable }}</p>
      <LoadingNote v-else-if="!loaded" :label="t.panel.toolsInfo.loading" />
      <template v-else>
        <section v-for="group in loaded" :key="group.group" class="tools-info-group" :data-group="group.group">
          <h4>{{ text[group.group]?.[0] ?? group.group }}</h4>
          <p v-if="text[group.group]?.[1]">{{ text[group.group]![1] }}</p>
          <ul>
            <li v-for="tool in group.tools" :key="tool.name" :title="tool.description" :data-write="tool.write || undefined">
              <code>{{ tool.name.replace(/^atlas_|^projector_/, "") }}</code>
              <span class="tools-info-kind"><AtlasIcon :name="tool.write ? 'pencil' : 'eye'" :size="11" />{{ tool.write ? t.panel.toolsInfo.write : t.panel.toolsInfo.read }}</span>
            </li>
          </ul>
        </section>
        <p class="tools-info-note">{{ t.panel.toolsInfo.writeNote }}</p>
      </template>
    </PopoverContent>
  </Popover>
</template>

<style scoped>
.tools-info-btn { display: inline-flex; flex: none; align-items: center; justify-content: center; width: 28px; height: 28px; margin-left: -2px; border: 0; border-radius: 8px; background: transparent; color: var(--muted); cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .tools-info-btn:hover { background: var(--ai-wash); color: var(--text); } }
.tools-info-btn[data-state="open"] { background: var(--ai-wash-strong); color: var(--text); }
.tools-info-btn:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
</style>

<style>
/* PopoverContent is portalled, out of this component's scope. */
.tools-info-pop { width: 340px; max-height: min(70vh, 520px); overflow-y: auto; padding: 14px 16px 16px; font-size: 13px; }
.tools-info-pop .tools-info-title { margin: 0 0 6px; font-size: 14px; font-weight: 650; }
.tools-info-pop .tools-info-note { margin: 6px 0; color: var(--muted); font-size: 12px; }
.tools-info-pop .tools-info-group { margin-top: 12px; }
.tools-info-pop .tools-info-group h4 { margin: 0; font-size: 13px; font-weight: 600; }
.tools-info-pop .tools-info-group p { margin: 2px 0 4px; color: var(--muted); font-size: 12px; }
.tools-info-pop .tools-info-group ul { margin: 0; padding: 0; list-style: none; }
.tools-info-pop .tools-info-group li { display: flex; align-items: baseline; justify-content: space-between; gap: 8px; padding: 2px 0; }
.tools-info-pop .tools-info-group code { overflow-wrap: anywhere; font-size: 12px; }
.tools-info-pop .tools-info-kind { display: inline-flex; flex: none; align-items: center; gap: 3px; color: var(--muted); font-size: 11px; }
.tools-info-pop li[data-write] .tools-info-kind { color: var(--accent); }
</style>
