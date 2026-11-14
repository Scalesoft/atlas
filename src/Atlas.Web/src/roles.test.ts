// @vitest-environment node
import { describe, expect, it } from "vitest";
import { roleLabel, roleLabels } from "./roles";

describe("role vocabulary", () => {
  it("names every role in Czech, space and page roles as steps of the access staircase", () => {
    expect(roleLabels).toEqual({ Reader: "Čte", Writer: "Upravuje", Admin: "Spravuje", WikiAdmin: "Správce wiki" });
  });
  it("labels a known role and keeps an unknown one as it came", () => {
    expect(roleLabel("Writer")).toBe("Upravuje");
    expect(roleLabel("WikiAdmin")).toBe("Správce wiki");
    expect(roleLabel("Vlastní")).toBe("Vlastní");
    expect(roleLabel("")).toBe("");
  });
  it("leaks no technical role name into a label", () => {
    for (const label of Object.values(roleLabels)) expect(label).not.toMatch(/Reader|Writer|Admin/);
  });
});
