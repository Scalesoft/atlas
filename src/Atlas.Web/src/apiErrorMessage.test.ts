// @vitest-environment node
import { expect, it } from "vitest";
import { ApiError, errorMessage } from "./api";

// A dropped connection must not show the browser's English "Failed to fetch"; anything else keeps its own text.
it.each([
  [new TypeError("Failed to fetch"), "Spojení se serverem se nepodařilo. Zkontrolujte připojení a zkuste to znovu."],
  [new TypeError("NetworkError when attempting to fetch resource."), "Spojení se serverem se nepodařilo. Zkontrolujte připojení a zkuste to znovu."],
  [new TypeError("Load failed"), "Spojení se serverem se nepodařilo. Zkontrolujte připojení a zkuste to znovu."],
  [new ApiError(409, "Stránku mezitím změnil někdo jiný."), "Stránku mezitím změnil někdo jiný."],
  [new TypeError("Cannot read properties of undefined"), "Cannot read properties of undefined"],
  ["něco", "Operace se nezdařila. Zkuste ji znovu."],
])("shows %s as %s", (error, text) => {
  expect(errorMessage(error)).toBe(text);
});
