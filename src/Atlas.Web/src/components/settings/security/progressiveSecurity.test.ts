import { describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import NetworkRuleWizard from "./NetworkRuleWizard.vue";
import ProtectionWizard from "./ProtectionWizard.vue";
import GrantFlow from "../GrantFlow.vue";
import ChoiceCards from "../ChoiceCards.vue";
import SearchableChoiceList from "../SearchableChoiceList.vue";
import AccessLevelPicker from "../AccessLevelPicker.vue";
import GrantValidityFields from "../GrantValidityFields.vue";
import { provideReauthEnvironment, type ReauthOutcome } from "../useReauth";
import type { ProtectionState } from "../../../protection";

describe("progressive security and grants", () => {
  it("reveals network impact immediately and refuses an invalidated range", async () => {
    const host = mount(defineComponent({ setup() {
      provideReauthEnvironment({ method: "local", supported: true, verifyPassword: async () => true, verifyHub: async () => true });
      return () => h(NetworkRuleWizard, { existing: [], impact: () => [] });
    } }));
    const view = host.findComponent(NetworkRuleWizard);
    await view.findComponent(ChoiceCards).vm.$emit("update:modelValue", "lan");
    await flushPromises();
    expect(view.find("[data-network-name]").exists()).toBe(true);
    await view.get("[data-network-name]").setValue("Office");
    await view.get("[data-network-cidr]").setValue("10.0.0.0/16");
    await flushPromises();
    expect(view.find(".confirm-reauth").exists()).toBe(true);
    expect(view.find("[data-network-name]").exists()).toBe(true);
    await view.get('input[type="password"]').setValue("password");
    expect((view.get("[data-confirm-reauth]").element as HTMLButtonElement).disabled).toBe(false);
    await view.get("[data-network-cidr]").setValue("0.0.0.0/0");
    expect((view.get("[data-confirm-reauth]").element as HTMLButtonElement).disabled).toBe(true);
    expect(view.emitted("save")).toBeUndefined();
  });

  it("discards a pending protection proof when the mode changes", async () => {
    let resolve!: (value: ReauthOutcome) => void;
    const verifyPassword = vi.fn(() => new Promise<ReauthOutcome>((done) => { resolve = done; }));
    const state = { level: "instance", targetId: null, effective: "always", chain: [{ level: "instance", targetId: null, name: null, mode: "always" }], impact: { vaults: 1, secrets: 2 } } as ProtectionState;
    const host = mount(defineComponent({ setup() {
      provideReauthEnvironment({ method: "local", supported: true, verifyPassword, verifyHub: async () => false });
      return () => h(ProtectionWizard, { target: "Instance", state, activeNetworks: 1 });
    } }));
    const view = host.findComponent(ProtectionWizard);
    await view.findComponent(ChoiceCards).vm.$emit("update:modelValue", "never");
    await flushPromises();
    expect(view.find(".confirm-reauth").exists()).toBe(true);
    await view.get('input[type="password"]').setValue("password");
    await view.get("form").trigger("submit");
    expect(verifyPassword).toHaveBeenCalledOnce();
    await view.findComponent(ChoiceCards).vm.$emit("update:modelValue", "outsideTrustedNetworks");
    await flushPromises();
    resolve({ ok: true, proofId: "old-proof" });
    await flushPromises();
    expect(view.emitted("save")).toBeUndefined();
    await view.get('input[type="password"]').setValue("password");
    await view.get("form").trigger("submit");
    resolve({ ok: true, proofId: "new-proof" });
    await flushPromises();
    expect(view.emitted("save")).toEqual([["outsideTrustedNetworks", "new-proof"]]);
    host.unmount();
  });

  it("shows grant validity and summary after a role, then blocks saving invalid dates", async () => {
    const view = mount(GrantFlow, { props: {
      subjects: [{ id: "u", kind: "user", name: "Alice" }],
      levels: [{ value: "read", label: "Read", icon: "eye" }], target: "Vault",
    } });
    await view.findComponent(ChoiceCards).vm.$emit("update:modelValue", "user");
    await flushPromises();
    expect(view.findComponent(SearchableChoiceList).exists()).toBe(true);
    await view.findComponent(SearchableChoiceList).vm.$emit("update:modelValue", "u");
    await flushPromises();
    await view.findComponent(AccessLevelPicker).vm.$emit("update:modelValue", "read");
    await flushPromises();
    expect(view.findComponent(GrantValidityFields).exists()).toBe(true);
    expect(view.text()).toContain("Zkontrolujte a uložte");
    await view.findComponent(GrantValidityFields).vm.$emit("update:startAt", "2030-01-02T10:00");
    await flushPromises();
    await view.findComponent(GrantValidityFields).vm.$emit("update:expiresAt", "2030-01-01T10:00");
    await flushPromises();
    const save = view.findAll("button").find((button) => button.text() === "Uložit přístup")!;
    expect((save.element as HTMLButtonElement).disabled).toBe(true);
    await save.trigger("click");
    expect(view.emitted("save")).toBeUndefined();
    await view.findComponent(GrantValidityFields).vm.$emit("update:expiresAt", "");
    await flushPromises();
    await view.findComponent(GrantValidityFields).vm.$emit("update:startAt", "");
    await flushPromises();
    await save.trigger("click");
    expect(view.emitted("save")).toEqual([[{ kind: "user", subjectId: "u", role: "read", startAt: "", expiresAt: "" }, null]]);
  });
});
