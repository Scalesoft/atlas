import { ApiError } from "./api";
import { readStored, writeStored } from "./lib/browserStorage";
import t from "./i18n/shell";

/** What a PDF cover states about the revision, in the server's order (PdfCoverDetails.cs). */
export const PDF_COVER_DETAILS = [
  { value: "revision", get label() { return t.pdf.revision.label; }, get description() { return t.pdf.revision.description; } },
  { value: "state", get label() { return t.pdf.state.label; }, get description() { return t.pdf.state.description; } },
  { value: "revisionDate", get label() { return t.pdf.revisionDate.label; }, get description() { return t.pdf.revisionDate.description; } },
  { value: "exportedAt", get label() { return t.pdf.exportedAt.label; }, get description() { return t.pdf.exportedAt.description; } },
  { value: "pageId", get label() { return t.pdf.pageId.label; }, get description() { return t.pdf.pageId.description; } },
] as const;
export type PdfCoverDetail = typeof PDF_COVER_DETAILS[number]["value"];
export const ALL_PDF_COVER_DETAILS: PdfCoverDetail[] = PDF_COVER_DETAILS.map(detail => detail.value);

const COVER_DETAILS_KEY = "atlas.pdfExport.coverDetails";

/** The cover details this browser chose last for a manual PDF export; all until it chooses. */
export function rememberedCoverDetails(): PdfCoverDetail[] {
  try {
    const stored = JSON.parse(readStored(COVER_DETAILS_KEY) ?? "null");
    if (Array.isArray(stored)) return ALL_PDF_COVER_DETAILS.filter(detail => stored.includes(detail));
  } catch { /* Damaged storage: the default applies. */ }
  return [...ALL_PDF_COVER_DETAILS];
}

export function rememberCoverDetails(details: readonly PdfCoverDetail[]) {
  writeStored(COVER_DETAILS_KEY, JSON.stringify(details));
}

/** A file name from the document's title, portable on every file system (ContentExportNames.cs on the server). */
export function exportFileStem(title: string | undefined, fallback: string) {
  const stem = (title ?? "").normalize("NFC")
    // "Návod: krok/A?" reads as "Návod - krok-A".
    .replace(/[\p{Cc}\p{Cf}]/gu, " ").replace(/:/g, " - ").replace(/[/\\|]/g, "-").replace(/"/g, "'").replace(/[<>?*]/g, "")
    .replace(/\s+/g, " ").replace(/^ /, "")
    .slice(0, 100).replace(/[\uD800-\uDBFF]$/, "")
    .replace(/[.\s-]+$/, "").replace(/^[.\s]+/, "");
  return !stem || /^(con|prn|aux|nul|com\d|lpt\d)(\.|$)/i.test(stem) ? fallback : stem;
}

export interface ContentExportOptions {
  /** The document's title (or the space's name); the file is named after it. */
  name: string;
  revisionId?: string;
  /** For a PDF: what its cover states about the revision; all when left out. */
  coverDetails?: readonly PdfCoverDetail[];
}

/** Downloads an authenticated export without navigating away from unsaved editor state. */
export async function downloadContentExport(kind: "pages" | "spaces", id: string, format: "zip" | "pdf", options: ContentExportOptions) {
  const pdf = kind === "pages" && format === "pdf";
  const revision = pdf ? options.revisionId : undefined;
  const query = format === "pdf"
    ? "?format=pdf" + (revision ? "&revisionId=" + encodeURIComponent(revision) : "")
      + (pdf && options.coverDetails ? "&details=" + encodeURIComponent(options.coverDetails.join(",")) : "")
    : "";
  const response = await fetch("/api/" + kind + "/" + encodeURIComponent(id) + "/export" + query, { credentials: "same-origin" });
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new ApiError(response.status, problem?.detail ?? problem?.title ?? t.pdf.exportFailed);
  }
  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  // Named after the loaded entity's title, never after an arbitrary header filename.
  link.download = exportFileStem(options.name, kind === "pages" ? "stranka" : "prostor") + "." + format;
  link.hidden = true;
  document.body.append(link);
  try { link.click(); } finally { link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000); }
}
