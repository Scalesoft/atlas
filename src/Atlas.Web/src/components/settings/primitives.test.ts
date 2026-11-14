import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import AccessExplanation from "./AccessExplanation.vue";
import AccessLevelBadge from "./AccessLevelBadge.vue";
import AccessLevelPicker from "./AccessLevelPicker.vue";
import ActivityLog from "./ActivityLog.vue";
import DecisionChain from "./DecisionChain.vue";
import EmptyState from "./EmptyState.vue";
import ErrorState from "./ErrorState.vue";
import GrantFlow from "./GrantFlow.vue";
import ImpactPreview from "./ImpactPreview.vue";
import OneTimeSecret from "./OneTimeSecret.vue";
import OptionPills from "./OptionPills.vue";
import ResultBanner from "./ResultBanner.vue";
import SearchableChoiceList from "./SearchableChoiceList.vue";
import SearchableTags from "./SearchableTags.vue";
import SettingsList from "./SettingsList.vue";
import SettingsListRow from "./SettingsListRow.vue";
import SettingsNav from "./SettingsNav.vue";
import SettingsPage from "./SettingsPage.vue";
import SettingsRow from "./SettingsRow.vue";
import SettingsSection from "./SettingsSection.vue";
import SettingsShell from "./SettingsShell.vue";
import SettingsTabs from "./SettingsTabs.vue";
import StatTile from "./StatTile.vue";
import StatusBadge from "./StatusBadge.vue";
import SubjectAvatar from "./SubjectAvatar.vue";
import SummaryList from "./SummaryList.vue";
import TileCard from "./TileCard.vue";
import { Switch } from "../ui/switch";
import { Checkbox } from "../ui/checkbox";
import { provideReauthEnvironment } from "./useReauth";
import type { ActivityItem } from "./activity";
import type { ChoiceOption } from "./types";

afterEach(() => {
  document.body.innerHTML = "";
});

// A three-step staircase as vaults use it; page and space roles use the same shape.
const levels: ChoiceOption[] = [
  { value: "discover", label: "Vidí", description: "Název, cesta a pole", icon: "eye" },
  { value: "read", label: "Čte", description: "Navíc hodnota", icon: "key-round" },
  { value: "manage", label: "Spravuje", description: "Navíc přístupy, ochrana, audit", icon: "shield-check" },
];

describe("AccessLevelPicker", () => {
  it("is one radiogroup whose chosen level includes the lower ones", async () => {
    const wrapper = mount(AccessLevelPicker, { props: { options: levels, label: "Úroveň", modelValue: "read" } });
    expect(wrapper.get('[role="radiogroup"]').attributes("aria-labelledby")).toBeTruthy();
    const steps = wrapper.findAll('[role="radio"]');
    expect(steps.map((step) => step.attributes("data-level"))).toEqual(["included", "selected", "above"]);
    expect(wrapper.text()).toContain("Čte zahrnuje i Vidí.");
    await steps[2]!.trigger("click");
    expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual(["manage"]);
  });

  it("serves any ladder, e.g. space roles, and can be read-only", () => {
    const roles: ChoiceOption[] = [
      { value: "Reader", label: "Čte", icon: "book-open" },
      { value: "Writer", label: "Upravuje", icon: "pencil" },
      { value: "Admin", label: "Spravuje", icon: "shield-check" },
    ];
    const wrapper = mount(AccessLevelPicker, { props: { options: roles, label: "Přístup", modelValue: "Admin", readonly: true } });
    expect(wrapper.text()).toContain("Spravuje zahrnuje i Čte a Upravuje.");
    expect(wrapper.findAll('[role="radio"]').every((radio) => radio.attributes("data-disabled") !== undefined)).toBe(true);
  });
});

describe("AccessLevelBadge", () => {
  it("names the level and says what it includes, without a column per right", () => {
    const wrapper = mount(AccessLevelBadge, { props: { options: levels, value: "manage" } });
    expect(wrapper.text()).toBe("Spravuje");
    expect(wrapper.attributes("aria-label")).toBe("Úroveň Spravuje, zahrnuje Vidí a Čte");
    expect(wrapper.findAll("i[data-on]")).toHaveLength(3);
  });
});

describe("AccessExplanation", () => {
  it("explains every path with its level and names the highest", () => {
    const wrapper = mount(AccessExplanation, {
      props: {
        subject: { name: "Petr", kind: "user" },
        target: { name: "Servery", icon: "lock" },
        options: levels,
        paths: [
          { via: "group", label: "Provoz", scope: "trezoru Servery", level: "read" },
          { via: "spaceAdmin", label: "správce prostoru Provoz", scope: "trezoru Servery", level: "manage", locked: true },
        ],
      },
    });
    expect(wrapper.get(".access-summary").text()).toBe("Petr má úroveň Spravuje (zahrnuje Vidí a Čte), nejvyšší z 2 cest.");
    expect(wrapper.text()).toContain("Petr přes skupinu Provoz má úroveň Čte na trezoru Servery.");
    expect(wrapper.text()).toContain("automaticky úroveň Spravuje jako správce prostoru Provoz");
    expect(wrapper.text()).toContain("Automaticky");
  });
});

describe("lists and cards", () => {
  it("SettingsListRow renders title, detail, meta and actions, and highlights a new row", () => {
    const wrapper = mount(SettingsList, {
      props: { label: "Sítě" },
      global: { stubs: { TransitionGroup: false } },
      slots: {
        default: () =>
          h(SettingsListRow, { title: "VPN", description: "10.6.0.0/20", icon: "globe", highlighted: true }, { meta: () => h("span", { class: "meta" }, "Aktivní"), actions: () => h("button", "Odebrat") }),
      },
    });
    expect(wrapper.get("ul").attributes("aria-label")).toBe("Sítě");
    const row = wrapper.get("li");
    expect(row.text()).toContain("VPN");
    expect(row.text()).toContain("10.6.0.0/20");
    expect(row.find(".meta").exists()).toBe(true);
    expect(row.attributes("data-new")).toBe("true");
  });

  it("TileCard is one link with its facts and footer", () => {
    const wrapper = mount(TileCard, { props: { title: "Provoz", description: "Servery", icon: "server", href: "#/x" }, slots: { default: "<span>3 trezory</span>", footer: "Nastavení" } });
    expect(wrapper.element.tagName).toBe("A");
    expect(wrapper.attributes("href")).toBe("#/x");
    expect(wrapper.text()).toContain("3 trezory");
    expect(wrapper.get(".tile-card-footer").text()).toContain("Nastavení");
  });

  it("StatTile shows a figure with context", () => {
    const wrapper = mount(StatTile, { props: { label: "Sítě", value: "2 aktivní", icon: "network" }, slots: { default: "1 vypnutá" } });
    expect(wrapper.element.tagName).toBe("DIV");
    expect(wrapper.get(".stat-tile-value").text()).toBe("2 aktivní");
    expect(wrapper.text()).toContain("1 vypnutá");
  });

  it("SummaryList renders label/value rows and lets a value be replaced", () => {
    const wrapper = mount(SummaryList, {
      props: { items: [{ key: "name", icon: "lock", label: "Název", value: "Servery" }, { key: "file", icon: "file-text", label: "Soubor", value: "a.kdbx", code: true }, { key: "level", icon: "key-round", label: "Úroveň" }] },
      slots: { "value-level": "<b>Čte</b>" },
    });
    expect(wrapper.findAll("dt").map((dt) => dt.text())).toEqual(["Název", "Soubor", "Úroveň"]);
    expect(wrapper.get("code").text()).toBe("a.kdbx");
    expect(wrapper.get('[data-summary="level"] b').text()).toBe("Čte");
  });

  it("SubjectAvatar shows initials for a person and an icon for a group", () => {
    expect(mount(SubjectAvatar, { props: { kind: "user", name: "Jana Nováková" } }).text()).toBe("JN");
    expect(mount(SubjectAvatar, { props: { kind: "group", name: "Provoz" } }).find("svg").exists()).toBe(true);
  });

  it("SettingsTabs are links with aria-current", () => {
    const wrapper = mount(SettingsTabs, { props: { label: "Části", modelValue: "access", options: [{ id: "status", label: "Stav", icon: "database", href: "#a" }, { id: "access", label: "Přístupy", icon: "users", href: "#b", count: 3 }] } });
    expect(wrapper.get("nav").attributes("aria-label")).toBe("Části");
    expect(wrapper.get('[aria-current="page"]').text()).toContain("Přístupy3");
  });
});

describe("page building blocks", () => {
  it("SettingsPage has a titled region, actions and a back link", () => {
    const wrapper = mount(SettingsPage, { props: { title: "Sítě", description: "Popis", back: { href: "#/b", label: "Zpět" } }, slots: { actions: "<button>Přidat</button>", default: "<p>obsah</p>" } });
    const region = wrapper.get("section");
    expect(region.attributes("aria-labelledby")).toBe(wrapper.get("h2").attributes("id"));
    expect(wrapper.get("h2").text()).toBe("Sítě");
    expect(wrapper.get("a").attributes("href")).toBe("#/b");
    expect(wrapper.text()).toContain("obsah");
  });

  it("SettingsSection shows a count, and an advanced one opens on demand with its summary visible", async () => {
    const counted = mount(SettingsSection, { props: { title: "Sítě", count: 3 } });
    expect(counted.get(".st-count").text()).toBe("3");
    const advanced = mount(SettingsSection, { props: { title: "Proxy", advanced: true, summary: "1 proxy" }, slots: { default: "<p class='inside'>x</p>" } });
    const toggle = advanced.get("button");
    expect(toggle.text()).toContain("1 proxy");
    expect(toggle.attributes("aria-expanded")).toBe("false");
    expect(advanced.find(".inside").exists()).toBe(false);
    await toggle.trigger("click");
    await flushPromises();
    expect(advanced.find(".inside").exists()).toBe(true);
    const bare = mount(SettingsSection, { props: { label: "Průvodce" } });
    expect(bare.get("section").attributes("aria-label")).toBe("Průvodce");
  });

  it("SettingsRow wires label, help and error to the control", () => {
    const wrapper = mount(SettingsRow, {
      props: { label: "Název", help: "Nápověda", error: "Chybí" },
      slots: { default: `<template #default="{ id, describedBy, invalid }"><input :id="id" :aria-describedby="describedBy" :aria-invalid="invalid" /></template>` },
    });
    const input = wrapper.get("input");
    expect(wrapper.get("label").attributes("for")).toBe(input.attributes("id"));
    const described = input.attributes("aria-describedby")!.split(" ");
    expect(described).toHaveLength(2);
    expect(input.attributes("aria-invalid")).toBe("true");
    expect(wrapper.get('[role="alert"]').text()).toContain("Chybí");
  });

  it("uses the shared AI glyph for AI settings headings and navigation", () => {
    const groups = [{ id: "g", items: [{ path: "ai", label: "AI", icon: "ai" }, { path: "networks", label: "Sítě", icon: "network" }] }];
    const nav = mount(SettingsNav, { props: { groups, label: "Nastavení", currentPath: "ai" } });
    expect(nav.get('[data-path="ai"] [data-ai-glyph]').attributes("aria-hidden")).toBe("true");
    expect(nav.find('[data-path="networks"] [data-ai-glyph]').exists()).toBe(false);
    const shell = mount(SettingsShell, { props: { title: "Nastavení", groups, navLabel: "Nastavení", currentPath: "ai" } });
    expect(shell.find('.settings-shell-sections-current [data-ai-glyph]').exists()).toBe(true);
    const page = mount(SettingsPage, { props: { title: "AI", icon: "ai" } });
    expect(page.find('.settings-page-tile [data-ai-glyph]').exists()).toBe(true);
    const section = mount(SettingsSection, { props: { title: "AI odpovědi", icon: "ai" } });
    expect(section.find('.settings-section-icon [data-ai-glyph]').exists()).toBe(true);
    for (const view of [nav, shell, page, section]) {
      expect(view.find('[data-ai-glyph][data-working]').exists()).toBe(false);
      view.unmount();
    }
  });
  it("SettingsNav marks the current section and SettingsShell renders it", () => {
    const groups = [{ id: "g", label: "Zabezpečení", items: [{ path: "a/networks", label: "Sítě", icon: "network", count: 2 }, { path: "a/audit", label: "Audit", icon: "scroll-text" }] }];
    const nav = mount(SettingsNav, { props: { groups, label: "Nastavení", currentPath: "a/networks/new" } });
    expect(nav.get('[aria-current="page"]').text()).toContain("Sítě");
    const shell = mount(SettingsShell, { props: { title: "Globální", groups, navLabel: "Nastavení" }, slots: { default: "<p class='page'>stránka</p>" } });
    expect(shell.get("h1").text()).toBe("Globální");
    expect(shell.find(".page").exists()).toBe(true);
    expect(shell.find('nav[aria-label="Nastavení"]').exists()).toBe(true);
  });
});

describe("choices and states", () => {
  it("OptionPills is a radiogroup of pills", async () => {
    const wrapper = mount(OptionPills, { props: { label: "Kdy", modelValue: "", options: [{ value: "", label: "Kdykoli" }, { value: "7d", label: "7 dní", icon: "calendar" }] } });
    expect(wrapper.find('[role="radiogroup"]').exists()).toBe(true);
    await wrapper.get('[data-value="7d"]').trigger("click");
    expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual(["7d"]);
  });

  it("SearchableChoiceList narrows the tiles and picks one", async () => {
    const wrapper = mount(SearchableChoiceList, { props: { label: "Lidé", modelValue: null, options: [{ value: "a", label: "Jana" }, { value: "b", label: "Petr" }] } });
    await wrapper.get('input[type="search"]').setValue("pe");
    const radios = wrapper.findAll('[role="radio"]');
    expect(radios).toHaveLength(1);
    await radios[0]!.trigger("click");
    expect(wrapper.emitted("pick")?.at(-1)).toEqual(["b"]);
  });

  it("SearchableChoiceList matches every word in any order, without diacritics", async () => {
    const wrapper = mount(SearchableChoiceList, { props: { label: "Lidé", modelValue: null, options: [{ value: "a", label: "Marek Musil", description: "mmusil" }, { value: "b", label: "Jiří Starý", description: "jiris" }] } });
    await wrapper.get('input[type="search"]').setValue("musil mare");
    expect(wrapper.findAll('[role="radio"]').map((radio) => radio.attributes("data-value"))).toEqual(["a"]);
    await wrapper.get('input[type="search"]').setValue("stary jiri");
    expect(wrapper.findAll('[role="radio"]').map((radio) => radio.attributes("data-value"))).toEqual(["b"]);
  });

  it("SearchableChoiceList in remote mode leaves searching to its owner", async () => {
    const wrapper = mount(SearchableChoiceList, { props: { label: "Hesla", modelValue: null, remote: true, options: [{ value: "a", label: "Router" }, { value: "b", label: "Pošta" }] } });
    await wrapper.get('input[type="search"]').setValue("vpn");
    expect(wrapper.emitted("update:query")?.at(-1)).toEqual(["vpn"]);
    expect(wrapper.findAll('[role="radio"]')).toHaveLength(2);
  });

  it("SearchableChoiceList preselects the first result of a search: Enter picks it, never while composing, loading or with a modifier", async () => {
    const options = [{ value: "a", label: "Jana Nováková", disabled: true }, { value: "b", label: "Jan Novák" }, { value: "c", label: "Janek" }];
    const wrapper = mount(SearchableChoiceList, { props: { label: "Lidé", modelValue: null, options } });
    const input = wrapper.get<HTMLInputElement>('input[type="search"]');
    const enter = (init: KeyboardEventInit = {}) => {
      const event = new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true, ...init });
      input.element.dispatchEvent(event);
      return event;
    };
    // Untyped, the list is no result yet: Enter keeps its meaning (a surrounding form submits).
    expect(enter().defaultPrevented).toBe(false);
    await input.setValue("jan");
    // The disabled one cannot be picked, so the first result is the next one.
    expect(wrapper.findAll("[data-first]").map((item) => item.attributes("data-value"))).toEqual(["b"]);
    expect(enter({ isComposing: true }).defaultPrevented).toBe(false);
    expect(enter({ ctrlKey: true }).defaultPrevented).toBe(false);
    expect(wrapper.emitted("pick")).toBeUndefined();
    expect(enter().defaultPrevented).toBe(true);
    expect(wrapper.emitted("pick")).toEqual([["b"]]);
    // The shown results belong to the previous query while the owner still searches: Enter waits for the answer.
    await wrapper.setProps({ loading: true });
    enter();
    await input.setValue("zzz");
    await wrapper.setProps({ loading: false });
    expect(enter().defaultPrevented).toBe(true);
    expect(wrapper.emitted("pick")).toEqual([["b"]]);
  });

  it("SearchableChoiceList announces its preselected result: the search points at it with aria-activedescendant", async () => {
    const wrapper = mount(SearchableChoiceList, { props: { label: "Lidé", modelValue: null, options: [{ value: "a", label: "Jana" }, { value: "b", label: "Petr" }] } });
    const input = wrapper.get('input[type="search"]');
    expect(input.attributes("aria-activedescendant")).toBeUndefined();
    await input.setValue("petr");
    const id = input.attributes("aria-activedescendant");
    expect(id).toBeTruthy();
    expect(wrapper.get(`#${id}`).attributes("data-value")).toBe("b");
    expect(input.attributes("aria-controls")).toBe(wrapper.get('[role="radiogroup"]').attributes("id"));
  });

  it("StatusBadge always pairs the tone with an icon and a word", () => {
    const wrapper = mount(StatusBadge, { props: { tone: "warning", label: "Pozor" } });
    expect(wrapper.find("svg").exists()).toBe(true);
    expect(wrapper.text()).toBe("Pozor");
  });

  it("ImpactPreview lists the effects and what stays the same", () => {
    const wrapper = mount(ImpactPreview, { props: { items: [{ icon: "lock", label: "2 trezory převezmou režim" }], unchanged: ["audit"], before: "Vždy", after: "Mimo sítě" } });
    expect(wrapper.text()).toContain("2 trezory převezmou režim");
    expect(wrapper.text()).toContain("Beze změny: audit");
    expect(wrapper.text()).toContain("Mimo sítě");
  });

  it("DecisionChain explains a decision step by step", () => {
    const wrapper = mount(DecisionChain, { props: { label: "Připojení", steps: [{ icon: "globe", label: "Adresa", value: "10.0.0.1", tone: "info" }, { icon: "circle-check", label: "Rozhodnutí", value: "Důvěryhodná síť", tone: "success" }] } });
    expect(wrapper.get("ol").attributes("aria-label")).toBe("Připojení");
    expect(wrapper.findAll("li").map((item) => item.attributes("data-tone"))).toEqual(["info", "success"]);
  });

  it("EmptyState, ErrorState and ResultBanner say what happened and offer the next step", async () => {
    expect(mount(EmptyState, { props: { title: "Nic tu není", text: "Přidejte první" } }).get('[role="status"]').text()).toContain("Přidejte první");
    const error = mount(ErrorState, { props: { title: "Nelze načíst" } });
    expect(error.get('[role="alert"]').text()).toContain("Nelze načíst");
    await error.get("button").trigger("click");
    expect(error.emitted("retry")).toHaveLength(1);
    const banner = mount(ResultBanner, { props: { message: "Síť je přidaná" } });
    expect(banner.get('[role="status"]').text()).toContain("Síť je přidaná");
    await banner.get("button").trigger("click");
    expect(banner.emitted("dismiss")).toHaveLength(1);
  });

  it("OneTimeSecret needs a confirmation and then forgets the value", async () => {
    const wrapper = mount(OneTimeSecret, { props: { value: "tajne-heslo-123" } });
    expect(wrapper.get("[data-one-time-value]").text()).not.toContain("tajne");
    await wrapper.get('button[aria-pressed="false"]').trigger("click");
    expect(wrapper.get("[data-one-time-value]").text()).toBe("tajne-heslo-123");
    expect(wrapper.get("[data-one-time-done]").attributes("disabled")).toBeDefined();
    await wrapper.get('input[type="checkbox"]').setValue(true);
    await wrapper.get("[data-one-time-done]").trigger("click");
    expect(wrapper.emitted("done")).toHaveLength(1);
    expect(wrapper.html()).not.toContain("tajne-heslo-123");
  });

  it("ui Switch and Checkbox are the shadcn primitives with their roles", async () => {
    const toggle = mount(Switch, { props: { modelValue: false } });
    const control = toggle.get('[role="switch"]');
    expect(control.attributes("aria-checked")).toBe("false");
    await control.trigger("click");
    expect(toggle.emitted("update:modelValue")?.at(-1)).toEqual([true]);
    const box = mount(Checkbox, { props: { modelValue: true } });
    expect(box.get('[role="checkbox"]').attributes("aria-checked")).toBe("true");
  });
});

describe("SearchableTags", () => {
  const people = Array.from({ length: 20 }, (_, index) => ({ value: `u${index}`, label: `Osoba ${index}` }));
  function mountTags(modelValue: string[] = []) {
    const wrapper = mount(SearchableTags, {
      props: { modelValue, options: [...people, { value: "jana", label: "Jana Nováková" }], label: "Kdo", "onUpdate:modelValue": (value: string[]) => wrapper.setProps({ modelValue: value }) },
    });
    return wrapper;
  }

  it("offers only the first few options until searched and says how many more exist", () => {
    const wrapper = mountTags();
    expect(wrapper.findAll("[data-suggestion]")).toHaveLength(5);
    expect(wrapper.text()).toContain("+16 dalších");
  });

  it("adds searched picks as tags, clears the query and removes a tag by its close button", async () => {
    const wrapper = mountTags();
    const input = wrapper.get("input");
    await input.setValue("nová");
    expect(wrapper.findAll("[data-suggestion]").map((pill) => pill.attributes("data-suggestion"))).toEqual(["jana"]);
    await input.trigger("keydown", { key: "Enter" });
    expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual([["jana"]]);
    expect((input.element as HTMLInputElement).value).toBe("");
    await wrapper.get('[data-suggestion="u3"]').trigger("click");
    expect(wrapper.props("modelValue")).toEqual(["jana", "u3"]);
    expect(wrapper.find('[data-suggestion="u3"]').exists()).toBe(false);
    await wrapper.get('[aria-label="Odebrat Jana Nováková"]').trigger("click");
    expect(wrapper.props("modelValue")).toEqual(["u3"]);
    await input.trigger("keydown", { key: "Backspace" });
    expect(wrapper.props("modelValue")).toEqual([]);
  });

  it("tells when nothing matches", async () => {
    const wrapper = mountTags();
    await wrapper.get("input").setValue("xyz");
    expect(wrapper.find("[data-suggestion]").exists()).toBe(false);
    expect(wrapper.text()).toContain("Nic neodpovídá");
  });
});

describe("ActivityLog", () => {
  it("filters its timeline locally outside a settings surface", async () => {
    const now = new Date(2026, 8, 27, 12, 0);
    const items: ActivityItem[] = [
      { id: "1", at: new Date(2026, 8, 27, 10, 0).toISOString(), actorId: "jana", actor: "Jana", action: "zobrazila heslo", category: "release", outcome: "allowed", icon: "eye" },
      { id: "2", at: new Date(2026, 8, 27, 9, 0).toISOString(), actorId: "petr", actor: "Petr", action: "přidal síť", category: "network", outcome: "changed", icon: "network" },
    ];
    const wrapper = mount(ActivityLog, { props: { items, label: "Audit", now, categories: { release: { label: "Vydání hesel", icon: "key-round" } } } });
    expect(wrapper.findAll(".activity-item")).toHaveLength(2);
    expect(wrapper.find('[data-filter="what"]').exists()).toBe(true);
    // Compact filter bar: people are suggested once the Kdo field has focus.
    await wrapper.get('[data-filter="who"] input').trigger("focusin");
    await wrapper.get('[data-filter="who"] [data-suggestion="petr"]').trigger("click");
    expect(wrapper.findAll(".activity-item")).toHaveLength(1);
    expect(wrapper.get(".activity-item").text()).toContain("Petr");
  });
});

describe("GrantFlow", () => {
  it("reveals Komu → Kdo → Úroveň → Platnost → Souhrn and saves one level", async () => {
    const save = vi.fn();
    const Host = defineComponent({
      setup() {
        provideReauthEnvironment({ method: "local", supported: true, verifyPassword: async () => true, verifyHub: async () => true });
        return () =>
          h(GrantFlow, {
            subjects: [{ id: "g1", name: "Provoz", kind: "group" }],
            levels,
            target: "trezor Servery",
            requiresReauth: (draft: { role: string }) => draft.role === "manage",
            onSave: save,
          });
      },
    });
    const wrapper = mount(Host);
    const next = () => wrapper.get("[data-flow-next]").trigger("click");
    await wrapper.get('[data-value="group"] [role="radio"]').trigger("click");
    await flushPromises();
    await wrapper.get('[data-value="g1"]').trigger("click");
    await flushPromises();
    expect(wrapper.find('[data-access-level-picker]').exists()).toBe(true);
    expect(wrapper.find('input[type="checkbox"]').exists()).toBe(false);
    await wrapper.get('[data-access-level-picker] [data-value="read"]').trigger("click");
    await flushPromises();
    await flushPromises();
    expect(wrapper.text()).toContain("Souhrn");
    expect(wrapper.get('[data-summary="level"]').text()).toContain("Čte");
    await next();
    // No fresh sign-in was asked for, so the save carries no proof.
    expect(save).toHaveBeenCalledWith(expect.objectContaining({ kind: "group", subjectId: "g1", role: "read", expiresAt: "" }), null);
  });

  it("hands the focus to each step a choice reveals and says it is searching until the people arrive (UX1-4, UX1-11)", async () => {
    const searching = ref(false);
    const Host = defineComponent({
      setup() {
        provideReauthEnvironment({ method: "local", supported: true, verifyPassword: async () => true, verifyHub: async () => true });
        return () => h(GrantFlow, { subjects: [{ id: "u1", name: "Jana", kind: "user" }], levels, target: "prostor Provoz", remote: true, searching: searching.value });
      },
    });
    const wrapper = mount(Host, { attachTo: document.body });
    await wrapper.get('[data-value="user"] [role="radio"]').trigger("click");
    await flushPromises();
    const search = wrapper.get('[data-step="recipient"] input[type=search]');
    expect(document.activeElement).toBe(search.element);
    searching.value = true;
    await search.setValue("ja");
    expect(wrapper.get('[data-step="recipient"]').text()).not.toContain("Nikdo neodpovídá");
    searching.value = false;
    await flushPromises();
    await search.trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(document.activeElement?.closest('[data-step="level"]')).not.toBeNull();
    wrapper.unmount();
  });
});
