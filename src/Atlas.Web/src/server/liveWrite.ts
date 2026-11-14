import { processAuthorityRequest, processLiveWriteRequest, type AuthorityRequest } from "@atlas/markdown/server";
import { atlasContentPlugins } from "../documentWidgets";
import { loadMarkdownDialect } from "@atlas/markdown/core";

// Requests parse and write Markdown synchronously: the dialect and the forms of every plugin imported above load before the first one.
await loadMarkdownDialect();

// Dev-time only (3.6: the server runs no Node.js): the JavaScript reference of the managed document authority, which
// yjsWriterFixtures.test.ts records as parity fixtures for tests/Atlas.Yjs.Tests. Never bundled into the app or the server.

/** One request: without `target` the document is only reduced; with it, `update` turns it into the target. */
export interface LiveWriteFrameRequest extends AuthorityRequest { target?: unknown }

/** The rich document as an object, not the JSON string of the package. */
export function processLiveWrite(request: LiveWriteFrameRequest) {
  // Fresh plugin instances for every request: the registry destroys what it was given. Each factory is called once, for its id.
  const byId = new Map(atlasContentPlugins({ kind: "history" }).map(plugin => [plugin.id, plugin]));
  const factories = Object.fromEntries([...byId].map(([id, plugin]) => [id, () => plugin]));
  const { target, ...reduce } = request;
  const result = target === undefined ? processAuthorityRequest(reduce, factories) : processLiveWriteRequest({ ...reduce, target }, factories);
  return { ...result, editorJson: (JSON.parse(result.editorJson) as { document: unknown }).document };
}
