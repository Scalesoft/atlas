<script setup lang="ts">
import shell from "../../i18n/settings-shell";
import AppDialog from "../AppDialog.vue";
import ConfirmWithReauth from "./ConfirmWithReauth.vue";
import type { ReauthOperation, ReauthTarget } from "../../protection";

/**
 * ConfirmWithReauth in a dialog, for a security change started by a single action that has no flow of its own to end in: a
 * level change or revoke in a list, approving an access request, a password release. The default slot says what will happen
 * (ImpactPreview or a sentence). `confirm` carries the proof; the owner runs the change, shows `busy` and a refusal in `error`,
 * and closes the dialog. Inside a StepFlow or an inline row confirmation use ConfirmWithReauth directly.
 */
withDefaults(
  defineProps<{
    open: boolean;
    title: string;
    confirmLabel: string;
    heading?: string;
    description?: string;
    cancelLabel?: string;
    operation?: ReauthOperation;
    target?: ReauthTarget;
    busy?: boolean;
    error?: string;
    /** A new key starts a fresh attempt (another change in the same dialog). */
    attempt?: string | number;
  }>(),
  { heading: undefined, description: undefined, cancelLabel: undefined, operation: "security", target: () => ({}), busy: false, error: undefined, attempt: 0 },
);
/** `closed`: the dialog finished leaving (as ConfirmDialog), for an owner whose opener went with the change. */
const emit = defineEmits<{ confirm: [proofId: string | null]; cancel: []; closed: [] }>();
</script>

<template>
  <AppDialog :open="open" :title="title" @close="!busy && emit('cancel')" @closed="emit('closed')">
    <div v-if="open" class="settings-ui reauth-dialog" data-reauth-dialog>
      <ConfirmWithReauth
        :key="attempt"
        :title="heading"
        :description="description"
        :confirm-label="confirmLabel"
        :cancel-label="cancelLabel ?? shell.cancel"
        :operation="operation"
        :target="target"
        :busy="busy"
        :error="error"
        @confirm="emit('confirm', $event)"
        @cancel="emit('cancel')"
      >
        <slot v-if="$slots.default" />
      </ConfirmWithReauth>
    </div>
  </AppDialog>
</template>

<style scoped>
.reauth-dialog { min-width: 0; }
</style>
