/**
 * The Hub (OIDC) step of a fresh sign-in (WP4 V4): the provider's login runs in a popup, which returns to `reauthReturnUrl`
 * on our origin after the callback. That page closes itself before the app mounts (main.ts); the opener notices that the popup
 * closed or came back and then asks the server for the challenge state. Nothing here polls the API: only the popup's own state
 * is watched until it closes.
 */
export const reauthWindowName = "atlas-reauth";
export const reauthReturnParameter = "atlasReauth";
export const reauthReturnUrl = `/?${reauthReturnParameter}=1`;

/** Opens the popup synchronously inside the click, so the browser does not block it; the address follows once known. */
export function openReauthPopup(open: typeof window.open = (...args) => window.open(...args)): Window | null {
  return open("", reauthWindowName, "popup,width=520,height=720");
}

/** Resolves when the popup closed, came back to our origin, or the deadline passed (then it is closed). */
export function waitForReauthPopup(popup: Window, deadline: number, origin = location.origin, interval = 400): Promise<void> {
  return new Promise((resolve) => {
    let away = false;
    const timer = setInterval(() => {
      let back = false;
      try {
        // Reading the location throws while the popup shows the provider (another origin).
        back = away && popup.location.origin === origin;
      } catch {
        away = true;
      }
      if (popup.closed || back || Date.now() > deadline) {
        clearInterval(timer);
        if (!popup.closed) popup.close();
        resolve();
      }
    }, interval);
  });
}

/** In main.ts: the popup's return page closes itself instead of starting a second Atlas. */
export function closeReauthReturn(win: Window = window): boolean {
  if (win.name !== reauthWindowName || !win.opener || !new URLSearchParams(win.location.search).has(reauthReturnParameter)) return false;
  win.close();
  return true;
}
