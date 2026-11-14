/** What was typed while a picker was on its way: the text and whether Enter came after it. */
export interface TypeAhead { take(): { text: string; enter: boolean } }

/**
 * Keeps the keys typed between a pick that opens a picker (a slash menu entry) and the picker's search field taking the focus, so
 * `/kontakt` Enter `jan kovarik` Enter, typed in one go, searches for „jan kovarik“ and picks the first person instead of writing the
 * name into the document. Characters, Backspace and Enter are held back, as keys or as input without keys (an IME's or a tool's
 * insertText); the picker `take`s them when it mounts, which ends the capture (it ends by itself after two seconds).
 */
export function captureTypeAhead(doc: Document, ms = 2000): TypeAhead {
  let text = "", enter = false, done = false;
  const hold = (event: Event, typed: { text: string } | "Backspace" | "Enter") => {
    if (typed === "Enter") enter = true;
    else if (!enter) text = typed === "Backspace" ? text.slice(0, -1) : text + typed.text;
    event.preventDefault();
    event.stopPropagation();
  };
  const onKey = (event: KeyboardEvent) => {
    if (done || event.isComposing || event.ctrlKey || event.metaKey || event.altKey) return;
    if (event.key.length === 1) hold(event, { text: event.key });
    else if (event.key === "Backspace" || event.key === "Enter") hold(event, event.key);
  };
  // A held key never gets here (its default was prevented); text put in without a key does.
  const onInput = (event: InputEvent) => {
    if (done || event.isComposing) return;
    if (event.inputType === "insertText" && event.data) hold(event, { text: event.data });
    else if (event.inputType === "deleteContentBackward") hold(event, "Backspace");
    else if (event.inputType === "insertParagraph" || event.inputType === "insertLineBreak") hold(event, "Enter");
  };
  const stop = () => { done = true; doc.removeEventListener("keydown", onKey, true); doc.removeEventListener("beforeinput", onInput, true); clearTimeout(timer); };
  const timer = setTimeout(stop, ms);
  doc.addEventListener("keydown", onKey, true);
  doc.addEventListener("beforeinput", onInput, true);
  return { take() { stop(); return { text, enter }; } };
}
