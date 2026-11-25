<script setup lang="ts">
import AppDialog from "./AppDialog.vue";
import { Button } from "./ui/button";
import t from "../i18n/components-c";

// One confirmation for leaving a dirty settings form anywhere in the wiki: stay on the
// form, throw the draft away, or save it before navigating. Shared by profile settings
// and (later) the OIDC module so the wording and the three actions never drift apart.
// It opens on Uložit (Enter keeps the work), or on Zůstat when the draft cannot be saved; never on the close icon.
// A component's own draft (the page source) says what it holds in `message` and how it is kept in `saveLabel` („Použít a zavřít“).
withDefaults(defineProps<{ open: boolean; saving?: boolean; canSave?: boolean; message?: string; saveLabel?: string }>(), { saving: false, canSave: true });
// `closed` comes once the dialog has gone and given the page back: the place to move focus when its opener is gone.
const emit = defineEmits<{ stay: []; discard: []; save: []; closed: [] }>();
</script>

<template>
  <AppDialog :title="t.unsaved.title" :open="open" @close="emit('stay')" @closed="emit('closed')">
    <p>{{ message ?? (canSave ? t.unsaved.saveOrDiscard : t.unsaved.discard) }}</p>
    <template #footer>
      <Button variant="outline" type="button" :autofocus="!canSave || undefined" @click="emit('stay')">{{ t.unsaved.stay }}</Button>
      <Button variant="destructive" type="button" :disabled="saving" @click="emit('discard')">{{ t.unsaved.discardButton }}</Button>
      <Button variant="default" v-if="canSave" type="button" autofocus :disabled="saving" @click="emit('save')">{{ saving ? t.unsaved.saving : saveLabel ?? t.unsaved.save }}</Button>
    </template>
  </AppDialog>
</template>
