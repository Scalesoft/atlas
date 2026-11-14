import { toast } from "vue-sonner";
import t from "../i18n/shell";

/** A confirmation is glanceable; a failure has to survive being read. */
const CONFIRMATION_MS = 4000;
const FAILURE_MS = 8000;

export interface ToastOptions {
  /** Renders an "Zpět" action on the toast. Omit for a plain confirmation. */
  undo?: () => void;
  /** Another action of the toast, e.g. "Otevřít" what was just created; "Zpět" wins when both are given. */
  action?: { label: string; run: () => void };
  /** A toast with this id replaces the one shown under it, so a run of steps (Alt+arrow moves in the tree) is one toast, not a pile. */
  id?: string;
}

export interface Toaster {
  success(message: string, options?: ToastOptions): void;
  /** Work that takes a moment and is already under way (a PDF rendering): shown at once under `id`; the success or error toast with the same id replaces it, `dismiss(id)` removes it. */
  progress(message: string, id: string): void;
  dismiss(id: string): void;
  error(message: string): void;
  /** Something to know that blocked nothing (a contact placed twice, a space created without its picture); it is read, so it stays as long as an error. */
  warning(message: string): void;
  /** Removes every toast, e.g. when the account that raised them signs out. */
  dismissAll(): void;
}

/**
 * The one notification system (docs/interaction-rules.md P6): every delete, move to the trash, rename, move and conversion
 * confirms here with `undo` („Zpět“). Only the short publication state of the open page („Stránka byla publikována.“)
 * still shows in the page header's status slot (App.vue `confirmDocument`), until the owner decides whether it becomes a toast too.
 *
 * Transient confirmations only. Anything a form field owns -- a validation
 * error, a pending state -- stays inline next to that field: moving it into a
 * toast detaches it from the control it describes, which is a regression rather
 * than an improvement.
 */
export function useToast(): Toaster {
  return {
    success(message, options) {
      toast.success(message, {
        duration: CONFIRMATION_MS,
        id: options?.id,
        action: options?.undo
          ? { label: t.undo, onClick: options.undo }
          : options?.action ? { label: options.action.label, onClick: options.action.run } : undefined,
      });
    },
    progress(message, id) {
      toast.loading(message, { id, duration: Infinity });
    },
    dismiss(id) {
      toast.dismiss(id);
    },
    error(message) {
      toast.error(message, { duration: FAILURE_MS });
    },
    warning(message) {
      toast.warning(message, { duration: FAILURE_MS });
    },
    dismissAll() {
      toast.dismiss();
    },
  };
}
