// @vitest-environment node
import { describe, expect, it } from "vitest";
import { cleanText, contactProps, contactSubtitle, httpUrl, readPageTarget, refusedChars } from "./boardTargets";

const id = "3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b";

describe("readPageTarget", () => {
  it("reads the target the extended server sends", () => {
    const raw = { type: "atlas.page", resourceId: id, title: "Provozní příručka", spaceTitle: "Provoz", excerpt: "Tento postup popisuje, jak se vydává.", available: true };
    expect(readPageTarget(raw)).toEqual({ resourceId: id, title: "Provozní příručka", spaceTitle: "Provoz", excerpt: "Tento postup popisuje, jak se vydává.", available: true });
  });

  it("is content with what the current server sends: no space and no excerpt", () => {
    expect(readPageTarget({ resourceId: id, type: "atlas.page", title: "Příručka", available: true })).toEqual({
      resourceId: id, title: "Příručka", spaceTitle: null, excerpt: null, available: true,
    });
  });

  it("reads what the session already keeps (a title and a flag) next to the item's own resource id", () => {
    expect(readPageTarget({ title: "Příručka", available: true }, id)).toEqual({ resourceId: id, title: "Příručka", spaceTitle: null, excerpt: null, available: true });
    expect(readPageTarget({ title: "Příručka", available: true })).toBeNull();
  });

  it("is idempotent: a view reads as itself", () => {
    const view = readPageTarget({ resourceId: id, title: "A", spaceTitle: "B", excerpt: "C", available: true });
    expect(readPageTarget(view)).toEqual(view);
  });

  it("carries no content for a page the reader may not see, whatever the answer holds", () => {
    const expected = { resourceId: id, title: null, spaceTitle: null, excerpt: null, available: false };
    expect(readPageTarget({ type: "atlas.page", resourceId: id, title: null, spaceTitle: null, excerpt: null, available: false })).toEqual(expected);
    expect(readPageTarget({ resourceId: id, title: "Tajné", spaceTitle: "Vedení", excerpt: "Plat", available: false })).toEqual(expected);
  });

  it("takes the page as available when the answer has a title but no flag, and as unavailable when it has neither", () => {
    expect(readPageTarget({ resourceId: id, title: "Příručka" })?.available).toBe(true);
    expect(readPageTarget({ resourceId: id })).toEqual({ resourceId: id, title: null, spaceTitle: null, excerpt: null, available: false });
  });

  it("keeps an excerpt on one line and short, and drops fields that are no text", () => {
    const view = readPageTarget({ resourceId: id, title: "  A  ", spaceTitle: 5, excerpt: `řádek\n\n  druhý\t${"x".repeat(900)}`, available: true });
    expect(view?.title).toBe("A");
    expect(view?.spaceTitle).toBeNull();
    expect(view?.excerpt?.startsWith("řádek druhý x")).toBe(true);
    expect(view?.excerpt?.length).toBeLessThanOrEqual(600);
  });

  it.each([null, undefined, 4, "x", [], { type: "youtrack.issue", resourceId: id }])("is no page target: %j", (raw) => {
    expect(readPageTarget(raw as unknown, id)).toBeNull();
  });
});

describe("httpUrl", () => {
  it("is an absolute http(s) address without credentials, and nothing else", () => {
    expect(httpUrl("https://yt.example/issue/ATL-1")?.href).toBe("https://yt.example/issue/ATL-1");
    expect(httpUrl("http://yt.example")?.href).toBe("http://yt.example/");
    for (const bad of ["javascript:alert(1)", "ftp://yt.example", "https://user:pw@yt.example", "/relative", "not a url", ""]) expect(httpUrl(bad), bad).toBeUndefined();
  });
});

describe("contactSubtitle", () => {
  it("is what the editor's contact shows under the name", () => {
    expect(contactSubtitle({ title: "Vedoucí provozu", organization: "Modelsoft", department: "Provoz" })).toBe("Vedoucí provozu · Modelsoft · Provoz");
    expect(contactSubtitle({ organization: "Modelsoft" })).toBe("Modelsoft");
    expect(contactSubtitle({})).toBe("");
  });
});

describe("contactProps", () => {
  it("keeps the fields the board takes in, trimmed, and drops the rest (kind, internalUser); the logo or photo stays (package G)", () => {
    expect(contactProps({
      name: " Jana Nováková ", title: "Vedoucí", organization: "Modelsoft", department: "Provoz", username: "jana", photo: "https://example.cz/j.jpg",
      kind: "internal", internalUser: { id: "u1" }, emails: [" jana@example.cz ", "jana@example.cz"], phones: ["+420 777 123 456"],
      website: "https://example.test", address: "Hlavní 1", directoryId: "d-1",
    })).toEqual({
      name: "Jana Nováková", title: "Vedoucí", organization: "Modelsoft", department: "Provoz", username: "jana", photo: "https://example.cz/j.jpg", emails: ["jana@example.cz"], phones: ["+420 777 123 456"],
      website: "https://example.test", address: "Hlavní 1", directoryId: "d-1",
    });
  });

  it("leaves out empty fields and empty lists, and keeps at most twenty phones and e-mails", () => {
    expect(contactProps({ name: "Karel", title: "", organization: "  ", emails: [], phones: [""] })).toEqual({ name: "Karel" });
    const emails = Array.from({ length: 30 }, (_, index) => `a${index}@example.cz`);
    expect(contactProps({ name: "A", emails })?.emails).toHaveLength(20);
  });

  it("lets nothing through that the server refuses: a tab is a space, control and bidirectional characters go, an address keeps its lines", () => {
    const bidi = String.fromCharCode(0x202e, 0x2066, 0x200f);
    const contact = contactProps({
      name: `Jana${bidi}\tNováková\u0000`, title: `Ve\tdoucí\n\tprovozu`, emails: [`jana@example.cz${bidi}`], phones: ["+420\t777\u0007123"],
      address: `Hlavní 1\t\u0007\r\n${bidi}Praha\n\n`,
    });
    expect(contact).toEqual({ name: "Jana Nováková", title: "Ve doucí provozu", emails: ["jana@example.cz"], phones: ["+420 777123"], address: "Hlavní 1\nPraha" });
    for (const text of [contact!.name, contact!.title!, contact!.emails![0]!, contact!.phones![0]!, contact!.address!.replace(/\n/g, "")]) expect(text).not.toMatch(refusedChars);
  });

  it("is nothing without a name", () => {
    expect(contactProps({ title: "Vedoucí" })).toBeNull();
    expect(contactProps({ name: "  " })).toBeNull();
    expect(contactProps(null)).toBeNull();
  });
});

describe("cleanText", () => {
  it("makes a tab with its surrounding spaces one space and drops the other refused characters", () => {
    expect(cleanText("a \t\t b")).toBe("a b");
    expect(cleanText("a\u0007b\u007fc\u0085d")).toBe("abcd");
    expect(cleanText(`a${String.fromCharCode(0x202a, 0x202e, 0x2066, 0x2069, 0x200e, 0x061c)}b`)).toBe("ab");
    expect(cleanText("Příliš žluťoučký kůň")).toBe("Příliš žluťoučký kůň");
  });
});
