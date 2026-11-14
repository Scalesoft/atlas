import { watch, type Ref, type WatchStopHandle } from "vue";
import { accountChannel, liveAccount } from "../live/accountChannel";
import { liveConnection, type LiveConnection } from "../live/liveConnection";

/**
 * Access events for password references (WP4 V6): `secretAccessRevoked { secretId }` and `accessRequestChanged` on the signed-in
 * account's own channel `user:{id}` of the tab's one live connection (/hubs/live), beside the inbox signal, so a reader holds no
 * second connection for them. The channel is held from the first subscription (a mounted reference, a request feed) to the last.
 */
/** `accessRequestChanged { requestId, secretId?, vaultId, state }`; a vault-wide request has no secret. */
export interface SecretRequestChange {
  secretId: string | null;
  /** pending, approved, rejected or cancelled; null when the server sent none. */
  state: string | null;
}

export interface SecretAccessEvents {
  /** Calls `revoke` when access to the secret is revoked (or the sign-in ends); returns the unsubscribe. */
  subscribe(secretId: string, revoke: () => void): () => void;
  /** Calls `changed` when an access request of the caller changes state (approved, rejected, cancelled…). */
  onRequestChanged(changed: (change: SecretRequestChange) => void): () => void;
  /** Revokes every subscription, e.g. on sign-out. */
  revokeAll(): void;
}

const events = ["secretAccessRevoked", "accessRequestChanged"] as const;

function secretIdOf(payload: unknown): string | null {
  const id = payload && typeof payload === "object" ? (payload as { secretId?: unknown }).secretId : undefined;
  return typeof id === "string" ? id : null;
}

export function createSecretAccessEvents(options: { live?: () => LiveConnection; account?: Ref<string | undefined> } = {}): SecretAccessEvents {
  const account = options.account ?? liveAccount;
  const subscriptions = new Map<string, Set<() => void>>();
  const requestListeners = new Set<(change: SecretRequestChange) => void>();
  let channel: { accountId: string; close(): void } | undefined;
  let stopWatch: WatchStopHandle | undefined;

  const count = () => [...subscriptions.values()].reduce((sum, set) => sum + set.size, 0);
  function revoke(secretId: string) {
    for (const callback of [...(subscriptions.get(secretId) ?? [])]) {
      try { callback(); } catch { /* one broken surface must not keep another one revealed */ }
    }
  }
  function message(event: string, payload: unknown) {
    if (event === "secretAccessRevoked") {
      const id = secretIdOf(payload);
      if (id) revoke(id);
    } else if (event === "accessRequestChanged") {
      const state = payload && typeof payload === "object" ? (payload as { state?: unknown }).state : undefined;
      const change = { secretId: secretIdOf(payload), state: typeof state === "string" ? state : null };
      requestListeners.forEach(listener => listener(change));
    }
  }
  /** The server ended the account's channel (deactivated, a new security stamp, a sign-in ended), maybe before a revocation reached
   *  it (SEC2-1): every revealed value hides, and the channel is asked for again, refused while the access stays gone. */
  function revoked() {
    channel = undefined;
    [...subscriptions.keys()].forEach(revoke);
    sync();
  }
  /** Holds the account's channel while anything listens; follows a change of the signed-in account. Without a live connection,
   *  revocations still take effect at the next release and values keep their short server lifetime. */
  function sync() {
    const busy = count() > 0 || requestListeners.size > 0;
    const accountId = busy ? account.value : undefined;
    if (channel?.accountId !== accountId) {
      channel?.close();
      channel = undefined;
      if (accountId) {
        const live = (options.live ?? liveConnection)();
        const name = accountChannel(accountId);
        // The payloads name no channel: one tab has one account, so they are its channel's.
        events.forEach(event => live.route(event, () => name));
        const subscription = live.subscribe(name, { message, revoked });
        channel = { accountId, close: () => subscription.close() };
      }
    }
    if (busy) stopWatch ??= watch(account, sync);
    else { stopWatch?.(); stopWatch = undefined; }
  }
  return {
    subscribe(secretId, callback) {
      let set = subscriptions.get(secretId);
      if (!set) subscriptions.set(secretId, (set = new Set()));
      set.add(callback);
      sync();
      return () => {
        set.delete(callback);
        if (set.size === 0) subscriptions.delete(secretId);
        sync();
      };
    },
    onRequestChanged(listener) {
      requestListeners.add(listener);
      sync();
      return () => { requestListeners.delete(listener); sync(); };
    },
    revokeAll() {
      [...subscriptions.keys()].forEach(revoke);
    },
  };
}
