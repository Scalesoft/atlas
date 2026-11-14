// @vitest-environment node
import { describe, expect, it } from "vitest";
import { findDeadline, findDependencyTrigger, findIssueKey, findPartyTrigger, parseDeadline } from "./requirementPhrases";

// Thursday 29 October 2026, as in the prototype.
const today = "2026-10-29";

describe("typed deadlines", () => {
  it.each([
    ["15. 11.", "2026-11-15"], ["15. 11", "2026-11-15"], ["15.11.", "2026-11-15"], ["15.11.2026", "2026-11-15"], ["15. 11. 2027", "2027-11-15"], ["2026-12-01", "2026-12-01"],
    ["zítřka", "2026-10-30"], ["zitra", "2026-10-30"], ["pozítří", "2026-10-31"], ["dneška", today],
    ["pátku", "2026-10-30"], ["čtvrtka", "2026-11-05"], ["příštího pondělí", "2026-11-09"], ["pondělí", "2026-11-02"],
    ["konce týdne", "2026-10-30"], ["konce měsíce", "2026-10-31"], ["konce roku", "2026-12-31"], ["týdne", "2026-11-05"],
    ["3 dnů", "2026-11-01"], ["2 týdnů", "2026-11-12"],
    // A day long past is next year's; one just past stays (an overdue deadline is still written down).
    ["15. 1.", "2027-01-15"], ["27. 10.", "2026-10-27"],
  ])("„do %s“ is %s", (typed, expected) => expect(parseDeadline(typed, today)).toBe(expected));

  // Without a year a version number is no date: a space after the first dot or a closing dot says it is one.
  it.each(["31. 2.", "serveru", "15.", "", "pátku ráno", "3.1", "2.11", "15.11"])("„do %s“ is no deadline", typed => expect(parseDeadline(typed, today)).toBeNull());

  it("finds the phrase ending at the typed space, the last „do“ that reads as a date", () => {
    expect(findDeadline("Certifikát do 15. 11. ", today)).toEqual({ index: 11, text: "do 15. 11. ", value: "2026-11-15" });
    expect(findDeadline("Připojit do sítě do pátku ", today)).toMatchObject({ text: "do pátku ", value: "2026-10-30" });
    expect(findDeadline("Nahrát do serveru ", today)).toBeNull();
    expect(findDeadline("Certifikát do 15. 11.", today)).toBeNull();
    expect(findDeadline("Upgrade z 2.8 do 3.1 ", today)).toBeNull();
    expect(findDeadline("Upgrade do 2.11 ", today)).toBeNull();
    expect(findDeadline("Upgrade do 15. 11. ", today)).toMatchObject({ value: "2026-11-15" });
    expect(findDeadline("Upgrade do 15.11. ", today)).toMatchObject({ value: "2026-11-15" });
  });
});

describe("typed triggers", () => {
  it("opens the party picker after „čekáme na “ and its variants, the name typed so far as the query", () => {
    expect(findPartyTrigger("VPN čekáme na ")).toEqual({ index: 4, query: "" });
    expect(findPartyTrigger("VPN čeká na Jiř")).toEqual({ index: 4, query: "Jiř" });
    expect(findPartyTrigger("Čekáme na Jiří Sý")).toEqual({ index: 0, query: "Jiří Sý" });
    expect(findPartyTrigger("VPN waiting for Petra")).toMatchObject({ query: "Petra" });
    expect(findPartyTrigger("přečeká na ")).toBeNull();
    expect(findPartyTrigger("čekáme na Jiří  ")).toBeNull();
  });

  it("opens the dependency picker after „po #“ or „závisí na #“, never after @ (the contacts' picker)", () => {
    expect(findDependencyTrigger("Mapování po #")).toEqual({ index: 9, query: "" });
    expect(findDependencyTrigger("Mapování závisí na #exp")).toEqual({ index: 9, query: "exp" });
    expect(findDependencyTrigger("Mapování závisí na @exp")).toBeNull();
    expect(findDependencyTrigger("Mapování po @")).toBeNull();
    expect(findDependencyTrigger("Mapování #exp")).toBeNull();
  });

  it("recognises a YouTrack key once a space or a mark ends it", () => {
    expect(findIssueKey("Chyba #PX2-1235 ")).toEqual({ index: 6, text: "#PX2-1235 ", value: "PX2-1235" });
    expect(findIssueKey("viz PX2-7,")).toMatchObject({ text: "PX2-7,", value: "PX2-7" });
    expect(findIssueKey("Chyba #PX2-1235")).toBeNull();
    expect(findIssueKey("a-PX2-1 ")).toBeNull();
    expect(findIssueKey("px2-1 ")).toBeNull();
  });
});
