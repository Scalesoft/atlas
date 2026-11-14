import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import AdminRecovery from "./AdminRecovery.vue";
import { api, post } from "../../api";
vi.mock("../../api", async (original) => ({
  ...(await original<object>()),
  api: vi.fn(),
  post: vi.fn(),
}));
let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount();
  vi.clearAllMocks();
});
it("shows real disk instructions and blocks submission when no recovery account is available", async () => {
  vi.mocked(api).mockResolvedValue({
    available: false,
    builtinEnabled: false,
    tokenFilePath: "C:/atlas/recovery-token.txt",
    instructions: "Nastavte Recovery:Username a restartujte server.",
  });
  wrapper = mount(AdminRecovery);
  await flushPromises();
  expect(wrapper.text()).toContain("C:/atlas/recovery-token.txt");
  expect(wrapper.text()).toContain("restartujte server");
  expect(wrapper.find("form").exists()).toBe(false);
  expect(post).not.toHaveBeenCalled();
});
it("requires matching passwords and returns the recovered session even when builtin login is disabled", async () => {
  vi.mocked(api).mockResolvedValue({
    available: true,
    builtinEnabled: false,
    tokenFilePath: "C:/atlas/recovery-token.txt",
    instructions: "Použijte token ze souboru.",
  });
  const recoveredUser = {
    id: "admin-id",
    username: "admin",
    displayName: "Administrator",
    isWikiAdmin: true,
    appearance: {},
  };
  vi.mocked(post).mockResolvedValue(recoveredUser);
  wrapper = mount(AdminRecovery);
  await flushPromises();
  const fields = wrapper.findAll("input");
  await fields[0]!.setValue("recovery-secret");
  await fields[1]!.setValue("long-new-password");
  await fields[2]!.setValue("different-password");
  await wrapper.get("form").trigger("submit");
  expect(post).not.toHaveBeenCalled();
  expect(wrapper.text()).toContain("neshodují");
  await fields[2]!.setValue("long-new-password");
  await wrapper.get("form").trigger("submit");
  await flushPromises();
  expect(post).toHaveBeenCalledExactlyOnceWith("/auth/recovery", {
    token: "recovery-secret",
    newPassword: "long-new-password",
  });
  expect(wrapper.emitted("recovered")).toEqual([[recoveredUser]]);
  expect(
    fields.every((field) => (field.element as HTMLInputElement).value === ""),
  ).toBe(true);
  expect(location.href).not.toContain("recovery-secret");
});
