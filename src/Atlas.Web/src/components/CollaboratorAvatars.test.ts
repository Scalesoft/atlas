import { afterEach, describe, expect, it } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import CollaboratorAvatars from "./CollaboratorAvatars.vue";
import type { CollaborationPeer } from "../collaborationPresence";

const peers: CollaborationPeer[] = [
  { clientId: 11, id: "ana", name: "Ana Reader", color: "#e85d75", cursorAvailable: true },
  { clientId: 12, id: "bo", name: "Bo Lokální", color: "#2a9d8f", cursorAvailable: true },
  { clientId: 13, name: "Chloë", color: "#457b9d", cursorAvailable: false },
  { clientId: 14, name: "Daan", color: "#f4a261", cursorAvailable: true },
  { clientId: 15, name: "Eliška Nováková", color: "#8d5cf6", cursorAvailable: true },
  { clientId: 16, name: "Farah", color: "#d97706", cursorAvailable: false },
];

let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  document.body.innerHTML = "";
});

describe("collaborator avatars", () => {
  it("shows four colored collaborators and groups every remaining peer in an accessible overflow", async () => {
    wrapper = mount(CollaboratorAvatars, { props: { peers }, attachTo: document.body });

    const avatars = wrapper.findAll(".collaborator-avatar");
    expect(avatars).toHaveLength(4);
    expect(wrapper.findAll(".collaborator-avatar-slot")).toHaveLength(4);
    expect(avatars.map(avatar => avatar.text())).toEqual(["AR", "BL", "C", "D"]);
    expect(avatars[0].attributes("style")).toContain("--collaborator-color: #e85d75");

    const overflow = wrapper.get('button[aria-label="Další spolupracovníci: 2"]');
    expect(overflow.text()).toBe("+2");
    await overflow.trigger("click");

    const extras = document.body.querySelector('[role="group"][aria-label="Další spolupracovníci"]')!;
    expect(extras).not.toBeNull();
    expect(extras.querySelector('[role="menuitem"]')).toBeNull();
    expect(extras.textContent).toContain("Eliška Nováková");
    expect(extras.textContent).toContain("Farah");
  });

  it("emits the selected client id from both the compact row and overflow menu", async () => {
    wrapper = mount(CollaboratorAvatars, { props: { peers }, attachTo: document.body });

    await wrapper.get('button[aria-label="Přejít na kurzor uživatele Ana Reader"]').trigger("click");
    await wrapper.get('button[aria-label="Další spolupracovníci: 2"]').trigger("click");
    const overflowPeer = document.body.querySelector<HTMLButtonElement>('button[aria-label="Přejít na kurzor uživatele Eliška Nováková"]')!;
    overflowPeer.click();

    expect(wrapper.emitted("navigate")).toEqual([[11], [15]]);
  });

  it("keeps peers without a cursor focusable while describing why navigation is unavailable", async () => {
    wrapper = mount(CollaboratorAvatars, { props: { peers }, attachTo: document.body });

    const unavailable = wrapper.get('button[aria-label="Chloë – kurzor není dostupný"]');
    expect(unavailable.attributes("aria-disabled")).toBe("true");
    expect(unavailable.attributes("disabled")).toBeUndefined();
    await unavailable.trigger("focus");
    await unavailable.trigger("click");

    expect(wrapper.emitted("navigate")).toBeUndefined();
  });

  it("shows an agent writing for a person with the person's initials and says it writes", () => {
    const agent: CollaborationPeer = { clientId: 21, id: "ai:0f", name: "Jana Nováková · AI (MCP: laptop)", color: "#7c3aed", cursorAvailable: false };
    wrapper = mount(CollaboratorAvatars, { props: { peers: [agent] }, attachTo: document.body });

    const avatar = wrapper.get(".collaborator-avatar");
    expect(avatar.text()).toBe("JN");
    expect(avatar.attributes("aria-label")).toBe("Jana Nováková · AI (MCP: laptop) – zapisuje do stránky");
  });
});
