import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import AccentPicker from "./AccentPicker.vue";
import { collaboratorChoices } from "../../live/collaboratorColors";

// „Moje barva“: the picker of the interface accent drawn with the exact colours of the collaborator palette.
describe("AccentPicker with the collaborator palette", () => {
  const mountPicker = (mode: "light" | "dark", modelValue = "blue") =>
    mount(AccentPicker, { props: { accents: collaboratorChoices, mode, label: "Moje barva", modelValue, "onUpdate:modelValue": () => {} } });

  it("is one radiogroup of the 12 colours, the chosen one checked, each named for assistive technology", () => {
    const picker = mountPicker("light");
    expect(picker.get("[role=radiogroup]").attributes("aria-label")).toBe("Moje barva");
    const radios = picker.findAll("[role=radio]");
    expect(radios).toHaveLength(12);
    expect(radios.every((radio) => radio.attributes("aria-label"))).toBe(true);
    expect(radios.filter((radio) => radio.attributes("aria-checked") === "true").map((radio) => radio.attributes("aria-label"))).toEqual([collaboratorChoices.find((choice) => choice.id === "blue")!.label]);
  });

  it("draws each swatch in its palette colour for the previewed mode and tells the parent which was picked", async () => {
    const light = mountPicker("light");
    const blue = collaboratorChoices.find((choice) => choice.id === "blue")!;
    const swatch = (picker: typeof light, id: string) => picker.get(`[data-value="${id}"] [role=radio]`);
    expect(swatch(light, "blue").attributes("style")).toContain(blue.swatch.light);
    expect(swatch(mountPicker("dark"), "blue").attributes("style")).toContain(blue.swatch.dark);
    await swatch(light, "red").trigger("click");
    expect(light.emitted("update:modelValue")?.at(-1)).toEqual(["red"]);
  });
});
