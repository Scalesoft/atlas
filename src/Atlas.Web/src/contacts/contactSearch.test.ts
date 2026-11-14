// @vitest-environment node
import { describe, expect, it } from "vitest";
import { describePeople, matchesPerson, mergePeople, rankPeople, sameName, serverTerms, teachesAlias, type PersonHit } from "./contactSearch";

const person = (key: string, name: string, more: Partial<PersonHit["contact"]> = {}, source: PersonHit["source"] = "directory", origin = "Adresář"): PersonHit =>
  ({ key, contact: { name, ...more }, source, origin });

describe("matchesPerson", () => {
  it("finds a name by the beginnings of its words, in any order, whatever the case and diacritics", () => {
    const jan = { name: "Jan Kovařík" };
    expect(matchesPerson("jan kovarik", jan)).toBe(true);
    expect(matchesPerson("KOVAR j", jan)).toBe(true);
    expect(matchesPerson("kovařík", jan)).toBe(true);
    expect(matchesPerson("varik", jan)).toBe(false);
    expect(matchesPerson("jan novak", jan)).toBe(false);
  });

  it("knows Czech nicknames both ways: Honza is Jan, Pepa is Josef", () => {
    expect(matchesPerson("jan kovarik", { name: "Honza Kovařík" })).toBe(true);
    expect(matchesPerson("honza kovarik", { name: "Jan Kovařík" })).toBe(true);
    expect(matchesPerson("pepa", { name: "Josef Dvořák" })).toBe(true);
    // Only a whole nickname: „hon“ is no Jan.
    expect(matchesPerson("hon", { name: "Jan Kovařík" })).toBe(false);
  });

  it("finds a person by organization, function or e-mail too", () => {
    const jana = { name: "Jana Nováková", organization: "AB Klinika", title: "Vedoucí IT", emails: ["j.novakova@office.example.test"] };
    expect(matchesPerson("klinika jana", jana)).toBe(true);
    expect(matchesPerson("vedouci", jana)).toBe(true);
    expect(matchesPerson("novakova@office", jana)).toBe(true);
  });
});

describe("rankPeople", () => {
  it("puts a name that starts as typed first, then other name matches, then matches by other fields; the rest keeps its order", () => {
    const hits = [person("a", "Petr Jan", { organization: "X" }), person("b", "Klára", { organization: "Jan a syn" }), person("c", "Jan Novák"), person("d", "Eva")];
    expect(rankPeople("jan", hits).map(hit => hit.key)).toEqual(["c", "a", "b"]);
  });
});

describe("initials and aliases", () => {
  const martin = person("m1", "Marek Musil"), michal = person("m2", "Michal Macek"), nikola = person("n1", "Nikola Mertová"), marta = person("m3", "Marta Malá");
  it("finds by initials of the given name and the beginning of the surname, whatever the case and diacritics, after ordinary matches", () => {
    expect(rankPeople("MM", [nikola, martin, michal]).map(hit => hit.key)).toEqual(["m1", "m2"]);
    expect(rankPeople("MMu", [michal, martin]).map(hit => hit.key)).toEqual(["m1"]);
    expect(rankPeople("mma", [martin, michal]).map(hit => hit.key)).toEqual(["m2"]);
    expect(rankPeople("NMe", [martin, nikola]).map(hit => hit.key)).toEqual(["n1"]);
    // „ma“ begins Marta's name and Macek's surname: ordinary matches, the name that starts as typed first.
    expect(rankPeople("ma", [michal, marta]).map(hit => hit.key)).toEqual(["m3", "m2"]);
    expect(matchesPerson("m", { name: "Marek Musil" })).toBe(true);
    expect(matchesPerson("xm", { name: "Marek Musil" })).toBe(false);
  });
  it("puts the person whose alias was typed first, the more often picked for it first", () => {
    const mara1 = { ...martin, aliases: ["Mára"], aliasPicks: { mara: 2 } }, mara2 = { ...michal, aliases: ["mara"], aliasPicks: { mara: 5 } };
    expect(rankPeople("Mára", [person("x", "Mára Novák"), mara1, mara2]).map(hit => hit.key)).toEqual(["m2", "m1", "x"]);
    // The beginning of an alias finds as any other field does.
    expect(rankPeople("Már", [nikola, { ...nikola, key: "n2", aliases: ["Mára"] }]).map(hit => hit.key)).toEqual(["n2"]);
    expect(matchesPerson("mara", michal.contact, ["Mára"])).toBe(true);
  });
  it("teaches an alias only after a search that is no ordinary match of a directory contact", () => {
    const directory = { ...martin, objectId: "c1", contact: { name: "Marek Musil", emails: ["marek.musil@example.com"] } };
    expect(teachesAlias("MMu", directory)).toBe(true);
    expect(teachesAlias("Mára", directory)).toBe(true);
    for (const plain of ["Mar", "mus", "marek mu", "marek.mus", "m"]) expect(teachesAlias(plain, directory)).toBe(false);
    expect(teachesAlias("MMu", martin)).toBe(false);
  });
});

describe("mergePeople", () => {
  it("shows one person found in several places once (the same directory id or e-mail), the first source winning", () => {
    const merged = mergePeople([
      person("obj:1", "Jan Kovařík", { emails: ["jan@office.example.test"] }),
      person("user:9", "Jan Kovařík", { emails: ["JAN@office.example.test"], directoryId: "9" }, "account", "Účet Atlasu"),
      person("page:x", "Honza Kovařík", { emails: ["jan@office.example.test"] }, "page", "Stránka Kontakty"),
      person("user:9b", "Jan M.", { directoryId: "9" }, "account", "Účet Atlasu"),
    ]);
    expect(merged.map(hit => hit.key)).toEqual(["obj:1"]);
  });

  it("gives an account the object id of the directory contact it is", () => {
    const [account] = mergePeople([
      person("user:9", "Jan Kovařík", { emails: ["jan@office.example.test"], directoryId: "9" }, "account", "Účet Atlasu"),
      { ...person("obj:1", "Jan Kovařík", { emails: ["JAN@office.example.test"] }), objectId: "o1" },
    ]);
    expect(account).toMatchObject({ key: "user:9", objectId: "o1" });
  });

  it("never merges different people of the same name", () => {
    const merged = mergePeople([person("obj:1", "Jan Novák", { emails: ["jan@a.cz"] }), person("obj:2", "Jan Novák", { emails: ["jan@b.cz"] }), person("page:p", "Jan Novák")]);
    expect(merged.map(hit => hit.key)).toEqual(["obj:1", "obj:2", "page:p"]);
  });
});

describe("describePeople", () => {
  it("says the function and organization, and for two people of one name also the e-mail and where each was found", () => {
    const hits = [
      person("obj:1", "Jan Novák", { organization: "Modelsoft", emails: ["jan@example.test"] }),
      person("page:p", "jan novák", { organization: "AB Klinika" }, "page", "Stránka Kontakty · Zakázky"),
      person("obj:3", "Eva Malá", { title: "Ředitelka", emails: ["eva@x.cz"] }),
    ];
    const lines = describePeople(hits);
    expect(lines.get("obj:1")).toBe("Modelsoft · jan@example.test · Adresář");
    expect(lines.get("page:p")).toBe("AB Klinika · Stránka Kontakty · Zakázky");
    expect(lines.get("obj:3")).toBe("Ředitelka");
  });
});

describe("sameName and serverTerms", () => {
  it("finds the people a new contact's name would repeat", () => {
    const hits = [person("a", "Jan Novák"), person("b", "Jana Nováková")];
    expect(sameName(" jan  NOVAK ", hits).map(hit => hit.key)).toEqual(["a"]);
  });

  it("asks a server that matches one text for the longest word and, for a lone nickname, the names it stands for", () => {
    expect(serverTerms("jan kovarik")).toEqual(["kovarik"]);
    expect(serverTerms("Honza")).toEqual(["honza", "jan"]);
    expect(serverTerms("  ")).toEqual([]);
  });
});
