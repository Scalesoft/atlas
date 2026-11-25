import { afterEach, expect, it, vi } from "vitest";
import { automaticCollaboratorColorId, collaboratorChoices, loadPreferredColors, preferredColorChanged, preferredColorEvent, themedCollaboratorColor } from "./collaboratorColors";
import { userColor } from "./objectPresence";

const api = vi.hoisted(() => vi.fn());
vi.mock("../api", () => ({ api }));

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

it("is the server's automatic slot, from the editor's light palette entry (the C# CollaboratorPaletteTests pin the same slots)", () => {
  expect(collaboratorChoices.map((choice) => choice.id).indexOf(automaticCollaboratorColorId("00000000-0000-0000-0000-000000000001"))).toBe(1);
  expect(collaboratorChoices.map((choice) => choice.id).indexOf(automaticCollaboratorColorId("ffffffff-0000-0000-0000-000000000000"))).toBe(8);
});

it("draws an account where no presence comes in its „Moje barva“ once known, the automatic colour otherwise, and announces the own change", async () => {
  const id = "00000000-0000-0000-0000-00000000000a", other = "00000000-0000-0000-0000-00000000000b";
  const violet = collaboratorChoices.find((choice) => choice.id === "violet")!.swatch.light;
  const automatic = userColor(id);
  api.mockResolvedValueOnce({ [id]: violet });
  await loadPreferredColors();
  expect(api).toHaveBeenCalledWith("/collaborator-colors");
  expect(userColor(id.toUpperCase())).toBe(violet);
  expect(userColor(other)).toBe(collaboratorChoices.find((choice) => choice.id === automaticCollaboratorColorId(other))!.swatch.light);
  const announced = vi.fn();
  window.addEventListener(preferredColorEvent, announced);
  try {
    preferredColorChanged(id, "auto");
    expect(userColor(id)).toBe(automatic);
    expect(announced).toHaveBeenCalledOnce();
  } finally { window.removeEventListener(preferredColorEvent, announced); }
});
