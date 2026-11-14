// @vitest-environment node
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { expect, it } from "vitest";
import { maxNoteText, maxNoteTitle } from "./personalApi";

// The note editor lets through exactly what the server takes; a smaller limit would cut a note, a larger one fail with a 400.
it("limits a note's title and text as the server does", () => {
  const server = readFileSync(resolve(__dirname, "../../../Atlas.Server/Features/PersonalItems/NoteObjectType.cs"), "utf8");
  const of = (name: string) => Number(new RegExp(`const int ${name} = ([\\d_]+);`).exec(server)?.[1]?.replace(/_/g, ""));
  expect([maxNoteTitle, maxNoteText]).toEqual([of("MaxTitle"), of("MaxText")]);
});
