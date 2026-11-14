import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import LoginMethods from "./LoginMethods.vue";
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
  history.replaceState(null, "", "/");
});
describe("login and one-time activation", () => {
  it("shows configured provider and omits builtin credentials when disabled", async () => {
    vi.mocked(api).mockResolvedValue({
      builtinEnabled: false,
      providers: [
        {
          id: "hub",
          displayName: "Firemní Hub",
          loginUrl: "/api/auth/oidc/hub/login",
        },
      ],
    });
    wrapper = mount(LoginMethods);
    await flushPromises();
    expect(wrapper.text()).toContain("Pokračovat přes Firemní Hub");
    expect(wrapper.find('input[type="password"]').exists()).toBe(false);
    expect(post).not.toHaveBeenCalled();
  });
  it("activates with one token and returns to login without storing or logging in automatically", async () => {
    vi.mocked(api).mockResolvedValue({ builtinEnabled: true, providers: [] });
    vi.mocked(post).mockResolvedValue(undefined);
    wrapper = mount(LoginMethods);
    await flushPromises();
    await wrapper.get(".login-alternative").trigger("click");
    const fields = wrapper.findAll("input");
    await fields[0]!.setValue("one-use-token");
    await fields[1]!.setValue("a-long-new-password");
    await fields[2]!.setValue("a-long-new-password");
    await wrapper.get("form").trigger("submit");
    await flushPromises();
    expect(post).toHaveBeenCalledExactlyOnceWith("/auth/activate", {
      token: "one-use-token",
      newPassword: "a-long-new-password",
    });
    expect(wrapper.text()).toContain("Heslo bylo nastaveno");
    expect(wrapper.find('input[autocomplete="username"]').exists()).toBe(true);
    expect(wrapper.html()).not.toContain("one-use-token");
    expect(wrapper.emitted("signedIn")).toBeUndefined();
  });
});
describe("where the keyboard starts", () => {
  it("puts the caret in the user name once the sign-in methods load, and the first provider when there is no local account", async () => {
    vi.mocked(api).mockResolvedValue({ builtinEnabled: true, providers: [] });
    wrapper = mount(LoginMethods, { attachTo: document.body });
    await flushPromises();
    expect(document.activeElement).toBe(wrapper.get('input[autocomplete="username"]').element);
    wrapper.unmount();
    vi.mocked(api).mockResolvedValue({ builtinEnabled: false, providers: [{ id: "hub", displayName: "Firemní Hub", loginUrl: "/api/auth/oidc/hub/login" }] });
    wrapper = mount(LoginMethods, { attachTo: document.body });
    await flushPromises();
    expect(document.activeElement?.textContent).toContain("Pokračovat přes Firemní Hub");
  });
});
describe("developer sign-in", () => {
  const developerButton = () => wrapper!.findAll("button").find((button) => button.text().includes("Přihlásit jako vývojář"));
  it("is offered only when the server allows it for this request", async () => {
    vi.mocked(api).mockResolvedValue({ builtinEnabled: true, providers: [] });
    wrapper = mount(LoginMethods);
    await flushPromises();
    expect(developerButton()).toBeUndefined();
    expect(wrapper.text()).not.toContain("jen pro vývoj");
  });
  it("signs in without credentials and continues like a normal login", async () => {
    const user = { id: "admin-id", username: "administrator" };
    vi.mocked(api).mockResolvedValue({ builtinEnabled: true, providers: [], developerSignIn: true });
    vi.mocked(post).mockResolvedValue(user);
    wrapper = mount(LoginMethods);
    await flushPromises();
    expect(wrapper.text()).toContain("jen pro vývoj");
    await developerButton()!.trigger("click");
    await flushPromises();
    expect(post).toHaveBeenCalledExactlyOnceWith("/auth/developer");
    expect(wrapper.emitted("signedIn")).toEqual([[user]]);
  });
  it("shows the wait on its own button, not on the password one (UX cycle 1, SH-8)", async () => {
    vi.mocked(api).mockResolvedValue({ builtinEnabled: true, providers: [], developerSignIn: true });
    vi.mocked(post).mockReturnValue(new Promise(() => {}));
    wrapper = mount(LoginMethods);
    await flushPromises();
    const developer = developerButton()!;
    await developer.trigger("click");
    expect(developer.text()).toBe("Přihlašujeme…");
    expect(wrapper.get('form button:not([type="button"])').text()).toBe("Přihlásit se");
  });
});
describe("administrator recovery entry", () => {
  it("waits in the version details instead of beside the login form", async () => {
    vi.mocked(api).mockResolvedValue({ builtinEnabled: true, providers: [] });
    wrapper = mount(LoginMethods, { props: { version: "0.1.1+abcdef0123" }, attachTo: document.body });
    await flushPromises();
    const recovery = wrapper.get(".login-recovery");
    expect(recovery.isVisible()).toBe(false);
    await wrapper.get(".atlas-version-toggle").trigger("click");
    expect(recovery.isVisible()).toBe(true);
    await recovery.trigger("click");
    await flushPromises();
    expect(wrapper.find(".atlas-version").exists()).toBe(false);
  });
});
