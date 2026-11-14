import { describe, expect, it, vi } from "vitest";
import { createMarkdownImportHooks, documentFiles, type ImportFile, type ImportPage } from "./markdownImport";
import { pageLink } from "./pageLinks";

function hooks(options: { title?: string; pages?: ImportPage[]; files?: ImportFile[] } = {}) {
  let title = options.title ?? "";
  const setTitle = vi.fn((value: string) => { title = value; });
  const created = createMarkdownImportHooks({ title: () => title, setTitle, pages: () => options.pages ?? [], files: () => options.files ?? [] });
  return { ...created, setTitle, title: () => title };
}

describe("Markdown import hooks", () => {
  it("fills an empty title from front matter, else the leading H1, and drops the duplicate heading", () => {
    const fromFrontMatter = hooks();
    expect(fromFrontMatter.onMarkdownImport({ title: "  Release Process ", leadingHeading: "Release Process" })).toEqual({ omitLeadingHeading: true });
    expect(fromFrontMatter.setTitle).toHaveBeenCalledWith("Release Process");

    const fromHeading = hooks();
    expect(fromHeading.onMarkdownImport({ leadingHeading: "Incident Policy" })).toEqual({ omitLeadingHeading: true });
    expect(fromHeading.title()).toBe("Incident Policy");

    // Front matter names the page, the H1 says something else: both stay.
    const different = hooks();
    expect(different.onMarkdownImport({ title: "Runbook", leadingHeading: "Overview" })).toBeUndefined();
    expect(different.title()).toBe("Runbook");

    const limit = hooks();
    limit.onMarkdownImport({ title: "x".repeat(250) });
    expect(limit.title()).toHaveLength(200);
  });

  it("keeps an existing title and still drops a leading H1 that repeats it", () => {
    const named = hooks({ title: "Onboarding" });
    expect(named.onMarkdownImport({ title: "Imported", leadingHeading: "onboarding" })).toEqual({ omitLeadingHeading: true });
    expect(named.setTitle).not.toHaveBeenCalled();
    expect(named.onMarkdownImport({ leadingHeading: "Something else" })).toBeUndefined();
    expect(named.onMarkdownImport({})).toBeUndefined();
  });

  it("resolves wiki links to pages of the space by title, GitHub order included, and keeps unknown or ambiguous ones literal", () => {
    const pages = [{ id: "alpha", title: "Project Alpha" }, { id: "home", title: "Domů" }, { id: "twin-1", title: "Twin" }, { id: "twin-2", title: "twin" }];
    const { resolveWikiLink } = hooks({ pages });
    expect(resolveWikiLink({ target: "Project Alpha" })).toEqual({ href: pageLink("alpha") });
    expect(resolveWikiLink({ target: "project-alpha", heading: "Goals", alias: "the project" })).toEqual({ href: pageLink("alpha") });
    expect(resolveWikiLink({ target: "Domu" })).toEqual({ href: pageLink("home") });
    // GitHub wikis write [[Link text|Page-Name]]: the page is the alias, the text stays the label.
    expect(resolveWikiLink({ target: "Read the plan", alias: "Project-Alpha" })).toEqual({ href: pageLink("alpha"), label: "Read the plan" });
    expect(resolveWikiLink({ target: "Twin" })).toBeNull();
    expect(resolveWikiLink({ target: "Missing" })).toBeNull();
    expect(resolveWikiLink({ target: "  " })).toBeNull();
  });

  it("resolves embeds to files already on the page, then to pages, else literal", () => {
    const { resolveEmbed } = hooks({
      pages: [{ id: "alpha", title: "Project Alpha" }],
      files: [{ name: "whiteboard.png", url: "/api/attachments/1/content", image: true }, { name: "Plan.pdf", url: "/api/attachments/2/download", image: false }],
    });
    expect(resolveEmbed({ target: "attachments/whiteboard.png" })).toEqual({ src: "/api/attachments/1/content", alt: "whiteboard.png" });
    expect(resolveEmbed({ target: "plan.pdf", alias: "Plán" })).toEqual({ href: "/api/attachments/2/download", label: "Plán" });
    expect(resolveEmbed({ target: "Project Alpha", heading: "Goals" })).toEqual({ href: pageLink("alpha"), label: "Project Alpha" });
    expect(resolveEmbed({ target: "unknown.png" })).toBeNull();
  });

  it("collects attachments and named images of the live document", () => {
    const value = { format: "atlas-rich", version: 1, document: { type: "doc", content: [
      { type: "attachment", attrs: { data: { version: 1, name: "Plan.pdf", url: "/api/attachments/2/download" } } },
      { type: "attachment", attrs: { data: { version: 1, name: "Empty", url: "" } } },
      { type: "paragraph", content: [{ type: "image", attrs: { src: "/api/attachments/1/content", alt: "whiteboard.png" } }] },
      { type: "image", attrs: { src: "/unnamed.png", alt: "" } },
    ] } } as never;
    expect(documentFiles(value)).toEqual([
      { name: "Plan.pdf", url: "/api/attachments/2/download", image: false },
      { name: "whiteboard.png", url: "/api/attachments/1/content", image: true },
    ]);
    expect(documentFiles(undefined)).toEqual([]);
  });
});
