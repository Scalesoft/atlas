import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PasswordSettings from "./PasswordSettings.vue";
import { api, post } from "../../api";
vi.mock("../../api", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  api: vi.fn(),
  post: vi.fn(),
}));
let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount();
  vi.clearAllMocks();
});
describe("own password security", () => {
  it("lets a Hub account set its first local password with a sign-in name, confirmed by a fresh sign-in", async () => {
    // Inside the ten-minute window of an earlier fresh sign-in the confirmation needs no further sign-in (ConfirmWithReauth).
    const reauthWindowExpiresAt = new Date(Date.now() + 9 * 60_000).toISOString();
    vi.mocked(api).mockImplementation(async (path: string) =>
      path === "/security/me" ? { reauthWindowExpiresAt } as never
        : path === "/profile/security" ? { hasBuiltinCredential: false, credentialState: "none", username: null, email: "jana@example.com" } as never
        : undefined as never);
    wrapper = mount(PasswordSettings);
    await flushPromises();
    await wrapper.get("[data-password-change]").trigger("click");
    await flushPromises();
    const inputs = wrapper.findAll(".first-password input");
    expect((inputs[0]!.element as HTMLInputElement).value).toBe("jana@example.com");
    await inputs[1]!.setValue("first long password");
    await inputs[2]!.setValue("first long passwor");
    expect(wrapper.text()).toContain("neshodují");
    expect(wrapper.get("[data-confirm-reauth]").attributes("disabled")).toBeDefined();
    await inputs[2]!.setValue("first long password");
    await wrapper.get(".first-password").trigger("submit");
    await flushPromises();
    expect(api).toHaveBeenCalledWith("/profile/password/local", expect.objectContaining({
      method: "POST", body: JSON.stringify({ username: "jana@example.com", newPassword: "first long password" }),
    }));
    expect(post).not.toHaveBeenCalled();
  });
  it("opens the change in place and binds a mismatch to the confirmation before sending credentials", async () => {
    vi.mocked(api).mockResolvedValue({ hasBuiltinCredential: true, credentialState: "active" });
    wrapper = mount(PasswordSettings);
    await flushPromises();
    expect(wrapper.find("form").exists()).toBe(false);
    await wrapper.get("[data-password-change]").trigger("click");
    await flushPromises();
    const inputs = wrapper.findAll("form input");
    await inputs[0]!.setValue("current-password");
    await inputs[1]!.setValue("new-password-first");
    await inputs[2]!.setValue("new-password-second");
    await wrapper.get("form").trigger("submit");
    const alert = wrapper.get('[role="alert"]');
    expect(alert.text()).toContain("neshodují");
    expect(inputs[2]!.attributes("aria-describedby")).toContain(alert.attributes("id"));
    expect(inputs[2]!.attributes("aria-invalid")).toBe("true");
    expect(post).not.toHaveBeenCalled();
  });
});
