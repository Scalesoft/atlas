import { api } from "./api";
import { createPublicationChecks, linkFindings, type LinkFinding } from "./publishing/publicationChecks";
import type { PublicationCheck } from "./publishing/publication";

/**
 * The check before publication of a publication proposal (the assistant's card, WP-41): the server's link findings for the
 * requested version, completed in the browser. The web confirmation of MCP publications is gone in 3.6: a token with the
 * scope write publishes at once.
 */

/** The server's link check for the requested version, the same shape as `GET /pages/{id}/publication-check`. */
export interface RequestFindings { version: number; links: LinkFinding[] }

/**
 * The findings for exactly the requested version: the browser's rules and the server's link check run over the page
 * as saved, but only when its working version is the requested one. Otherwise only the findings the server sent are
 * shown. Never rejects. Findings never carry the text of a detected value (see publicationChecks).
 */
export async function findingsFor(request: { pageId: string; version: number; findings: RequestFindings }, load: PageLoader = loadPage): Promise<PublicationCheck[]> {
  const sent: PublicationCheck[] = linkFindings(request.findings.links).map(({ target: _target, ...check }) => check);
  try {
    const page = await load(request.pageId);
    if (page.version !== request.version) return sent;
    const run = createPublicationChecks({ page: () => page, save: async () => false })();
    if (!run) return sent;
    const own = [...run.local, ...(await run.server)].map(({ target: _target, ...check }) => check);
    const seen = new Set(own.map(check => check.id));
    return [...own, ...sent.filter(check => !seen.has(check.id))];
  } catch {
    return sent;
  }
}

export type PageLoader = (pageId: string) => Promise<{ id: string; version: number; contentJson: string }>;
const loadPage: PageLoader = pageId => api(`/pages/${encodeURIComponent(pageId)}`);
