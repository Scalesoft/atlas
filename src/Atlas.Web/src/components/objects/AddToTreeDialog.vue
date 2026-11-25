<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { api, ApiError, errorMessage, type Space } from "../../api";
import type { DraggedObject } from "../../boards/boardDrop";
import { useToast } from "../../composables/useToast";
import t from "../../i18n/components-objects";
import { organizationApi, treeReferenceApi, type OrganizationTreeItem } from "../../organization";
import { isPageNode } from "../../treeMove";
import AppDialog from "../AppDialog.vue";
import PageLocationPicker from "../PageLocationPicker.vue";
import { Button } from "../ui/button";

/**
 * „Přidat do stromu…“: the keyboard path of dropping an object on the page tree. It uses the tree's own location picker and add-reference
 * command (treeReferenceApi.add, the same call the drop makes); the reference goes to the end of the chosen page's children or of the space root.
 * A loaded tree shows it by itself (the server's TreeChanged).
 */
const props = defineProps<{ open: boolean; object: DraggedObject | null }>();
const emit = defineEmits<{ close: [] }>();
const toaster = useToast();
const spaces = ref<Space[]>([]), spaceId = ref(""), items = ref<OrganizationTreeItem[]>([]), parentId = ref("");
const loading = ref(false), saving = ref(false), failed = ref("");
const pages = computed(() => items.value.filter(isPageNode));
let load = 0;

watch(() => props.open, async open => {
  if (!open) return;
  failed.value = ""; loading.value = true;
  try {
    spaces.value = (await api<Space[]>("/spaces")).filter(space => space.role !== "Reader");
    spaceId.value = spaces.value[0]?.id ?? "";
    if (!spaceId.value) loading.value = false;
  } catch (failure) { failed.value = errorMessage(failure); loading.value = false; }
});
watch(spaceId, async id => {
  parentId.value = ""; items.value = [];
  if (!id) return;
  const mine = ++load;
  loading.value = true;
  try { const tree = await organizationApi.tree(id); if (mine === load) items.value = tree.items; }
  catch (failure) { if (mine === load) failed.value = errorMessage(failure); }
  finally { if (mine === load) loading.value = false; }
});

async function add() {
  if (!props.object || !spaceId.value || saving.value) return;
  saving.value = true; failed.value = "";
  try {
    await treeReferenceApi.add(spaceId.value, { targetId: props.object.id, anchor: props.object.anchor ?? null, parentId: parentId.value || null, beforeId: null });
    toaster.success(t.addToTree.added);
    emit("close");
  } catch (failure) {
    failed.value = failure instanceof ApiError && failure.status === 404 ? t.addToTree.refused : errorMessage(failure);
  } finally { saving.value = false; }
}
</script>

<template>
  <AppDialog :title="t.addToTree.title" :open="open" @close="emit('close')">
    <form id="add-to-tree-form" class="add-to-tree" @submit.prevent="add">
      <p v-if="object?.title">{{ object.title }}</p>
      <label v-if="spaces.length > 1" class="add-to-tree-space">{{ t.addToTree.space }}
        <select v-model="spaceId" data-add-to-tree-space><option v-for="space in spaces" :key="space.id" :value="space.id">{{ space.name }}</option></select>
      </label>
      <p v-if="!loading && !spaces.length && !failed" role="status">{{ t.addToTree.noSpace }}</p>
      <PageLocationPicker v-else id="add-to-tree-parent" v-model="parentId" :items="pages" :disabled="loading || saving" :label="t.addToTree.under" />
      <p v-if="failed" role="alert">{{ failed }}</p>
    </form>
    <template #footer>
      <Button variant="outline" type="button" @click="emit('close')">{{ t.addToTree.cancel }}</Button>
      <Button variant="default" type="submit" form="add-to-tree-form" :disabled="loading || saving || !spaceId">{{ t.addToTree.confirm }}</Button>
    </template>
  </AppDialog>
</template>

<style scoped>
.add-to-tree { display: grid; gap: 12px; }
.add-to-tree-space { display: grid; gap: 4px; font-size: 13px; }
</style>
