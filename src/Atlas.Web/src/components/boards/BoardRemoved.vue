<script setup lang="ts">
import t from "../../i18n/boards";
import { ref, watch } from "vue";
import { PopoverContent, PopoverPortal, PopoverRoot, PopoverTrigger } from "reka-ui";
import { boardApi, type BoardItemKind } from "../../boards/boardApi";
import { kindNames } from "../../boards/boardFlow";
import { errorMessage } from "../../api";
import { useEditorTheme } from "../../editor/editorSetup";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import { Button } from "../ui/button";
import "./canvas/boardPopover.css";

/**
 * „Odebrané“: what was taken off this board and can still come back (the server keeps a removed item, a `restore` change brings it
 * back). The toast's „Zpět“ and Ctrl+Z reach only the last step of this tab; this list is what is left after a reload or from another
 * person's deletion. Read when the popover opens (newest first); a restore is one gesture of the board (`restore`), after which the
 * list is read again. Only a Writer sees it.
 */
const props = defineProps<{ boardId: string }>();
const emit = defineEmits<{ restore: [itemId: string] }>();
const open = ref(false);
const items = ref<{ id: string; kind: BoardItemKind; text: string | null }[]>();
const error = ref("");
const theme = useEditorTheme();

async function load() {
  error.value = "";
  try { items.value = (await boardApi.removed(props.boardId)).items; } catch (failure) { error.value = errorMessage(failure); }
}
watch(open, now => { if (now) void load(); else items.value = undefined; });
function restore(id: string) {
  emit("restore", id);
  items.value = items.value?.filter(item => item.id !== id);
}
</script>

<template>
  <PopoverRoot v-model:open="open">
    <PopoverTrigger as-child>
      <Button variant="outline" size="sm" type="button" data-board-removed-trigger><AtlasIcon name="trash" :size="15" />{{ t.removed.button }}</Button>
    </PopoverTrigger>
    <PopoverPortal>
      <PopoverContent class="md-editor atlas-document-editor md-context-popover board-settings nodrag nopan nowheel" :class="theme" side="bottom" align="end" :side-offset="8"
        :collision-padding="8" :aria-label="t.removed.label" data-positioned data-board-removed>
        <div class="md-context-popover-body">
          <p v-if="error" class="board-removed-note" role="alert">{{ error }}</p>
          <LoadingSkeleton v-else-if="!items" :rows="3" row-height="44px" :label="t.removed.loading" />
          <p v-else-if="!items.length" class="board-removed-note">{{ t.removed.none }}</p>
          <ul v-else class="board-removed-list">
            <li v-for="item in items" :key="item.id">
              <span class="board-removed-label"><strong>{{ kindNames[item.kind] ?? t.removed.item }}</strong><span v-if="item.text">{{ item.text }}</span></span>
              <button type="button" class="md-btn" :data-board-restore="item.id" @click="restore(item.id)"><AtlasIcon name="undo-2" :size="15" />{{ t.removed.restore }}</button>
            </li>
          </ul>
        </div>
      </PopoverContent>
    </PopoverPortal>
  </PopoverRoot>
</template>

<style scoped>
.board-removed-note { margin: 0; color: var(--muted); font-size: 13px; }
.board-removed-list { display: grid; gap: 6px; max-height: 50dvh; margin: 0; padding: 0; overflow: auto; list-style: none; }
.board-removed-list li { display: flex; align-items: center; justify-content: space-between; gap: 10px; font-size: 13px; }
.board-removed-label { display: grid; min-width: 0; }
.board-removed-label span { overflow: hidden; color: var(--muted); text-overflow: ellipsis; white-space: nowrap; }
</style>
