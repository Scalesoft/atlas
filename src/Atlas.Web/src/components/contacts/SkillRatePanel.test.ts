import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import SkillRatePanel from "./SkillRatePanel.vue";
import type { ContactSkills, RateSkillRequest } from "../../contacts/skillApi";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });
const person: ContactSkills = { contactId: "c1", name: "Tomáš Kolář", self: false, suggested: [], skills: [] };

it("offers the existing topic first, Enter takes it, 5 sets the level and Enter saves", async () => {
  const rate = vi.fn(async (_id: string, request: RateSkillRequest) => ({ ...person, skills: [{ tagId: "t1", name: request.topic, score: null, peers: 0, fewPeers: false, mine: request.level, raters: [], evidence: 0 }] }));
  const api = { contact: vi.fn(async () => person), rate, topics: vi.fn(async () => ({ items: [{ id: "t1", name: "C#", people: 3, matched: "csharp" }] })) };
  wrapper = mount(SkillRatePanel, { attachTo: document.body, props: { contactId: "c1", api } });
  await flushPromises();
  const topic = wrapper.get("[data-skill-topic]");
  await topic.setValue("csha");
  await vi.waitFor(() => expect(wrapper!.find("[data-first]").text()).toContain("C#"));
  // The typed text is no topic of its own: Enter takes the existing one, so another spelling never makes a new topic.
  await topic.trigger("keydown", { key: "Enter" });
  expect((topic.element as HTMLInputElement).value).toBe("C#");
  // The level has the keyboard now: 5 sets it, Enter on it saves.
  await vi.waitFor(() => expect(document.activeElement?.getAttribute("role")).toBe("radio"));
  await wrapper.get('[data-value="3"]').trigger("keydown", { key: "5" });
  await wrapper.get('[data-value="5"]').trigger("keydown", { key: "Enter" });
  await flushPromises();
  expect(rate).toHaveBeenCalledWith("c1", { topic: "C#", level: 5, visibility: "shared", note: null });
  expect(wrapper.emitted("saved")).toHaveLength(1);
});
