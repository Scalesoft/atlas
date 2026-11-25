import type { MarkdownPlugin } from "@atlas/markdown/core";
import shellMore from "../i18n/shell-more";
import { createIconElement } from "@atlas/markdown/icons";
import { FEEDBACK_HOLD_MS } from "../motion/aiMotion";
import "./codeCopyPlugin.css";

/** Names a reader recognises; any other language shows its id in capitals. */
const LANGUAGE_NAMES: Record<string, string> = {
  js: "JavaScript", javascript: "JavaScript", ts: "TypeScript", typescript: "TypeScript", cs: "C#", csharp: "C#",
  sh: "Shell", bash: "Bash", ps1: "PowerShell", powershell: "PowerShell", yml: "YAML", plaintext: "Text", text: "Text",
};
export function codeLanguageLabel(language: string) {
  return LANGUAGE_NAMES[language.toLowerCase()] ?? language.toUpperCase();
}

/**
 * A code block being read gets a quiet header with its language and „Kopírovat“ (refresh 2.5, board 03). It decorates the
 * statically rendered page only (the plugin's onRender hook): the stored document, the editor and its own code-block
 * toolbar are untouched. Copying is a reader's content action, so it works without the right to edit.
 */
export function createCodeCopyPlugin(): MarkdownPlugin {
  return {
    id: "atlas.code-copy", apiVersion: 1, profiles: ["markdown-safe", "rich"],
    create: () => ({
      onRender({ container, ownerDocument, signal }) {
        const timers = new Set<ReturnType<typeof setTimeout>>();
        for (const pre of container.querySelectorAll<HTMLElement>("pre")) {
          const code = pre.querySelector<HTMLElement>(":scope > code");
          if (!code || pre.closest("[data-md-module]") || pre.querySelector(":scope > .atlas-code-head")) continue;
          const language = [...code.classList].find((name) => name.startsWith("language-"))?.slice("language-".length) ?? "";
          const head = ownerDocument.createElement("div");
          head.className = "atlas-code-head";
          const label = ownerDocument.createElement("span");
          label.className = "atlas-code-language";
          label.textContent = language ? codeLanguageLabel(language) : shellMore.code.code;
          const button = ownerDocument.createElement("button");
          button.type = "button";
          button.className = "atlas-code-copy";
          button.setAttribute("aria-label", language ? shellMore.code.copyLanguage(codeLanguageLabel(language)) : shellMore.code.copyCode);
          const text = ownerDocument.createElement("span");
          text.textContent = shellMore.code.copy;
          button.append(createIconElement(ownerDocument, "copy", 14), text);
          const status = ownerDocument.createElement("span");
          status.className = "visually-hidden";
          status.setAttribute("role", "status");
          button.addEventListener("click", async () => {
            let copied = false;
            try { await ownerDocument.defaultView?.navigator.clipboard.writeText(code.textContent ?? ""); copied = true; } catch { /* reported below */ }
            if (signal.aborted) return;
            text.textContent = copied ? shellMore.code.copied : shellMore.code.notCopied;
            status.textContent = copied ? shellMore.code.copiedStatus : shellMore.code.copyFailedStatus;
            button.dataset.state = copied ? "copied" : "failed";
            const timer = setTimeout(() => { timers.delete(timer); text.textContent = shellMore.code.copy; status.textContent = ""; delete button.dataset.state; }, FEEDBACK_HOLD_MS);
            timers.add(timer);
          }, { signal });
          head.append(label, button, status);
          pre.prepend(head);
        }
        return () => { timers.forEach(clearTimeout); container.querySelectorAll(".atlas-code-head").forEach((head) => head.remove()); };
      },
    }),
  };
}
