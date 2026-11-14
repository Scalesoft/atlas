// @vitest-environment node
import { expect, it } from "vitest";
import { AI_CAPABILITY_KEYS, noAiCapabilities, parseAiCapabilities } from "./capabilities";

it("reads the answer and assistant keys strictly and treats unknown or missing ones as false", () => {
  expect(AI_CAPABILITY_KEYS).toContain("answer");
  expect(AI_CAPABILITY_KEYS).toContain("assistant");
  expect(parseAiCapabilities({ answer: true, assistant: true })).toMatchObject({ answer: true, assistant: true, semanticSearch: false });
  expect(parseAiCapabilities({ answer: "true", assistant: 1, chat: true })).toEqual(noAiCapabilities());
  expect(parseAiCapabilities({ semanticSearch: true })).toMatchObject({ answer: false, assistant: false });
  for (const body of [null, undefined, [], "x", 3]) expect(parseAiCapabilities(body)).toEqual(noAiCapabilities());
  expect(noAiCapabilities().answer).toBe(false);
});
