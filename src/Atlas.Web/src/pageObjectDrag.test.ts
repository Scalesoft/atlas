import { describe, expect, it } from "vitest";
import { contactAt, contactObject, contactObjects } from "./pageObjectDrag";

const page = "3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b";
const contact = "a1b2c3d4-0000-4000-8000-000000000001";

describe("pageObjectDrag", () => {
  it("drags a contact card's own contact by its objectId, and nothing for a contact without one", () => {
    document.body.innerHTML = `<div class="md-contact-list"><article class="md-contact-card"></article><article class="md-contact-card"></article></div>`;
    const [first, second] = [...document.querySelectorAll(".md-contact-card")];
    const data = { contacts: [{ name: "Jan" }, { name: "Eva", objectId: contact.toUpperCase() }] };
    expect(contactObject(second!, data)).toEqual({ id: contact, typeId: "atlas.contact", title: "Eva" });
    expect(contactObject(first!, data)).toBeUndefined();
    expect(contactObject(second!, undefined)).toBeUndefined();
  });

  it("drags a card of a read view by the contact the rendered card names", () => {
    document.body.innerHTML = `<div class="md-contact-list"><article class="md-contact-card" data-object-id="${contact}" data-contact-name="Eva"></article><article class="md-contact-card"></article></div>`;
    const [named, plain] = [...document.querySelectorAll(".md-contact-card")];
    expect(contactObject(named!, undefined)).toEqual({ id: contact, typeId: "atlas.contact", title: "Eva" });
    expect(contactObject(plain!, undefined)).toBeUndefined();
  });

});

describe("pageObjectDrag: contacts without an id and whole lists (spec 2026-10-30 §8)", () => {
  it("drags a contact linked on the press by that id, and a selected list as all its linked contacts", () => {
    document.body.innerHTML = `<div class="md-contact-list"><article class="md-contact-card"></article><article class="md-contact-card"></article></div>`;
    const [first] = [...document.querySelectorAll(".md-contact-card")];
    const data = { contacts: [{ name: "Jan" }, { name: "Eva", objectId: contact }] };
    expect(contactAt(first!, data)).toEqual({ name: "Jan" });
    expect(contactObject(first!, data, page)).toEqual({ id: page, typeId: "atlas.contact", title: "Jan" });
    expect(contactObjects(data)).toEqual([{ id: contact, typeId: "atlas.contact", title: "Eva" }]);
    expect(contactObject(document.body, data, page)).toBeUndefined();
  });
});
