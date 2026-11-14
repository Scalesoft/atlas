import type { PageSummary, Space } from "../../../api";

// Local data of the library examples (dev/refresh/Library*Example.vue): no requests, drawings as data URLs.

/** A schematic in greys, like an architecture diagram someone would put on a space card. */
export function galleryDrawing(kind: "architecture" | "servers" = "architecture") {
  const shapes = kind === "servers"
    ? Array.from({ length: 5 }, (_, i) => `<rect x='${250 + i * 66}' y='30' width='52' height='190' rx='6' fill='#fff' stroke='#7b8794' stroke-width='3'/>${Array.from({ length: 6 }, (_, j) => `<rect x='${258 + i * 66}' y='${44 + j * 28}' width='36' height='14' rx='3' fill='${(i + j) % 3 ? '#9aa5b1' : '#52606d'}'/>`).join("")}`).join("")
      + "<path d='M40 150c0-30 50-40 70-20 10-30 70-30 80 10 30 0 40 40 10 50H50c-20 0-30-20-10-40z' fill='#fff' stroke='#7b8794' stroke-width='3'/><path d='M190 170h50' stroke='#7b8794' stroke-width='3' stroke-dasharray='8 6'/>"
    : "<rect x='220' y='20' width='160' height='40' rx='8' fill='#fff' stroke='#616e7c' stroke-width='3'/><path d='M300 60v30M120 90h360M120 90v30M240 90v30M360 90v30M480 90v30' stroke='#7b8794' stroke-width='3' fill='none'/>"
      + [60, 180, 300, 420].map((x, i) => `<rect x='${x}' y='120' width='120' height='40' rx='8' fill='${i === 1 ? '#52606d' : '#fff'}' stroke='#616e7c' stroke-width='3'/><path d='M${x + 24} 140h72' stroke='${i === 1 ? '#fff' : '#9aa5b1'}' stroke-width='6' stroke-linecap='round'/>`).join("")
      + "<ellipse cx='240' cy='200' rx='34' ry='10' fill='#fff' stroke='#9aa5b1' stroke-width='3'/><path d='M206 200v26c0 6 15 10 34 10s34-4 34-10v-26' fill='none' stroke='#9aa5b1' stroke-width='3'/>";
  const svg = `<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 600 240'><rect width='600' height='240' fill='#f5f7fa'/><path d='M0 60h600M0 120h600M0 180h600' stroke='#e4e7eb' stroke-width='2'/>${shapes}</svg>`;
  return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
}

const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();
const space = (id: string, name: string, icon: string, color: string, extra: Partial<Space> = {}): Space => ({
  id, name, icon, color, role: "Admin", pinPolicy: "None", pinned: false, pageCount: 0, lastChangedAt: minutesAgo(4), ...extra,
});
export const gallerySpaces: Space[] = [
  space("runbooks", "Runbooky a provoz", "server", "red-2", { description: "Postupy pro nasazení, obnovu a řešení incidentů jednotlivých systémů.", pageCount: 110, pinned: true }),
  space("platform", "Care Portal – produktová a technická dokumentace", "layout-dashboard", "violet-2", { description: "Kompletní dokumentace modulů: architektura, API, návody pro správce, release notes a známé problémy.", pageCount: 465, pinPolicy: "Required", pinned: true, lastChangedAt: minutesAgo(3) }),
  space("meetings", "Porady a rozhodnutí", "calendar", "amber-2", { description: "Zápisy z porad vývoje, provozu a vedení po měsících.", pageCount: 258, pinPolicy: "Default", pinned: true }),
  space("sales", "Obchod a klienti", "building-2", "green-2", { description: "Obchodní proces, klienti, smlouvy a SLA.", pageCount: 102, role: "Writer" }),
  space("people", "Lidé a onboarding", "users", "pink-2", { description: "Nástup, směrnice, týmy a nástroje.", pageCount: 44, role: "Reader" }),
  space("atlas3", "Projekt Atlas 3.0 – příprava migrace a nového rozhraní pro všechny týmy", "lightbulb", "indigo-2", { lastChangedAt: null }),
  space("sandbox", "Sandbox", "box", "teal-2", { description: "Místo na zkoušení šablon a importů.", lastChangedAt: null }),
];

const page = (id: string, title: string, parentId: string | null, isPublished = true): PageSummary => ({ id, spaceId: "platform", parentId, title, version: 3, isPublished });
export const galleryPages: PageSummary[] = [
  page("orders", "Objednávky a rezervace", null),
  page("arch", "Architektura", "orders"),
  page("components", "Komponenty a závislosti", "arch"),
  page("model", "Datový model", "arch"),
  page("api", "API", "orders"),
  page("search", "GET /api/v2/objednavky/search", "api", false),
  page("intro", "Úvod do Care Portal", null),
  page("glossary", "Slovník pojmů", "intro"),
  page("map", "Mapa systému a vazby mezi moduly", "intro", false),
  page("long", "Scénář 1: rotace certifikátů s velmi dlouhým názvem, který se nevejde na jeden řádek", "arch"),
];
