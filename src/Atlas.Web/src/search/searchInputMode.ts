import { nextTick } from "vue";

/** The same input keeps its question and selection while the existing mode prefix changes. */
export async function switchSearchInputMode(field: HTMLInputElement | null, value: string, mode: "pages" | "ask" | "commands", apply: (value: string) => void) {
  const text = value.trimStart().replace(/^[>?]\s*/u, "");
  const prefix = mode === "commands" ? "> " : mode === "ask" ? "? " : "";
  const delta = prefix.length - (value.length - text.length);
  const start = field?.selectionStart, end = field?.selectionEnd, direction = field?.selectionDirection;
  const next = prefix + text;
  apply(next);
  await nextTick();
  field?.focus({ preventScroll: true });
  if (field && start != null && end != null) {
    const position = (offset: number) => Math.max(prefix.length, Math.min(next.length, offset + delta));
    field.setSelectionRange(position(start), position(end), direction ?? "none");
  }
}
