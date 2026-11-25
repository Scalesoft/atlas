<script setup lang="ts">
import t from "../../i18n/components-personal";
import { ref } from "vue";
import { accessApi } from "../../accessApi";
import AppDialog from "../AppDialog.vue";
import { useToast } from "../../composables/useToast";
import ResourceAccess from "../settings/access/ResourceAccess.vue";
import ResourceShareForm from "../settings/access/ResourceShareForm.vue";
import type { ResourceAccess as ResourceAccessModel } from "../settings/access/accessModel";

/**
 * Sdílet: who may read or edit one personal note (spec 2026-10-29 §3), as one compact form (ResourceShareForm: a person or group,
 * Čte or Upravuje, an end only on demand; Enter picks, Enter shares) above the current shares (ResourceAccess of kind `item`,
 * compact: the owner's locked row, people and groups with their level and validity, changed or removed in place). Only the owner
 * opens it; the owner is never offered. `changed` tells the host to refresh what it shows of the shares.
 */
withDefaults(defineProps<{ open: boolean; itemId: string; title: string; api?: Pick<typeof accessApi, "resource" | "candidates" | "grant" | "revoke"> }>(),
  { api: () => accessApi });
const emit = defineEmits<{ close: []; changed: [] }>();
const list = ref<InstanceType<typeof ResourceAccess>>();
const taken = ref<string[]>([]);
const toaster = useToast();
function loaded(access: ResourceAccessModel) {
  taken.value = access.entries.map((entry) => `${entry.subject.kind}:${entry.subject.id}`);
}
function shared(message: string) {
  toaster.success(message);
  void list.value?.reload();
  emit("changed");
}
</script>

<template>
  <AppDialog :title="t.share.title(title)" :open="open" size="wide" @close="emit('close')">
    <div class="share-item-dialog" data-share-item>
      <p class="st-muted share-item-note">{{ t.share.note }}</p>
      <ResourceShareForm v-if="open" kind="item" :id="itemId" :title="title" :taken="taken" :api="api" @shared="shared" />
      <ResourceAccess v-if="open" ref="list" kind="item" :id="itemId" :title="t.share.sharedWith" compact :api="api" @loaded="loaded" @changed="emit('changed')" />
    </div>
  </AppDialog>
</template>

<style scoped>
.share-item-dialog { display: grid; gap: 16px; min-width: 0; }
.share-item-note { margin: 0; font-size: 13px; }
</style>
