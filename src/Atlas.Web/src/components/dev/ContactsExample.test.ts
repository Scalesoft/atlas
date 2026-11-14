import { afterEach, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import ContactsExample from "./ContactsExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

it("puts a person in from the people picker by a nickname and an any-order name, two Jan Nováks apart", async () => {
  wrapper = mount(ContactsExample, { attachTo: document.body });
  const query = wrapper.get<HTMLInputElement>("[data-example-contact-picker] [data-contact-picker-query]");
  const rows = () => [...document.querySelectorAll("[data-example-contact-picker] [role=option]")].map(row => row.textContent ?? "");
  await query.setValue("novak jan");
  await vi.waitFor(() => expect(rows().filter(row => row.startsWith("Jan Novák"))).toHaveLength(2));
  expect(rows()[0]).toContain("Modelsoft");
  expect(rows()[1]).toContain("AB Klinika");
  await query.setValue("kovarik jan");
  await vi.waitFor(() => expect(rows()[0]).toContain("Honza Kovařík"));
  await query.trigger("keydown", { key: "Enter" });
  expect(wrapper.get("[data-example-picked]").text()).toBe("Vloženo: Honza Kovařík");
});
