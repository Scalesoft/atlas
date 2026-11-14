// Fixture wiki for the Projector v1 AI evaluation (README.md): deployment pages with requirement blocks (spec 2026-10-27 §2), a catalog
// page and ordinary wiki pages as distractors, written as a Git backup tree that `load-backup` of experiments/Atlas.AiPrototype loads
// through the server's own write API. Two variants of the same wiki: "with" (blocks as designed) and "without" (every requirement block
// and summary removed, what the AI saw before the feature). Synthetic data only.
//   node tests/ai-eval/projector/fixtures.mjs <variant: with|without> <output directory>
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

let counter = 0;
const uuid = () => `6f1c1d3e-8a4b-4c55-9d1e-${String(++counter).padStart(12, "0")}`;
const text = (value) => ({ type: "text", text: value });
export const p = (value) => ({ type: "paragraph", content: [text(value)] });
export const heading = (level, value) => ({ type: "heading", attrs: { level }, content: [text(value)] });
export const h2 = (value) => heading(2, value);
export const bullets = (...items) => ({ type: "bulletList", content: items.map((item) => ({ type: "listItem", content: [p(item)] })) });
export const summary = () => ({ type: "overview", attrs: { id: uuid(), kind: "requirements" } });
/** A requirement block: attributes are those of spec §2.1, children are notes and nested blocks. */
export const req = (key, title, attrs = {}, ...children) => ({
  type: "requirement",
  attrs: { id: uuid(), key, direction: "internal", state: "planned", date: "", party: "", where: "", requires: "", ...attrs },
  content: [p(title), ...children],
});

/** The same wiki without any requirement node: what a page held before the feature. */
export function withoutRequirements(nodes) {
  return nodes.filter((node) => node.type !== "requirement" && node.type !== "overview");
}

// The KBN page and its fresh copy for Kostelec share keys, directions, dependencies and the structure; state, date, party and place are
// reset and the KBN-specific notes dropped (spec §1.5).
const kbn = (copy) => {
  const s = (attrs) => (copy ? { direction: attrs.direction, requires: attrs.requires } : attrs);
  const note = (...nodes) => (copy ? [] : nodes);
  return [
    p(copy
      ? "Produkční nasazení Atlasu pro Nemocnici Kostelec. Stránka vznikla kopií stránky KBN — Production, stavy jsou vynulované."
      : "Produkční nasazení Atlasu pro nemocnici KBN. Zodpovědný je Jan Novák (Modelsoft), kontakt na straně zákazníka je Petr Dvořák z IT KBN."),
    h2("Stav nasazení"),
    summary(),
    h2("Infrastruktura"),
    req("portal.ssl", "SSL certifikát", s({ direction: "inbound", state: "done", date: "2026-09-10", party: "IT KBN", where: "Keepass › KBN", requires: "" }),
      ...note(p("Certifikát platí do 9. 9. 2027, obnovu hlídá IT KBN."))),
    req("portal.vpn", "VPN přístup", s({ direction: "inbound", state: "done", date: "2026-09-03", party: "IT KBN", requires: "" }),
      ...note(p("Přístupové údaje k VPN: uloženo v Keepass › KBN, položka VPN."))),
    req("portal.sms", "SMS brána", s({ direction: "inbound", state: "waiting_for_input", party: "IT KBN", requires: "portal.ssl" }),
      ...note(p("Požadavek odeslán 2. 10. 2026, urgováno u Petra Dvořáka."))),
    h2("Aplikace"),
    req("forms", "Strukturované požadavky", s({ direction: "internal", state: "in_progress", party: "Jan Novák", requires: "portal.ssl" }),
      ...note(p("Hotové formuláře: příjem a propuštění. Zbývá konzilium a předávací zpráva."), bullets("Formulář příjmu — hotovo", "Konzilium — rozpracováno")),
      req("forms.catalog", "Číselník diagnóz MKN-10", s({ direction: "inbound", state: "done", date: "2026-09-25", party: "IT KBN", requires: "" })),
      req("forms.templates", "Šablony formulářů od primářů", s({ direction: "inbound", state: "in_progress", party: "MUDr. Králová", requires: "" }))),
    req("mpi", "MPI import", s({ direction: "internal", state: "in_progress", requires: "" }),
      req("mpi.mapping", "Mapování polí pacienta", s({ direction: "internal", state: "done", date: "2026-09-18", requires: "" })),
      req("mpi.test", "Testovací import 500 pacientů", s({ direction: "internal", state: "in_progress", requires: "mpi.mapping" })),
      req("mpi.prod", "Ostrý import", s({ direction: "internal", state: "planned", requires: "mpi.test" }))),
  ];
};

const px2 = () => [
  p("Testovací prostředí zakázky PX2 (Nemocnice PX2). Kontakt na straně zákazníka je IT PX2."),
  h2("Stav nasazení"),
  summary(),
  h2("Požadavky"),
  req("portal.ssl", "SSL certifikát", { direction: "inbound", state: "done", date: "2026-06-02", party: "IT PX2", where: "Keepass › PX2" }),
  req("portal.sms", "SMS brána", { direction: "inbound", state: "planned", party: "IT PX2" }),
  req("medikace", "Modul medikace", { direction: "internal", state: "done", date: "2026-06-08", party: "Eva Malá" }),
  req("forms", "Strukturované požadavky", { direction: "internal", state: "planned" }),
];

// Deployment of the scenario "copy Dubovice and add teleconferences": Produkt › Kategorie › Modul headings, history lines in the notes.
const dubovice = () => [
  p("Produkční nasazení portálu pro Nemocnici Dubovice. Zodpovědná je Eva Malá (Modelsoft), kontakt na straně zákazníka je IT Dubovice."),
  heading(2, "Portál pacienta"),
  heading(3, "Jádro"),
  heading(4, "Portál — Core (500)"),
  req("portal.ssl", "SSL certifikát", { direction: "inbound", state: "done", date: "2026-10-09", party: "IT Dubovice", where: "Keepass › Dubovice" },
    bullets("9. 10. 2026 — Dodáno: IT Dubovice", "7. 10. 2026 — Vyzváno: IT Dubovice")),
  req("portal.sms", "SMS brána", { direction: "inbound", state: "waiting_for_input", party: "IT Dubovice", requires: "portal.ssl" },
    bullets("14. 10. 2026 — Vyzváno: IT Dubovice")),
  req("portal.vpn", "VPN přístup", { direction: "inbound", state: "blocked", party: "IT Dubovice" },
    p("Bezpečnostní oddělení přístup zatím neschválilo."), bullets("15. 10. 2026 — Blokováno: Bezpečnostní oddělení přístup zatím neschválilo.")),
];

const catalog = () => [
  p("Katalog předdefinovaných požadavků nasazení. Při vložení požadavku se přidají i jeho závislosti. Balíček je sekce pod nadpisem."),
  h2("Portál"),
  req("portal.ssl", "SSL certifikát", { direction: "inbound" }, p("Certifikát pro doménu portálu včetně řetězu a privátního klíče, uložit do trezoru.")),
  req("portal.vpn", "VPN přístup", { direction: "inbound" }, p("Účet a profil VPN pro správce Modelsoftu.")),
  req("portal.sms", "SMS brána", { direction: "inbound", requires: "portal.ssl" }, p("Přístup k SMS bráně zákazníka: adresa API, uživatel a heslo do trezoru.")),
  h2("Strukturované požadavky"),
  req("forms.templates", "Šablony formulářů", { direction: "inbound" }, p("Vzory papírových formulářů od primářů jednotlivých oddělení.")),
  req("forms.catalog", "Číselník diagnóz MKN-10", { direction: "inbound" }, p("Aktuální číselník diagnóz od zákazníka ve formátu CSV.")),
  req("forms.mapping", "Mapování formulářů na datový model", { direction: "internal", requires: "forms.templates,forms.catalog" }),
  req("forms.review", "Revize formulářů primářem", { direction: "outbound", requires: "forms.mapping" }),
  h2("MPI integrace"),
  req("mpi.mapping", "Mapování polí pacienta", { direction: "internal" }),
  req("mpi.test", "Testovací import", { direction: "internal", requires: "mpi.mapping" }),
  heading(1, "Portál pacienta"),
  heading(2, "Telemedicína"),
  // Its anchor is the module's stable key (moduleKey), whatever the heading reads.
  { ...heading(3, "Telekonference přes Azure (ACS)"), attrs: { level: 3, headingId: "telekonference-azure" } },
  p("Videokonzultace lékaře s pacientem přes Azure Communication Services přímo z portálu pacienta."),
  req("portal.telekonference-azure", "Telekonference přes Azure (ACS)", { direction: "internal", requires: "portal.telekonference-azure.tenant,portal.ssl" },
    p("Zapnutí videohovorů v portálu pacienta.")),
  req("portal.telekonference-azure.tenant", "Azure tenant a ACS zdroj", { direction: "inbound" }, p("Tenant, předplatné a připojovací řetězec ke zdroji Communication Services.")),
  req("portal.telekonference-azure.domena", "Povolená doména pro videohovory", { direction: "inbound", requires: "portal.ssl" }),
];

export const pages = [
  { path: "zakazky/kbn-production", title: "KBN — Production", nodes: () => kbn(false) },
  { path: "zakazky/px2-test", title: "PX2 — Test", nodes: px2 },
  { path: "zakazky/kostelec-production", title: "Kostelec — Production", nodes: () => kbn(true) },
  { path: "zakazky/dubovice-nasazeni", title: "Dubovice — nasazení", nodes: dubovice },
  { path: "katalog/katalog-pozadavku", title: "Katalog požadavků nasazení", nodes: catalog },
  { path: "wiki/obnova-ssl-certifikatu", title: "Obnova SSL certifikátu", nodes: () => [
    p("Postup obnovy SSL certifikátu na produkčním serveru Atlasu. Obnova se dělá nejpozději 30 dní před koncem platnosti."),
    h2("Postup"),
    bullets("Vygenerujeme nový privátní klíč a požadavek CSR.", "CSR odešleme certifikační autoritě.", "Nový certifikát nahrajeme na reverzní proxy a restartujeme ji.", "Zkontrolujeme řetěz v prohlížeči."),
  ] },
  { path: "wiki/vpn-klient-navod", title: "VPN klient: instalace a připojení", nodes: () => [
    p("Jak se připojit do firemní VPN z notebooku."),
    h2("Instalace"),
    p("Klienta WireGuard nainstalujeme ze Správy softwaru. Konfigurační soubor dostaneme od správce sítě."),
    h2("Připojení"),
    p("Po importu konfigurace klikneme na Aktivovat. Při potížích se obraťte na helpdesk."),
  ] },
  { path: "wiki/sms-brana-integrace", title: "SMS brána: technický popis integrace", nodes: () => [
    p("Atlas odesílá upozornění přes SMS bránu zákazníka. Podporujeme rozhraní HTTP JSON a SMPP."),
    h2("Konfigurace"),
    p("V administraci zadáme adresu API brány, uživatele a heslo. Heslo se ukládá do trezoru, ne do konfiguračního souboru."),
    h2("Řešení potíží"),
    p("Pokud SMS nedorazí, zkontrolujeme kredit u operátora a odpověď brány v auditním logu."),
  ] },
  { path: "wiki/dovolena-a-nepritomnost", title: "Dovolená a nepřítomnost", nodes: () => [
    p("Každý zaměstnanec má nárok na 25 dní dovolené ročně. Dovolenou schvaluje nadřízený v docházkovém systému."),
    h2("Nemoc"),
    p("Nemoc se hlásí nadřízenému ráno prvního dne, nejpozději do 9:00."),
  ] },
  { path: "wiki/zalohovani-databaze", title: "Zálohování databáze", nodes: () => [
    p("Databáze PostgreSQL se zálohuje každou noc ve 2:00 a záloha se drží 30 dní."),
    h2("Obnova"),
    p("Obnova se provádí příkazem pg_restore na čistou instanci; po obnově se spustí kontrola konzistence indexu."),
  ] },
  { path: "wiki/nastup-noveho-zamestnance", title: "Nástup nového zaměstnance", nodes: () => [
    p("Při nástupu dostane nový kolega notebook, účet v adresáři a přístup do Keepass. Heslo do Keepass si nastaví sám při prvním přihlášení."),
    h2("První týden"),
    p("První týden prochází školení bezpečnosti a seznamuje se s produkty."),
  ] },
  { path: "wiki/strukturovane-pozadavky-prehled", title: "Strukturované požadavky: přehled funkce", nodes: () => [
    p("Strukturovaný požadavek je formulář s pevnou strukturou polí, který lékař vyplňuje v nemocničním systému."),
    h2("Typy"),
    p("Rozlišujeme formuláře příjmu, propuštění a konzilia. Každý typ má vlastní šablonu."),
  ] },
];

export function buildPages(variant) {
  counter = 0;
  return pages.map((page) => ({ ...page, content: {
    format: "atlas-rich", version: 1,
    document: { type: "doc", content: variant === "with" ? page.nodes() : withoutRequirements(page.nodes()) },
    plugins: variant === "with" ? [{ id: "atlas-requirements", version: 1 }] : [],
  } }));
}

/** Writes the Git-backup layout BackupLoader reads: `<path>.md` with a frontmatter id and `.atlas/pages/<id>.json`. */
export function writeBackup(variant, directory) {
  mkdirSync(join(directory, ".atlas", "pages"), { recursive: true });
  buildPages(variant).forEach((page, index) => {
    const id = `00000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`;
    mkdirSync(join(directory, page.path.split("/")[0]), { recursive: true });
    writeFileSync(join(directory, `${page.path}.md`), `---\nid: ${id}\n---\n# ${page.title}\n`);
    writeFileSync(join(directory, ".atlas", "pages", `${id}.json`), JSON.stringify({ id, parentId: null, title: page.title, content: page.content }));
  });
}

if (process.argv[1]?.endsWith("fixtures.mjs") && process.argv[2]) {
  writeBackup(process.argv[2], process.argv[3]);
  console.log(`${pages.length} pages (${process.argv[2]}) in ${process.argv[3]}`);
}
