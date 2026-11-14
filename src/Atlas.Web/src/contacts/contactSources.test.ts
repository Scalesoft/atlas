// @vitest-environment node
import { expect, it, vi } from "vitest";
import { knownContact, learnAlias, learnPicked } from "./contactSources";

it("teaches the directory contact the searched text after a pick, never a plain beginning of the name, and a refusal leaves the pick alone", async () => {
  const change = vi.fn(async () => ({ version: 2, duplicate: false }));
  const martin = { contact: { name: "Marek Musil" }, objectId: "c-m" };
  learnAlias(martin, " MMu ", { change });
  expect(change).toHaveBeenCalledWith("c-m", { learnAlias: "MMu" });
  change.mockClear();
  learnAlias(martin, "Mar", { change });
  learnAlias({ contact: { name: "Marek Musil" } }, "MMu", { change });
  expect(change).not.toHaveBeenCalled();
  // Who may not change the contact: the server refuses, nothing is thrown.
  change.mockRejectedValueOnce(new Error("403"));
  expect(() => learnAlias(martin, "Mára", { change })).not.toThrow();
  await Promise.resolve();
});

it("learns from an editor pick by the snapshot's object id, and hands the editor a roster person with its aliases", () => {
  const change = vi.fn(async () => ({ version: 2, duplicate: false }));
  learnPicked("MMu", { name: "Marek Musil", objectId: "c-m" }, { change });
  learnPicked("MMu", { name: "Marek Musil" }, { change });
  expect(change).toHaveBeenCalledExactlyOnceWith("c-m", { learnAlias: "MMu" });
  const hit = { key: "obj:c-m", contact: { name: "Marek Musil" }, source: "directory" as const, objectId: "c-m", origin: "Adresář", aliases: ["MMu"] };
  expect(knownContact(hit)).toEqual({ name: "Marek Musil", objectId: "c-m", aliases: ["MMu"] });
  expect(knownContact({ key: "user:1", contact: { name: "Marek Musil" }, source: "account", origin: "Účet Atlasu" })).toEqual({ name: "Marek Musil" });
});
