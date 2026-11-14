import { computed, inject, onBeforeUnmount, onMounted, provide, ref, type ComputedRef, type InjectionKey, type Ref } from "vue";
import { errorMessage } from "../../../api";
import { accessRequestApi, type AccessRequest, type AccessRequestList } from "../../../secretRelease";
import { createSecretAccessEvents, type SecretAccessEvents } from "../../../secrets/secretEvents";

/**
 * The pending access requests the signed-in manager can decide (GET /api/access-requests), shared by the navigation badges, the
 * overview and the Žádosti lists of a space and a vault. Live: the server sends accessRequestChanged on /hubs/markdown to the
 * requester and the managers, and the feed reloads then (the same subscription as the password references, secrets/secretEvents.ts).
 * It also reloads when the window regains focus. Nothing polls.
 */
export interface AccessRequestFeed {
  pending: Ref<AccessRequest[]>;
  loaded: Ref<boolean>;
  error: Ref<string>;
  count: ComputedRef<number>;
  load(): Promise<void>;
  inSpace(spaceId: string): AccessRequest[];
  inVault(vaultId: string): AccessRequest[];
  /** A decided request leaves the pending list at once; the next load confirms it. */
  settle(request: AccessRequest): void;
  /** Starts the live updates (the host calls it once it knows the account manages vaults). */
  listen(): void;
}

const key: InjectionKey<AccessRequestFeed> = Symbol("access-request-feed");

export function createAccessRequestFeed(
  options: { list?: () => Promise<AccessRequestList>; events?: () => SecretAccessEvents } = {},
): AccessRequestFeed {
  const list = options.list ?? (() => accessRequestApi.list("pending"));
  const pending = ref<AccessRequest[]>([]);
  const loaded = ref(false);
  const error = ref("");
  let generation = 0;
  let disposed = false;
  let stop: (() => void) | undefined;

  async function load() {
    const current = ++generation;
    try {
      const page = await list();
      if (disposed || current !== generation) return;
      pending.value = page.items.filter((item) => item.state === "pending");
      error.value = "";
    } catch (failure) {
      if (!disposed && current === generation) error.value = errorMessage(failure);
    } finally {
      if (!disposed && current === generation) loaded.value = true;
    }
  }
  const focused = () => void load();
  onMounted(() => window.addEventListener("focus", focused));
  onBeforeUnmount(() => {
    disposed = true;
    window.removeEventListener("focus", focused);
    stop?.();
  });
  return {
    pending,
    loaded,
    error,
    count: computed(() => pending.value.length),
    load,
    inSpace: (spaceId) => pending.value.filter((item) => item.spaceId === spaceId),
    inVault: (vaultId) => pending.value.filter((item) => item.vaultId === vaultId),
    settle(request) {
      generation++;
      pending.value = pending.value.filter((item) => item.id !== request.id);
    },
    listen() {
      if (stop || disposed) return;
      stop = (options.events ?? createSecretAccessEvents)().onRequestChanged(() => void load());
    },
  };
}

export function provideAccessRequestFeed(feed: AccessRequestFeed) {
  provide(key, feed);
}
/** Undefined outside a settings surface. */
export function useAccessRequestFeed(): AccessRequestFeed | undefined {
  return inject(key, undefined);
}
