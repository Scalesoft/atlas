import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PublishControl from "./PublishControl.vue";
import { ApiError } from "../../api";
import type { PublicationCheck, PublicationCheckRun } from "../../publishing/publication";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; vi.restoreAllMocks(); });

const settle = async () => { await flushPromises(); await new Promise(resolve => setTimeout(resolve, 30)); await flushPromises(); };
const panel = () => document.querySelector<HTMLElement>('[role="dialog"][aria-label="Před publikací"]');
const field = () => document.querySelector<HTMLInputElement>('input[name="changeSummary"]')!;
const panelButton = (name: string) => [...panel()!.querySelectorAll("button")].find(button => button.textContent?.trim() === name)!;

function mountControl(props: Record<string, unknown> = {}) {
  const publish = vi.fn<(summary: string | null) => Promise<boolean | void>>().mockResolvedValue(true);
  // A stand-in for the document status that App renders next to the control.
  document.body.insertAdjacentHTML("beforeend", '<div class="document-status">Koncept</div>');
  wrapper = mount(PublishControl, { props: { publish, ...props }, attachTo: document.body });
  const main = () => wrapper!.find("button.publish-main");
  const arrow = () => wrapper!.get('button[aria-label="Publikovat s popisem změny…"]');
  return { publish, main, arrow };
}
async function type(value: string) { field().value = value; field().dispatchEvent(new Event("input", { bubbles: true })); await flushPromises(); }

describe("PublishControl", () => {
  it("offers Zahodit změny… in the panel only when the page can return to a published version, and hands it to the host", async () => {
    const { arrow, publish } = mountControl();
    await arrow().trigger("click"); await settle();
    expect(panelButton("Zahodit změny…")).toBeUndefined();
    await wrapper!.setProps({ canDiscard: true });
    panelButton("Zahodit změny…").click(); await settle();
    expect(wrapper!.emitted("discard")).toHaveLength(1);
    expect(arrow().attributes("aria-expanded")).toBe("false");
    expect(publish).not.toHaveBeenCalled();
  });

  it("reopens a closing panel without losing its draft or leaving it inert", async () => {
    const motions: Array<{ finished: Promise<void>; finish: () => void; cancel: ReturnType<typeof vi.fn> }> = [];
    Object.defineProperty(Element.prototype, "animate", { configurable: true, writable: true, value: vi.fn(() => {
      let finish!: () => void;
      const finished = new Promise<void>(resolve => { finish = resolve; });
      const motion = { finished, finish, cancel: vi.fn() };
      motions.push(motion);
      return motion;
    }) });
    try {
      const { arrow, publish } = mountControl();
      await arrow().trigger("click"); await settle();
      await type("Rozepsaná změna");
      await arrow().trigger("click"); await flushPromises();
      const leaving = motions.at(-1)!;
      expect(panel()!.hasAttribute("inert")).toBe(true);
      await arrow().trigger("click"); await settle();
      leaving.finish(); await settle();
      expect(arrow().attributes("aria-expanded")).toBe("true");
      expect(panel()!.hasAttribute("inert")).toBe(false);
      expect(panel()!.hasAttribute("aria-hidden")).toBe(false);
      expect(field().value).toBe("Rozepsaná změna");
      expect(document.activeElement).toBe(field());
      expect(publish).not.toHaveBeenCalled();
    } finally { Reflect.deleteProperty(Element.prototype, "animate"); }
  });

  it("shows both halves as unavailable while preparing instead of accepting repeated clicks", async () => {
    let finish!: (value: boolean) => void;
    const prepare = vi.fn(() => new Promise<boolean>(resolve => { finish = resolve; }));
    const { main, arrow, publish } = mountControl({ prepare });
    await main().trigger("click"); await flushPromises();
    expect(main().attributes("disabled")).toBeDefined();
    expect(arrow().attributes("disabled")).toBeDefined();
    await main().trigger("click");
    expect(prepare).toHaveBeenCalledOnce();
    finish(false); await settle();
    expect(main().attributes("disabled")).toBeUndefined();
    expect(publish).not.toHaveBeenCalled();
  });

  it("publishes at once from the main button without opening a panel", async () => {
    const { publish, main, arrow } = mountControl();
    expect(arrow().attributes("aria-haspopup")).toBe("dialog");
    expect(arrow().attributes("aria-expanded")).toBe("false");
    await main().trigger("click"); await settle();
    expect(publish).toHaveBeenCalledExactlyOnceWith(null);
    expect(panel()).toBeNull();
  });

  it("opens the anchored panel from the arrow and publishes with the trimmed summary", async () => {
    const animate = vi.fn(() => ({ finished: Promise.resolve(), cancel: vi.fn() }));
    Object.defineProperty(Element.prototype, "animate", { configurable: true, writable: true, value: animate });
    try {
      const { publish, arrow } = mountControl();
      await arrow().trigger("click"); await settle();
      expect(panel()).not.toBeNull();
      expect(panel()!.hasAttribute("aria-modal")).toBe(false);
      expect(document.activeElement).toBe(field());
      expect(animate).toHaveBeenCalled(); // the panel drops from the button
      await type("  Doplněn postup pro mobilní zařízení  ");
      expect(document.body.textContent).toContain("35 / 500");
      animate.mockClear();
      panelButton("Publikovat").click(); await settle();
      expect(publish).toHaveBeenCalledExactlyOnceWith("Doplněn postup pro mobilní zařízení");
      expect(panel()).toBeNull();
      // "Publikováno" pops on the document status.
      expect(animate.mock.instances.some(el => (el as Element).matches?.(".document-status"))).toBe(true);
    } finally { Reflect.deleteProperty(Element.prototype, "animate"); }
  });

  it("publishes with Enter in the field and keeps one line, 500 characters at most", async () => {
    const { publish, arrow } = mountControl();
    await arrow().trigger("click"); await settle();
    await type("a".repeat(501));
    expect(panel()!.textContent).toContain("Popis změny smí mít jeden řádek a nejvýše 500 znaků.");
    expect(field().getAttribute("aria-invalid")).toBe("true");
    expect(panelButton("Publikovat").disabled).toBe(true);
    field().dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    await settle();
    expect(publish).not.toHaveBeenCalled();
    await type("Krátký popis");
    const enter = new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true });
    field().dispatchEvent(enter); await settle();
    expect(enter.defaultPrevented).toBe(true);
    expect(publish).toHaveBeenCalledExactlyOnceWith("Krátký popis");
  });

  it("publishes the manual description while the optional model proposal is pending", async () => {
    const suggestSummary = vi.fn((_signal: AbortSignal) => new Promise<null>(() => {}));
    const { publish, arrow } = mountControl({ suggestSummary, summaryContextKey: "page-1:2" });
    await arrow().trigger("click"); await settle();
    await type("Ručně popsaná změna");
    panel()!.querySelector<HTMLButtonElement>("[data-change-summary-suggest]")!.click();
    await flushPromises();
    expect(suggestSummary).toHaveBeenCalledOnce();
    expect(field().disabled).toBe(false);
    expect(panelButton("Publikovat").disabled).toBe(false);
    panelButton("Publikovat").click(); await settle();
    expect(publish).toHaveBeenCalledExactlyOnceWith("Ručně popsaná změna");
    expect(panel()).toBeNull();
    expect(suggestSummary.mock.calls[0]![0].aborted).toBe(true);
  });

  it("keeps the summary and shows the error when publishing fails", async () => {
    const publish = vi.fn().mockRejectedValue(new ApiError(400, "Popis změny smí mít jeden řádek a nejvýše 500 znaků."));
    const { arrow } = mountControl({ publish });
    await arrow().trigger("click"); await settle();
    await type("Popis, který server odmítl");
    panelButton("Publikovat").click(); await settle();
    expect(panel()).not.toBeNull();
    expect(field().value).toBe("Popis, který server odmítl");
    expect(panel()!.querySelector('[role="alert"]')!.textContent).toContain("Popis změny smí mít jeden řádek");
    expect(panelButton("Publikovat").disabled).toBe(false); // can be corrected and sent again
  });

  it("Escape returns to editing without publishing and keeps the typed summary for the next opening", async () => {
    const { publish, arrow } = mountControl();
    await arrow().trigger("click"); await settle();
    await type("Rozepsaný popis");
    field().dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    await settle();
    expect(panel()).toBeNull();
    expect(publish).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(arrow().element);
    await arrow().trigger("click"); await settle();
    expect(field().value).toBe("Rozepsaný popis");
    panelButton("Zpět k úpravám").click(); await settle();
    expect(panel()).toBeNull();
    expect(publish).not.toHaveBeenCalled();
  });

  it.each(["prepare", "check"] as const)("Back cancels publication while %s is pending", async (stage) => {
    let finish!: () => void;
    const pending = new Promise<void>(resolve => { finish = resolve; });
    const prepare = async () => { if (stage === "prepare") await pending; return true; };
    const check = () => ({ local: [], server: stage === "check" ? pending.then(() => []) : Promise.resolve([]) });
    const { publish, arrow } = mountControl({ prepare, check });
    await arrow().trigger("click"); await settle();
    panelButton("Publikovat").click(); await flushPromises();
    panelButton("Zpět k úpravám").click(); await settle();
    finish(); await settle();
    expect(publish).not.toHaveBeenCalled();
    expect(panel()).toBeNull();
  });

  it("does not publish when the page could not be prepared, and asks to publish anyway on a warning", async () => {
    const prepare = vi.fn().mockResolvedValue(false);
    const { publish, main, arrow } = mountControl({ prepare, checks: [{ id: "secret", severity: "warning", text: "Možné heslo v textu" }] });
    await main().trigger("click"); await settle();
    expect(prepare).toHaveBeenCalledOnce();
    expect(publish).not.toHaveBeenCalled();
    await arrow().trigger("click"); await settle();
    expect(panelButton("Publikovat i tak")).toBeDefined();
  });

  describe("with the publication check (package E)", () => {
    const secret: PublicationCheck = { id: "secret:1:6", kind: "secret", severity: "warning", text: "Možné heslo v textu (sekce Přístup)", target: { kind: "text", path: [1], from: 6, to: 19 } };
    const link: PublicationCheck = { id: "link:p2", kind: "link", severity: "warning", text: "Odkaz na nedostupnou stránku", target: { kind: "page", pageId: "p2" } };
    const section: PublicationCheck = { id: "section:3", kind: "section", severity: "info", text: "Prázdná sekce „Linux“", target: { kind: "heading", id: "linux", path: [3] } };
    function checked(local: PublicationCheck[], server: Promise<readonly PublicationCheck[]> = Promise.resolve([])) {
      return vi.fn<() => PublicationCheckRun | null>(() => ({ local, server }));
    }
    const rowTexts = () => [...panel()!.querySelectorAll("li.publish-finding strong")].map(row => row.textContent?.trim());

    it("publishes immediately when nothing is found", async () => {
      const prepare = vi.fn().mockResolvedValue(true);
      const check = checked([]);
      const { publish, main } = mountControl({ prepare, check });
      await main().trigger("click"); await settle();
      expect(prepare).toHaveBeenCalledOnce();
      // The check runs on what prepare saved, then the page publishes exactly as without it.
      expect(prepare.mock.invocationCallOrder[0]).toBeLessThan(check.mock.invocationCallOrder[0]!);
      expect(publish).toHaveBeenCalledExactlyOnceWith(null);
      expect(panel()).toBeNull();
    });

    it("opens the findings panel with Publikovat i tak for warnings", async () => {
      let answer!: (findings: readonly PublicationCheck[]) => void;
      const prepare = vi.fn().mockResolvedValue(true);
      const check = checked([section, secret], new Promise(resolve => { answer = resolve; }));
      const { publish, main } = mountControl({ prepare, check });
      await main().trigger("click"); await settle();
      // Local findings open the panel at once; the links are still being checked where they will appear.
      expect(publish).not.toHaveBeenCalled();
      expect(panel()).not.toBeNull();
      expect(rowTexts()).toEqual(["Možné heslo v textu (sekce Přístup)", "Kontroluji…", "Prázdná sekce „Linux“"]);
      expect(panelButton("Publikovat i tak")).toBeDefined();
      expect(panelButton("Zpět k úpravám")).toBeDefined();
      // Opening is the result of the click, so focus goes to the first action of the panel.
      expect(document.activeElement?.textContent?.trim()).toBe("Zobrazit");
      expect(panel()!.querySelector("li.publish-finding")!.contains(document.activeElement)).toBe(true);
      answer([link]); await settle();
      expect(rowTexts()).toEqual(["Možné heslo v textu (sekce Přístup)", "Odkaz na nedostupnou stránku", "Prázdná sekce „Linux“"]);
      panelButton("Publikovat i tak").click(); await settle();
      // The findings were seen: no second save or check, the page publishes.
      expect(prepare).toHaveBeenCalledOnce();
      expect(check).toHaveBeenCalledOnce();
      expect(publish).toHaveBeenCalledExactlyOnceWith(null);
      expect(panel()).toBeNull();
    });

    it("opens the panel for findings of the server alone and says Publikovat for information only", async () => {
      const { publish, main } = mountControl({ prepare: vi.fn().mockResolvedValue(true), check: checked([], Promise.resolve([link])) });
      await main().trigger("click"); await settle();
      expect(publish).not.toHaveBeenCalled();
      expect(rowTexts()).toEqual(["Odkaz na nedostupnou stránku"]);
      wrapper!.unmount(); document.body.innerHTML = "";
      const info = mountControl({ prepare: vi.fn().mockResolvedValue(true), check: checked([section]) });
      await info.main().trigger("click"); await settle();
      expect(info.publish).not.toHaveBeenCalled();
      expect(panelButton("Publikovat")).toBeDefined();
      expect([...panel()!.querySelectorAll("button")].some(button => button.textContent?.trim() === "Publikovat i tak")).toBe(false);
    });

    it("Zobrazit reveals the target and returns focus to the editor", async () => {
      const { publish, main, arrow } = mountControl({ prepare: vi.fn().mockResolvedValue(true), check: checked([secret, section]) });
      await main().trigger("click"); await settle();
      (document.activeElement as HTMLElement).click(); await settle();
      expect(wrapper!.emitted("reveal")).toEqual([[secret.target]]);
      expect(panel()).toBeNull();
      // The host focused the editor at the revealed place; closing the panel does not take focus back to the button.
      expect(document.activeElement).not.toBe(arrow().element);
      expect(publish).not.toHaveBeenCalled();
    });

    it("Zpět k úpravám and Escape go back to the first finding", async () => {
      const { publish, main } = mountControl({ prepare: vi.fn().mockResolvedValue(true), check: checked([section, secret]) });
      await main().trigger("click"); await settle();
      panelButton("Zpět k úpravám").click(); await settle();
      expect(wrapper!.emitted("reveal")).toEqual([[secret.target]]);
      expect(panel()).toBeNull();
      await main().trigger("click"); await settle();
      panel()!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true })); await settle();
      expect(panel()).toBeNull();
      expect(wrapper!.emitted("reveal")).toHaveLength(2);
      expect(publish).not.toHaveBeenCalled();
    });

    it("checks before publishing from the panel of the arrow and keeps the typed summary", async () => {
      const prepare = vi.fn().mockResolvedValue(true);
      const { publish, arrow } = mountControl({ prepare, check: checked([secret]) });
      await arrow().trigger("click"); await settle();
      expect(prepare).not.toHaveBeenCalled();
      await type("Doplněn postup");
      panelButton("Publikovat").click(); await settle();
      expect(publish).not.toHaveBeenCalled();
      expect(rowTexts()).toEqual(["Možné heslo v textu (sekce Přístup)"]);
      expect(field().value).toBe("Doplněn postup");
      panelButton("Publikovat i tak").click(); await settle();
      expect(prepare).toHaveBeenCalledOnce();
      expect(publish).toHaveBeenCalledExactlyOnceWith("Doplněn postup");
    });
  });
});
