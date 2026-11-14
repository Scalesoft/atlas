import { onBeforeUnmount, ref, watch, type Ref } from "vue";
import { api } from "./api";
import { whenIdle } from "./idle";
import { accountChannel } from "./live/accountChannel";
import { liveConnection, type LiveConnection } from "./live/liveConnection";

/**
 * The unread badge of „Oznámení“ (inbox spec 2026-10-29), the one part of the inbox on the first screen; the inbox itself
 * (components/notifications) loads with its view. The server sends only `NotificationsChanged` on the account's own channel
 * `user:{id}` of the one live connection; the client reads the count itself.
 */
export const unreadNotificationsApi = { unread: (signal?: AbortSignal) => api<{ count: number }>("/notifications/unread", { signal }) };

/** The signed-in account's unread count: the sidebar, the phone's menu button and Knihovna's tab show it. */
export const unreadNotifications = ref(0);

let openInboxes = 0;
/** An open inbox reloads its list on every signal and reports the count from it, so the badge does not read the count as well.
 *  Returns the close. */
export function inboxOpened() {
  openInboxes++;
  let open = true;
  return () => { if (open) { open = false; openInboxes--; } };
}

/** After the first view's own requests (as the page view's warm-up in App.vue), then idle time: the badge never competes with them.
 *  Returns the cancel, so an unmounted badge leaves no timer behind. */
function afterFirstView(work: () => void) {
  let wanted = true;
  const timer = window.setTimeout(() => whenIdle(() => { if (wanted) work(); }), 1500);
  return () => { wanted = false; window.clearTimeout(timer); };
}

/**
 * The badge kept current over the live connection: started once the first view's requests are done and the browser is idle (never
 * before them), read on every subscription of `user:{id}` (the first and after a reconnect) and on every `NotificationsChanged`
 * while no inbox is open. `signal` counts the changes, so an open inbox reloads with them.
 */
export function useUnreadNotifications(userId: Ref<string | undefined>, options: { api?: typeof unreadNotificationsApi; live?: () => LiveConnection; idle?: (work: () => void) => (() => void) | void } = {}) {
  const unread = unreadNotifications, signal = ref(0);
  const source = options.api ?? unreadNotificationsApi;
  let close: (() => void) | undefined, cancelIdle: (() => void) | void, abort: AbortController | undefined, generation = 0;
  async function refresh() {
    abort?.abort();
    const controller = (abort = new AbortController());
    try {
      unread.value = (await source.unread(controller.signal)).count;
    } catch {
      // A badge that cannot load keeps its last count; the next signal tries again.
    }
  }
  watch(userId, id => {
    close?.();
    close = undefined;
    cancelIdle?.();
    unread.value = 0;
    const current = ++generation;
    if (!id) return;
    cancelIdle = (options.idle ?? afterFirstView)(() => {
      if (current !== generation) return;
      const live = (options.live ?? liveConnection)();
      live.route("NotificationsChanged", payload => (payload as { channel?: string } | undefined)?.channel);
      const listen = () => {
        const subscription = live.subscribe(accountChannel(id), {
          subscribed: () => void refresh(),
          message: event => {
            if (event !== "NotificationsChanged") return;
            signal.value++;
            if (!openInboxes) void refresh();
          },
          // The server ended the channel (a new security stamp, a sign-in ended): asked for again, as the passwords do; refused
          // while the account's access is gone, and the badge would otherwise stay silent until the account changes (ARCH3-7).
          revoked: () => { if (current === generation) listen(); },
        });
        close = () => subscription.close();
      };
      listen();
    });
  }, { immediate: true });
  onBeforeUnmount(() => { generation++; cancelIdle?.(); close?.(); abort?.abort(); });
  return { unread, signal, refresh };
}
