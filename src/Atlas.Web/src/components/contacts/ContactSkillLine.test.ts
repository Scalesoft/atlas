import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import ContactSkillLine from "./ContactSkillLine.vue";
import type { ContactSkill, ContactSkills } from "../../contacts/skillApi";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; });
const skill = (name: string, score: number | null): ContactSkill => ({ tagId: `t-${name}`, name, score, peers: 1, fewPeers: true, raters: [], evidence: 0 });

it("shows as chips the topics no level is shown for yet, beside the bars of the rated ones: no headed line without anything under it", async () => {
  const contact = vi.fn(async () => ({ skills: [skill("Kubernetes", null), skill("Vue", 4)] }) as unknown as ContactSkills);
  wrapper = mount(ContactSkillLine, { props: { contactId: "c-1", bars: true, api: { contact, rate: vi.fn(), topics: vi.fn() } } });
  await flushPromises();
  expect(wrapper.findAll(".contact-skill-line-chip").map(chip => chip.text())).toEqual(["Kubernetes"]);
  expect(wrapper.findComponent({ name: "SkillBars" }).props("skills").map((each: ContactSkill) => each.name)).toEqual(["Vue"]);
});

it("draws no bars at all when none of the topics has a level the reader may see", async () => {
  const contact = vi.fn(async () => ({ skills: [skill("Kubernetes", null)] }) as unknown as ContactSkills);
  wrapper = mount(ContactSkillLine, { props: { contactId: "c-1", bars: true, api: { contact, rate: vi.fn(), topics: vi.fn() } } });
  await flushPromises();
  expect(wrapper.findComponent({ name: "SkillBars" }).exists()).toBe(false);
  expect(wrapper.find(".contact-skill-line-chip").text()).toBe("Kubernetes");
});
