import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../../../api";
import { fakeApi, problem } from "../general/testing";
import ProfileHubStatus from "./ProfileHubStatus.vue";

let wrapper: VueWrapper;
const status = (verifiedUntil = "2020-01-01T12:00:00Z") => ({ mode: "Preview", identity: {
  state: "Active", verifiedUntil, lastSuccessAt: "2020-01-01T11:55:00Z", lastAttemptAt: "2020-01-01T11:55:00Z", lastErrorCode: null,
} });
beforeEach(clearCsrf);
afterEach(() => { wrapper?.unmount(); vi.useRealTimers(); vi.unstubAllGlobals(); });

it("marks verification expired while the profile stays open", async () => {
  vi.useFakeTimers({ toFake: ["Date", "setInterval", "clearInterval"] });
  vi.setSystemTime(new Date("2026-10-01T12:00:00Z"));
  fakeApi(() => status("2026-10-01T12:00:02Z"));
  wrapper = mount(ProfileHubStatus);
  await flushPromises();
  expect(wrapper.text()).not.toContain("Ověření vypršelo");
  await vi.advanceTimersByTimeAsync(3000);
  expect(wrapper.text()).toContain("Ověření vypršelo");
});

it("shows expired verification and refreshes only the signed-in identity before notifying the access list", async () => {
  let refreshed = false;
  const calls = fakeApi((method, path) => {
    if (path === "/profile/hub/verify" && method === "POST") { refreshed = true; return undefined; }
    return status(refreshed ? "2099-01-01T12:00:00Z" : undefined);
  });
  wrapper = mount(ProfileHubStatus);
  await flushPromises();
  expect(wrapper.text()).toContain("Ověření vypršelo");
  expect(wrapper.text()).toContain("Náhled");
  await wrapper.get('[data-profile-hub-verify]').trigger("click");
  await flushPromises();
  expect(calls.find(call => call.method === "POST")).toEqual({ method: "POST", path: "/profile/hub/verify", body: undefined });
  expect(wrapper.text()).toContain("Ověřeno");
  expect(wrapper.text()).not.toContain("Ověření vypršelo");
  expect(wrapper.emitted("verified")).toHaveLength(1);
});

it("keeps the previous diagnostics after failed verification and permits a deliberate retry", async () => {
  let failed = true;
  fakeApi((method) => method === "POST" && failed ? problem(503, "Hub právě neodpovídá.") : status());
  wrapper = mount(ProfileHubStatus);
  await flushPromises();
  await wrapper.get('[data-profile-hub-verify]').trigger("click");
  await flushPromises();
  expect(wrapper.text()).toContain("Hub právě neodpovídá.");
  expect(wrapper.text()).toContain("Ověření vypršelo");
  expect(wrapper.emitted("verified")).toBeUndefined();
  failed = false;
  await wrapper.get('[data-profile-hub-verify]').trigger("click");
  await flushPromises();
  expect(wrapper.text()).not.toContain("Hub právě neodpovídá.");
  expect(wrapper.emitted("verified")).toHaveLength(1);
});
