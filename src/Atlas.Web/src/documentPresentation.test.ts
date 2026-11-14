import { describe, expect, it } from "vitest";
import { documentNotice, type DocumentNoticeInput } from "./documentPresentation";
import type { CollaborationStatus } from "./collaboration";

const online: CollaborationStatus = {
  enabled: true, generation: "session", canSave: true, connection: "online",
  pendingChanges: 0, error: null, projectionSave: "current",
};
const published: DocumentNoticeInput = {
  browserOnline: true, collaboration: online,
  saveError: null, version: 3, publishedVersion: 3,
};

describe("document notice priority", () => {
  it.each([false, true])("leaves the connection notice to the editor when a session is offline (browser online: %s)", (browserOnline) => {
    // The editor renders its own CollaborationNotice for offline/reconnecting sessions;
    // the host must not announce the same state a second time.
    expect(documentNotice({ ...published, browserOnline,
      collaboration: { ...online, connection: "offline" } })).toEqual({ tone: "warning", text: "" });
  });
  it("stays silent while the editor reports reconnecting", () => {
    expect(documentNotice({ ...published, collaboration: { ...online, connection: "reconnecting", canSave: false } }))
      .toEqual({ tone: "warning", text: "" });
  });
  it("shows the revision state while the session is still connecting, so the chip never pops in on open", () => {
    expect(documentNotice({ ...published, collaboration: { ...online, connection: "connecting", canSave: false } }))
      .toEqual({ tone: "neutral", text: "Publikováno" });
  });
  it("says a slow join in the chip's place (the document never carries the line)", () => {
    expect(documentNotice({ ...published, collaboration: { ...online, connection: "connecting", canSave: false }, slowJoin: true }))
      .toEqual({ tone: "neutral", text: "Připojujeme společné úpravy…" });
  });
  it("keeps one notice through repeated offline emissions of a retrying provider", () => {
    // The provider stays `offline` across retries; each republish must yield the same notice
    // (no flicker between "connecting" and "offline", no host announcement on top of the editor's).
    const offline = { ...online, connection: "offline" as const, canSave: false, pendingChanges: 2 };
    const notices = [1, 2, 3].map(() => documentNotice({ ...published, collaboration: { ...offline } }));
    expect(new Set(notices.map(notice => JSON.stringify(notice)))).toEqual(new Set([JSON.stringify({ tone: "warning", text: "" })]));
  });
  it("keeps the revision state while typing waits for the session: saving is SaveState's", () => {
    expect(documentNotice({ ...published, version: 4, collaboration: { ...online, pendingChanges: 2, canSave: false } }))
      .toEqual({ tone: "neutral", text: "Nepublikované změny" });
  });
  it("explains browser offline immediately without a collaboration session", () => {
    expect(documentNotice({ ...published, browserOnline: false, collaboration: null })).toEqual({
      tone: "warning", text: "Bez připojení. Úpravy jsou dočasně pozastavené.",
    });
  });
  it("leaves unsent changes to the editor notice as well", () => {
    // CollaborationNotice already says "Některé změny ještě server nepotvrdil".
    expect(documentNotice({ ...published, collaboration: { ...online,
      connection: "reconnecting", pendingChanges: 2, canSave: false } })).toEqual({ tone: "warning", text: "" });
  });
  it("keeps a rejected save visible while the session is reconnecting", () => {
    expect(documentNotice({ ...published, saveError: "Konflikt revizí.", collaboration: { ...online,
      connection: "reconnecting", canSave: false } })).toEqual({ tone: "error", text: "Konflikt revizí." });
    expect(documentNotice({ ...published, collaboration: { ...online, connection: "reconnecting", canSave: false,
      error: { kind: "collaboration", message: "K úpravě stránky nemáte přístup.", recoverable: false } } }))
      .toEqual({ tone: "error", text: "K úpravě stránky nemáte přístup." });
  });

  it.each([{ connection: "failed" }, { projectionSave: "degraded" }] as Partial<CollaborationStatus>[])("reports failed synchronization %j", (patch) => {
    expect(documentNotice({ ...published, collaboration: { ...online, ...patch } }).tone).toBe("error");
  });
  it("preserves the actual controller error", () => {
    expect(documentNotice({ ...published, collaboration: { ...online, error: {
      kind: "collaboration", message: "K úpravě stránky nemáte přístup.", recoverable: false,
    } } })).toEqual({ tone: "error", text: "K úpravě stránky nemáte přístup." });
  });
  it("keeps a save failure ahead of synchronization and saved revision labels", () => {
    expect(documentNotice({ ...published, saveError: "Konflikt revizí.", collaboration: { ...online, canSave: false } }))
      .toEqual({ tone: "error", text: "Konflikt revizí." });
  });
  it("ignores stale transport state when collaboration is disabled", () => {
    expect(documentNotice({ ...published, collaboration: { ...online,
      enabled: false, connection: "failed", projectionSave: "degraded", pendingChanges: 2, canSave: false } }))
      .toEqual({ tone: "neutral", text: "Publikováno" });
    expect(documentNotice({ ...published, browserOnline: false, collaboration: { ...online, enabled: false, pendingChanges: 2 } }).text)
      .toBe("Bez připojení. Úpravy jsou dočasně pozastavené.");
  });
  it.each([
    [{}, "Publikováno"],
    [{ version: 4 }, "Nepublikované změny"],
    [{ publishedVersion: null }, "Koncept"],
    // A reader, or a live page's writer: nothing waits for Publikovat; a page never published stays a draft.
    [{ version: 4, nothingToPublish: true }, "Publikováno"],
    [{ publishedVersion: null, nothingToPublish: true }, "Koncept"],
  ] as [Partial<DocumentNoticeInput>, string][])("derives revision state independently of transport acknowledgement %j", (patch, text) => {
    expect(documentNotice({ ...published, ...patch })).toEqual({ tone: "neutral", text });
  });
  it("never shows the provider's raw failure", () => {
    const failed = (message: string) => documentNotice({ ...published, collaboration: { ...online, error: { kind: "collaboration", message, recoverable: false } } }).text;
    expect(failed("[FORBIDDEN] Access to the document has been revoked. Local changes remain in this session."))
      .toBe("Přístup ke stránce byl odebrán, nebo je stránka v koši. Neodeslané změny zůstávají v tomto okně.");
    expect(failed("[SOMETHING_NEW] Whatever")).toBe("Společné úpravy se přerušily. Připojte se znovu; neodeslané změny zůstávají v tomto okně.");
    expect(failed("Connection closed with an error.")).toBe("Společné úpravy se přerušily. Připojte se znovu; neodeslané změny zůstávají v tomto okně.");
  });
});
