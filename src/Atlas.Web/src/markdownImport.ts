import type {
  MarkdownEmbedResolution,
  MarkdownEmbedResolver,
  MarkdownImportDecision,
  MarkdownImportHandler,
  MarkdownImportInfo,
  MarkdownRichValue,
  MarkdownWikiLinkRequest,
  MarkdownWikiLinkResolution,
  MarkdownWikiLinkResolver,
} from "@atlas/markdown/core";
import { pageLink } from "./pageLinks";

/** The fields of a `[[target#heading|alias]]` link or `![[…]]` embed request Atlas reads. */
type WikiLinkRequest = Pick<MarkdownWikiLinkRequest, "target" | "heading" | "alias">;
type Satisfies<T extends Bound, Bound> = T;

/** The editor's `onMarkdownImport`, `resolveWikiLink` and `resolveEmbed` props, reading only what Atlas uses. */
export type MarkdownImportHooks = Satisfies<{
  onMarkdownImport(info: Pick<MarkdownImportInfo, "title" | "leadingHeading">): MarkdownImportDecision | undefined;
  resolveWikiLink(request: WikiLinkRequest): MarkdownWikiLinkResolution | null;
  resolveEmbed(request: WikiLinkRequest): MarkdownEmbedResolution | null;
}, { onMarkdownImport: MarkdownImportHandler; resolveWikiLink: MarkdownWikiLinkResolver; resolveEmbed: MarkdownEmbedResolver }>;

export interface ImportPage { readonly id: string; readonly title: string }
export interface ImportFile { readonly name: string; readonly url: string; readonly image: boolean }

export interface MarkdownImportContext {
  /** The page title as currently edited. */
  title(): string;
  /** Fills an empty page title. */
  setTitle(title: string): void;
  /** Pages of the current space, from the already loaded tree. */
  pages(): readonly ImportPage[];
  /** Files already known on this page (attachments and images of the live document). */
  files(): readonly ImportFile[];
}

const TITLE_LIMIT = 200;

/** Case, accent-insensitive key; GitHub wiki `Page-Name` / `Page_Name` spellings match `Page Name`. */
function key(text: string) {
  return text.normalize("NFD").replace(/[̀-ͯ]/g, "").replace(/[-_\s]+/g, " ").trim().toLocaleLowerCase("cs");
}
/** Only an unambiguous match resolves; two pages with the same title keep the literal text. */
function unique<T>(items: readonly T[], name: (item: T) => string, wanted: string): T | undefined {
  const target = key(wanted);
  if (!target) return undefined;
  const found = items.filter(item => key(name(item)) === target);
  return found.length === 1 ? found[0] : undefined;
}
function sameTitle(left: string, right: string) {
  const plain = (text: string) => text.replace(/\s+/g, " ").trim().toLocaleLowerCase("cs");
  return plain(left) === plain(right);
}
function fileName(target: string) {
  return target.split(/[\\/]/).pop() ?? target;
}

/**
 * Host hooks for Markdown pasted or dropped into a page (`@atlas/markdown` import hooks). Synchronous and
 * network-free: wiki links resolve to pages of the current space by title, embeds to files of this page or
 * to pages; anything unknown or ambiguous stays literal text.
 */
export function createMarkdownImportHooks(context: MarkdownImportContext): MarkdownImportHooks {
  function page(request: WikiLinkRequest) {
    const pages = context.pages();
    return unique(pages, item => item.title, request.target) ?? (request.alias ? unique(pages, item => item.title, request.alias) : undefined);
  }
  return {
    onMarkdownImport({ title, leadingHeading }) {
      const heading = leadingHeading?.trim();
      let current = context.title().trim();
      if (!current) {
        const filled = (title?.trim() || heading)?.slice(0, TITLE_LIMIT).trim();
        if (filled) { context.setTitle(filled); current = filled; }
      }
      // The leading H1 repeats the page title (just filled or already there): drop it from the content.
      return heading && current && sameTitle(heading, current) ? { omitLeadingHeading: true } : undefined;
    },
    resolveWikiLink(request) {
      const target = page(request);
      if (!target) return null;
      // GitHub order `[[Link text|Page-Name]]`: the page came from the alias, the target is the text.
      const label = key(target.title) === key(request.target) ? undefined : request.target;
      return label ? { href: pageLink(target.id), label } : { href: pageLink(target.id) };
    },
    resolveEmbed(request) {
      const file = unique(context.files(), item => item.name, fileName(request.target));
      if (file) return file.image ? { src: file.url, alt: request.alias ?? file.name } : { href: file.url, label: request.alias ?? file.name };
      const target = page(request);
      return target ? { href: pageLink(target.id), label: request.alias ?? target.title } : null;
    },
  };
}

type JsonNode = { type?: string; attrs?: Record<string, unknown>; content?: JsonNode[] };

/** Attachments and images of a document, by file name (images by their alt text, which uploads set to the file name). */
export function documentFiles(value: MarkdownRichValue | undefined): ImportFile[] {
  const files: ImportFile[] = [];
  const visit = (node: JsonNode) => {
    const data = node.attrs?.data as { name?: unknown; url?: unknown } | undefined;
    if (node.type === "attachment" && typeof data?.name === "string" && typeof data.url === "string" && data.url)
      files.push({ name: data.name, url: data.url, image: false });
    if (node.type === "image" && typeof node.attrs?.src === "string" && typeof node.attrs.alt === "string" && node.attrs.alt)
      files.push({ name: node.attrs.alt, url: node.attrs.src, image: true });
    node.content?.forEach(visit);
  };
  if (value) visit(value.document as JsonNode);
  return files;
}
