import { expect, it } from "vitest";
import { leaveInPlace } from "./leaveInPlace";

it("pins a leaving child where it stands, keeping what it already had", () => {
  const node = document.createElement("div");
  node.style.opacity = "1";
  // jsdom has no layout: the box the child had in the flow.
  for (const [name, value] of Object.entries({ offsetLeft: 12, offsetTop: 340, offsetWidth: 1098, offsetHeight: 1674 })) Object.defineProperty(node, name, { value });
  leaveInPlace(node);
  expect(node.style.left).toBe("12px");
  expect(node.style.top).toBe("340px");
  expect(node.style.width).toBe("1098px");
  expect(node.style.height).toBe("1674px");
  expect(node.style.opacity).toBe("1");
});

it("takes the leaving child out of reach, so its controls are not there twice while it fades", () => {
  const node = document.createElement("ul");
  leaveInPlace(node);
  expect(node.hasAttribute("inert")).toBe(true);
  expect(node.getAttribute("aria-hidden")).toBe("true");
});
