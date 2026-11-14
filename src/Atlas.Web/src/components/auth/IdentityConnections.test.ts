import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import IdentityConnections from "./IdentityConnections.vue";
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
describe("explicit identity linking", () => {
  it("shows the unconfigured state without requesting unavailable OIDC endpoints", async () => {
    vi.mocked(api).mockImplementation(async path => {
      if (path === "/auth/methods") return { builtinEnabled: true, providers: [] };
      throw new Error("Unavailable endpoint requested: " + path);
    });
    wrapper = mount(IdentityConnections);
    await flushPromises();
    expect(api).toHaveBeenCalledTimes(1);
    expect(api).toHaveBeenCalledWith("/auth/methods");
    expect(wrapper.text()).toContain("Správce zatím nenastavil žádného externího poskytovatele.");
    expect(wrapper.find('[role="alert"]').exists()).toBe(false);
  });
  it("requires current local password and cancellation never starts linking", async () => {
    vi.mocked(api).mockImplementation(async (path) => {
      if (path === "/auth/methods") return { builtinEnabled: true, providers: [{ id: "hub", displayName: "Hub", loginUrl: "/api/auth/oidc/hub/login" }] };
      if (path === "/profile/identities") return { items: [] };
      return { hasBuiltinCredential: true, credentialState: "active" };
    });
    wrapper = mount(IdentityConnections);
    await flushPromises();
    await wrapper.get("[data-identity-link]").trigger("click");
    await flushPromises();
    expect(wrapper.get('form input[type="password"]').attributes("required")).toBeDefined();
    await wrapper.get("form input").setValue("never-sent");
    await wrapper.get("[data-identity-cancel]").trigger("click");
    await flushPromises();
    expect(post).not.toHaveBeenCalled();
    expect(wrapper.find("form").exists()).toBe(false);
  });
});
