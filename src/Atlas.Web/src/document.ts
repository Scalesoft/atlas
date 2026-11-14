import t from "./i18n/shell";
import type {
  MarkdownEditorController,
  MarkdownRichValue,
} from "@atlas/markdown/core";

export function emptyDocument(): MarkdownRichValue {
  return {
    format: "atlas-rich",
    version: 1,
    document: { type: "doc", content: [{ type: "paragraph" }] },
  };
}
export function readDocument(contentJson: string): MarkdownRichValue {
  // Never fall back to Markdown on invalid rich data: doing so loses plugin data.
  const value = JSON.parse(contentJson);
  if (
    // Both ids, as the editor reads them (isRichFormat); checked here so the shell does not load the editor for it.
    (value?.format !== "atlas-rich" && value?.format !== "scalesoft-rich") ||
    value?.version !== 1 ||
    value?.document?.type !== "doc"
  )
    throw new Error(
      t.page.cannotOpenVersion,
    );
  return value;
}
export function captureDocument(controller: MarkdownEditorController<"rich">) {
  const projection = controller.getProjection();
  return {
    contentJson: JSON.stringify(controller.getValue()),
    markdown: projection.markdown,
  };
}
