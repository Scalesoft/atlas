import assert from "node:assert/strict";
import { test } from "node:test";
import { mentions, refuses, scoreAnswer } from "./score.mjs";
import { buildPages } from "./fixtures.mjs";

test("facts ignore case and diacritics and accept alternatives", () => {
  assert.ok(mentions("Čekáme na IT KBN.", "it kbn"));
  assert.ok(mentions("Hotovo je 40 procent.", "40 %|40 procent"));
  assert.ok(!mentions("Hotovo je 4 %.", "40 %|40 procent"));
});

test("an answer needs every fact and no forbidden text", () => {
  const item = { expected: ["a/b"], facts: ["Keepass"], forbidden: ["PX2"] };
  const run = (answer) => scoreAnswer(item, { answer, cited: ["a/b"], sources: ["a/b"] });
  assert.equal(run("V Keepass › KBN.").answerOk, true);
  assert.equal(run("V trezoru.").answerOk, false);
  assert.deepEqual(run("V Keepass u PX2.").forbiddenHits, ["PX2"]);
});

test("citation of several expected pages needs all of them when expectAll is set", () => {
  const item = { expected: ["a/x", "a/y"], expectAll: true };
  assert.equal(scoreAnswer(item, { answer: "", cited: ["a/x"], sources: ["a/x", "a/y"] }).citedOk, false);
  assert.equal(scoreAnswer(item, { answer: "", cited: ["a/x", "a/y"], sources: [] }).citedOk, true);
  assert.equal(scoreAnswer({ ...item, expectAll: false }, { answer: "", cited: ["a/y"], sources: [] }).citedOk, true);
});

test("an unanswerable case passes only without citations", () => {
  const item = { expected: [], forbidden: ["Paříž"] };
  assert.equal(scoreAnswer(item, { answer: "Nevím.", cited: [], sources: [] }).answerOk, true);
  assert.equal(scoreAnswer(item, { answer: "Nevím.", cited: ["wiki/x"], sources: [] }).answerOk, false);
  assert.equal(scoreAnswer(item, { answer: "Paříž.", cited: [], sources: [] }).answerOk, false);
});

test("a refusal does not pass an answerable case, and a fact in a refusal does not rescue it", () => {
  assert.ok(refuses("Ze stránek nevyplývá, kdo má bránu dodat; kontakt je IT KBN."));
  assert.ok(!refuses("SMS bránu dodává IT KBN."));
  const item = { expected: ["a/b"], facts: ["IT KBN"] };
  assert.equal(scoreAnswer(item, { answer: "Nevyplývá, kdo dodá; kontakt je IT KBN.", cited: ["a/b"], sources: [] }).answerOk, false);
  assert.equal(scoreAnswer({ ...item, allowRefusal: true }, { answer: "Nevyplývá, kdo dodá; kontakt je IT KBN.", cited: ["a/b"], sources: [] }).answerOk, true);
});

test("a write scenario needs the expected tool calls and arguments", () => {
  const item = { write: true, expected: [], facts: ["Dubovic"], expectCalls: ["projector_create_deployment"],
    expectArgs: [{ tool: "projector_create_deployment", arg: "moduleKeys", contains: "portal.tele" }] };
  const run = (toolCalls, cited = ["zakazky/x"]) => scoreAnswer(item, { answer: "Podle Dubovic hotovo.", cited, sources: [], toolCalls });
  const create = (input) => ({ name: "projector_create_deployment", input });
  assert.equal(run([create({ moduleKeys: ["portal.tele", "x"] })]).answerOk, true);
  assert.deepEqual(run([]).missingCalls, ["projector_create_deployment"]);
  assert.equal(run([create({ moduleKeys: ["x"] })]).missingArgs.length, 1);
  assert.equal(run([create({})]).answerOk, false);
});

test("the fixture wiki has the same pages with and without blocks, and no block ids repeat", () => {
  const withBlocks = buildPages("with");
  const without = buildPages("without");
  assert.deepEqual(withBlocks.map((page) => page.path), without.map((page) => page.path));
  const ids = [...JSON.stringify(withBlocks).matchAll(/"id":"(6f1c1d3e[^"]+)"/g)].map((match) => match[1]);
  assert.equal(new Set(ids).size, ids.length);
  assert.ok(!JSON.stringify(without).includes("requirement"));
  assert.ok(JSON.stringify(withBlocks).includes("portal.telekonference-azure.tenant"));
});
