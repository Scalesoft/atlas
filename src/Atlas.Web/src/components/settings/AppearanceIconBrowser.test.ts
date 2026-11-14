import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type DOMWrapper, type VueWrapper } from "@vue/test-utils";
import AppearanceIconBrowser from "./AppearanceIconBrowser.vue";
import { appearanceIconGroups, appearanceIcons } from "./appearanceIcons";
import { iconPackCatalog, iconPackCount, loadIconPackCatalog, loadIconPackMarkup, readRecentIcons } from "../../iconPack";

// The real catalog: the cases name icons by id and search by English words, because people correct the Czech labels.
// The 87 open in their eight groups (appearance.ts); with four columns "Obecné" is
//   book-open home    star      lightbulb
//   smile     compass megaphone ticket
//   palette   pin     box       blocks
//   plug      highlighter pencil search
// and "Dokumenty" starts file-text, file-plus, folder, folder-plus and ends with a row of three.
const total = (appearanceIcons.length + iconPackCount).toLocaleString("cs-CZ");
let wrapper: VueWrapper;

beforeAll(async () => { await Promise.all([loadIconPackCatalog(), loadIconPackMarkup()]); });
afterEach(() => { wrapper.unmount(); localStorage.clear(); });

async function browser(props: { modelValue?: string; disabled?: boolean } = {}) {
  wrapper = mount(AppearanceIconBrowser, {
    attachTo: document.body,
    props: { modelValue: "server", color: "teal-2", "onUpdate:modelValue": (value: string) => wrapper.setProps({ modelValue: value }), ...props },
  });
  await flushPromises();
  fourColumns();
  return wrapper;
}
/** jsdom lays nothing out; the browser reads the columns of a group at key time. */
function fourColumns() {
  for (const group of wrapper.findAll<HTMLElement>('[role="group"]')) group.element.style.gridTemplateColumns = "10px 10px 10px 10px";
}
const search = () => wrapper.get<HTMLInputElement>("[data-appearance-search]");
const options = () => wrapper.findAll('[role="option"]');
const option = (id: string) => wrapper.get<HTMLElement>(`[role="option"][data-value="${id}"]`);
const scope = (id: string) => wrapper.get(`[data-appearance-scopes] [data-value="${id}"]`);
const focusedId = () => document.activeElement?.getAttribute("data-value");
const tabStops = () => wrapper.findAll('[role="option"][tabindex="0"]').map((item) => item.attributes("data-value"));
const press = (target: Pick<DOMWrapper<Element>, "trigger">, key: string) => target.trigger("keydown", { key });
/** Presses a key on the focused option and says where the focus went. */
async function move(key: string) {
  await press(option(focusedId()!), key);
  return focusedId();
}
async function type(text: string) {
  await search().setValue(text);
  fourColumns();
}

describe("AppearanceIconBrowser", () => {
  it("opens with the 87 recommended icons under their eight headings", async () => {
    await browser();
    expect(options()).toHaveLength(appearanceIcons.length);
    expect(wrapper.findAll('[role="group"]').map((group) => group.attributes("aria-label"))).toEqual([...appearanceIconGroups]);
    expect(wrapper.get("[data-appearance-results]").attributes("role")).toBe("listbox");
    expect(scope("recommended").attributes("aria-checked")).toBe("true");
    expect(option("server").attributes("aria-selected")).toBe("true");
    expect(wrapper.find('[data-appearance-scopes] [data-value="recent"]').exists()).toBe(false);
    expect(search().attributes("placeholder")).toContain(total);
  });

  it("K1: Down in the search enters the results at the chosen option, else at the first", async () => {
    await browser();
    await press(search(), "ArrowDown");
    expect(focusedId()).toBe("server");
    wrapper.unmount();

    await browser({ modelValue: "rocket" });
    await press(search(), "ArrowDown");
    expect(focusedId()).toBe("book-open");
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
  });

  it("K2, K4: Left and Right follow the reading order across groups without wrapping; Home and End reach the ends", async () => {
    await browser();
    option("book-open").element.focus();
    expect(await move("ArrowLeft")).toBe("book-open");
    expect(await move("ArrowRight")).toBe("home");
    option("search").element.focus();
    expect(await move("ArrowRight")).toBe("file-text");
    expect(await move("ArrowLeft")).toBe("search");
    expect(await move("End")).toBe("building-2");
    expect(await move("ArrowRight")).toBe("building-2");
    expect(await move("Home")).toBe("book-open");
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
  });

  it("K3: Down and Up move one row and keep the column, also between groups, and stop at the edges", async () => {
    await browser();
    option("home").element.focus();
    expect(await move("ArrowUp")).toBe("home");
    expect(await move("ArrowDown")).toBe("compass");
    option("highlighter").element.focus();
    expect(await move("ArrowDown")).toBe("file-plus");
    expect(await move("ArrowUp")).toBe("highlighter");
    // The last row of "Dokumenty" has three options: the fourth column gives its last one.
    option("type").element.focus();
    expect(await move("ArrowDown")).toBe("external-link");
    option("building-2").element.focus();
    expect(await move("ArrowDown")).toBe("building-2");
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
  });

  it("K5: a search preselects its first result, which Enter in the field chooses; a composing or modified Enter does not", async () => {
    await browser();
    await search().trigger("keydown", { key: "Enter" });
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
    await search().setValue("database");
    const first = options()[0]!.attributes("data-value");
    expect(wrapper.findAll("[data-first]").map((item) => item.attributes("data-value"))).toEqual([first]);
    await search().trigger("keydown", { key: "Enter", isComposing: true });
    await search().trigger("keydown", { key: "Enter", altKey: true });
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
    await search().trigger("keydown", { key: "Enter" });
    expect(wrapper.emitted("update:modelValue")).toEqual([[first]]);
    await search().setValue("zzzzqqq");
    await search().trigger("keydown", { key: "Enter" });
    expect(wrapper.emitted("update:modelValue")).toHaveLength(1);
  });

  it("K5: Enter, Space and a click choose the option, keep the focus on it and remember the icon", async () => {
    await browser();
    option("database").element.focus();
    await press(option("database"), "Enter");
    expect(wrapper.emitted("update:modelValue")).toEqual([["database"]]);
    expect(option("database").attributes("aria-selected")).toBe("true");
    expect(option("server").attributes("aria-selected")).toBe("false");
    expect(focusedId()).toBe("database");

    option("folder").element.focus();
    await press(option("folder"), " ");
    expect(option("folder").attributes("aria-selected")).toBe("true");
    expect(focusedId()).toBe("folder");

    await option("star").trigger("click");
    expect(wrapper.emitted("update:modelValue")).toEqual([["database"], ["folder"], ["star"]]);
    expect(JSON.parse(localStorage.getItem("atlas:appearance:recent-icons")!)).toEqual(["star", "folder", "database"]);
    expect(readRecentIcons()).toEqual(["star", "folder", "database"]);
    // The first remembered icon brings the "recently used" scope.
    expect(wrapper.find('[data-appearance-scopes] [data-value="recent"]').exists()).toBe(true);
  });

  it("K6: one tab stop: the last focused option, else the chosen one when shown, else the first", async () => {
    await browser();
    expect(tabStops()).toEqual(["server"]);
    option("server").element.focus();
    expect(await move("ArrowRight")).toBe("network");
    await flushPromises();
    expect(tabStops()).toEqual(["network"]);

    // A new scope or query forgets the last focused option, even while it is still shown: "Vše" is every icon
    // under the category headings.
    await scope("all").trigger("click");
    await flushPromises();
    expect(options()).toHaveLength(appearanceIcons.length + iconPackCount);
    expect(wrapper.findAll('[role="group"]')).toHaveLength(iconPackCatalog.value!.categories.length);
    expect(tabStops()).toEqual(["server"]);
    await type("rocket");
    expect(tabStops()).toEqual([options()[0]!.attributes("data-value")]);
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
  });

  it("K7: Escape returns from an option to the search, then clears it, and only then leaves the browser", async () => {
    const escapes = vi.fn();
    const listener = (event: KeyboardEvent) => { if (event.key === "Escape") escapes(); };
    document.body.addEventListener("keydown", listener);
    try {
      await browser();
      await type("rocket");
      await press(search(), "ArrowDown");
      expect(focusedId()).toBe(options()[0]!.attributes("data-value"));

      await press(option(focusedId()!), "Escape");
      expect(document.activeElement).toBe(search().element);
      expect(search().element.value).toBe("rocket");
      await press(search(), "Escape");
      expect(search().element.value).toBe("");
      expect(escapes).not.toHaveBeenCalled();
      // An empty search does not handle Escape, so the panel around the picker can close.
      await press(search(), "Escape");
      expect(escapes).toHaveBeenCalledTimes(1);
    } finally {
      document.body.removeEventListener("keydown", listener);
    }
  });

  it("K8: typing in the recommended view searches everything and never changes the model", async () => {
    await browser();
    await type("rocket");
    expect(scope("all").attributes("aria-checked")).toBe("true");
    expect(options().map((item) => item.attributes("data-value"))).toContain("rocket");
    expect(wrapper.findAll('[role="group"]')).toHaveLength(1);
    expect(wrapper.get("[data-appearance-count]").text()).toBe(`Nalezeno ${options().length.toLocaleString("cs-CZ")} z ${total}`);
    expect(wrapper.get("[data-appearance-count]").attributes("role")).toBe("status");
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
  });

  it("returns to the scope typing left once the query is cleared, but not to one the reader picked meanwhile", async () => {
    await browser();
    await type("rocket");
    expect(scope("all").attributes("aria-checked")).toBe("true");
    await type("");
    expect(scope("recommended").attributes("aria-checked")).toBe("true");
    expect(options()).toHaveLength(appearanceIcons.length);

    // Clearing with Escape does the same.
    await type("rocket");
    await press(search(), "Escape");
    expect(scope("recommended").attributes("aria-checked")).toBe("true");

    await type("lock");
    await scope("security").trigger("click");
    await flushPromises();
    await type("");
    expect(scope("security").attributes("aria-checked")).toBe("true");
  });

  it("lists the recently used icons newest first and does not reorder them under the focus", async () => {
    localStorage.setItem("atlas:appearance:recent-icons", JSON.stringify(["dog", "no-such-icon", "rocket"]));
    await browser();
    await scope("recent").trigger("click");
    await flushPromises();
    const shown = () => options().map((item) => item.attributes("data-value"));
    expect(shown()).toEqual(["dog", "rocket"]);
    expect(wrapper.findAll('[role="group"]').map((group) => group.attributes("aria-label"))).toEqual([undefined]);

    option("rocket").element.focus();
    await press(option("rocket"), "Enter");
    expect(readRecentIcons()[0]).toBe("rocket");
    expect(shown()).toEqual(["dog", "rocket"]);
    expect(focusedId()).toBe("rocket");

    // K8 holds here too; the view shows the new order when it is opened again.
    await type("server");
    expect(scope("all").attributes("aria-checked")).toBe("true");
    await scope("recent").trigger("click");
    await flushPromises();
    await type("");
    expect(shown()).toEqual(["rocket", "dog"]);
  });

  describe("a query with more than 200 results", () => {
    const showAll = () => wrapper.get("[data-appearance-show-all]");
    /** The number of the count line "Nalezeno N z …"; the cases do not hard-code how many icons a letter finds. */
    const found = () => Number(wrapper.get("[data-appearance-count]").text().replace(/^Nalezeno (.+) z .+$/, "$1").replace(/\s/g, ""));

    it("mounts the best 200 and leaves the rest behind one button that says how many there are", async () => {
      await browser();
      await type("a");
      const all = found();
      expect(all).toBeGreaterThan(200);
      expect(options()).toHaveLength(200);
      expect(showAll().text()).toBe(`Zobrazit všech ${all.toLocaleString("cs-CZ")}`);
      expect(wrapper.get("[data-appearance-count]").text()).toBe(`Nalezeno ${all.toLocaleString("cs-CZ")} z ${total}`);
      // The listbox holds options only; the button sits after it in the same scrolling list.
      expect(wrapper.get("[data-appearance-results]").find("button:not([role=option])").exists()).toBe(false);

      const best = options().map((item) => item.attributes("data-value"));
      await showAll().trigger("click");
      expect(options()).toHaveLength(all);
      expect(options().slice(0, 200).map((item) => item.attributes("data-value"))).toEqual(best);
      expect(wrapper.find("[data-appearance-show-all]").exists()).toBe(false);
      expect(wrapper.emitted("update:modelValue")).toBeUndefined();
    });

    it("a new query starts with the best 200 again, and a query with fewer results has no button", async () => {
      await browser();
      await type("a");
      await showAll().trigger("click");
      expect(options().length).toBeGreaterThan(200);
      await type("ar");
      expect(found()).toBeGreaterThan(200);
      expect(options()).toHaveLength(200);
      expect(showAll().text()).toBe(`Zobrazit všech ${found().toLocaleString("cs-CZ")}`);

      await type("rocket");
      expect(wrapper.find("[data-appearance-show-all]").exists()).toBe(false);
    });

    // Enter and Space activate a button by themselves; jsdom does not turn them into a click, so the case clicks.
    it("is reached with Down from the last row, returns with Up, and moves the focus to the first icon it shows", async () => {
      await browser();
      await type("a");
      await press(search(), "ArrowDown");
      const last = options()[199]!.attributes("data-value")!;
      expect(await move("End")).toBe(last);
      await press(option(last), "ArrowDown");
      expect(document.activeElement).toBe(showAll().element);

      // Up returns to the option the focus came from; Escape goes to the search like on an option.
      await press(showAll(), "ArrowUp");
      expect(focusedId()).toBe(last);
      await press(option(last), "ArrowDown");
      await press(showAll(), "Escape");
      expect(document.activeElement).toBe(search().element);

      await showAll().trigger("click");
      await flushPromises();
      expect(focusedId()).toBe(options()[200]!.attributes("data-value"));
    });
  });

  it("combines a category with the query", async () => {
    await browser();
    await scope("security").trigger("click");
    await flushPromises();
    const catalog = iconPackCatalog.value!;
    const security = new Set([
      ...Object.keys(catalog.core).filter((id) => catalog.core[id] === "security"),
      ...catalog.icons.filter((icon) => icon.category === "security").map((icon) => icon.id),
    ]);
    expect(options()).toHaveLength(security.size);
    expect(wrapper.get("[data-appearance-count]").text()).toContain(security.size.toLocaleString("cs-CZ"));

    await type("lock");
    expect(scope("security").attributes("aria-checked")).toBe("true");
    const shown = options().map((item) => item.attributes("data-value")!);
    expect(shown).toContain("lock");
    expect(shown.length).toBeLessThan(security.size);
    expect(shown.filter((id) => !security.has(id))).toEqual([]);
    expect(wrapper.get("[data-appearance-count]").text()).toBe(`Nalezeno ${shown.length} z ${security.size}`);
  });

  it("says so when nothing matches", async () => {
    await browser();
    await type("qqq");
    expect(options()).toHaveLength(0);
    expect(wrapper.find('[role="listbox"]').exists()).toBe(false);
    expect(wrapper.text()).toContain("Žádná ikona neodpovídá.");
    expect(wrapper.get("[data-appearance-count]").text()).toBe(`Nalezeno 0 z ${total}`);
  });

  it("offers to search every category when the query finds nothing in the chosen one but would elsewhere", async () => {
    await browser();
    await scope("security").trigger("click");
    await flushPromises();
    const everywhere = () => wrapper.find("[data-appearance-search-all]");

    // Nothing anywhere: the sentence alone.
    await type("qqq");
    expect(wrapper.text()).toContain("Žádná ikona neodpovídá.");
    expect(everywhere().exists()).toBe(false);

    await type("rocket");
    expect(options()).toHaveLength(0);
    expect(wrapper.text()).toContain("Žádná ikona neodpovídá.");
    expect(everywhere().text()).toBe("Hledat ve všech kategoriích");
    await everywhere().trigger("click");
    await flushPromises();
    expect(scope("all").attributes("aria-checked")).toBe("true");
    expect(search().element.value).toBe("rocket");
    expect(options().map((item) => item.attributes("data-value"))).toContain("rocket");
    expect(everywhere().exists()).toBe(false);
    // The button is gone from under the focus: it goes back to the search.
    expect(document.activeElement).toBe(search().element);
  });

  it("K9: disabled, nothing can be searched, filtered or chosen", async () => {
    await browser({ disabled: true });
    expect(search().element.disabled).toBe(true);
    expect(wrapper.findAll("[data-appearance-scopes] [role=radio]").every((pill) => pill.attributes("disabled") !== undefined)).toBe(true);
    expect(options().every((item) => (item.element as HTMLButtonElement).disabled)).toBe(true);
    await option("database").trigger("click");
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
  });
});
