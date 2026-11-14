// @vitest-environment node
import { describe, expect, it } from "vitest";
import { orderSearchGroups, titleMatch } from "./searchGroupOrder";

const group = (kind: string, ...titles: string[]) => ({ kind, rankFloor: undefined as number | undefined, items: titles.map(title => ({ title })) });
const order = (groups: ReturnType<typeof group>[], query: string) =>
  orderSearchGroups(groups, query).map(g => `${g.kind}: ${g.items.map(item => item.title).join(", ")}`);

describe("orderSearchGroups", () => {
  it("puts a board whose whole title is the query first, before unrelated pages (UX SE3-3)", () => {
    const groups = [group("pages", "Infrastruktura", "Vývoj aplikací"), group("headings", "Architektura"), group("boards", "Velká tabule", "Architektura: infrastruktura a vývoj")];
    expect(order(groups, "Architektura: infrastruktura a vývoj")).toEqual([
      "boards: Architektura: infrastruktura a vývoj, Velká tabule",
      "pages: Infrastruktura, Vývoj aplikací",
      "headings: Architektura",
    ]);
  });

  it("prefers the whole title to a title's start, in any group", () => {
    const groups = [group("pages", "Nasazení Portálu"), group("boards", "Nasazení")];
    expect(order(groups, "nasazeni")[0]).toBe("boards: Nasazení");
    expect(order([group("pages", "Postupy"), group("boards", "Nasazení portálu")], "Nasazení")[0]).toBe("boards: Nasazení portálu");
  });

  it("never lets YouTrack issues outrank page content, even when an issue's title is the whole query", () => {
    const groups = [group("pages", "Serverová strana"), { ...group("youtrack", "Server"), rankFloor: 3 }];
    expect(order(groups, "server")[0]).toBe("pages: Serverová strana");
  });

  it("puts YouTrack last, after spaces that do not match the query either", () => {
    const groups = [{ ...group("youtrack", "Server"), rankFloor: 3 }, group("spaces", "Provoz")];
    expect(order(groups, "server")).toEqual(["spaces: Provoz", "youtrack: Server"]);
  });

  it("does not let a heading named as the query lead the page titles that start with it (UX SE5-1)", () => {
    const groups = [group("pages", "Serverová strana", "Seznam Serverů"), { ...group("headings", "Server:", "Server:"), rankFloor: 1 }];
    expect(order(groups, "server")[0]).toBe("pages: Serverová strana, Seznam Serverů");
    expect(order([group("pages", "Seznam Serverů"), { ...group("headings", "Server:"), rankFloor: 1 }], "server")[0]).toBe("headings: Server:");
    expect(order([group("pages", "Server"), { ...group("headings", "Server:"), rankFloor: 1 }], "server")[0]).toBe("pages: Server");
  });

  it("keeps the server's order without a title match, and an earlier group on a tie", () => {
    const groups = [group("pages", "B", "A"), group("boards", "C")];
    expect(order(groups, "xyz")).toEqual(["pages: B, A", "boards: C"]);
    expect(order([group("pages", "Server 1"), group("boards", "Server 2")], "server")).toEqual(["pages: Server 1", "boards: Server 2"]);
    expect(order([], "server")).toEqual([]);
  });

  it("compares titles as the server does", () => {
    expect(titleMatch("Začínáme", "zacinam")).toBe(1);
    expect(titleMatch("C# / .NET", "c net")).toBe(0);
    expect(titleMatch("Cokoli", "  ")).toBe(2);
  });
});
