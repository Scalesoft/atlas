import { ref } from "vue";

/**
 * The signed-in account's own channel `user:{id}` on the tab's one live connection (plan §11): its inbox signal
 * (`NotificationsChanged`) and its security events (`secretAccessRevoked`, `accessRequestChanged`). Only the account's own
 * connections may subscribe it. The session owns it: the shell (App.vue) sets it at sign-in and clears it at sign-out, so no listener
 * (the badge, a revealed password) depends on another's life (ARCH2-5).
 */
export const liveAccount = ref<string>();
export const accountChannel = (accountId: string) => `user:${accountId}`;
