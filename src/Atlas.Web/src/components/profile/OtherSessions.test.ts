import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../../api";
import { fakeApi } from "../settings/general/testing";
import OtherSessions from "./OtherSessions.vue";

let wrapper: VueWrapper | undefined;
beforeEach(() => clearCsrf());
afterEach(() => {
  wrapper?.unmount();
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
});

it("signs out the other devices only after a confirmation that names the ended tokens", async () => {
  const calls = fakeApi(() => undefined);
  wrapper = mount(OtherSessions, { attachTo: document.body });
  await wrapper.get("[data-end-other-sessions]").trigger("click");
  await flushPromises();
  expect(calls).toHaveLength(0);
  expect(document.querySelector("dialog[open]")!.textContent).toContain("osobní API tokeny");
  document.querySelector<HTMLButtonElement>("dialog[open] [data-confirm-action]")!.click();
  await flushPromises();
  expect(calls).toEqual([{ method: "POST", path: "/profile/sessions/end-others", body: {} }]);
  expect(wrapper.text()).toContain("Ostatní přihlášení skončila.");
  expect(wrapper.emitted("changed")).toHaveLength(1);
});
