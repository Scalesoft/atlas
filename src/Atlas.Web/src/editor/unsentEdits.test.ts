// @vitest-environment node
import { expect, it } from "vitest";
import { reapplyUnsent } from "./unsentEdits";

const doc = (...texts: string[]) => ({ type: "doc", content: texts.map(text => ({ type: "paragraph", content: [{ type: "text", text }] })) });
const texts = (value: ReturnType<typeof reapplyUnsent>) => value?.content?.map(block => block.content?.[0]?.text);

it("puts back what was typed and keeps what the server changed elsewhere", () => {
  // Acknowledged A B; typed into A, unsent; the page started again from a revision that rewrote B.
  expect(texts(reapplyUnsent(doc("A", "B"), doc("A typed", "B"), doc("A", "B by AI")))).toEqual(["A typed", "B by AI"]);
  // A new block typed right where the server inserted one.
  expect(texts(reapplyUnsent(doc("A", "B"), doc("A", "Mine", "B"), doc("A", "Theirs", "B")))).toEqual(["A", "Mine", "Theirs", "B"]);
  expect(texts(reapplyUnsent(doc("A", "B"), doc("A", "B typed"), doc("A", "New", "B")))).toEqual(["A", "New", "B typed"]);
});

it("keeps both where the server changed the same place, and nothing when there is nothing to put back", () => {
  expect(texts(reapplyUnsent(doc("A", "B"), doc("A", "B typed"), doc("A", "B by AI")))).toEqual(["A", "B by AI", "B typed"]);
  expect(reapplyUnsent(doc("A", "B"), doc("A", "B"), doc("A", "B by AI"))).toBeNull();
  // The server holds the same edit already.
  expect(reapplyUnsent(doc("A", "B"), doc("A typed", "B"), doc("A typed", "B"))).toBeNull();
});
