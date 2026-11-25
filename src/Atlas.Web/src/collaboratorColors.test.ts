import { afterEach, expect, it } from "vitest";
import { automaticCollaboratorColorId, collaboratorChoices, themedCollaboratorColor } from "./collaboratorColors";
import { userColor } from "./live/objectPresence";

afterEach(() => { delete document.documentElement.dataset.mode; });

it("lists the 12 palette colours named as the appearance hues, and 'Automaticky' is the colour a user id hashes to", () => {
  expect(collaboratorChoices).toHaveLength(12);
  expect(collaboratorChoices.map((choice) => choice.id)).toContain("slate");
  expect(collaboratorChoices.every((choice) => choice.label && choice.label !== choice.id)).toBe(true);
  const id = "00000000-0000-0000-0000-000000000004";
  expect(collaboratorChoices.find((choice) => choice.id === automaticCollaboratorColorId(id))!.swatch.light).toBe(userColor(id));
});

it("draws the stamped colour in the dark variant on a dark surface and leaves colours outside the palette", () => {
  const blue = collaboratorChoices.find((choice) => choice.id === "blue")!.swatch;
  expect(themedCollaboratorColor(blue.light)).toBe(blue.light);
  document.documentElement.dataset.mode = "dark";
  expect(themedCollaboratorColor(blue.light)).toBe(blue.dark);
  expect(themedCollaboratorColor("#7c3aed")).toBe("#7c3aed");
});
