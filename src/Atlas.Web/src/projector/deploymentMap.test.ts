import { describe, expect, it } from "vitest";
import type { BoardChange } from "../boards/boardApi";
import { arrangeChanges, cardRequirementNodes, composeChanges, deploymentMap, linkLabel, orderSection, productHeight, tagRows, type ComposeItem } from "./deploymentMap";

const h = (level: number, text: string, headingId?: string) => ({ type: "heading", attrs: { level, ...(headingId && { headingId }) }, content: [{ type: "text", text }] });
const block = (id: string, title: string, attrs: Record<string, unknown> = {}, nested: object[] = []) =>
  ({ type: "requirement", attrs: { id, ...attrs }, content: [{ type: "paragraph", content: [{ type: "text", text: title }] }, ...nested] });

// Two products: the portal with its server (base), a core module and a category with two modules; the MPI with a module that needs
// the portal's NIA input. NIA is one service whichever product uses it.
const document = { type: "doc", content: [
  block("intro", "Před prvním nadpisem"),
  h(2, "Portál pacienta", "portal"),
  block("srv", "Server", { direction: "inbound", state: "done" }),
  h(3, "Základ portálu", "zaklad"),
  block("smtp", "SMTP", { direction: "inbound", state: "waiting_for_input", system: "SMTP server" }),
  h(3, "Autentizace", "autentizace"),
  h(4, "NIA (501)", "nia-501"),
  block("nia-cert", "Certifikát NIA", { key: "nia.cert", direction: "inbound", state: "done", system: "NIA" }, [block("nia-sub", "Registrace", { direction: "inbound", state: "blocked", system: "nia" })]),
  h(4, "BankID", "bankid"),
  block("bank", "Klient BankID", { direction: "inbound", system: "BankID" }),
  h(2, "MPI", "mpi"),
  h(3, "Core", "core"),
  block("mpi-work", "Napojení", { requires: "nia.cert", state: "in_progress" }),
  block("na", "Netýká se", { state: "not_applicable" }),
] };

describe("deploymentMap", () => {
  const map = deploymentMap(document);

  it("reads products, the infrastructure right under them, categories and modules from the headings", () => {
    expect(map.products.map(product => product.anchor)).toEqual(["portal", "mpi"]);
    const portal = map.products[0]!;
    expect(portal.base.map(entry => entry.id)).toEqual(["srv"]);
    expect(portal.categories.map(category => [category.name, category.modules.map(module => module.name)])).toEqual([
      ["Jádro", ["Základ portálu"]], ["Autentizace", ["NIA (501)", "BankID"]],
    ]);
    // A nested block is its module's too.
    expect(portal.categories[1]!.modules[0]!.blocks.map(entry => entry.id)).toEqual(["nia-cert", "nia-sub"]);
  });

  it("keeps each input of a service once, by the slug of its name, with the products that use it", () => {
    const nia = map.services.find(service => service.anchor === "nia")!;
    expect(nia.inputs.map(entry => entry.id)).toEqual(["nia-cert", "nia-sub"]);
    expect(nia.products).toEqual(["Portál pacienta", "MPI"]);
    expect(map.services.map(service => service.anchor)).toEqual(["smtp-server", "nia", "bankid"]);
  });

  it("links a product to each service it holds or needs, labelled with the modules and the count", () => {
    const label = (product: string, service: string) => { const link = map.links.find(entry => entry.product === product && entry.service === service); return link && linkLabel(link); };
    expect(label("portal", "nia")).toBe("NIA (501) · 2 pož.");
    expect(label("mpi", "nia")).toBe("Core · 1 pož.");
    expect(label("mpi", "bankid")).toBeUndefined();
    expect(map.links.find(entry => entry.service === "nia" && entry.product === "portal")!.state).toBe("blocked");
  });
});

describe("composeChanges", () => {
  const map = deploymentMap(document);
  let next = 0;
  const newId = () => `n${++next}`;

  it("lays out the products side by side, each over the services it is the first to use, and the connectors between them", () => {
    const changes = composeChanges(map, "page-1", [], newId);
    const created = changes.map(change => (change.type === "create" ? change.item : undefined)!);
    const cards = created.filter(item => item.kind === "page");
    // The portal stands centred over its three services (a row 964 wide, every other one a step lower so the connectors' labels do not
    // cover each other); the MPI, which only shares NIA, in the next lane.
    expect(cards.map(item => [item.props!.display, item.props!.anchor, item.x, item.y])).toEqual([
      ["product", "portal", 282, 0], ["service", "smtp-server", 0, 480], ["service", "nia", 332, 536], ["service", "bankid", 664, 480], ["product", "mpi", 1084, 0],
    ]);
    expect(cards.every(item => item.targetResourceId === "page-1")).toBe(true);
    const lines = created.filter(item => item.kind === "connector");
    expect(lines.map(item => item.text)).toEqual(["Základ portálu · 1 pož.", "NIA (501) · 2 pož.", "BankID · 1 pož.", "Core · 1 pož."]);
    // Down to its own services; to one that stands under another product by the sides that face each other (none stored).
    expect(lines.map(item => item.props!.fromSide ?? "auto")).toEqual(["bottom", "bottom", "bottom", "auto"]);
  });

  // Owner, 4.0: a zakázka's board is its architecture, and whatever a person laid out on it is theirs.
  describe("an order's page", () => {
    const rq = (level: number, id: string, text: string, attrs: object = {}) => ({ type: "heading", attrs: { level, headingId: id, state: "planned", ...attrs }, content: [{ type: "text", text }] });
    const order = deploymentMap({ type: "doc", content: [
      h(2, "Zakázka", "zakazka"), rq(3, "dodatek", "Podepsaný dodatek", { state: "done" }),
      h(2, "MPI", "mpi"), h(3, "Integrace", "mpi-int"), h(4, "MPI — ÚZIS", "mpi-uzis"), rq(5, "uzis-cert", "Certifikát", { system: "ÚZIS" }),
      h(2, "Portál pacienta", "portal"), h(3, "Integrace", "int"), h(4, "Napojení portálu na MPI", "napojeni"), rq(5, "api", "Přístup k API", { system: "mpi" }),
      h(4, "NIA", "nia"), rq(5, "nia-cert", "Certifikát NIA", { system: "NIA" }),
    ] });

    it("reads a system that names a product of the page as that product, not as an external service", () => {
      expect(order.services.map(service => service.anchor)).toEqual(["uzis", "nia"]);
      expect(order.links.find(link => link.internal)).toMatchObject({ product: "portal", service: "mpi", modules: ["Napojení portálu na MPI"] });
      expect(orderSection(order)?.anchor).toBe("zakazka");
      // A page of one product, or one that starts with a product of modules, has no order section.
      expect(orderSection(map)).toBeUndefined();
    });

    it("connects the two products of an internal integration and gives the order's section no connector", () => {
      const created = composeChanges(order, "page-1", [], newId).map(change => (change.type === "create" ? change.item : undefined)!);
      const id = (anchor: string) => created.find(item => item.props?.anchor === anchor)!.id;
      const lines = created.filter(item => item.kind === "connector").map(item => `${item.from}>${item.to}`);
      expect(lines).toContain(`${id("portal")}>${id("mpi")}`);
      expect(lines.some(line => line.includes(id("zakazka")))).toBe(false);
      // The order's section leads the page, so its card is top left.
      expect(created[0]).toMatchObject({ x: 0, y: 0, props: { anchor: "zakazka" } });
    });

    // Owner, 4.0: „Uspořádat“ puts an existing board (one column from 3.5) into the picture a new one is composed in.
    it("arranges a scrambled board into what a fresh compose gives, in one batch, and moves nothing it does not know", () => {
      const fresh = composeChanges(order, "page-1", [], newId).map(change => (change.type === "create" ? change.item : undefined)!);
      const board = (list: typeof fresh) => list.map((item): ComposeItem => ({ ...item, target: item.targetResourceId ? { resourceId: item.targetResourceId } : null }));
      // One column, every connector on other sides, plus a person's note, their arrow from the note and a card of another page.
      const scrambled = board(fresh).map((item, index) => (item.kind === "connector" ? { ...item, props: { ...item.props, fromSide: "left" as const, toSide: "right" as const } } : { ...item, x: 0, y: index * 700 }));
      const note: ComposeItem = { id: "note", kind: "note", x: -420, y: 40, width: 200, height: 200, z: 50 };
      const arrow: ComposeItem = { id: "mine", kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: "note", to: scrambled[0]!.id, props: { fromSide: "left", toSide: "right" } };
      const other: ComposeItem = { ...scrambled[0]!, id: "other", x: -900, y: 5, target: { resourceId: "page-2" } };
      const changes = arrangeChanges([["page-1", order]], [...scrambled, note, arrow, other]);
      expect(changes.every(change => change.type === "move" || change.type === "set")).toBe(true);
      expect(changes.some(change => "itemId" in change && ["note", "mine", "other"].includes(change.itemId))).toBe(false);
      const arranged = scrambled.map(item => {
        const move = changes.find(change => change.type === "move" && change.itemId === item.id), set = changes.find(change => change.type === "set" && change.itemId === item.id);
        const props = set?.type === "set" ? Object.fromEntries(Object.entries({ ...item.props, ...set.props }).filter(([, value]) => value !== null)) : item.props;
        return move?.type === "move" ? { ...item, x: move.x, y: move.y, width: move.width, height: move.height } : { ...item, props };
      });
      // The same geometry and connector sides as the fresh board (which starts at 0, 0, as the scrambled column does).
      expect(arranged).toEqual(board(fresh));
      // Arranged, there is nothing to arrange; and a selection arranges only its cards, from where they stand.
      expect(arrangeChanges([["page-1", order]], arranged)).toEqual([]);
      const portal = scrambled.find(item => item.props?.anchor === "portal")!, nia = scrambled.find(item => item.props?.anchor === "nia")!;
      // The service goes under its product, which stands where it stood; nothing out of the selection moves.
      expect(arrangeChanges([["page-1", order]], scrambled, [portal.id, nia.id]).filter(change => change.type === "move")).toEqual([
        { type: "move", itemId: nia.id, x: portal.x + 50, y: portal.y + portal.height + 160, width: 300, height: 150 }]);
    });

    // Review, 2026-11-11: what an arrangement must not do.
    const laid = (pageId: string, on: ComposeItem[] = []) => composeChanges(order, pageId, on, newId).map(change => (change.type === "create" ? change.item : undefined)!)
      .map((item): ComposeItem => ({ ...item, target: item.targetResourceId ? { resourceId: item.targetResourceId } : null }));
    const moved = (items: ComposeItem[], changes: BoardChange[]) => items.map(item => { const move = changes.find(change => change.type === "move" && change.itemId === item.id); return move?.type === "move" ? { ...item, x: move.x, y: move.y } : item; });
    const overlaps = (a: ComposeItem, b: ComposeItem) => a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;
    const column = (list: ComposeItem[], x: number) => list.map((item, index) => (item.kind === "connector" ? item : { ...item, x, y: index * 700 }));

    it("arranges the cards of two pages on one board one picture under the other, never over each other", () => {
      const first = laid("page-1"), second = laid("page-2", first), items = [...column(first, 0), ...column(second, 500)];
      const cards = moved(items, arrangeChanges([["page-1", order], ["page-2", order]], items)).filter(item => item.kind === "page");
      expect(cards.flatMap(a => cards.filter(b => a.id < b.id && overlaps(a, b)).map(b => `${a.id}/${b.id}`))).toEqual([]);
      const top = (pageId: string) => Math.min(...cards.filter(card => card.target?.resourceId === pageId).map(card => card.y));
      expect(top("page-2")).toBeGreaterThan(Math.max(...cards.filter(card => card.target?.resourceId === "page-1").map(card => card.y + card.height)));
    });

    it("does not lay the picture over an item that stays: it goes under everything that stays instead", () => {
      const items = column(laid("page-1"), 0), note: ComposeItem = { id: "note", kind: "note", x: 900, y: 40, width: 200, height: 200, z: 50 };
      const changes = arrangeChanges([["page-1", order]], [...items, note]);
      const cards = moved([...items, note], changes).filter(item => item.kind === "page");
      expect(cards.filter(card => overlaps(card, note))).toEqual([]);
      expect(Math.min(...cards.map(card => card.y))).toBe(240 + 32);
      // A frame or group around the cards holds the picture, it is nothing to avoid.
      const frame: ComposeItem = { id: "frame", kind: "frame", x: -100, y: -100, width: 5000, height: 9000, z: 0 };
      expect(Math.min(...moved([...items, frame], arrangeChanges([["page-1", order]], [...items, frame])).filter(item => item.kind === "page").map(card => card.y))).toBe(0);
    });

    it("leaves a person's own arrows between two arranged cards as they drew them: only the connectors compose made are turned", () => {
      const items = laid("page-1"), portal = items.find(item => item.props?.anchor === "portal")!, nia = items.find(item => item.props?.anchor === "nia")!;
      const back: ComposeItem = { id: "back", kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: nia.id, to: portal.id, props: { fromSide: "left", toSide: "left" } };
      // The same way as the page's link, but with the person's own label (or none): theirs too.
      const own: ComposeItem = { id: "own", kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: portal.id, to: nia.id, text: "ověřit", props: { fromSide: "left", toSide: "right" } };
      const made = items.find(item => item.kind === "connector" && item.from === portal.id && item.to === nia.id)!;
      const turned = { ...made, props: { ...made.props, fromSide: "left" as const, toSide: "right" as const } };
      const changes = arrangeChanges([["page-1", order]], [...items.filter(item => item !== made), turned, back, own]);
      expect(changes).toEqual([{ type: "set", itemId: made.id, props: { fromSide: "bottom", toSide: "top" } }]);
    });

    it("keeps what a person laid out when composed again: moved and resized cards, their own arrow and note stay, new cards go below", () => {
      const first = composeChanges(order, "page-1", [], newId).map(change => (change.type === "create" ? change.item : undefined)!);
      const moved = first.map((item): ComposeItem => ({ ...item, target: item.targetResourceId ? { resourceId: item.targetResourceId } : null,
        ...(item.props?.anchor === "mpi" && { x: -700, y: 300, width: 520, height: 500 }) }));
      const arrow: ComposeItem = { id: "mine", kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: moved[0]!.id, to: moved[1]!.id };
      const note: ComposeItem = { id: "note", kind: "note", x: 0, y: 900, width: 200, height: 200, z: 50 };
      expect(composeChanges(order, "page-1", [...moved, arrow, note], newId)).toEqual([]);
      // The page grows by a product: only its cards come, under everything on the board.
      const grown = deploymentMap({ type: "doc", content: [h(2, "Zakázka", "zakazka"), rq(3, "dodatek", "Podepsaný dodatek"), h(2, "MPI", "mpi"), h(3, "Integrace", "mpi-int"), h(4, "MPI — ÚZIS", "mpi-uzis"),
        rq(5, "uzis-cert", "Certifikát", { system: "ÚZIS" }), h(2, "Portál pacienta", "portal"), h(3, "Integrace", "int"), h(4, "Napojení portálu na MPI", "napojeni"), rq(5, "api", "Přístup k API", { system: "mpi" }),
        h(4, "NIA", "nia"), rq(5, "nia-cert", "Certifikát NIA", { system: "NIA" }), h(2, "Registr", "registr"), h(3, "Jádro registru", "jadro"), rq(4, "sms", "SMS brána", { system: "SMS brána" })] });
      const changes = composeChanges(grown, "page-1", [...moved, arrow, note], newId);
      expect(changes.every(change => change.type === "create")).toBe(true);
      const added = changes.map(change => (change.type === "create" ? change.item : undefined)!);
      expect(added.filter(item => item.kind === "page").map(item => [item.props!.anchor, item.x, item.y])).toEqual([["registr", -700, 1132], ["sms-brana", -650, 1132 + 164 + 160]]);
      expect(added.filter(item => item.kind === "connector")).toHaveLength(1);
    });
  });

  it("adds only what the board lacks and moves nothing", () => {
    const portal: ComposeItem = { id: "p", kind: "page", x: 900, y: 500, width: 400, height: 300, z: 7, target: { resourceId: "page-1" }, props: { display: "product", anchor: "portal" } };
    const nia: ComposeItem = { id: "s", kind: "page", x: 50, y: 50, width: 300, height: 150, z: 3, target: { resourceId: "page-1" }, props: { display: "service", anchor: "nia" } };
    const line: ComposeItem = { id: "c", kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: "p", to: "s" };
    // Another page's card with the same anchor is not this page's.
    const other: ComposeItem = { ...portal, id: "o", target: { resourceId: "page-2" } };
    const changes = composeChanges(map, "page-1", [portal, nia, line, other], newId);
    expect(changes.every(change => change.type === "create")).toBe(true);
    const created = changes.map(change => (change.type === "create" ? change.item : undefined)!);
    // In a row under everything the board holds, from its left edge: the portal's missing services, then the missing product.
    expect(created.filter(item => item.kind === "page").map(item => [item.props!.anchor, item.x, item.y])).toEqual([["smtp-server", 50, 832], ["bankid", 382, 888], ["mpi", 802, 832]]);
    expect(created.filter(item => item.kind === "connector").map(item => `${item.from}>${item.text}`)).toEqual([
      "p>Základ portálu · 1 pož.", "p>BankID · 1 pož.", `${created[2]!.id}>Core · 1 pož.`,
    ]);
    expect(Math.min(...created.filter(item => item.kind === "page").map(item => item.z))).toBe(8);
    // Once they are on the board, a second run has nothing to add.
    const placed = created.map(item => ({ ...item, target: item.targetResourceId ? { resourceId: item.targetResourceId } : null }));
    expect(composeChanges(map, "page-1", [portal, nia, line, other, ...placed], newId)).toEqual([]);
  });

  // MPI of a zakázka (owner, 4.0): 13 requirements right under the product and its „Integrace“ group were cut off at a fixed 40 px of tags.
  it("makes a product card tall enough for every row its tags wrap into and the groups below them", () => {
    const long = Array.from({ length: 13 }, (_, n) => block(`b${n}`, `Interní URL a DNS záznam MPI číslo ${n}`));
    const mpi = deploymentMap({ type: "doc", content: [h(2, "MPI", "mpi"), ...long, h(3, "Integrace", "integrace"), h(4, "MPI — ÚZIS", "uzis"), block("u", "Certifikát", { system: "ÚZIS" })] }).products[0]!;
    expect(tagRows(mpi.base)).toBe(13);
    expect(productHeight(mpi)).toBeGreaterThanOrEqual(84 + 13 * 25 + 44 + 36);
    // Short tags share a row, as before.
    expect(tagRows(deploymentMap({ type: "doc", content: [h(2, "P", "p"), block("a", "DNS"), block("b", "SSL"), block("c", "RDP")] }).products[0]!.base)).toBe(1);
  });
});

describe("what a board accepts", () => {
  it("takes no temporary heading id for an anchor and cuts a label to a board item's text (DeploymentBoardParityTests)", () => {
    const map = deploymentMap({ type: "doc", content: [h(2, "Portál", "v3-h1"), block("b1", "Server")] });
    expect(map.products[0]!.anchor).toBe("portal");
    expect(linkLabel({ product: "p", service: "s", modules: ["m".repeat(5000)], count: 1, state: "planned" })).toHaveLength(4000);
  });
});

describe("a converted page (requirement headings, model §8)", () => {
  const page = "11111111-1111-4111-8111-111111111111";
  const requirement = (level: number, id: string, title: string, attrs: Record<string, unknown> = {}) =>
    ({ type: "heading", attrs: { level, headingId: id, state: "planned", ...attrs }, content: [{ type: "text", text: title }] });
  const p = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
  const deps = (id: string, title: string) => ({ type: "paragraph", attrs: { dependencies: true }, content: [{ type: "pageReference", attrs: { id: page, title, url: `#/pages/${page}?heading=${id}` } }] });
  const converted = { type: "doc", content: [
    h(2, "Portál pacienta", "portal"), h(3, "NIA (501)", "nia-501"),
    requirement(4, "nia.cert", "Certifikát NIA", { direction: "inbound", state: "done", system: "NIA", party: "IT nemocnice", partyContactId: "6f1c1d3e-8a4b-4c55-9d1e-0000000000c1" }), p("do 12. 10. 2026"), p("Poznámka."),
    requirement(4, "nia-sub", "Registrace", { state: "blocked", system: "NIA" }), deps("nia.cert", "Certifikát NIA"),
  ] };

  it("reads a requirement heading as a block of the plain heading's module, its facts and dependency line as its attributes; a card's nodes carry the heading's id", async () => {
    const map = deploymentMap(converted), module = map.products[0]!.categories[0]!.modules[0]!;
    expect([module.name, module.blocks.map(block => [block.id, block.title, block.attrs.party, block.attrs.partyContactId, block.attrs.date, block.attrs.requires])])
      .toEqual(["NIA (501)", [["nia.cert", "Certifikát NIA", "IT nemocnice", "6f1c1d3e-8a4b-4c55-9d1e-0000000000c1", "2026-10-12", ""], ["nia-sub", "Registrace", "", "", "", "nia.cert"]]]);
    const nodes = await cardRequirementNodes([{ kind: "page", targetResourceId: page, props: { display: "service", anchor: "nia" } }], async () => converted);
    expect(nodes.map(node => node.attrs!.id)).toEqual(["nia.cert", "nia-sub"]);
  });

  // The work is counted, not timed: a shared CI runner took 16.9 ms where 5 ms was the budget (run 153). Every look at a top-level
  // node's type is one read; a rescan of the page per heading (what the 5 ms guarded against) multiplies them.
  it("reads a page of 2 000 paragraphs and 500 requirement headings in a few passes, and one twice as long in twice the reads", () => {
    let reads = 0;
    const counted = <T extends { type: string }>(node: T): T => { const type = node.type; return Object.defineProperty(node, "type", { enumerable: true, get: () => (reads++, type) }); };
    const pageOf = (modules: number) => {
      const content: { type: string }[] = [h(1, "Produkt", "produkt")];
      for (let module = 0; module < modules; module++) {
        content.push(h(2, `Modul ${module}`, `modul-${module}`));
        for (let n = 0; n < 10; n++) {
          const id = `r${module}-${n}`;
          content.push(requirement(3, id, `Požadavek ${id}`, { system: n % 3 ? "" : "NIA" }), ...(n ? [deps(`r${module}-${n - 1}`, "Předchozí")] : []),
            p("do 12. 10. 2026"), p("Poznámka jedna."), p("Poznámka dvě."), p("Poznámka tři."));
        }
      }
      return { type: "doc", content: content.map(counted) };
    };
    const readsOf = (page: ReturnType<typeof pageOf>, blocks: number) => { reads = 0; const map = deploymentMap(page), count = reads; expect(map.products[0]!.blocks).toHaveLength(blocks); return count; };
    const big = pageOf(50), double = pageOf(100);
    expect(big.content.filter(node => node.type === "paragraph").length).toBeGreaterThanOrEqual(2000);
    const once = readsOf(big, 500);
    // Three reads a node today (9 055 for 3 001 nodes).
    expect(once).toBeLessThanOrEqual(4 * big.content.length);
    expect(readsOf(double, 1000)).toBeLessThanOrEqual(2 * once + 8);
  });
});
