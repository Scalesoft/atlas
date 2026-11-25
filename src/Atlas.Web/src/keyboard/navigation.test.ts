import { afterEach, describe, expect, it, vi } from "vitest";
import { cardKeys, focusWhenPresent, gridKeys, landFocus, landFocusWhenRendered, listKeys, rovingInit, rowKeys, tablistKeys, typeAheadIndex } from "./navigation";

afterEach(() => document.body.replaceChildren());

describe("type-ahead in lists and trees", () => {
  const labels = ["Architektura", "Bezpečnost", "Čtení", "Cesty", "Backup"];
  it("moves to the next label that starts with the typed letters, from after the current one", () => {
    expect(typeAheadIndex(labels, 0, "b")).toBe(1);
    expect(typeAheadIndex(labels, 1, "b")).toBe(4);
    expect(typeAheadIndex(labels, 4, "b")).toBe(1);
  });
  it("ignores diacritics and case, and keeps the current item while the typed prefix still fits it", () => {
    expect(typeAheadIndex(labels, 0, "C")).toBe(2);
    expect(typeAheadIndex(labels, 2, "ct")).toBe(2);
    expect(typeAheadIndex(labels, 2, "ce")).toBe(3);
  });
  it("returns -1 when nothing starts with the letters", () => {
    expect(typeAheadIndex(labels, 0, "x")).toBe(-1);
  });
});

describe("tabs with the arrow keys", () => {
  function tabs() {
    document.body.innerHTML = '<div role="tablist"><button role="tab" aria-selected="true">Stránky</button><button role="tab" aria-selected="false">O prostoru</button><button role="tab" aria-selected="false" disabled>Ke schválení</button><button role="tab" aria-selected="false">Na co čekáme</button></div>';
    const list = document.querySelector<HTMLElement>('[role="tablist"]')!;
    const all = [...list.querySelectorAll<HTMLButtonElement>('[role="tab"]')];
    const clicked: string[] = [];
    all.forEach(tab => tab.addEventListener("click", () => clicked.push(tab.textContent!)));
    return { list, all, clicked };
  }
  const key = (target: HTMLElement, key: string) => {
    const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true });
    Object.defineProperty(event, "currentTarget", { value: target.closest('[role="tablist"]') });
    Object.defineProperty(event, "target", { value: target });
    tablistKeys(event);
    return event;
  };
  it("moves to and opens the next or previous tab, skipping disabled ones and wrapping", () => {
    const { all, clicked } = tabs();
    all[0]!.focus();
    expect(key(all[0]!, "ArrowRight").defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(all[1]);
    key(all[1]!, "ArrowRight");
    expect(document.activeElement).toBe(all[3]);
    key(all[3]!, "ArrowRight");
    expect(document.activeElement).toBe(all[0]);
    key(all[0]!, "ArrowLeft");
    expect(document.activeElement).toBe(all[3]);
    expect(clicked).toEqual(["O prostoru", "Na co čekáme", "Stránky", "Na co čekáme"]);
  });
  it("goes to the first and the last tab with Home and End", () => {
    const { all } = tabs();
    key(all[1]!, "End");
    expect(document.activeElement).toBe(all[3]);
    key(all[3]!, "Home");
    expect(document.activeElement).toBe(all[0]);
  });
  it("leaves other keys alone", () => {
    const { all } = tabs();
    expect(key(all[0]!, "Enter").defaultPrevented).toBe(false);
  });
});

describe("a plain list of links and buttons with the arrow keys", () => {
  it("moves among its items with ↑ ↓ Home End, without wrapping, and leaves letters to the page", () => {
    document.body.innerHTML = '<nav><button>Knihovna</button><button>Nedávné</button><button disabled>Oblíbené</button><a href="#">Moje koncepty</a></nav>';
    const nav = document.querySelector("nav")!;
    const items = [...nav.querySelectorAll<HTMLElement>("button, a")];
    const key = (key: string) => {
      const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true });
      Object.defineProperty(event, "currentTarget", { value: nav });
      Object.defineProperty(event, "target", { value: document.activeElement });
      listKeys(event);
      return event;
    };
    items[0]!.focus();
    key("ArrowDown"); expect(document.activeElement).toBe(items[1]);
    key("ArrowDown"); expect(document.activeElement).toBe(items[3]);
    key("ArrowDown"); expect(document.activeElement).toBe(items[3]);
    key("Home"); expect(document.activeElement).toBe(items[0]);
    key("ArrowUp"); expect(document.activeElement).toBe(items[0]);
    key("End"); expect(document.activeElement).toBe(items[3]);
    expect(key("n").defaultPrevented).toBe(false);
  });
});

describe("rows of several controls with the arrow keys", () => {
  it("moves between rows to each row's first control, and not from inside a field", () => {
    document.body.innerHTML = '<ul><li><span tabindex="0">Jana</span><a href="#">jana@x.cz</a></li><li><span tabindex="0">Petr</span></li><li><input aria-label="Jméno"><button>Smazat</button></li></ul>';
    const list = document.querySelector("ul")!;
    const key = (key: string) => {
      const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true });
      Object.defineProperty(event, "currentTarget", { value: list });
      Object.defineProperty(event, "target", { value: document.activeElement });
      rowKeys(event, "li");
      return event;
    };
    list.querySelector("a")!.focus();
    key("ArrowDown"); expect(document.activeElement?.textContent).toBe("Petr");
    key("ArrowDown"); expect(document.activeElement?.getAttribute("aria-label")).toBe("Jméno");
    expect(key("ArrowUp").defaultPrevented).toBe(false);
    list.querySelector("button")!.focus();
    key("Home"); expect(document.activeElement?.textContent).toBe("Jana");
    key("End"); expect(document.activeElement?.getAttribute("aria-label")).toBe("Jméno");
  });

  it("ticks the row's bulk-selection box with Space, unless the focused control takes Space itself", () => {
    document.body.innerHTML = '<ul><li tabindex="0"><button class="bulk-box" tabindex="-1"></button><a href="#">Jana</a><button class="open">Otevřít</button></li><li tabindex="-1"><button class="bulk-box" disabled></button></li></ul>';
    const list = document.querySelector("ul")!;
    const ticks: string[] = [];
    list.querySelectorAll<HTMLElement>(".bulk-box").forEach((box, index) => box.addEventListener("click", () => ticks.push(`row ${index}`)));
    const space = (from: Element) => {
      const event = new KeyboardEvent("keydown", { key: " ", bubbles: true, cancelable: true });
      Object.defineProperty(event, "currentTarget", { value: list });
      Object.defineProperty(event, "target", { value: from });
      rowKeys(event, "li");
      return event;
    };
    expect(space(list.children[0]!).defaultPrevented).toBe(true);
    expect(space(list.querySelector("a")!).defaultPrevented).toBe(true);
    expect(space(list.querySelector(".open")!).defaultPrevented).toBe(false);
    expect(space(list.children[1]!).defaultPrevented).toBe(false);
    expect(ticks).toEqual(["row 0", "row 0"]);
  });
});

describe("cards in a wrapping grid with the arrow keys (Knihovna's spaces)", () => {
  it("goes along with ←/→, to the nearest card of the row above or below with ↑/↓, and to the ends with Home/End", () => {
    // Two rows of three, the second short: A B C / D E.
    document.body.innerHTML = '<ul>' + ["A", "B", "C", "D", "E"].map(name => `<li><a href="#">${name}</a><button>Připnout ${name}</button></li>`).join("") + "</ul>";
    const grid = document.querySelector("ul")!;
    [...grid.children].forEach((card, index) => {
      Object.defineProperty(card, "offsetTop", { value: Math.floor(index / 3) * 200 });
      Object.defineProperty(card, "offsetLeft", { value: (index % 3) * 300 });
    });
    const key = (key: string) => {
      const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true });
      Object.defineProperty(event, "currentTarget", { value: grid });
      cardKeys(event, "li");
      return event;
    };
    const at = () => document.activeElement?.textContent;
    grid.querySelector("a")!.focus();
    key("ArrowRight"); expect(at()).toBe("B");
    key("ArrowDown"); expect(at()).toBe("E");
    key("ArrowRight"); expect(at()).toBe("E");
    key("ArrowUp"); expect(at()).toBe("B");
    expect(key("ArrowUp").defaultPrevented).toBe(false);
    key("End"); expect(at()).toBe("E");
    key("Home"); expect(at()).toBe("A");
    key("ArrowLeft"); expect(at()).toBe("A");
    // From a card's second control the card counts as the current one.
    grid.querySelectorAll("button")[2]!.focus();
    key("ArrowDown"); expect(at()).toBe("E");
  });
});

describe("a row of tab links that is no tablist (Knihovna)", () => {
  it("takes its tabs from the given selector", () => {
    document.body.innerHTML = '<nav><a href="#/a">A</a><a href="#/b">B</a></nav>';
    const nav = document.querySelector("nav")!;
    const opened: string[] = [];
    nav.addEventListener("click", event => { event.preventDefault(); opened.push((event.target as HTMLElement).textContent!); });
    nav.querySelector("a")!.focus();
    const event = new KeyboardEvent("keydown", { key: "ArrowRight", bubbles: true, cancelable: true });
    Object.defineProperty(event, "currentTarget", { value: nav });
    Object.defineProperty(event, "target", { value: document.activeElement });
    tablistKeys(event, "a[href]");
    expect(document.activeElement?.textContent).toBe("B");
    expect(opened).toEqual(["B"]);
  });
});

describe("a table of links as one tab stop (the deployment overview)", () => {
  function table() {
    document.body.innerHTML = `<table><tbody>
      <tr><th><a href="#a">A</a></th><td><a href="#a1">A1</a></td><td>–</td><td><a href="#a3">A3</a></td></tr>
      <tr><th><a href="#b">B</a></th><td>–</td><td><a href="#b2">B2</a></td><td><a href="#b3">B3</a></td></tr>
      <tr><th><a href="#c">C</a></th><td><a href="#c1">C1</a></td><td>–</td><td>–</td></tr>
    </tbody></table>`;
    const root = document.querySelector("table")!;
    rovingInit(root, "a[href]");
    const key = (key: string, init: KeyboardEventInit = {}) => {
      const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...init });
      Object.defineProperty(event, "currentTarget", { value: root });
      Object.defineProperty(event, "target", { value: document.activeElement });
      gridKeys(event);
      return event;
    };
    return { root, key, at: () => document.activeElement?.textContent };
  }
  it("keeps one tab stop, the first link, until another takes the keyboard", () => {
    const { root, key } = table();
    expect([...root.querySelectorAll("a")].filter(a => a.tabIndex === 0).map(a => a.textContent)).toEqual(["A"]);
    root.querySelector<HTMLElement>("a")!.focus();
    key("ArrowRight");
    expect([...root.querySelectorAll("a")].filter(a => a.tabIndex === 0).map(a => a.textContent)).toEqual(["A1"]);
  });
  it("moves along a row, down a column to the next row that has a link there, and to the ends", () => {
    const { root, key, at } = table();
    root.querySelector<HTMLElement>("a")!.focus();
    key("ArrowRight"); expect(at()).toBe("A1");
    key("ArrowDown"); expect(at()).toBe("C1");
    key("ArrowUp"); expect(at()).toBe("A1");
    key("End"); expect(at()).toBe("A3");
    key("ArrowDown"); expect(at()).toBe("B3");
    key("Home"); expect(at()).toBe("B");
    key("End", { ctrlKey: true }); expect(at()).toBe("C1");
    key("Home", { ctrlKey: true }); expect(at()).toBe("A");
  });
});

describe("focus on a control that mounts a moment later", () => {
  it("waits for it frame by frame and gives up after the limit", async () => {
    document.body.innerHTML = '<form id="composer"></form>';
    const form = document.getElementById("composer")!;
    const found = focusWhenPresent(form, '[contenteditable="true"]', 10);
    requestAnimationFrame(() => requestAnimationFrame(() => { form.innerHTML = '<div contenteditable="true" tabindex="0">nový</div>'; }));
    expect(await found).toBe(true);
    expect(document.activeElement?.textContent).toBe("nový");
    expect(await focusWhenPresent(form, "textarea", 3)).toBe(false);
  });
});

describe("where focus lands after navigation", () => {
  it("takes the first heading of the new view, focusable without a tab stop", () => {
    document.body.innerHTML = '<main id="main"><header><button>Upravit</button></header><h1>Návod</h1></main>';
    const main = document.getElementById("main")!;
    expect(landFocus(main)).toBe(true);
    const heading = main.querySelector("h1")!;
    expect(document.activeElement).toBe(heading);
    expect(heading.getAttribute("tabindex")).toBe("-1");
  });
  it("prefers an element marked as the landing place", () => {
    document.body.innerHTML = '<main id="main"><h1>Začínáme</h1><input data-focus-landing aria-label="Hledat"></main>';
    landFocus(document.getElementById("main"));
    expect(document.activeElement?.getAttribute("aria-label")).toBe("Hledat");
  });
  it("lands in a writer's title field with the caret at its end (F4)", () => {
    document.body.innerHTML = '<main id="main"><div class="document-title-input"><textarea data-focus-landing>Návod k VPN</textarea></div></main>';
    landFocus(document.getElementById("main"));
    const field = document.activeElement as HTMLTextAreaElement;
    expect(field.tagName).toBe("TEXTAREA");
    expect([field.selectionStart, field.selectionEnd]).toEqual([11, 11]);
  });
  it("does not take the focus from where the reader already is", () => {
    document.body.innerHTML = '<main id="main"><h1>Návod</h1></main><input id="field">';
    document.getElementById("field")!.focus();
    expect(landFocus(document.getElementById("main"))).toBe(false);
    expect(document.activeElement?.id).toBe("field");
  });
  it("moves on from the container itself (a skip link, a command just run) to the heading", () => {
    document.body.innerHTML = '<main id="main" tabindex="-1"><h1>Knihovna</h1></main>';
    const main = document.getElementById("main")!;
    main.focus();
    expect(landFocus(main)).toBe(true);
    expect(document.activeElement?.textContent).toBe("Knihovna");
  });
  it("falls back to the container itself", () => {
    document.body.innerHTML = '<main id="main" tabindex="-1"><p>Bez nadpisu</p></main>';
    landFocus(document.getElementById("main"));
    expect(document.activeElement?.id).toBe("main");
  });
});

describe("a view that renders its landing after loading (P7: watched, not chased by timers)", () => {
  const settle = async () => { for (let turn = 0; turn < 4; turn++) await Promise.resolve(); };
  afterEach(() => vi.restoreAllMocks());
  it("lands on the heading the moment it appears, with no timer", async () => {
    const timers = vi.spyOn(globalThis, "setTimeout");
    document.body.innerHTML = '<main id="main" tabindex="-1"><p>Načítání…</p></main>';
    const main = document.getElementById("main")!;
    const stop = landFocusWhenRendered(main);
    expect(document.activeElement).toBe(main);
    main.innerHTML = "<h1>Kdo má přístup</h1>";
    await settle();
    expect(document.activeElement?.textContent).toBe("Kdo má přístup");
    expect(timers).not.toHaveBeenCalled();
    stop();
  });
  it("waits while a dialog or the editor forbids it, and lands once it does not", async () => {
    document.body.innerHTML = '<main id="main" tabindex="-1"><h1>Knihovna</h1></main>';
    const main = document.getElementById("main")!;
    let allowed = false;
    landFocusWhenRendered(main, () => allowed);
    expect(document.activeElement).toBe(document.body);
    allowed = true;
    main.append(document.createElement("p"));
    await settle();
    expect(document.activeElement?.textContent).toBe("Knihovna");
  });
  it("leaves the reader where they are, also when the heading arrives later", async () => {
    document.body.innerHTML = '<main id="main" tabindex="-1"></main><input id="field">';
    const main = document.getElementById("main")!;
    document.getElementById("field")!.focus();
    landFocusWhenRendered(main);
    main.innerHTML = "<h1>Pozdě</h1>";
    await settle();
    expect(document.activeElement?.id).toBe("field");
  });
  it("stops waiting at the reader's own key", async () => {
    document.body.innerHTML = '<main id="main" tabindex="-1"><p>Načítání…</p></main>';
    const main = document.getElementById("main")!;
    landFocusWhenRendered(main);
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "a", bubbles: true }));
    main.innerHTML = "<h1>Pozdě</h1>";
    await settle();
    expect(document.activeElement).toBe(main);
  });
  it("stops when asked, so a newer navigation replaces it", async () => {
    document.body.innerHTML = '<main id="main" tabindex="-1"><p>Načítání…</p></main>';
    const main = document.getElementById("main")!;
    landFocusWhenRendered(main)();
    main.innerHTML = "<h1>Pozdě</h1>";
    await settle();
    expect(document.activeElement).toBe(main);
  });
});
