import { foldText } from "../../../lib/foldText";

/** Firemní přihlášení (OpenID Connect): the shapes of `/admin/modules/oidc` and the provider draft. */
export interface OidcAdminMapping {
  claimType: string;
  claimValue: string;
}
export interface OidcProvider {
  id: string;
  displayName: string;
  issuer: string;
  clientId: string;
  hasSecret: boolean;
  scopes: string[];
  allowSignup: boolean;
  callbackUrl: string;
  adminMappings?: OidcAdminMapping[] | null;
  /** Accounts linked through the provider's issuer: who loses this way in when it is removed. */
  linkedAccounts?: number;
}
export interface OidcSettings {
  version: string | null;
  publicOrigin: string;
  providers: OidcProvider[];
  restartRequired: boolean;
  builtinEnabled: boolean;
}
export interface OidcTestResult {
  ok: boolean;
  message: string;
  checkedAt?: string;
}

/**
 * One provider as edited. The client secret lives only here and in the save request: it is never
 * returned by the server, and the page clears it after every save attempt. `savedId` is the id the
 * provider has on the server (null until saved); the discovery test runs against that one.
 */
export interface OidcProviderDraft {
  savedId: string | null;
  id: string;
  displayName: string;
  issuer: string;
  clientId: string;
  hasSecret: boolean;
  clientSecret: string;
  clearSecret: boolean;
  scopes: string[];
  allowSignup: boolean;
  adminMappings: OidcAdminMapping[];
}

export const maxOidcProviders = 10;
export const maxAdminMappings = 20;
export const providerIdPattern = /^[a-z][a-z0-9-]{0,39}$/;
export const issuerPattern = /^https?:\/\/[^\s/]+\S*$/i;

export function providerDraft(provider: OidcProvider): OidcProviderDraft {
  return {
    savedId: provider.id,
    id: provider.id,
    displayName: provider.displayName,
    issuer: provider.issuer,
    clientId: provider.clientId,
    hasSecret: provider.hasSecret,
    clientSecret: "",
    clearSecret: false,
    scopes: [...provider.scopes],
    allowSignup: provider.allowSignup,
    adminMappings: (provider.adminMappings ?? []).map((mapping) => ({ ...mapping })),
  };
}

export function emptyProviderDraft(): OidcProviderDraft {
  return { savedId: null, id: "", displayName: "", issuer: "", clientId: "", hasSecret: false, clientSecret: "", clearSecret: false, scopes: ["openid", "profile"], allowSignup: false, adminMappings: [] };
}

/** A stable id suggested from the button name: "Firemní účet" → "firemni-ucet". */
export function providerSlug(name: string) {
  const slug = foldText(name, "block")
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  const start = /^[a-z]/.test(slug) ? slug : slug ? `p-${slug}` : "";
  return start.slice(0, 40).replace(/-+$/, "");
}

export function callbackUrl(origin: string, id: string) {
  return `${origin.trim().replace(/\/+$/, "")}/signin-oidc/${id}`;
}

export function scopesFromText(text: string) {
  return text.split(/\s+/).filter(Boolean);
}

/** The PUT body: one request with every provider, the secret only when a new one was typed. */
export function oidcSaveBody(version: string | null, origin: string, providers: OidcProviderDraft[]) {
  return {
    expectedVersion: version,
    publicOrigin: origin.trim(),
    providers: providers.map((provider) => ({
      id: provider.id.trim(),
      displayName: provider.displayName.trim(),
      issuer: provider.issuer.trim(),
      clientId: provider.clientId.trim(),
      clientSecret: provider.clientSecret || undefined,
      clearSecret: provider.clearSecret,
      scopes: provider.scopes,
      allowSignup: provider.allowSignup,
      adminMappings: provider.adminMappings.map((mapping) => ({ claimType: mapping.claimType.trim(), claimValue: mapping.claimValue.trim() })),
    })),
  };
}
