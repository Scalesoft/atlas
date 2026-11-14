<script setup lang="ts">
import t from "../../i18n/settings-shell";
import AtlasIcon from "../AtlasIcon.vue";
import { disclosureHooks } from "./disclosure";
import AppDialog from "../AppDialog.vue";
import { Button } from "../ui/button";

/**
 * One confirmation for an irreversible or destructive settings action (archive a vault, remove a
 * location, revoke access): what happens, what stays, and two buttons. Built on AppDialog, so Escape
 * and the close button only ask; the owner closes it. Use UnsavedChangesDialog for leaving a dirty
 * form and ConfirmWithReauth when the change needs a fresh sign-in.
 */
withDefaults(
  defineProps<{
    open: boolean;
    title: string;
    confirmLabel: string;
    confirmIcon?: string;
    cancelLabel?: string;
    /** Destructive (red) confirm button; false for a reversible but important action. */
    destructive?: boolean;
    busy?: boolean;
    error?: string;
    /** The confirmation is not possible yet: something inside it is still missing (a typed name, the subpages of a page to trash). */
    confirmDisabled?: boolean;
    /** "wide" when the confirmation shows what it changes (the comparison of Zahodit změny). */
    size?: "default" | "wide";
  }>(),
  { size: "default", confirmIcon: undefined, cancelLabel: undefined, destructive: true, busy: false, error: undefined, confirmDisabled: false },
);
/** `closed`: the dialog finished leaving, for an owner that unmounts it then. */
const emit = defineEmits<{ confirm: []; cancel: []; closed: [] }>();
</script>

<template>
  <AppDialog :title="title" :open="open" :size="size" @close="!busy && emit('cancel')" @closed="emit('closed')">
    <div class="confirm-dialog-body settings-ui">
      <slot />
      <Transition :css="false" v-bind="disclosureHooks">
        <p v-if="error" class="confirm-dialog-error" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p>
      </Transition>
    </div>
    <template #footer>
      <Button variant="outline" type="button" :disabled="busy" data-confirm-cancel @click="emit('cancel')">{{ cancelLabel ?? t.cancel }}</Button>
      <Button :variant="destructive ? 'destructive' : 'default'" type="button" :disabled="busy || confirmDisabled" data-confirm-action @click="emit('confirm')">
        <AtlasIcon v-if="busy" name="loader-circle" :size="16" class="atlas-spinner" /><AtlasIcon v-else-if="confirmIcon" :name="confirmIcon" :size="16" />{{ confirmLabel }}
      </Button>
    </template>
  </AppDialog>
</template>

<style scoped>
.confirm-dialog-body { display: grid; gap: 10px; font-size: 14px; line-height: 1.55; }
.confirm-dialog-body :deep(p) { margin: 0; }
.confirm-dialog-error { display: flex; align-items: center; gap: 6px; color: var(--error); font-size: 13px; font-weight: 550; }
</style>
