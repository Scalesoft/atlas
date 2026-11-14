import { expect, it } from "vitest";
import { dropNeutralFilter } from "./printImages";

it("prints an image without the renderer's neutral filter but keeps a real adjustment", () => {
  const neutral = document.createElement("img");
  neutral.style.filter = "brightness(100%) contrast(100%) saturate(100%)";
  const adjusted = document.createElement("img");
  adjusted.style.filter = "brightness(120%) contrast(100%) saturate(100%)";
  document.body.append(neutral, adjusted);
  dropNeutralFilter(neutral);
  dropNeutralFilter(adjusted);
  expect(neutral.style.filter).toBe("none");
  expect(adjusted.style.filter).toBe("brightness(120%) contrast(100%) saturate(100%)");
});
