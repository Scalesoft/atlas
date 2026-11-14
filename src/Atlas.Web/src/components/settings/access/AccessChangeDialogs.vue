<script setup lang="ts">
import AtlasIcon from "../../AtlasIcon.vue";
import ConfirmDialog from "../ConfirmDialog.vue";
import ReauthDialog from "../ReauthDialog.vue";
import type { AccessChanges } from "./accessChanges";

/**
 * The confirmations of useAccessChanges: a small destructive ConfirmDialog for a plain revoke, and ReauthDialog
 * (ConfirmWithReauth) for a change that needs a fresh sign-in. Only these small confirmations are dialogs; flows are views.
 */
const props = defineProps<{ changes: AccessChanges }>();
const pending = props.changes.pending;
const busy = props.changes.busy;
const error = props.changes.error;
</script>

<template>
  <ReauthDialog
    :open="!!pending && pending.reauth"
    :title="pending?.title ?? ''"
    :confirm-label="pending?.confirmLabel ?? ''"
    :target="pending?.target"
    :busy="busy"
    :error="error || undefined"
    :attempt="pending?.attempt"
    @confirm="changes.confirm($event)"
    @cancel="changes.cancel()"
  >
    <p class="st-note" data-access-change><AtlasIcon :name="pending?.destructive ? 'trash' : 'info'" :size="15" />{{ pending?.text }}</p>
  </ReauthDialog>
  <ConfirmDialog
    :open="!!pending && !pending.reauth"
    :title="pending?.title ?? ''"
    :confirm-label="pending?.confirmLabel ?? ''"
    :confirm-icon="pending?.confirmIcon"
    :destructive="pending?.destructive ?? true"
    :busy="busy"
    :error="error || undefined"
    @confirm="changes.confirm()"
    @cancel="changes.cancel()"
  >
    <p data-access-change>{{ pending?.text }}</p>
  </ConfirmDialog>
</template>
