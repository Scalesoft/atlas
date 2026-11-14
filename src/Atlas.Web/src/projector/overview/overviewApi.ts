import { api } from "../../api";
import { contactTypeId, type ContactData } from "../../contacts/contactData";
import { objectApi } from "../../objects/objectApi";
import type { RequirementDirection, RequirementState } from "../requirementTypes";

// The space-wide reads of the projector module (server Features/Projector/ProjectorEndpoints.cs, spec §4). Thin on purpose: the
// server decides by the page rights what the account may read; the overview asks once for every block (`all`) and filters it itself.

export type OverviewFilter = "missing" | "inbound-missing" | "all";

/** A block as the server's queries read it (`Requirement` in Contracts/ProjectorContracts.cs). */
export interface RequirementBlock {
  blockId: string; key: string; title: string; direction: RequirementDirection; state: RequirementState; date: string;
  party: string; where: string; requires: string[]; parentBlockId: string | null; position: number; waitingOn?: string[];
  /** The headings above the block on its page, outermost first (the server's RequirementText.Sectioned), where the answer has them. */
  section?: string[];
}

/** One page of the matrix: its keyed blocks, in key order, and `percent`, done of what applies (the server's RequirementSummary). */
export interface OverviewPage { pageId: string; title: string; cells: RequirementBlock[]; percent: number }
export interface RequirementOverview { keys: string[]; pages: OverviewPage[] }

export interface WaitingItem {
  pageId: string; pageTitle: string; blockId: string; key: string; title: string; direction: RequirementDirection; state: RequirementState; date: string;
}
/** waiting_for_input blocks grouped by party, the longest waiting (oldest date, undated last) first. */
export interface WaitingList { parties: { party: string; items: WaitingItem[] }[] }

const space = (spaceId: string) => `/projector/spaces/${encodeURIComponent(spaceId)}`;

/** The overview's cells come without their empty attributes (null, "", []); these fill them back in. */
type WireCell = Pick<RequirementBlock, "blockId" | "key" | "direction" | "state" | "position"> & Partial<RequirementBlock>;
type WireOverview = { keys: string[]; pages: (Omit<OverviewPage, "cells"> & { cells: WireCell[] })[] };
const emptyCell = { title: "", date: "", party: "", where: "", parentBlockId: null };

export const overviewApi = {
  overview: async (spaceId: string, filter: OverviewFilter, signal?: AbortSignal): Promise<RequirementOverview> => {
    const answer = await api<WireOverview>(`${space(spaceId)}/overview?filter=${filter}`, { signal });
    return { ...answer, pages: answer.pages.map(page => ({ ...page, cells: page.cells.map(cell => ({ ...emptyCell, requires: [], ...cell })) })) };
  },
  waiting: (spaceId: string, signal?: AbortSignal) => api<WaitingList>(`${space(spaceId)}/waiting`, { signal }),
  /** The contacts of the directory named exactly as one of `parties`, by party, in one request. */
  async contacts(parties: readonly string[], signal?: AbortSignal): Promise<Map<string, ContactData>> {
    const { items } = await objectApi.list({ type: contactTypeId, scope: "wiki", name: parties }, signal);
    const byName = new Map(items.flatMap(item => { const data = item.data as ContactData | null; return data?.name ? [[data.name.trim().toLocaleLowerCase("cs"), data] as const] : []; }));
    return new Map(parties.flatMap(party => { const match = byName.get(party.trim().toLocaleLowerCase("cs")); return match ? [[party, match] as const] : []; }));
  },
};
