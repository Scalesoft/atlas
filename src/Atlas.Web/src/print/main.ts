import { createApp } from "vue";
import "@fontsource-variable/dm-sans";
import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/themes/midnight.css";
import "./print.css";

export interface PrintSnapshot {
  sourceOrigin: string; title: string; space: string; version: number; isPublished: boolean;
  activeFrom: string; exportedAt: string; pageId: string; contentJson: string;
  /** What the cover states about the revision (PdfCoverDetails.cs): revision, state, revisionDate, exportedAt, pageId. */
  details: string[];
}
declare global {
  interface Window {
    atlasPrint: (snapshot: PrintSnapshot) => Promise<void>;
  }
}
// Some widget renderers remove a failed image; remember the failure before it disappears.
let failedImage = false;
document.addEventListener("error", event => { if (event.target instanceof HTMLImageElement) failedImage = true; }, true);
// Invoked only inside the server's credential-free, network-isolated browser context.
window.atlasPrint = async snapshot => {
  document.title = snapshot.title;
  const { default: PrintDocument } = await import("./PrintDocument.vue");
  await new Promise<void>((resolve, reject) => {
    const app = createApp(PrintDocument, { snapshot, onReady: resolve, onFailure: reject });
    app.config.errorHandler = reject;
    app.mount("#app");
  });
  if (failedImage) throw new Error("Některý obrázek není dostupný.");
};
