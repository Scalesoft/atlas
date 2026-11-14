import { ref } from "vue";
import { accessApi } from "../../../accessApi";
import { ApiError, errorMessage } from "../../../api";
import { locale } from "../../../i18n";
import t from "../../../i18n/settings-access";
import { reauthRequiredCode, type ReauthTarget } from "../../../protection";
import { accessKind, levelLabel } from "./accessKinds";
import type { AccessGrantBody, AccessLevel } from "./accessModel";

/**
 * Grant changes of every surface (a person's card, a resource's panel, the grant flows) through PUT/DELETE
 * /api/access/resources/{kind}/{id}/grants, with the owner's guard rules (docs/access-model.md §2, decision 3a): every
 * vault and secret grant change and granting or changing to Spravuje on a space or page needs a fresh sign-in; lower space
 * and page grants and revokes do not. Those are asked for up front (ConfirmWithReauth in a dialog); should the server
 * still answer 403 reauth_required, the same dialog asks and the change is repeated with the proof. A plain revoke asks in
 * a ConfirmDialog. A conflict (409) reloads and says so; nothing is retried silently.
 */
export interface AccessTarget {
  kind: string;
  id: string;
  /** "trezor Servery a síť", "prostor Provoz", said in the confirmations. */
  name: string;
}
export interface AccessSubjectRef {
  kind: "user" | "group";
  id: string;
  name: string;
}
export interface AccessGrantChange {
  target: AccessTarget;
  subject: AccessSubjectRef;
  level: AccessLevel;
  /** The level held before (a change), or null for a new grant. */
  previous: AccessLevel | null;
  startAt: string | null;
  expiresAt: string | null;
  expectedVersion: string | null;
}
export interface AccessRevoke {
  target: AccessTarget;
  subject: AccessSubjectRef;
  level: AccessLevel;
  version: string | null | undefined;
}

/** Whether a change needs a fresh sign-in before it is sent (the server stays the judge). */
export function grantNeedsReauth(kind: string, level: AccessLevel | null) {
  if (kind === "vault" || kind === "secret") return true;
  // A space or page grant with Spravuje (new, raised to it, or its validity changed) is a manage grant being set.
  return (kind === "space" || kind === "page") && level === "manage";
}
export function revokeNeedsReauth(kind: string) {
  return kind === "vault" || kind === "secret";
}
/**
 * The scope a fresh sign-in is bound to. Every guarded change of the access layer (a space or page grant with Spravuje,
 * vault and secret grants, Správce wiki, revoke-all) binds to the global security target: the challenge starts without a
 * target and the proof, or the session's ten-minute window, covers the change.
 */
export function reauthTargetOf(_target: Pick<AccessTarget, "kind" | "id">): ReauthTarget {
  return {};
}
export interface PendingAccessChange {
  title: string;
  confirmLabel: string;
  confirmIcon: string;
  text: string;
  /** Needs a fresh sign-in (ReauthDialog); otherwise a ConfirmDialog. */
  reauth: boolean;
  target: ReauthTarget;
  destructive: boolean;
  run: (proof: string | null) => Promise<void>;
  success: string;
  successIcon: string;
  /** Each change starts a fresh ConfirmWithReauth. */
  attempt: number;
}

export interface AccessChangeOptions {
  /** Reads the current state again (after a change or a conflict). */
  reload: () => Promise<unknown>;
  /** A change went through: a flash, a highlight, the shell's role refresh. */
  done?: (message: string, icon: string, key?: string) => void;
  api?: Pick<typeof accessApi, "grant" | "revoke">;
}


export function useAccessChanges(options: AccessChangeOptions) {
  const api = options.api ?? accessApi;
  const busy = ref(false);
  const pending = ref<PendingAccessChange>();
  const error = ref("");
  const conflict = ref("");
  let attempt = 0;

  async function execute(change: PendingAccessChange, proof: string | null): Promise<boolean> {
    busy.value = true;
    error.value = "";
    conflict.value = "";
    try {
      await change.run(proof);
      pending.value = undefined;
      await options.reload();
      options.done?.(change.success, change.successIcon);
      return true;
    } catch (failure) {
      if (failure instanceof ApiError && failure.status === 403 && failure.code === reauthRequiredCode) {
        // The server wants a fresh sign-in after all: ask in the same place and repeat the change with the proof.
        pending.value = { ...change, reauth: true, attempt: ++attempt };
        if (change.reauth) error.value = t.changes.expired;
      } else if (failure instanceof ApiError && failure.status === 409) {
        pending.value = undefined;
        conflict.value = t.changes.conflict;
        await options.reload().catch(() => undefined);
      } else error.value = errorMessage(failure);
      return false;
    } finally {
      busy.value = false;
    }
  }

  /** Starts a change: at once, or after the fresh sign-in it needs. Resolves true once it went through. */
  function start(change: Omit<PendingAccessChange, "attempt">, ask: boolean, proof: string | null) {
    error.value = "";
    const next = { ...change, attempt: ++attempt };
    if (ask) {
      pending.value = next;
      return Promise.resolve(false);
    }
    return execute(next, proof);
  }

  /**
   * Sets or changes a grant. `confirmed` comes from a flow that already asked (its own ConfirmWithReauth) and carries
   * the proof; without it a change that needs a fresh sign-in asks in the dialog first.
   */
  function grant(change: AccessGrantChange, confirmed?: { proof: string | null }) {
    const body: AccessGrantBody = {
      subjectKind: change.subject.kind,
      subjectId: change.subject.id,
      level: change.level,
      startAt: change.startAt,
      expiresAt: change.expiresAt,
      expectedVersion: change.expectedVersion,
    };
    const label = levelLabel(change.level);
    const reauth = grantNeedsReauth(change.target.kind, change.level);
    const fresh = change.previous === null;
    return start(
      {
        title: fresh ? t.changes.addTitle : t.changes.changeTitle,
        confirmLabel: fresh ? t.changes.addTitle : t.changes.save,
        confirmIcon: "check",
        text: fresh || change.previous === change.level
          ? `${change.subject.name}: ${label} · ${change.target.name}.`
          : `${change.subject.name}: ${levelLabel(change.previous)} → ${label} · ${change.target.name}.`,
        reauth,
        destructive: false,
        target: reauthTargetOf(change.target),
        run: (proof) => api.grant(change.target.kind, change.target.id, body, proof),
        success: fresh ? t.changes.added(change.subject.name, label, change.target.name) : t.changes.saved(change.subject.name, label),
        successIcon: "circle-check",
      },
      reauth && !confirmed,
      confirmed?.proof ?? null,
    );
  }

  /** Asks before closing a grant (ConfirmDialog, or ConfirmWithReauth where a revoke needs a fresh sign-in). */
  function revoke(change: AccessRevoke) {
    const reauth = revokeNeedsReauth(change.target.kind);
    error.value = "";
    pending.value = {
      title: t.changes.removeTitle,
      confirmLabel: t.changes.removeTitle,
      confirmIcon: "trash",
      text: t.changes.removeText(change.subject.name, levelLabel(change.level), change.target.name),
      reauth,
      destructive: true,
      target: reauthTargetOf(change.target),
      run: (proof) => api.revoke(change.target.kind, change.target.id, change.subject.kind, change.subject.id, change.version, proof),
      success: t.changes.removed(change.subject.name),
      successIcon: "trash",
      attempt: ++attempt,
    };
  }

  /** The dialog confirmed: with the proof of the fresh sign-in, or null (a plain confirmation, or inside the window). */
  function confirm(proof: string | null = null) {
    const change = pending.value;
    if (!change || busy.value) return Promise.resolve(false);
    return execute(change, proof);
  }
  function cancel() {
    if (busy.value) return;
    pending.value = undefined;
    error.value = "";
  }

  return { busy, pending, error, conflict, grant, revoke, confirm, cancel };
}
export type AccessChanges = ReturnType<typeof useAccessChanges>;

/** The kind's own noun for a confirmation: "trezor Servery a síť", "prostor Provoz", "YouTrack projekt OPS". */
export function targetName(kind: string, title: string) {
  return `${accessKind(kind).one.toLocaleLowerCase(locale)} ${title}`.trim();
}
