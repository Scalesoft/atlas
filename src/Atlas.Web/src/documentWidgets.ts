import { createContactsPlugin, type ContactDirectoryService, type ContactsPluginOptions, type MarkdownContact } from "@atlas/markdown/plugins/contacts";
import t from "./i18n/shell";
import { createChartsPlugin } from "@atlas/markdown/plugins/charts";
import { createConcealedContentPlugin } from "@atlas/markdown/plugins/concealed";
import { createDiagramsPlugin } from "@atlas/markdown/plugins/diagrams";
import { createMathPlugin } from "@atlas/markdown/plugins/math";
import { createTocPlugin } from "@atlas/markdown/plugins/toc";
import { createEmojiPlugin } from "@atlas/markdown/plugins/emoji";
import { createOpenApiPlugin, type OpenApiPluginOptions } from "@atlas/markdown/plugins/openapi";
import { createPagesPlugin, type MarkdownPageSource } from "@atlas/markdown/plugins/pages";
import { createSecretsPlugin, type SecretsPluginHandle, type SecretsPluginOptions, type SecretsServices } from "@atlas/markdown/plugins/secrets";
import { createAttachmentsPlugin, type AttachmentUploadService } from "@atlas/markdown/plugins/attachments";
import { createEditorToolsPlugin } from "@atlas/markdown/plugins/editor-tools";
import { createHeadingAnchorsPlugin, type HeadingAnchorsOptions } from "@atlas/markdown/plugins/heading-anchors";
import { createYouTrackPlugin, type YouTrackClient } from "@atlas/markdown/plugins/youtrack";
import { setMarkdownStrings, type MarkdownPlugin } from "@atlas/markdown/core";
import { language, waitForTexts } from "./i18n";
import "@atlas/markdown/concealment/style.css";
import "./verification/pageLinkMark.css";
import { contactPluginOptions, type ContactHost } from "./contacts/contactLinking";
import { drawContactQr } from "./contacts/contactQr";
import { themedUserColor } from "./live/collaboratorColors";
import { pickContact, withContactPicker, type PeopleSearch } from "./contacts/contactSlash";
import { createRequirementsPlugin, type ProjectorHost } from "./projector/requirementsPlugin";
// The contacts overview (a kind of the page overview, after the requirements one) wherever a page is shown.
import "./contacts/contactOverview";
// The issues overview with the YouTrack integration it lists (youtrack/issueOverview.ts).
import "./youtrack/issueOverview";
import { inertSecretsServices } from "./secrets/secretsHost";

// Every surface that shows a document gets its plugins here, so the editor's own words switch here, before the first plugin is
// created (README „Strings“). Czech is the editor's default; the English dictionary loads only for English.
if (language === "en") waitForTexts(import("./i18n/markdown.en").then((english) => setMarkdownStrings(english.default)));

/**
 * Read services of the page shown in the app, whether the page can be written now or not. A mixed service (`contacts`, `pages`,
 * `youTrack`, `projector`, `headingAnchors`) has its write part gated by `canWrite()` (gateWrites below); the rest is read.
 */
export interface DocumentReadServices {
  uploaderName?: string; secrets?: SecretsServices;
  /** The OpenAPI detail: its API view is reading, and the host holds a YAML request until the editor can write (DocumentEditor `yamlHandover`). */
  openDetail?: OpenApiPluginOptions["openDetail"];
  /** People for contacts and `@` mentions (editorDirectories.ts); `search`/`cached` (the `@` picker) are write. */
  contacts?: ContactDirectoryService;
  /** The people whose full names the editor recognises in the text (editorDirectories.ts `createKnownPeople`). Data only: taking one links through `contactHost`. */
  knownPeople?: () => Promise<MarkdownContact[]>;
  /** How much a contact is used here and in all, ranking the people of one name (contacts/contactSources.ts `contactUsage`). Data only. */
  contactUsage?: (objectId: string, context: { signal: AbortSignal }) => Promise<{ here: number; total: number } | null>;
  /** The page as a place of contacts: linking contacts without `objectId` and the last-usage question of „Odebrat“ (contactLinking.ts); both write. */
  contactHost?: ContactHost;
  /** Pages for links (editorDirectories.ts): `resolve`/`resolveUrl` read, `search` (`[[` and the slash menu) write. */
  pages?: MarkdownPageSource;
  /** The page for requirement blocks (module Projector): the catalog insert and the summary's menu; `people` and `api.setCatalogPage`/`createDeployment` write. */
  projector?: ProjectorHost;
  /** Receives the secrets plugin of an instance whose references are on screen (refresh/conceal in place). */
  onSecretsPlugin?: (plugin: SecretsPluginHandle) => void;
  /** The page's heading links (`getHeadingUrl`) and when the collaboration lets the editor give headings their ids (`collaborationReady`, write). */
  headingAnchors?: HeadingAnchorsOptions;
  /** The space's YouTrack: `resolve` (chips, pasted issue URLs) read, `search` (the issue picker) write. */
  youTrack?: YouTrackClient;
}

/** Write capabilities: asked through `canWrite()` when a person acts, so they switch without rebuilding plugins or remounting the editor. */
export interface DocumentWriteCapabilities {
  /** „Vyměnit heslo“: the host SecretPicker answering another secret id for a reference in the page. */
  pickSecretReplacement?: SecretsPluginOptions["pickReplacement"];
  /** People for the picker of „/Kontakt“ and „Udělat kontakt“ (contacts/contactSlash.ts); without it the inserts put a placeholder in. */
  people?: PeopleSearch;
  /** Where attachments dropped into the page are uploaded (editorUploads.ts). */
  upload?: AttachmentUploadService;
}

/**
 * Where a document is displayed. Only the policy differs; the registered node types never do. `edit` is the live page in the app,
 * read or written: its read services are always on, its write capabilities follow `canWrite()` (absent: always, `editor.isEditable`
 * decides). `read` is a static render of something live in the app (a board's texts at rest, a page's preview on a board): its
 * contacts are live with the reader's card, as a page shows them, and nothing else differs from history. History and the public view
 * keep the snapshot of their time. None of the three writes.
 */
export type DocumentWidgetContext =
  | ({ kind: "edit"; canWrite?: () => boolean } & DocumentReadServices & DocumentWriteCapabilities)
  | { kind: "read" }
  | { kind: "history" }
  | { kind: "public" };

/**
 * OpenAPI runtime policy per display context. Try It stays off everywhere: the Scalar frame is
 * same-origin, so a request would target Atlas's origin with the reader's cookies. No context
 * supplies a proxy or request transport, and public readers get documentation only.
 */
export function openApiPolicy(context: DocumentWidgetContext): OpenApiPluginOptions {
  switch (context.kind) {
    case "edit": {
      const uploaderName = context.uploaderName?.trim();
      return {
        allowRequests: false,
        ...(uploaderName ? { uploaderName } : {}),
        ...(context.openDetail ? { openDetail: context.openDetail } : {}),
      };
    }
    case "read":
    case "history":
    case "public":
      return { allowRequests: false };
  }
}

/**
 * Password references (WP4 V6): only the page's own editor or reader view gets the Atlas host services; history and
 * the public view render every reference hidden (the server already strips them from public copies), so neither can
 * ever offer a release.
 */
export function secretsPolicy(context: DocumentWidgetContext): SecretsServices {
  return context.kind === "edit" && context.secrets ? context.secrets : inertSecretsServices;
}

type PageContext = Extract<DocumentWidgetContext, { kind: "edit" }>;

/** The answer of a write after the right to write is gone: the attachment that was not uploaded says why. */
export const readonlyWriteText = t.page.cannotEditNow;

/**
 * The page's write capabilities behind its one `canWrite()`, asked when a person acts, so switching writability neither rebuilds the
 * plugins nor remounts the editor. A closed gate answers as if the capability were missing: nothing found, picked, linked or
 * uploaded. Read services pass through; `editor.isEditable` stays the plugins' last guard.
 */
export function gateWrites<C extends DocumentWidgetContext>(context: C): C {
  if (context.kind !== "edit") return context;
  const { canWrite, people, contacts, pages, youTrack, projector, headingAnchors, upload, pickSecretReplacement } = context as PageContext;
  if (!canWrite) return context;
  const gate = <A extends unknown[], R>(write: (...args: A) => R, closed: () => R) => (...args: A): R => (canWrite() ? write(...args) : closed());
  const nothing = async () => [] as never[];
  const refused = () => Promise.reject(new Error(readonlyWriteText));
  const gatedPeople = people && Object.assign(gate(people, nothing), people.cached && { cached: gate(people.cached, () => []) });
  return {
    ...context,
    ...(gatedPeople && { people: gatedPeople }),
    ...(contacts && { contacts: { ...contacts, search: gate(contacts.search, nothing), ...(contacts.cached && { cached: gate(contacts.cached, () => []) }) } }),
    ...(pages && { pages: { ...pages, search: gate(pages.search, nothing) } }),
    ...(youTrack && { youTrack: { ...youTrack, search: gate(youTrack.search, nothing) } }),
    ...(projector && { projector: { ...projector, ...(gatedPeople && { people: gatedPeople }),
      api: { ...projector.api, setCatalogPage: gate(projector.api.setCatalogPage, refused), createDeployment: gate(projector.api.createDeployment, refused) } } }),
    ...(headingAnchors?.collaborationReady && { headingAnchors: { ...headingAnchors, collaborationReady: gate(headingAnchors.collaborationReady, () => false) } }),
    ...(upload && { upload: { upload: gate(upload.upload, refused) } }),
    ...(pickSecretReplacement && { pickSecretReplacement: gate(pickSecretReplacement, async () => null) }),
  } as C;
}

// Editing, historical and public rendering must understand the same document nodes, so every
// context registers the same plugin ids; only runtime options differ.
export function createDocumentWidgets(context: DocumentWidgetContext) {
  return widgets(gateWrites(context));
}

function widgets(context: DocumentWidgetContext) {
  // The reader types an optional reason in the plugin's request panel; the host adds the page it came from.
  const secrets = createSecretsPlugin({ services: secretsPolicy(context), requestReason: "optional",
    ...(context.kind === "edit" && context.pickSecretReplacement ? { pickReplacement: context.pickSecretReplacement } : {}) });
  if (context.kind === "edit") context.onSecretsPlugin?.(secrets);
  // Concealed blocks are visual only (invisible ink over text that stays in the document, search and public copies);
  // real passwords belong in secret references.
  // Directories only while editing: history and public views render the stored contact and page snapshots.
  // Live contacts wherever a page is shown in the app (reading too); history and public views keep the snapshot of their time.
  const contacts = context.kind === "edit" ? gateContactWrites({ ...(context.contacts && { directory: context.contacts, suggestions: true }), ...contactPluginOptions(context.contactHost),
    ...(context.people && { pick: pickContact(context.people) }), ...(context.knownPeople && { known: context.knownPeople }), ...(context.contactUsage && { usage: context.contactUsage }) }, context.canWrite)
    : context.kind === "read" ? contactPluginOptions() : { qrCode: drawContactQr, userColor: themedUserColor };
  const pages = context.kind === "edit" && context.pages ? { source: context.pages, typing: true } : {};
  const people = context.kind === "edit" ? context.people : undefined;
  return [people ? withContactPicker(createContactsPlugin(contacts), people) : createContactsPlugin(contacts), createChartsPlugin(), createConcealedContentPlugin(), createDiagramsPlugin(),
    createMathPlugin(), createTocPlugin(), createEmojiPlugin(), createOpenApiPlugin(openApiPolicy(context)),
    createPagesPlugin(pages), secrets, inPageMenu(createRequirementsPlugin(context.kind === "edit" && context.projector ? { host: context.projector } : {}))];
}

/** The person picker, the contact form of a linked contact, linking and „Odebrat“ write; closed they pick, edit, link and remove nothing. */
function gateContactWrites(options: ContactsPluginOptions, canWrite?: () => boolean): ContactsPluginOptions {
  if (!canWrite) return options;
  const { pick, link, remove, editLinked } = options;
  return { ...options,
    ...(pick && { pick: (...args: Parameters<typeof pick>) => (canWrite() ? pick(...args) : Promise.resolve(null)) }),
    ...(editLinked && { editLinked: (...args: Parameters<typeof editLinked>) => (canWrite() ? editLinked(...args) : () => {}) }),
    ...(link && { link: (...args: Parameters<typeof link>) => (canWrite() ? link(...args) : Promise.resolve(null)) }),
    ...(remove && { remove: (...args: Parameters<typeof remove>) => (canWrite() ? remove(...args) : Promise.resolve(false)) }) };
}

const readonlyYouTrack: YouTrackClient = { search: async () => [], resolve: async () => null };

/**
 * Every content plugin of an Atlas document in one list: the page editor, its reading view, history, print, previews, the public view
 * and the host manifest the server validates documents against (hostDocumentSchema.test.ts) all take it from here, so a plugin added
 * once reaches all of them. The one exception: the public view has no YouTrack, the server turns issue chips into text and drops the
 * plugin from public copies (SharedDocument.SanitizeYouTrack).
 */
export function atlasContentPlugins(context: DocumentWidgetContext): MarkdownPlugin[] {
  const gated = gateWrites(context);
  const edit = gated.kind === "edit" ? gated : undefined;
  return [createEditorToolsPlugin(), createAttachmentsPlugin(edit?.upload ? { upload: edit.upload } : {}), ...widgets(gated),
    createHeadingAnchorsPlugin(edit?.headingAnchors),
    ...(context.kind === "public" ? [] : [createYouTrackPlugin({ client: edit?.youTrack ?? readonlyYouTrack })])];
}

/**
 * The page editor has no toolbar of its own: its toolbar actions are the page's „…“ menu (DocumentEditor `pluginActions`).
 * A plugin that offers its inserts only in the slash menu is offered there too, with the same labels, icons and commands; one with
 * toolbar actions of its own lists those.
 */
function inPageMenu(plugin: MarkdownPlugin): MarkdownPlugin {
  return {
    ...plugin,
    create(context) {
      const created = plugin.create(context);
      const actions = created.actions;
      return actions?.slash?.length && !actions.toolbar ? { ...created, actions: { ...actions, toolbar: actions.slash } } : created;
    },
  };
}
