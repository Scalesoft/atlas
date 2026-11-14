import { afterEach, expect, it } from "vitest";
import { mount, flushPromises } from "@vue/test-utils";
import ComponentGallery from "./ComponentGallery.vue";

let wrapper: ReturnType<typeof mount>;
afterEach(() => { wrapper?.unmount(); history.replaceState(null, "", "/"); });

it("reveals dependent network fields inline in layouts", async () => {
  history.replaceState(null, "", "/?gallery=1#/gallery/layouts");
  wrapper = mount(ComponentGallery, { attachTo: document.body });
  expect(wrapper.find(".step-flow").exists()).toBe(false);
  const vpn = wrapper.find('[role="radio"][value="vpn"]');
  expect(vpn.exists()).toBe(true);
  expect(wrapper.find('input[placeholder="VPN pro externisty"]').exists()).toBe(false);
  await vpn.trigger("click");
  await flushPromises();
  await wrapper.get('input[placeholder="VPN pro externisty"]').setValue("Externisté");
  await wrapper.get('input[placeholder="10.4.0.0/22"]').setValue("10.4.1.7/22");
  await flushPromises();
  expect(wrapper.get('[aria-label="Souhrn sítě"]').text()).toContain("10.4.0.0/22");
  expect(wrapper.get('input[placeholder="VPN pro externisty"]').element.closest('[data-inline-reveal]')?.getAttribute('data-state')).toBe('open');
  await wrapper.get('[role="radio"][value="office"]').trigger("click");
  await flushPromises();
  expect((wrapper.get('input[placeholder="VPN pro externisty"]').element as HTMLInputElement).value).toBe("Externisté");
  expect(wrapper.get('[aria-label="Souhrn sítě"]').text()).toContain("Kancelář");
  await wrapper.get('input[placeholder="10.4.0.0/22"]').setValue("neplatné");
  await flushPromises();
  expect(wrapper.find('[aria-label="Souhrn sítě"]').exists()).toBe(false);
  expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeDefined();
});

it("keeps the phased wizard and explains its relationship to inline disclosure", () => {
  history.replaceState(null, "", "/?gallery=1#/gallery/settings/networks");
  wrapper = mount(ComponentGallery);
  expect(wrapper.find(".step-flow").exists()).toBe(true);
  expect(wrapper.text()).toContain("uvnitř jednoho kroku");
  expect(wrapper.find('a[href="#/gallery/layouts"]').exists()).toBe(true);
});
