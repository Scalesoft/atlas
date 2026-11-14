// @vitest-environment node
import { expect, it } from "vitest";
import { Schema } from "@tiptap/pm/model";
import { placeOnPage } from "./requirementCommands";

// Plan 3.5 C: „Moduly produktu“ puts a module under the page's own product › category headings, as the preset (Standardizace) lays them out.
const schema = new Schema({ nodes: { doc: { content: "block+" }, text: {},
  heading: { group: "block", content: "text*", attrs: { level: { default: 2 } } }, paragraph: { group: "block", content: "text*" } } });
const page = (...blocks: [number | 0, string][]) => schema.node("doc", null, blocks.map(([level, text]) =>
  level ? schema.node("heading", { level }, [schema.text(text)]) : schema.node("paragraph", null, [schema.text(text)])));
const at = (doc: ReturnType<typeof page>, text: string) => { let found = -1; doc.forEach((node, pos) => { if (found < 0 && node.textContent === text) found = pos; }); return found; };

it("goes to the end of the deepest section along product › category › module and names the headings that are missing", () => {
  const doc = page([2, "Společné"], [0, "vstupy"], [2, "Portál pacienta"], [0, "základ"], [3, "Rezervace"], [4, "Webové objednávání (534)"], [0, "terminy"],
    [3, "Zdravotní data"], [0, "ehr"], [2, "Zakázkový vývoj"]);

  expect(placeOnPage(doc, ["Portál pacienta", "Rezervace", "Webové objednávání (534)"])).toEqual({ pos: at(doc, "Zdravotní data"), level: 5, missing: [] });
  expect(placeOnPage(doc, ["Portál pacienta", "Rezervace", "Objednávání lékařem (526)"])).toEqual({ pos: at(doc, "Zdravotní data"), level: 4, missing: ["Objednávání lékařem (526)"] });
  // Case and accents aside; a category the page lacks goes at the end of the product, before the next product.
  expect(placeOnPage(doc, ["portal pacienta", "Telemedicína", "Telekonzultace (521)"])).toEqual({ pos: at(doc, "Zakázkový vývoj"), level: 3, missing: ["Telemedicína", "Telekonzultace (521)"] });
  // A category of another product does not count: „Rezervace“ is searched under MPI only.
  expect(placeOnPage(doc, ["MPI", "Rezervace", "x"])).toBeNull();
  expect(placeOnPage(page([2, "Portál pacienta"], [3, "Rezervace"]), ["Portál pacienta", "Rezervace", "Web"])).toEqual({ pos: page([2, "Portál pacienta"], [3, "Rezervace"]).content.size, level: 4, missing: ["Web"] });
});
