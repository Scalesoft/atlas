import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h, nextTick, ref } from "vue";
import ActivityFilters from "./ActivityFilters.vue";
import ActivityTimeline from "./ActivityTimeline.vue";
import ChoiceCards from "./ChoiceCards.vue";
import ConfirmWithReauth from "./ConfirmWithReauth.vue";
import EffectiveValue from "./EffectiveValue.vue";
import InlineReveal from "./InlineReveal.vue";
import SaveBar from "./SaveBar.vue";
import StepFlow from "./StepFlow.vue";
import stepFlowSource from "./StepFlow.vue?raw";
import { readFileSync } from "node:fs";
import { activityFiltersFromQuery, activityFiltersToQuery, filterActivity, foldRepeats, groupActivityByDay, type ActivityItem } from "./activity";
import { buildSettingsHash, parseSettingsHash } from "./useSettingsRoute";
import { provideReauthEnvironment, type ReauthEnvironment } from "./useReauth";
import type { ChoiceOption } from "./types";

afterEach(() => {
  vi.useRealTimers();
  document.body.innerHTML = "";
});

const options: ChoiceOption[] = [
  { value: "lan", label: "LAN", description: "Kancelář", icon: "building-2" },
  { value: "vpn", label: "VPN", description: "Klienti VPN", icon: "globe" },
  { value: "single", label: "Jedna adresa", icon: "map-pin" },
];

describe("ChoiceCards", () => {
  // Arrow keys (roving focus) are covered in a real browser by tests/browser/settings-security.spec.ts;
  // jsdom does not move focus through reka's roving focus group.
  it("is a labelled radiogroup with a single Tab stop", async () => {
    const wrapper = mount(ChoiceCards, { props: { options, label: "Druh sítě", modelValue: "lan" }, attachTo: document.body });
    const group = wrapper.get('[role="radiogroup"]');
    expect(document.getElementById(group.attributes("aria-labelledby")!)?.textContent).toBe("Druh sítě");
    const radios = wrapper.findAll('[role="radio"]');
    expect(radios).toHaveLength(3);
    expect(radios[0]!.attributes("aria-checked")).toBe("true");
    // The whole tile is the label of its radio: its title and description name the option.
    expect(wrapper.get(`label[for="${radios[1]!.attributes("id")}"]`).text()).toContain("Klienti VPN");
    expect(radios.filter((radio) => radio.attributes("tabindex") === "0").length).toBeLessThanOrEqual(1);
    await radios[1]!.trigger("click");
    expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual(["vpn"]);
    wrapper.unmount();
  });

  it("selects by click and opens the contextual next step only for the chosen value", async () => {
    const wrapper = mount(ChoiceCards, {
      props: { options, label: "Druh", modelValue: null, revealFor: ["vpn"], "onUpdate:modelValue": (value: unknown) => wrapper.setProps({ modelValue: value as string }) },
      slots: { reveal: '<p class="next-step">Rozsah VPN</p>' },
    });
    expect(wrapper.find(".next-step").exists()).toBe(false);
    await wrapper.findAll('[role="radio"]')[0]!.trigger("click");
    expect(wrapper.find(".next-step").exists()).toBe(false);
    await wrapper.findAll('[role="radio"]')[1]!.trigger("click");
    await flushPromises();
    expect(wrapper.find(".next-step").exists()).toBe(true);
  });

  it("uses checkboxes in the multi-select variant", async () => {
    const wrapper = mount(ChoiceCards, {
      props: { options, label: "Schopnosti", multiple: true, modelValue: ["lan"], "onUpdate:modelValue": (value: unknown) => wrapper.setProps({ modelValue: value as string[] }) },
    });
    expect(wrapper.find('[role="radiogroup"]').exists()).toBe(false);
    const boxes = wrapper.findAll('[role="checkbox"]');
    expect(boxes).toHaveLength(3);
    expect(boxes[0]!.attributes("aria-checked")).toBe("true");
    await boxes[2]!.trigger("click");
    expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual([["lan", "single"]]);
  });
});

describe("InlineReveal", () => {
  it("renders its content only while open", async () => {
    const wrapper = mount(InlineReveal, { props: { open: false }, slots: { default: "<p class='detail'>Podrobnosti</p>" } });
    expect(wrapper.find(".detail").exists()).toBe(false);
    await wrapper.setProps({ open: true });
    await flushPromises();
    expect(wrapper.find(".detail").exists()).toBe(true);
    expect(wrapper.get("[data-inline-reveal]").attributes("data-state")).toBe("open");
    await wrapper.setProps({ open: false });
    await flushPromises();
    expect(wrapper.find(".detail").exists()).toBe(false);
  });

  it("clips only while it animates, so focus rings at its edges stay whole", async () => {
    const animations: Array<EventTarget & { cancel: ReturnType<typeof vi.fn> }> = [];
    Object.defineProperty(Element.prototype, "animate", { configurable: true, value: () => {
      const animation = Object.assign(new EventTarget(), { cancel: vi.fn() });
      animations.push(animation);
      return animation;
    } });
    try {
      const wrapper = mount(InlineReveal, { props: { open: true }, slots: { default: "<input class='detail' />" } });
      const content = () => wrapper.get("[data-inline-reveal]");
      // Open at mount: nothing animates, nothing is clipped.
      expect(content().classes()).toContain("is-settled");
      await wrapper.setProps({ open: false });
      await wrapper.setProps({ open: true });
      await flushPromises();
      expect(content().classes()).not.toContain("is-settled");
      // A superseded close must not release the new opening's clip or remove its content.
      animations[0]!.dispatchEvent(new Event("finish"));
      await nextTick();
      expect(content().classes()).not.toContain("is-settled");
      animations.at(-1)!.dispatchEvent(new Event("finish"));
      await nextTick();
      expect(content().classes()).toContain("is-settled");
    } finally {
      delete (Element.prototype as { animate?: unknown }).animate;
    }
  });
});

describe("EffectiveValue", () => {
  it("shows an inherited value with its source and offers an own setting", async () => {
    const wrapper = mount(EffectiveValue, {
      props: { chain: [{ level: "Instance", value: "Vždy", explicit: true }, { level: "Trezor", explicit: false }, { level: "Heslo", explicit: false }] },
    });
    expect(wrapper.get(".effective-current").text()).toBe("Vždy");
    expect(wrapper.text()).toContain("Zděděno");
    expect(wrapper.text()).toContain("Zděděno z úrovně instance");
    expect(wrapper.get("[data-source]").text()).toContain("Instance");
    await wrapper.get("[data-effective-customize]").trigger("click");
    expect(wrapper.emitted("customize")).toHaveLength(1);
  });

  it("marks the nearest own value as Vlastní and can return to the inherited one", async () => {
    const wrapper = mount(EffectiveValue, {
      props: { chain: [{ level: "Instance", value: "Vždy", explicit: true }, { level: "Trezor", value: "Mimo důvěryhodné sítě", explicit: true }] },
    });
    expect(wrapper.get(".effective-current").text()).toBe("Mimo důvěryhodné sítě");
    expect(wrapper.text()).toContain("Vlastní");
    expect(wrapper.get("[data-source]").text()).toContain("Trezor");
    await wrapper.get("[data-effective-inherit]").trigger("click");
    expect(wrapper.emitted("inherit")).toHaveLength(1);
  });
});

const now = new Date(2026, 8, 27, 10, 30);
const iso = (days: number, hours: number) => new Date(2026, 8, 27 - days, hours, 0).toISOString();
const items: ActivityItem[] = [
  { id: "1", at: iso(0, 10), actorId: "jana", actor: "Jana", action: "zobrazila heslo", target: "Router", category: "release", outcome: "allowed", icon: "eye", details: [{ label: "Klient hlásí", value: "Zobrazeno", hint: "není důkaz přečtení" }] },
  { id: "2", at: iso(0, 8), actorId: "tomas", actor: "Tomáš", action: "požádal o heslo", target: "Firewall", category: "release", outcome: "denied", icon: "ban" },
  { id: "3", at: iso(1, 9), actorId: "jana", actor: "Jana", action: "změnila ochranu", target: "Trezor", category: "protection", outcome: "changed", icon: "lock" },
  { id: "4", at: iso(12, 9), actorId: "martin", actor: "Martin", action: "vypnul síť", target: "Stará VPN", category: "network", outcome: "changed", icon: "network" },
];

describe("activity filters", () => {
  it("folds a run of the same entry within ten minutes into its newest one", () => {
    const at = (minute: number) => new Date(2026, 9, 26, 14, minute).toISOString();
    const entry = (id: string, minute: number, action = "upravil(a) sekci stránky") =>
      ({ id, at: at(minute), actorId: "u", actor: "Jana", action, target: "Nabídka", category: "assistant", outcome: "changed", icon: "pencil" }) as const;
    const runs = foldRepeats([entry("a", 54), entry("b", 50), entry("c", 45), entry("d", 30), entry("e", 29, "přejmenoval(a) stránku")]);
    expect(runs.map((run) => [run.item.id, run.more.map((item) => item.id)])).toEqual([["a", ["b", "c"]], ["d", []], ["e", []]]);
  });
  it("filter by who, what, when and result, newest first", () => {
    expect(filterActivity(items, {}, now).map((item) => item.id)).toEqual(["1", "2", "3", "4"]);
    expect(filterActivity(items, { who: ["jana"] }, now).map((item) => item.id)).toEqual(["1", "3"]);
    expect(filterActivity(items, { who: ["jana", "martin"] }, now).map((item) => item.id)).toEqual(["1", "3", "4"]);
    expect(filterActivity(items, { what: "release", result: "denied" }, now).map((item) => item.id)).toEqual(["2"]);
    expect(filterActivity(items, { when: "7d" }, now).map((item) => item.id)).toEqual(["1", "2", "3"]);
    expect(filterActivity(items, { when: "24h" }, now).map((item) => item.id)).toEqual(["1", "2"]);
  });

  it("round-trip through readable URL keys and ignore unknown values", () => {
    const query = activityFiltersToQuery({ who: ["jana", "tomas"], what: "release", when: "7d", result: "denied" });
    expect(query).toEqual({ kdo: "jana,tomas", co: "release", kdy: "7d", vysledek: "denied" });
    expect(activityFiltersFromQuery(query as Record<string, string>).who).toEqual(["jana", "tomas"]);
    expect(activityFiltersFromQuery({ kdo: "jana", kdy: "rok", vysledek: "nevim" })).toEqual({ who: ["jana"], what: undefined, when: undefined, result: undefined });
    expect(activityFiltersToQuery({ who: [] }).kdo).toBeUndefined();
    expect(buildSettingsHash("#/settings", "global/audit", { co: "release", kdo: undefined })).toBe("#/settings/global/audit?co=release");
    expect(parseSettingsHash("#/settings/global/audit?co=release", "#/settings")).toEqual({ path: "global/audit", segments: ["global", "audit"], query: { co: "release" } });
  });

  it("group the timeline by day with readable sentences and expandable details", async () => {
    expect(groupActivityByDay(items, now).map((group) => group.label).slice(0, 2)).toEqual(["Dnes", "Včera"]);
    const wrapper = mount(ActivityTimeline, { props: { items, now } });
    expect(wrapper.findAll(".activity-day-label").map((label) => label.text()).slice(0, 2)).toEqual(["Dnes", "Včera"]);
    expect(wrapper.get(".activity-item").text()).toContain("Jana zobrazila heslo Router");
    expect(wrapper.text()).toContain("Zamítnuto");
    expect(wrapper.find(".activity-details").exists()).toBe(false);
    const toggle = wrapper.get("[data-activity-details]");
    await toggle.trigger("click");
    await flushPromises();
    expect(toggle.attributes("aria-expanded")).toBe("true");
    expect(wrapper.get(".activity-details").text()).toContain("není důkaz přečtení");
  });

  it("filters in one line: people by search, the rest through choice menus, never a native select", async () => {
    const model = ref({});
    const wrapper = mount(ActivityFilters, {
      props: { modelValue: model.value, actors: [{ id: "jana", label: "Jana" }, { id: "tomas", label: "Tomáš" }], categories: [{ id: "release", label: "Vydání hesel" }], total: 4, "onUpdate:modelValue": (value: unknown) => wrapper.setProps({ modelValue: value as object }) },
    });
    expect(wrapper.find("select").exists()).toBe(false);
    expect(wrapper.find('[data-filter="who"] [data-suggestion]').exists()).toBe(false);
    await wrapper.get('[data-filter="who"] input').trigger("focusin");
    await wrapper.get('[data-filter="who"] [data-suggestion="jana"]').trigger("click");
    expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual([{ who: ["jana"] }]);
    expect(wrapper.text()).toContain("Kdo: Jana");
    await wrapper.get('[data-filter="who"] [data-suggestion="tomas"]').trigger("click");
    expect(wrapper.text()).toContain("Kdo: 2 osoby");
    await wrapper.get('[aria-label="Zrušit filtr Kdo: 2 osoby"]').trigger("click");
    expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual([{ who: undefined }]);
  });
});

function withReauth(environment: Partial<ReauthEnvironment>, props: Record<string, unknown> = {}) {
  const env: ReauthEnvironment = { method: "local", supported: true, verifyPassword: async () => true, verifyHub: async () => true, ...environment };
  const confirm = vi.fn();
  const Host = defineComponent({
    setup() {
      provideReauthEnvironment(env);
      return () => h(ConfirmWithReauth, { confirmLabel: "Uložit", onConfirm: confirm, ...props });
    },
  });
  return { wrapper: mount(Host), confirm };
}

describe("ConfirmWithReauth", () => {
  it("blocks the change when the provider cannot prove a fresh sign-in", async () => {
    const verifyHub = vi.fn(async () => true);
    const { wrapper, confirm } = withReauth({ method: "hub", supported: false, verifyHub });
    expect(wrapper.get('[role="alert"]').text()).toContain("Nelze ověřit");
    const button = wrapper.get("[data-confirm-reauth]");
    expect(button.attributes("disabled")).toBeDefined();
    await wrapper.get("form").trigger("submit");
    await flushPromises();
    expect(verifyHub).not.toHaveBeenCalled();
    expect(confirm).not.toHaveBeenCalled();
  });

  it("confirms only after the local password was verified, and forgets it either way", async () => {
    const verifyPassword = vi.fn(async (password: string) => password === "spravne");
    const { wrapper, confirm } = withReauth({ verifyPassword });
    const input = wrapper.get<HTMLInputElement>('input[type="password"]');
    expect(wrapper.get("[data-confirm-reauth]").attributes("disabled")).toBeDefined();
    await input.setValue("chyba");
    await wrapper.get("form").trigger("submit");
    await flushPromises();
    expect(confirm).not.toHaveBeenCalled();
    expect(wrapper.text()).toContain("Heslo nesouhlasí");
    expect(input.element.value).toBe("");
    expect(input.attributes("aria-invalid")).toBe("true");
    await input.setValue("spravne");
    await wrapper.get("form").trigger("submit");
    await flushPromises();
    expect(verifyPassword).toHaveBeenLastCalledWith("spravne", { operation: "security", target: {} });
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(input.element.value).toBe("");
  });

  it("goes through the Hub sign-in without a password field", async () => {
    const { wrapper, confirm } = withReauth({ method: "hub" });
    expect(wrapper.find('input[type="password"]').exists()).toBe(false);
    expect(wrapper.get("[data-confirm-reauth]").text()).toContain("Přihlásit se znovu");
    await wrapper.get("form").trigger("submit");
    await flushPromises();
    expect(confirm).toHaveBeenCalledTimes(1);
  });

  it("confirms without a sign-in inside the server's re-authentication window and asks again once it has closed", async () => {
    const until = ref<number | null>(Date.now() + 5 * 60_000);
    const verifyPassword = vi.fn(async () => true);
    const confirm = vi.fn();
    const Host = defineComponent({
      setup() {
        provideReauthEnvironment({ method: "local", supported: true, verifyPassword, verifyHub: async () => true, get windowUntil() { return until.value; } });
        return () => h(ConfirmWithReauth, { confirmLabel: "Uložit", onConfirm: confirm });
      },
    });
    const wrapper = mount(Host);
    expect(wrapper.find('input[type="password"]').exists()).toBe(false);
    expect(wrapper.get("[data-reauth-window]").text()).toContain("Nové přihlášení teď není potřeba");
    expect(wrapper.get("h4").text()).toBe("Potvrďte změnu");
    await wrapper.get("form").trigger("submit");
    expect(confirm).toHaveBeenLastCalledWith(null);
    expect(verifyPassword).not.toHaveBeenCalled();
    // The server refused (reauth_required) and the window closed: the same form signs in again.
    until.value = null;
    await nextTick();
    expect(wrapper.find('input[type="password"]').exists()).toBe(true);
    expect(wrapper.get("h4").text()).toBe("Potvrďte změnu novým přihlášením");
  });
});

it("rings a focused text field with the one focus ring of the app, not a halo of its own (D6)", () => {
  // vitest runs in src/Atlas.Web, and a ?raw import of CSS comes back empty there.
  expect(readFileSync("src/components/settings/settings.css", "utf8")).toMatch(/\.st-input:focus-visible \{ outline: var\(--state-focus-ring\); outline-offset: var\(--state-focus-offset\); \}/);
});

describe("SaveBar", () => {
  it("appears with unsaved changes, saves explicitly and confirms with a check", async () => {
    vi.useFakeTimers();
    const wrapper = mount(SaveBar, { props: { dirty: false } });
    expect(wrapper.find(".save-bar").exists()).toBe(false);
    await wrapper.setProps({ dirty: true });
    expect(wrapper.get(".save-bar").text()).toContain("Máte neuložené změny");
    await wrapper.get("[data-save-bar-save]").trigger("click");
    expect(wrapper.emitted("save")).toHaveLength(1);
    await wrapper.setProps({ saving: true });
    // The button keeps its label (and width); the message beside it reports the save.
    expect(wrapper.get("[data-save-bar-save]").attributes("aria-busy")).toBe("true");
    expect(wrapper.get(".save-bar-message").text()).toContain("Ukládám");
    await wrapper.setProps({ saving: false, dirty: false });
    expect(wrapper.get('[role="region"]').text()).toContain("Uloženo");
    vi.advanceTimersByTime(2000);
    await nextTick();
    expect(wrapper.find(".save-bar-message").exists()).toBe(false);
  });

  it("keeps the draft on a failed save and offers Zrušit", async () => {
    const wrapper = mount(SaveBar, { props: { dirty: true, saving: true } });
    await wrapper.setProps({ saving: false, error: "Uložení se nezdařilo." });
    expect(wrapper.text()).toContain("Uložení se nezdařilo.");
    expect(wrapper.text()).not.toContain("Uloženo");
    await wrapper.get("[data-save-bar-cancel]").trigger("click");
    expect(wrapper.emitted("cancel")).toHaveLength(1);
  });

  it("gives the keyboard back to the edited field once the save went through, never to <body> (P7)", async () => {
    const field = document.createElement("input");
    document.body.append(field);
    const wrapper = mount(SaveBar, { attachTo: document.body, props: { dirty: true } });
    field.focus();
    (wrapper.get("[data-save-bar-save]").element as HTMLButtonElement).focus();
    await wrapper.setProps({ saving: true });
    (document.activeElement as HTMLElement).blur(); // a disabled button loses the focus
    await wrapper.setProps({ saving: false, dirty: false });
    await nextTick();
    expect(document.activeElement).toBe(field);
    wrapper.unmount();
    field.remove();
  });
});

describe("StepFlow", () => {
  it("shows one step with its question, lists done steps as chips and goes back", async () => {
    const steps = [
      { id: "kind", label: "Druh", title: "Jakou síť?", icon: "network", summary: "VPN" },
      { id: "range", label: "Rozsah", title: "Název a adresy", icon: "map-pin" },
    ];
    const wrapper = mount(StepFlow, {
      props: { steps, label: "Přidat síť", modelValue: "range", "onUpdate:modelValue": (value: string) => wrapper.setProps({ modelValue: value }) },
      slots: { kind: "<p class='kind'>druh</p>", range: "<p class='range'>rozsah</p>" },
    });
    expect(wrapper.text()).toContain("Krok 2 z 2");
    expect(wrapper.get("[data-step-heading]").text()).toBe("Název a adresy");
    expect(wrapper.find(".range").exists()).toBe(true);
    expect(wrapper.get('[data-step-chip="kind"]').text()).toContain("VPN");
    expect(wrapper.get('[aria-current="step"]').text()).toContain("Rozsah");
    await wrapper.get("[data-flow-next]").trigger("click");
    expect(wrapper.emitted("next")).toHaveLength(1);
    await wrapper.get("[data-flow-back]").trigger("click");
    expect(wrapper.emitted("update:modelValue")?.at(-1)).toEqual(["kind"]);
  });

  it("tells which way the flow went, so the steps move in the direction of travel", async () => {
    const steps = [
      { id: "kind", label: "Druh", title: "Jakou síť?", icon: "network" },
      { id: "range", label: "Rozsah", title: "Název a adresy", icon: "map-pin" },
    ];
    const wrapper = mount(StepFlow, {
      props: { steps, label: "Přidat síť", modelValue: "kind", "onUpdate:modelValue": (value: string) => wrapper.setProps({ modelValue: value }) },
      slots: { kind: "<p>druh</p>", range: "<p>rozsah</p>" },
    });
    expect(wrapper.get(".step-flow").attributes("data-direction")).toBe("forward");
    await wrapper.get("[data-flow-next]").trigger("click");
    expect(wrapper.get(".step-flow").attributes("data-direction")).toBe("forward");
    await wrapper.get("[data-flow-back]").trigger("click");
    expect(wrapper.get(".step-flow").attributes("data-direction")).toBe("back");
    await wrapper.get("[data-flow-next]").trigger("click");
    expect(wrapper.get(".step-flow").attributes("data-direction")).toBe("forward");
  });

  it("turns the rise round only for the step that is entering or leaving, not for what rises in inside a settled step", () => {
    // The direction stays on the flow until the next move (the attribute is not reset), so the turned rise is scoped in CSS, which jsdom
    // does not apply: the rule is pinned. A step that has settled (900 ms after Zpět) kept --atlas-rise: calc(8px * -1) and a row, a card or a
    // tile that rose in inside it came down from above.
    const rule = /\.step-flow\[data-direction="back"\] \.step-flow-step([^{]*)\{ --atlas-rise: var\(--step-rise-back\); \}/.exec(stepFlowSource);
    expect(rule?.[1]?.trim()).toBe(":is(.st-step-enter-active, .st-step-leave-active)");
  });
});
