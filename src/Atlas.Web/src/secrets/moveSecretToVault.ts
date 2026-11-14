import type { MarkdownCommand, MarkdownEditorController, MarkdownRichValue } from "@atlas/markdown/core";
import t from "../i18n/secrets";
import { insertInlineSecretReference, insertSecretReference } from "@atlas/markdown/plugins/secrets";
import { fnv1a } from "../assist/rules/examples";
import { secretSearchText } from "../assist/secretQuery";
import type { PickedSecret, SecretPickRequest } from "../assist/types";
import type { RevealTarget } from "../editor/revealTarget";

/** The insert command of a picked reference (the secrets plugin stays inside src/secrets). */
export function secretReferenceCommand(secretId: string, inline: boolean): MarkdownCommand {
  return inline ? insertInlineSecretReference({ secretId }) : insertSecretReference({ secretId });
}

interface JsonNode { type?: string; text?: string; content?: JsonNode[] }

/** The textblock at a child-index path and its text, one character per position (as `RevealTarget` counts). */
function blockAt(value: MarkdownRichValue, path: readonly number[]): string | null {
  let node: JsonNode | undefined = value.document as JsonNode;
  for (const index of path) node = node?.content?.[index];
  if (!node?.content?.some(child => child.type === "text")) return null;
  return node.content.map(child => child.type === "text" ? child.text ?? "" : child.type === "hardBreak" ? "\n" : "￼").join("");
}

export interface MoveSecretHost {
  readonly controller: () => MarkdownEditorController<"rich"> | undefined;
  /** Selects the target in the live editor (DocumentEditor `reveal`). */
  readonly reveal: (target: RevealTarget) => boolean;
  readonly pick: (request: SecretPickRequest) => Promise<PickedSecret | null>;
  readonly notify: (message: string) => void;
  readonly warn: (message: string) => void;
}

/**
 * „Přesunout do trezoru“ for a password the publication check found (plan §12 H.5, `DocumentEditor.moveSecretToVault`),
 * also with assistance off: the passwords are protected on publishing either way. Replace mode (decision O2): the
 * author stores the value in KeePass and picks it; the span becomes an inline reference in one undo step. The value
 * is never sent: the picker gets the words around it, and only a hash of the span is kept to see whether it changed
 * while the dialog was open.
 */
export async function moveSecretToVault(host: MoveSecretHost, target: RevealTarget, signal: AbortSignal = new AbortController().signal): Promise<boolean> {
  if (target.kind !== "text") return false;
  const live = host.controller();
  if (!live?.state.editable) return false;
  const spanHash = (value: MarkdownRichValue) => {
    const text = blockAt(value, target.path);
    return text !== null && target.to <= text.length ? fnv1a(text.slice(target.from, target.to)) : null;
  };
  const before = live.getValue();
  const text = blockAt(before, target.path);
  const hash = spanHash(before);
  if (text === null || hash === null || !host.reveal(target)) return false;
  const picked = await host.pick({ mode: "replace", initialQuery: secretSearchText(text), signal });
  if (!picked || signal.aborted) return false;
  const current = host.controller();
  if (!current?.state.editable || spanHash(current.getValue()) !== hash || !host.reveal(target)) {
    host.warn(t.staleProposal);
    return false;
  }
  // The selection is the span; the insert command replaces it and closes the history before itself.
  if (!current.dispatch(picked.command)) return false;
  host.notify(t.moved(picked.vaultName));
  return true;
}
