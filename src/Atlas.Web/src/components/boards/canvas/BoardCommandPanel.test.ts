import { afterEach, describe, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import type { BoardCommand } from "../../../boards/boardCommands";
import BoardCommandPanel from "./BoardCommandPanel.vue";

const commands: BoardCommand[] = [
  { id: "create:note", title: "Poznámka", icon: "pin", keywords: ["lístek"], section: "create" },
  { id: "create:frame", title: "Rám", icon: "layout-dashboard", keywords: [], section: "create" },
  { id: "board:settings", title: "Nastavení tabule", icon: "settings-2", keywords: ["mřížka"], section: "board" },
];
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

function open(events: { run?: (id: string) => void; close?: () => void } = {}) {
  wrapper = mount(BoardCommandPanel, { attachTo: document.body, props: { commands, at: { x: 40, y: 40 }, room: { width: 800, height: 600 }, onRun: events.run, onClose: events.close } });
  return wrapper.get<HTMLInputElement>("[data-board-command-query]");
}
const rows = () => [...document.querySelectorAll("[data-testid=board-command-list] [role=option]")].map(row => row.textContent);
const key = (input: { trigger(event: string, options: object): Promise<void> }, name: string) => input.trigger("keydown", { key: name });

describe("BoardCommandPanel finding items (BD2-7)", () => {
  const finds: BoardCommand[] = [
    { id: "find:a", title: "Karta stránky: Ansible", icon: "file-text", keywords: [], section: "find", description: "Najít na tabuli" },
    { id: "find:b", title: "Poznámka: Ansible role", icon: "pin", keywords: [], section: "find", description: "Najít na tabuli" },
  ];
  const openWith = (run: string[]) => {
    wrapper = mount(BoardCommandPanel, { attachTo: document.body, props: { commands, finds, at: { x: 40, y: 40 }, room: { width: 800, height: 600 }, onRun: (id: string) => run.push(id) } });
    return wrapper.get<HTMLInputElement>("[data-board-command-query]");
  };
  it("lists the items only for a typed name, after the commands, and Enter takes the first", async () => {
    const run: string[] = [];
    const input = openWith(run);
    await flushPromises();
    expect(rows()).toEqual(["Poznámka", "Rám", "Nastavení tabule"]);
    await input.setValue("a");
    expect(rows()).not.toContain("Karta stránky: Ansible");
    await input.setValue("ansible");
    expect(rows()).toEqual(["Karta stránky: Ansible", "Poznámka: Ansible role"].map(title => expect.stringContaining(title)));
    await input.trigger("keydown", { key: "Enter" });
    expect(run).toEqual(["find:a"]);
  });

  it("starts its search with what was typed while it loaded (BD2-9), and Enter takes the first", async () => {
    const run: string[] = [];
    wrapper = mount(BoardCommandPanel, { attachTo: document.body, props: { commands, finds, typed: "ansible r", at: { x: 40, y: 40 }, room: { width: 800, height: 600 }, onRun: (id: string) => run.push(id) } });
    await flushPromises();
    const input = wrapper.get<HTMLInputElement>("[data-board-command-query]");
    expect(input.element.value).toBe("ansible r");
    expect(rows()[0]).toContain("Poznámka: Ansible role");
    await input.trigger("keydown", { key: "Enter" });
    expect(run).toEqual(["find:b"]);
  });
});

describe("BoardCommandPanel (objects spec §5.2)", () => {
  it("takes the focus and lists every command in the editor's suggestion list", async () => {
    const input = open();
    await flushPromises();
    expect(document.activeElement).toBe(input.element);
    expect(rows()).toEqual(["Poznámka", "Rám", "Nastavení tabule"]);
    // Nothing is highlighted before something is typed or the arrows moved, as Enter takes none; the first arrow shows the first command.
    expect(input.attributes("aria-activedescendant")).toBeUndefined();
    expect(wrapper!.get("[data-board-command-panel]").attributes("data-untouched")).toBeDefined();
    await key(input, "ArrowDown");
    expect(input.attributes("aria-activedescendant")).toBe(document.querySelector("[role=option]")!.id);
    expect(wrapper!.get("[data-board-command-panel]").attributes("data-untouched")).toBeUndefined();
  });

  it("filters as it is typed, by keywords too, and runs the active entry on Enter after the arrows moved it", async () => {
    const run: string[] = [];
    const input = open({ run: id => run.push(id) });
    await input.setValue("mrizka");
    expect(rows()).toEqual(["Nastavení tabule"]);
    await input.setValue("");
    await key(input, "ArrowDown");
    await key(input, "ArrowDown");
    await key(input, "ArrowDown");
    await key(input, "ArrowUp");
    await key(input, "Enter");
    expect(run).toEqual(["create:frame"]);
  });

  it("runs the preselected first command on Enter, but not while composing, with a modifier or when nothing matches", async () => {
    const run: string[] = [];
    const input = open({ run: id => run.push(id) });
    await input.setValue("nastav");
    await input.trigger("keydown", { key: "Enter", isComposing: true });
    await input.trigger("keydown", { key: "Enter", ctrlKey: true });
    await input.trigger("keydown", { key: "Enter", shiftKey: true });
    expect(run).toEqual([]);
    await input.trigger("keydown", { key: "Enter" });
    await input.setValue("nic takového");
    await input.trigger("keydown", { key: "Enter" });
    expect(run).toEqual(["board:settings"]);
  });

  it("stays inside a short canvas: above the point while it fits there, else from the top, never with its search cut off", () => {
    const place = (y: number, height: number) => {
      wrapper?.unmount();
      wrapper = mount(BoardCommandPanel, { attachTo: document.body, props: { commands, at: { x: 40, y }, room: { width: 800, height } } });
      const style = (wrapper.element as HTMLElement).style;
      return { top: style.top, bottom: style.bottom, maxHeight: style.maxHeight };
    };
    expect(place(500, 600)).toMatchObject({ bottom: "108px", top: "" });
    expect(place(300, 340)).toMatchObject({ top: "8px", bottom: "", maxHeight: "324px" });
  });

  it("takes nothing from the untouched empty search, but the command the arrows moved to; Tab runs the first match once something is typed (P8)", async () => {
    const run: string[] = [];
    const input = open({ run: id => run.push(id) });
    await key(input, "Enter");
    expect(run).toEqual([]);
    await key(input, "ArrowDown");
    await key(input, "Enter");
    expect(run).toEqual(["create:note"]);
    await input.setValue("nastav");
    const tab = new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true });
    input.element.dispatchEvent(tab);
    expect(run).toEqual(["create:note", "board:settings"]);
    expect(tab.defaultPrevented).toBe(true);
  });

  it("counts the pointer on a command as moving to it: Enter then runs the command under it", async () => {
    const run: string[] = [];
    const input = open({ run: id => run.push(id) });
    await key(input, "Enter");
    expect(run).toEqual([]);
    const second = document.querySelectorAll("[data-testid=board-command-list] [role=option]")[1]!;
    second.dispatchEvent(new MouseEvent("mouseenter"));
    second.dispatchEvent(new MouseEvent("mouseover", { bubbles: true }));
    await key(input, "Enter");
    expect(run).toEqual(["create:frame"]);
  });

  it("empties the search with the first Esc and closes with the next one (P3)", async () => {
    let closed = 0;
    const input = open({ close: () => closed++ });
    await input.setValue("rám");
    await key(input, "Escape");
    expect(input.element.value).toBe("");
    expect(closed).toBe(0);
    await key(input, "Escape");
    expect(closed).toBe(1);
  });

  it("closes on Esc, on Backspace in an empty field and on a pointer outside", async () => {
    let closed = 0;
    const input = open({ close: () => closed++ });
    await key(input, "Escape");
    await key(input, "Backspace");
    document.body.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
    expect(closed).toBe(3);
  });
});
