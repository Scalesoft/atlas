import type { SecretDiscovery, SecretField, SecretRelease, SecretsPluginHandle, SecretsServices } from "@atlas/markdown/plugins/secrets";
import t from "../i18n/secrets";
import { ApiError } from "../api";
import { releaseSecret, type ReleaseResult, type SecretField as ReleaseField } from "../secretRelease";
import {
  secretsApi, type SecretApiField, type SecretOperation, type SecretReauthRequirement, type SecretReferenceView, type SecretsApi,
} from "./secretsApi";
import type { SecretAccessEvents } from "./secretEvents";

/**
 * Atlas host services of the editor's secrets plugin (WP4 V6). The document holds only the secret id; everything else
 * comes from the server at the moment it is needed:
 *
 * - discover: GET /api/secrets/{id}/reference; read or manage = available, discover = requestable, 404 = hidden;
 * - reveal/copy: one POST /api/secrets/{id}/release with every shown field in `fields` (title and path are metadata,
 *   never released; copy sends its one field); a reauth_required refusal (403) asks for a fresh sign-in (the reauth prompt)
 *   before this release, never covered by the security-change window, once and retries once
 *   with its proof, which the server consumes; unsupported fails closed;
 * - requestAccess: POST /api/secrets/{id}/access-requests (level read) with the reason the reader typed in the
 *   plugin's request panel, followed by the page it came from;
 * - a 403/404 on a release and an `accessRequestChanged` that settles a request (approved; also rejected and
 *   cancelled, which end the pending state) rerun discovery through `plugin.refresh(secretId)`, so the reference repaints
 *   at its new level without a remount; a vault-wide request refreshes every reference;
 * - revocation: `secretAccessRevoked` clears released values and invokes each reference's revoke callback, aborting
 *   pending reveals and copies before refreshing, so the reference shows what the new level allows (requestable, hidden).
 *
 * Released values live only in the text of the elements handed to the plugin, until the plugin disposes them or the
 * server lifetime (`expiresInSeconds`) ends, whichever comes first. Nothing is written to the document, Yjs, drafts,
 * web storage or the console; the reference metadata is cached in memory for this editor only.
 */
export interface SecretReauthRequest {
  secretId: string;
  operation: SecretOperation;
  requirement: SecretReauthRequirement;
  signal: AbortSignal;
}
/** A fresh sign-in for one release; resolves the proof id, rejects when the reader cancels. */
export type SecretReauthPrompt = (request: SecretReauthRequest) => Promise<string>;

export interface AtlasSecretsOptions {
  reauth: SecretReauthPrompt;
  events: SecretAccessEvents;
  /** Context sent with an access request after the reader's own reason, e.g. the page it came from. */
  reason: () => string;
  /** The page the references are shown on, sent with every release (Čtenost stránky). */
  pageId?: () => string | undefined;
  api?: SecretsApi;
  /** The V5 release client; a seam for tests. */
  release?: typeof releaseSecret;
  clipboard?: (text: string) => Promise<void>;
}

export interface AtlasSecretsServices extends SecretsServices {
  /**
   * Registers a `createSecretsPlugin` return value whose references are on screen (the live editor, the reader
   * view), so events and failures refresh or conceal them in place. Returns the detach.
   */
  attach(plugin: SecretsPluginHandle): () => void;
  /** Clears every released value and stops listening; the editor unmounts. */
  dispose(): void;
}

/** The typed reason first (the approver reads it), then where the request came from. */
export function accessRequestReason(typed: string, context: string) {
  const reason = typed.trim();
  return reason ? `${reason}\n\n${context}` : context;
}

export class SecretReleaseError extends Error {}

const apiField: Record<SecretField, SecretApiField> = {
  title: "title", path: "path", username: "userName", password: "password", url: "url", notes: "notes",
};
const releaseField: Partial<Record<SecretField, ReleaseField>> = { username: "userName", password: "password", url: "url", notes: "notes" };

function aborted(signal: AbortSignal) {
  if (signal.aborted) throw new DOMException(t.aborted, "AbortError");
}

export function createAtlasSecretsServices(options: AtlasSecretsOptions): AtlasSecretsServices {
  const api = options.api ?? secretsApi;
  const releaseValues = options.release ?? releaseSecret;
  const clipboard = options.clipboard ?? (text => navigator.clipboard.writeText(text));
  const views = new Map<string, Promise<SecretReferenceView | null>>();
  const released = new Map<string, Set<() => void>>();
  const plugins = new Set<SecretsPluginHandle>();
  // Several surfaces of one secret report the same event; one refresh per secret and turn is enough.
  const refreshing = new Set<string | undefined>();
  function refresh(secretId?: string) {
    if (refreshing.has(secretId)) return;
    refreshing.add(secretId);
    // After the current turn: a failed release first settles in the plugin, then discovery runs again.
    setTimeout(() => {
      refreshing.delete(secretId);
      for (const plugin of [...plugins]) plugin.refresh(secretId);
    });
  }
  const stopRequests = options.events.onRequestChanged(({ secretId, state }) => {
    if (secretId) views.delete(secretId);
    else views.clear();
    // A new pending request changes nothing the reader sees (its own is already shown as pending).
    if (state !== "pending") refresh(secretId ?? undefined);
  });

  // One shared request per secret: it must not carry any one consumer's signal, or the first node view destroyed during load fails all the others.
  function view(secretId: string) {
    let known = views.get(secretId);
    if (!known) {
      known = api.reference(secretId);
      views.set(secretId, known);
      known.catch(() => views.delete(secretId));
    }
    return known;
  }
  function clearReleased(secretId: string) {
    for (const clear of [...(released.get(secretId) ?? [])]) clear();
  }

  /**
   * One request for all fields, retried once after a fresh sign-in: a proof is single-use, so one reveal must not spend
   * it on more than one request.
   */
  async function release(secretId: string, fields: ReleaseField[], operation: SecretOperation, signal: AbortSignal) {
    try {
      const pageId = options.pageId?.();
      const page = pageId ? { pageId } : {};
      let result: ReleaseResult = await releaseValues(secretId, { fields, operation, ...page }, signal);
      if (result.kind === "reauth") {
        if (!result.reauth.supported)
          throw new SecretReleaseError(t.reauthUnsupported);
        const proofId = await options.reauth({ secretId, operation, requirement: result.reauth, signal });
        aborted(signal);
        result = await releaseValues(secretId, { fields, operation, proofId, ...page }, signal);
        if (result.kind === "reauth") throw new SecretReleaseError(t.reauthFailed);
      }
      return result;
    } catch (error) {
      // The level dropped (403) or the secret went away (404): the next discovery decides again (request access).
      if (error instanceof ApiError && (error.status === 403 || error.status === 404)) {
        views.delete(secretId);
        refresh(secretId);
      }
      throw error;
    }
  }

  return {
    async discover({ secretId, signal }): Promise<SecretDiscovery> {
      const reference = await view(secretId);
      aborted(signal);
      if (!reference) return "hidden";
      if (reference.level === "read" || reference.level === "manage") return "available";
      // requestRefused is read by editor 0.7.0+; an older editor ignores the extra field.
      const requestable = { access: "requestable" as const, requestPending: reference.requestPending === true, requestRefused: reference.requestRefused === true };
      return requestable;
    },
    async requestAccess({ secretId, signal, reason }) {
      await api.requestAccess(secretId, accessRequestReason(reason ?? "", options.reason()), signal);
      const known = await views.get(secretId)?.catch(() => null);
      if (known) views.set(secretId, Promise.resolve({ ...known, requestPending: true, requestRefused: false }));
    },
    async reveal({ secretId, fields, signal, ownerDocument }): Promise<SecretRelease> {
      const reference = await view(secretId);
      aborted(signal);
      if (!reference) throw new SecretReleaseError(t.unavailable);
      const elements: HTMLElement[] = [];
      const values: Partial<Record<SecretField, HTMLElement>> = {};
      let expiry: ReturnType<typeof setTimeout> | undefined;
      let lifetime = Number.POSITIVE_INFINITY;
      const clear = () => {
        clearTimeout(expiry);
        for (const element of elements) element.textContent = "";
        elements.length = 0;
        released.get(secretId)?.delete(clear);
      };
      const shown = fields.filter(field => reference.fields.includes(apiField[field]));
      const requested = shown.flatMap(field => releaseField[field] ?? []);
      let result: Partial<Record<ReleaseField, string>> = {};
      try {
        if (requested.length > 0) {
          const answer = await release(secretId, requested, "reveal", signal);
          aborted(signal);
          result = answer.values ?? {};
          lifetime = Math.max(1, answer.expiresInSeconds);
        }
        for (const field of shown) {
          const name = releaseField[field];
          const text = field === "title" ? reference.title : field === "path" ? reference.path : name ? result[name] : undefined;
          // A field that is empty in KeePass is left out of the values: the plugin shows its dash.
          if (text === undefined) continue;
          const element = ownerDocument.createElement("span");
          element.className = "atlas-secret-value";
          element.textContent = text;
          elements.push(element);
          values[field] = element;
        }
      } catch (error) {
        clear();
        throw error;
      }
      let set = released.get(secretId);
      if (!set) released.set(secretId, (set = new Set()));
      set.add(clear);
      // The server decides how long a released value may stay; the document's autoHideSeconds can only shorten it.
      if (Number.isFinite(lifetime)) expiry = setTimeout(clear, lifetime * 1000);
      return { values, dispose: clear };
    },
    async copy({ secretId, field, signal }) {
      if (field === "title" || field === "path") {
        const reference = await view(secretId);
        aborted(signal);
        if (!reference) throw new SecretReleaseError(t.unavailable);
        await clipboard(field === "title" ? reference.title : reference.path);
        return;
      }
      const name = releaseField[field];
      if (!name) throw new SecretReleaseError(t.fieldNotCopyable);
      const result = await release(secretId, [name], "copy", signal);
      aborted(signal);
      const value = result.values[name];
      if (value === undefined) throw new SecretReleaseError(t.fieldEmpty);
      await clipboard(value);
    },
    subscribeRevocation(secretId, revoke) {
      return options.events.subscribe(secretId, () => {
        views.delete(secretId);
        clearReleased(secretId);
        // Revocation also aborts pending operations; conceal only covers values already open.
        revoke();
        if (plugins.size === 0) return;
        refresh(secretId);
      });
    },
    attach(plugin) {
      plugins.add(plugin);
      return () => plugins.delete(plugin);
    },
    dispose() {
      stopRequests();
      plugins.clear();
      [...released.keys()].forEach(clearReleased);
      views.clear();
    },
  };
}

/** Contexts that must never offer a release (history preview, public view): every reference renders hidden. */
export const inertSecretsServices: SecretsServices = {
  discover: async () => "hidden",
  requestAccess: async () => { throw new SecretReleaseError(t.previewNoAccess); },
  reveal: async () => { throw new SecretReleaseError(t.previewNoReveal); },
  copy: async () => { throw new SecretReleaseError(t.previewNoCopy); },
  subscribeRevocation: () => () => {},
};
