import { expect, it } from "vitest";
import { outOfReach } from "./outOfReach";

it("takes a leaving node out of the keyboard's and assistive technology's reach", () => {
  const node = document.createElement("section");
  node.innerHTML = '<button type="button">Uložit</button>';
  outOfReach(node);
  expect(node.hasAttribute("inert")).toBe(true);
  expect(node.getAttribute("aria-hidden")).toBe("true");
});
