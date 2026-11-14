import { ref } from "vue";
import { ApiError, errorMessage } from "../../../api";
import t from "../../../i18n/settings-backups";
import { reauthRequiredCode, securityChange } from "../../../protection";

/**
 * A backup holds every private note and personal board (a keyless restore, owner rule), so the server treats a new Git repository,
 * a new deploy key or a new ZIP folder as a security change (SEC1-2), and so the download of a complete archive (SEC3-1, `note` says
 * why in the dialog). The panels send the change as usual; when the server answers
 * 403 reauth_required, `pending` opens a ReauthDialog and the same change goes again with the proof. Nothing is asked up front: the
 * server alone knows whether the destination changed.
 */
export const backupDestinationNote = () => t.destinationNote;

export function useBackupDestinationChange() {
  const pending = ref<{ title: string; confirmLabel: string; note?: string; run: (proof: string | null) => Promise<void> } | null>(null);
  const busy = ref(false);
  const error = ref("");

  /** Sends `path` without a proof; a refusal for a fresh sign-in keeps the change for `confirm`. Other failures throw. */
  async function send<T>(path: string, method: string, body: unknown, title: string, confirmLabel: string, done: (result: T) => void, note?: string) {
    const run = async (proof: string | null) => done(await securityChange<T>(path, method, body, proof));
    try {
      await run(null);
    } catch (failure) {
      if (!(failure instanceof ApiError && failure.code === reauthRequiredCode)) throw failure;
      error.value = "";
      pending.value = { title, confirmLabel, note, run };
    }
  }
  async function confirm(proof: string | null) {
    const change = pending.value;
    if (!change || busy.value) return;
    busy.value = true;
    error.value = "";
    try {
      await change.run(proof);
      pending.value = null;
    } catch (failure) {
      error.value = errorMessage(failure);
    } finally {
      busy.value = false;
    }
  }
  function cancel() {
    if (!busy.value) pending.value = null;
  }
  return { pending, busy, error, send, confirm, cancel };
}
