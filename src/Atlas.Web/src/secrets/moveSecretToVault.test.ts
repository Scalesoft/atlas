import { afterEach, describe, expect, it, vi } from "vitest";
import { createMarkdownEditorController, type MarkdownEditorController } from "@atlas/markdown/core";
import { insertInlineSecretReference } from "@atlas/markdown/plugins/secrets";
import { createDocumentWidgets } from "../documentWidgets";
import { createRevealPlugin, revealCommand } from "../editor/revealPlugin";
import type { RevealTarget } from "../editor/revealTarget";
import type { PickedSecret, SecretPickRequest } from "../assist/types";
import { moveSecretToVault } from "./moveSecretToVault";

// „Přesunout do trezoru“ for a finding of the publication check (plan §12 H.5), with or without assistance.
const password = "Srv#2026!rack";
const secretId = "0b6a0d9e-6e4e-4a8e-9a3f-1c2d3e4f5a61";
const controllers: MarkdownEditorController<"rich">[] = [];
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); });

function editor(text: string) {
  const controller = createMarkdownEditorController({
    profile: "rich",
    plugins: [...createDocumentWidgets({ kind: "edit" }), createRevealPlugin()],
    source: { kind: "local", value: { format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text }] }] } } },
  });
  controllers.push(controller);
  const host = document.createElement("div");
  document.body.append(host);
  controller.attachView(host, { ariaLabel: "Dokument" });
  const pick = vi.fn(async (_request: SecretPickRequest): Promise<PickedSecret | null> => ({ command: insertInlineSecretReference({ secretId }), inline: true, vaultName: "Provoz" }));
  const host_ = { controller: () => controller, reveal: (target: RevealTarget) => controller.dispatch(revealCommand(target)), pick, notify: vi.fn(), warn: vi.fn() };
  return { controller, host: host_ };
}
const text = `Nouzový účet: admin, heslo ${password}`;
const target: RevealTarget = { kind: "text", path: [0], from: text.indexOf(password), to: text.length };

describe("moveSecretToVault", () => {
  it("replaces the found password with an inline reference in one undo step", async () => {
    const { controller, host } = editor(text);
    expect(await moveSecretToVault(host, target)).toBe(true);
    expect(host.pick).toHaveBeenCalledWith(expect.objectContaining({ mode: "replace", initialQuery: "Nouzový účet admin" }));
    const json = JSON.stringify(controller.getValue());
    expect(json).not.toContain(password);
    expect(json).toContain(secretId);
    expect(host.notify).toHaveBeenCalledWith("Heslo je v trezoru Provoz. V textu zůstal jen odkaz.");
    expect(JSON.stringify(host.pick.mock.calls)).not.toContain(password);
    controller.dispatch({ type: "undo" });
    expect(JSON.stringify(controller.getValue())).toContain(password);
  });

  it("writes nothing when the text changed while the picker was open", async () => {
    const { controller, host } = editor(text);
    host.pick.mockImplementationOnce(async () => {
      // An edit before the password: the target's offsets now point one character off.
      controller.dispatch({ type: "paste-markdown", text: "X" }, { replaceRange: { from: 2, to: 2 } });
      return { command: insertInlineSecretReference({ secretId }), inline: true };
    });
    expect(await moveSecretToVault(host, target)).toBe(false);
    expect(host.warn).toHaveBeenCalledWith("Návrh už neodpovídá textu.");
    expect(JSON.stringify(controller.getValue())).not.toContain(secretId);
  });

  it("does nothing for a target that is not text or a cancelled picker", async () => {
    const { controller, host } = editor(text);
    expect(await moveSecretToVault(host, { kind: "page", pageId: "x" })).toBe(false);
    host.pick.mockResolvedValueOnce(null);
    expect(await moveSecretToVault(host, target)).toBe(false);
    expect(JSON.stringify(controller.getValue())).toContain(password);
  });
});
