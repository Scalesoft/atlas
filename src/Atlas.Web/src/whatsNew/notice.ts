import { h } from "vue";
import { toast } from "vue-sonner";
import { put } from "../api";
import t from "../i18n/whatsnew";
import ReleaseMark from "../components/release/ReleaseMark.vue";

/**
 * „Co je nového“: after sign-in, a release the account has not seen yet is announced once by a toast at the top (the one notification
 * system, docs/interaction-rules.md P6). Its action opens the notes (`#/whats-new`); the action, × and Esc all record the release on
 * the server, so the notice is per account, not per device. The notes themselves are files in `release-notes/` (whatsNew/releaseNotes.ts).
 */

/** The release of the server's version ("3.5.0+abc1234" → "3.5.0"); a development or unknown build has none. */
export function releaseOf(serverVersion: string | undefined): string | undefined {
  const release = serverVersion?.split("+")[0];
  return release && /^\d+\.\d+\.\d+$/.test(release) ? release : undefined;
}

/** „3.5“ for 3.5.0, a patch release in full. */
export const releaseLabel = (release: string) => release.replace(/\.0$/, "");

/** „3.5“ for 3.5.0 and its patch releases 3.5.1, 3.5.2 …: the named version a release belongs to. */
export const releaseLine = (release: string) => release.replace(/\.\d+$/, "");

/**
 * What the shell does after sign-in: `show` the notice, `adopt` the running release without it (a new account, which has seen
 * nothing), or `none` (seen already, or a development build, which has no notes).
 */
export function whatsNewNotice(serverVersion: string | undefined, seen: string | null | undefined): "show" | "adopt" | "none" {
  const release = releaseOf(serverVersion);
  if (!release || seen === release) return "none";
  return seen ? "show" : "adopt";
}

export const markReleaseNotesSeen = (version: string) => put<void>("/profile/release-notes", { version });

const toastId = "whats-new";

/**
 * The notice: it waits until dismissed and steals no focus (sonner's region is a polite live region, reached by Tab or Alt+T).
 * `seen` runs once, whichever way it closes; so does the returned `close` (the page opened another way, e.g. from the palette).
 * `note` is the named version the release belongs to (x.y.0, whatsNew/releaseNotes.ts): the notice says its codename and shows its
 * mark small, as decoration. A patch release after one of the same version the account has seen (`since`) is a small update of it.
 */
export function showWhatsNew(release: string, actions: { open: () => void; seen: () => void }, note?: { codename?: string; mark?: string }, since?: string): () => void {
  let done = false;
  const finish = () => {
    if (done) return;
    done = true;
    // Closed from the keyboard, the focus goes back to the workspace, not to the body the removed toast leaves it on.
    if (document.activeElement?.closest(`.${toastId}-toast`)) document.querySelector<HTMLElement>("main")?.focus({ preventScroll: true });
    document.removeEventListener("keydown", escape, true);
    actions.seen();
  };
  // Esc closes it while the keyboard is in it; elsewhere Esc belongs to whatever has the focus.
  const escape = (event: KeyboardEvent) => {
    if (event.key !== "Escape" || !(event.target instanceof Element) || !event.target.closest(`.${toastId}-toast`)) return;
    event.stopPropagation();
    finish();
    toast.dismiss(toastId);
  };
  document.addEventListener("keydown", escape, true);
  const mark = note?.mark;
  const named = `${releaseLine(release)}${note?.codename ? ` ${note.codename}` : ""}`;
  const patch = since !== undefined && since !== release && releaseLine(since) === releaseLine(release);
  toast(patch ? t.patchTitle(release, named) : t.title(named), {
    id: toastId,
    class: `${toastId}-toast`,
    position: "top-center",
    duration: Number.POSITIVE_INFINITY,
    description: patch ? t.patchText : t.text,
    action: { label: t.show, onClick: () => { finish(); actions.open(); } },
    onDismiss: finish,
    ...(mark ? { icon: () => h(ReleaseMark, { mark, variant: "icon" }) } : {}),
  });
  return () => { if (!done) { finish(); toast.dismiss(toastId); } };
}
