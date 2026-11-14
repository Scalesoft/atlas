import { afterEach, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import PublishFindings from "./PublishFindings.vue";
import type { PublicationCheck } from "../../publishing/publication";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); Reflect.deleteProperty(Element.prototype, "animate"); document.body.innerHTML = ""; });

const section: PublicationCheck = { id: "section:4", kind: "section", severity: "info", text: "Prázdná sekce „Linux“", target: { kind: "heading", id: "linux", path: [4] } };
const secret: PublicationCheck = { id: "secret:2:6", kind: "secret", severity: "warning", text: "Možné heslo v textu (sekce Přístup)", target: { kind: "text", path: [2], from: 6, to: 19 } };
const unpublished: PublicationCheck = { id: "link:p2", kind: "link", severity: "warning", text: "Odkaz na nepublikovanou stránku „Staré VPN“", target: { kind: "page", pageId: "p2" }, openHref: "#/pages/p2" };

const rows = () => wrapper!.findAll("li.publish-finding");
const states = () => rows().map(row => row.attributes("data-state"));

describe("PublishFindings", () => {
  it("shows each check as running, then its result in a fixed order, without delaying the final list", async () => {
    vi.useFakeTimers();
    const animate = vi.fn(() => ({ finished: Promise.resolve(), cancel: vi.fn() }));
    Object.defineProperty(Element.prototype, "animate", { configurable: true, writable: true, value: animate });
    wrapper = mount(PublishFindings, { props: { checks: [section, secret], running: true }, attachTo: document.body });
    // The final list is there at once: passwords, then the links still being checked, then empty sections. Each
    // finding says its severity in words and offers its actions right away.
    expect(rows().map(row => row.text())).toEqual([
      expect.stringContaining("Možné heslo v textu (sekce Přístup)"),
      expect.stringContaining("Kontroluji…"),
      expect.stringContaining("Prázdná sekce „Linux“"),
    ]);
    expect(rows()[0]!.text()).toContain("Varování");
    expect(rows()[2]!.text()).toContain("Informace");
    expect(rows()[0]!.findAll("button").map(button => button.text())).toEqual(["Zobrazit"]);
    // Every row starts as a spinner and draws its icon in order; the whole sequence stays within about 600 ms.
    expect(states()).toEqual(["checking", "checking", "checking"]);
    expect(wrapper.findAll(".publish-finding-spinner")).toHaveLength(3);
    await vi.advanceTimersByTimeAsync(160);
    expect(states()).toEqual(["done", "checking", "checking"]);
    await vi.advanceTimersByTimeAsync(440);
    expect(states()).toEqual(["done", "checking", "done"]);
    // The row still running keeps its spinner and is not a finding.
    expect(rows()[1]!.find("button").exists()).toBe(false);
    expect(wrapper.get("ul").attributes("aria-busy")).toBe("true");
    // Drawn icons: one per finished row, the type icon, never the AI glyph.
    expect(wrapper.findAll("[data-ai-glyph]")).toHaveLength(0);
    expect(animate.mock.instances.some(element => (element as Element).matches?.("li.publish-finding"))).toBe(true);

    // The links arrive: they take the running row's place and draw in turn.
    await wrapper.setProps({ checks: [section, secret, unpublished], running: false });
    expect(rows().map(row => row.text())).toEqual([
      expect.stringContaining("Možné heslo"), expect.stringContaining("Staré VPN"), expect.stringContaining("Prázdná sekce"),
    ]);
    expect(states()).toEqual(["done", "checking", "done"]);
    const open = rows()[1]!.get("a");
    expect(open.text()).toBe("Otevřít");
    expect(open.attributes("href")).toBe("#/pages/p2");
    await vi.advanceTimersByTimeAsync(600);
    expect(states()).toEqual(["done", "done", "done"]);
    expect(wrapper.get("ul").attributes("aria-busy")).toBeUndefined();
  });

  it("asks to reveal a finding with Zobrazit", async () => {
    wrapper = mount(PublishFindings, { props: { checks: [secret, { id: "x", severity: "info", text: "Bez místa" }] } });
    await rows()[0]!.get("button").trigger("click");
    expect(wrapper.emitted("reveal")).toEqual([[secret]]);
    // A finding without a place has nothing to show.
    expect(rows()[1]!.find("button").exists()).toBe(false);
  });
});
