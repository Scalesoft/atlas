// @vitest-environment node
import { expect, it } from "vitest";
import { editorAssetFileName } from "../scripts/editorSourceMap.mjs";

it("preserves the Scalar script identity used by the narrowly scoped CSP probe check", () => {
  expect(editorAssetFileName({ originalFileNames: ["../../editor/node_modules/@scalar/api-reference/dist/browser/standalone.js"] }))
    .toBe("assets/scalar-standalone-[hash][extname]");
  // Another vendor's same basename must never acquire Scalar's security identity.
  expect(editorAssetFileName({ originalFileNames: ["node_modules/other-vendor/standalone.js"] }))
    .toBe("assets/[name]-[hash][extname]");
});
