// @vitest-environment node
import { describe, expect, it } from "vitest";
import { spaceCoverProblems } from "../scripts/atlasBundle.mjs";

const drawing = Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" width="640" height="220" viewBox="0 0 640 220"><rect width="640" height="220" fill="#ffffff"/></svg>');
const drawings = new Map([["it-backup", drawing]]);

// The build fails on what this reports (scripts/atlasBundle.mjs); a check nobody can trip is no check.
describe("bundle check of the space drawings", () => {
  it("is silent when every drawing is emitted as a file of its own", () => {
    const bundle = {
      "assets/it-backup-AbCd1234.svg": { type: "asset", fileName: "assets/it-backup-AbCd1234.svg", source: drawing },
      "assets/index-x.js": { type: "chunk", fileName: "assets/index-x.js", code: 'const urls={"it-backup":"/assets/it-backup-AbCd1234.svg"}' },
    };
    expect(spaceCoverProblems(bundle, drawings)).toEqual([]);
  });

  it("reports a drawing that was inlined into a script chunk instead", () => {
    const inlined = "data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' width='640' height='220' viewBox='0 0 640 220'%3e%3c/svg%3e";
    const bundle = { "assets/index-x.js": { type: "chunk", fileName: "assets/index-x.js", code: `const urls={"it-backup":"${inlined}"}` } };
    expect(spaceCoverProblems(bundle, drawings)).toEqual(["not emitted as files of their own: it-backup", "inlined into assets/index-x.js"]);
  });
});
