import { afterEach, describe, expect, it, vi } from "vitest";
import { codeLanguageLabel, createCodeCopyPlugin } from "./codeCopyPlugin";

function render(html: string) {
  const container = document.createElement("div");
  container.innerHTML = html;
  document.body.append(container);
  const controller = new AbortController();
  const instance = createCodeCopyPlugin().create({} as never);
  const cleanup = instance.onRender!({ container, ownerDocument: document, signal: controller.signal, document: { type: "doc" } as never });
  return { container, controller, cleanup: cleanup as () => void };
}
afterEach(() => { document.body.innerHTML = ""; vi.useRealTimers(); vi.unstubAllGlobals(); });

describe("codeCopyPlugin", () => {
  it("heads every read code block with its language and copies only the code", async () => {
    vi.useFakeTimers();
    const writeText = vi.fn(async () => {});
    vi.stubGlobal("navigator", { clipboard: { writeText } });
    const { container, cleanup } = render(`<pre><code class="language-ts">const a = 1;</code></pre><div data-md-module="x"><pre><code>skip</code></pre></div><pre><code>plain</code></pre>`);
    const heads = container.querySelectorAll(".atlas-code-head");
    expect(heads).toHaveLength(2);
    expect(heads[0]!.querySelector(".atlas-code-language")!.textContent).toBe("TypeScript");
    expect(heads[1]!.querySelector(".atlas-code-language")!.textContent).toBe("Kód");
    const button = heads[0]!.querySelector<HTMLButtonElement>("button")!;
    expect(button.getAttribute("aria-label")).toBe("Kopírovat kód TypeScript");
    button.click();
    await vi.waitFor(() => expect(writeText).toHaveBeenCalledWith("const a = 1;"));
    expect(button.textContent).toBe("Zkopírováno");
    expect(heads[0]!.querySelector("[role=status]")!.textContent).toBe("Kód zkopírován");
    vi.runAllTimers();
    expect(button.textContent).toBe("Kopírovat");
    cleanup();
    expect(container.querySelector(".atlas-code-head")).toBeNull();
  });

  it("says when the clipboard refused, and names languages readably", async () => {
    vi.stubGlobal("navigator", { clipboard: { writeText: vi.fn(async () => { throw new Error("denied"); }) } });
    const { container } = render(`<pre><code class="language-json">{}</code></pre>`);
    const button = container.querySelector<HTMLButtonElement>(".atlas-code-copy")!;
    button.click();
    await vi.waitFor(() => expect(button.dataset.state).toBe("failed"));
    expect(container.querySelector("[role=status]")!.textContent).toContain("ručně");
    expect(["cs", "yml", "rust"].map(codeLanguageLabel)).toEqual(["C#", "YAML", "RUST"]);
  });
});
