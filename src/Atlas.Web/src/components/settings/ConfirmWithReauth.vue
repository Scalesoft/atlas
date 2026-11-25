<script setup lang="ts">
import { locale } from "../../i18n";
import t from "../../i18n/settings-flow";
import { computed, ref, useId } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingNote from "./LoadingNote.vue";
import { Button } from "../ui/button";
import StatusBadge from "./StatusBadge.vue";
import { useReauth } from "./useReauth";
import { formatTime } from "../../formatDate";
import type { ReauthOperation, ReauthTarget } from "../../protection";

/**
 * Last step of every security change: what will happen (default slot, usually ImpactPreview),
 * then a fresh sign-in, then the change. `confirm` is emitted only after the sign-in succeeded,
 * with the proof id the change carries (X-Atlas-Reauth; null when the environment has none).
 * `operation` and `target` bind the proof to the change's scope: no target is the global security
 * configuration, a vault or secret its own. An unsupported provider blocks the button and says why.
 * Inside the server's ten-minute re-authentication window (the session signed in again recently) no
 * sign-in is asked for: the button confirms at once and `confirm` carries null. When the server then
 * refuses (reauth_required), the window closes and the same form asks for the sign-in again.
 */
const props = withDefaults(
  defineProps<{
    confirmLabel: string;
    title?: string;
    description?: string;
    cancelLabel?: string;
    disabled?: boolean;
    busy?: boolean;
    operation?: ReauthOperation;
    target?: ReauthTarget;
    /** A failure of the change itself after the sign-in (a conflict, a refusal), said in place. */
    error?: string;
  }>(),
  {
    title: undefined, description: undefined,
    cancelLabel: undefined, disabled: false, busy: false, operation: "security", target: () => ({}), error: undefined,
  },
);
const emit = defineEmits<{ confirm: [proofId: string | null]; cancel: [] }>();
const reauth = useReauth();
const password = ref("");
const uid = useId();
const passwordId = `${uid}-password`;
const errorId = `${uid}-error`;

// The server's window covers this change: nothing to verify, whatever the provider could prove now.
const windowOpen = computed(() => reauth.windowOpen.value);
const blocked = computed(() => !windowOpen.value && reauth.state.value === "unsupported");
const heading = computed(() => props.title ?? (windowOpen.value ? t.reauth.confirmChange : t.reauth.confirmWithSignIn));
const summary = computed(
  () =>
    props.description ??
    (windowOpen.value
      ? t.reauth.recent
      : t.reauth.required),
);
const verifying = computed(() => reauth.state.value === "verifying");
const failed = computed(() => reauth.state.value === "failed");
const canSubmit = computed(
  () => !props.disabled && !props.busy && !blocked.value && !verifying.value && (windowOpen.value || reauth.method.value === "hub" || password.value.length > 0),
);

async function submit() {
  if (!canSubmit.value) return;
  if (windowOpen.value) {
    emit("confirm", null);
    return;
  }
  const ok = await reauth.verify(password.value, { operation: props.operation, target: props.target });
  // The password is never kept after the attempt, successful or not.
  password.value = "";
  if (ok) {
    emit("confirm", reauth.proofId.value);
    // A proof is used once; the next change goes out in the window it opened, or signs in again once that has closed.
    reauth.reset();
  }
}
</script>

<template>
  <form class="confirm-reauth" :aria-busy="verifying || busy || undefined" :aria-labelledby="`${uid}-title`" @submit.prevent="submit">
    <div v-if="$slots.default" class="confirm-reauth-summary"><slot /></div>
    <div class="confirm-reauth-box" :data-state="reauth.state.value">
      <div class="confirm-reauth-head">
        <span class="st-icon-tile" :data-tone="blocked ? 'danger' : failed ? 'warning' : undefined"><AtlasIcon :name="blocked ? 'shield-alert' : windowOpen ? 'shield-check' : 'fingerprint'" :size="20" /></span>
        <div>
          <h4 :id="`${uid}-title`">{{ heading }}</h4>
          <p>{{ summary }}</p>
        </div>
      </div>

      <div v-if="blocked" class="confirm-reauth-blocked" role="alert">
        <StatusBadge tone="danger" :label="t.reauth.cannotVerify" />
        <p>{{ reauth.unsupportedReason.value ?? t.reauth.unsupported }}</p>
      </div>
      <p v-else-if="windowOpen" class="confirm-reauth-notice" data-reauth-window>
        <AtlasIcon name="shield-check" :size="14" />{{ t.reauth.notNeeded(formatTime(reauth.windowUntil.value)) }}
      </p>
      <template v-else-if="reauth.method.value === 'local'">
        <label class="confirm-reauth-label" :for="passwordId">{{ t.reauth.password }}</label>
        <input
          :id="passwordId"
          v-model="password"
          class="st-input"
          type="password"
          autocomplete="current-password"
          :disabled="verifying || busy"
          :aria-invalid="failed || undefined"
          :aria-describedby="failed ? errorId : undefined"
          data-step-focus
        />
      </template>
      <p v-else class="confirm-reauth-hub">{{ t.reauth.hubOpens(reauth.providerName.value ?? t.reauth.hubDefault) }}</p>
      <p v-if="!blocked && !windowOpen && reauth.notice.value" class="confirm-reauth-notice"><AtlasIcon name="info" :size="14" />{{ reauth.notice.value }}</p>

      <p class="confirm-reauth-status" aria-live="polite">
        <LoadingNote v-if="verifying" inline :label="reauth.method.value === 'hub' ? t.reauth.waitingFor(reauth.providerName.value ?? t.reauth.waitingHubDefault) : t.reauth.verifyingPassword" />
        <LoadingNote v-else-if="busy" inline :label="t.reauth.saving" />
        <span v-else-if="failed" :id="errorId" class="confirm-reauth-error" data-reauth-error><AtlasIcon name="circle-x" :size="15" />{{ reauth.failure.value }}</span>
        <span v-else-if="error" class="confirm-reauth-error" role="alert" data-change-error><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</span>
      </p>
    </div>
    <div class="confirm-reauth-actions">
      <Button variant="outline" type="button" :disabled="verifying || busy" @click="emit('cancel')">{{ cancelLabel ?? t.reauth.cancel }}</Button>
      <Button type="submit" :disabled="!canSubmit" data-confirm-reauth>
        <AtlasIcon :name="reauth.method.value === 'hub' && !windowOpen ? 'external' : 'check'" :size="16" />
        {{ reauth.method.value === 'hub' && !blocked && !windowOpen ? t.reauth.signInAnd(confirmLabel.toLocaleLowerCase(locale)) : confirmLabel }}
      </Button>
    </div>
  </form>
</template>

<style scoped>
.confirm-reauth { display: grid; gap: 14px; min-width: 0; }
.confirm-reauth-summary { min-width: 0; }
.confirm-reauth-box {
  display: grid;
  gap: 10px;
  padding: 14px 16px;
  border: 1px solid var(--line);
  border-radius: var(--st-radius);
  background: color-mix(in srgb, var(--canvas) 60%, var(--surface));
  transition: border-color var(--atlas-motion-base) var(--atlas-ease-out);
}
.confirm-reauth-box[data-state="failed"] { border-color: color-mix(in srgb, var(--warning) 50%, var(--line)); animation: confirm-reauth-shake var(--atlas-motion-slow) var(--atlas-ease-out); }
.confirm-reauth-box[data-state="unsupported"] { border-color: color-mix(in srgb, var(--error) 45%, var(--line)); }
.confirm-reauth-head { display: flex; gap: 12px; align-items: flex-start; }
.confirm-reauth-head h4 { margin: 0; font-size: 14.5px; font-weight: 650; }
.confirm-reauth-head p { margin: 3px 0 0; color: var(--muted); font-size: 12.5px; line-height: 1.55; }
.confirm-reauth-label { font-size: 13px; font-weight: 600; }
.confirm-reauth-hub { margin: 0; font-size: 13px; color: var(--muted); }
.confirm-reauth-notice { display: flex; align-items: flex-start; gap: 6px; margin: 0; color: var(--muted); font-size: 12.5px; }
.confirm-reauth-notice > svg { flex-shrink: 0; margin-top: 2px; color: var(--info); }
.confirm-reauth-blocked { display: grid; gap: 6px; justify-items: start; }
.confirm-reauth-blocked p { margin: 0; font-size: 13px; line-height: 1.55; }
.confirm-reauth-status { margin: 0; min-height: 20px; font-size: 13px; }
.confirm-reauth-error { display: inline-flex; align-items: center; gap: 6px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.confirm-reauth-error { color: var(--warning); font-weight: 600; }
.confirm-reauth-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 8px; }
@keyframes confirm-reauth-shake {
  0%, 100% { transform: none; }
  25% { transform: translateX(calc(var(--atlas-lift) * -2)); }
  50% { transform: translateX(calc(var(--atlas-lift) * 2)); }
  75% { transform: translateX(calc(var(--atlas-lift) * -1)); }
}
</style>
