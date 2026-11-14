import type { AddressTestResult, ConnectionView, NetworkKind, TrustedMatch } from "../../../protection";
import t from "../../../i18n/settings-security";
import type { DecisionStep } from "../types";
import { modeInfo } from "./protection";

/** LAN / VPN / Jiná with the icon every list uses. */
export const networkKinds: Record<NetworkKind, { label: string; icon: string }> = {
  lan: { label: "LAN", icon: "building-2" },
  vpn: { label: "VPN", icon: "globe" },
  other: { get label() { return t.network.other; }, icon: "map-pin" },
};
export const networkIcon = (kind: NetworkKind | null) => (kind ? networkKinds[kind].icon : "server");


const rule = (network: TrustedMatch) => `${network.name} · ${network.range}`;

/**
 * "Vaše připojení" as a decision chain: address → how it was derived → matched rule → decision. The server derives the address
 * (X-Forwarded-For only through trusted proxies and at most hopLimit of them); this only puts its answer into words.
 */
export function connectionSteps(view: ConnectionView, hopLimit: number): DecisionStep[] {
  const derived: Record<ConnectionView["reason"], { value: string; detail: string; tone: DecisionStep["tone"]; icon: string }> = {
    direct: { icon: "arrow-right-left", value: t.network.direct, detail: t.network.directDetail(view.peer), tone: "neutral" },
    forwarded: {
      icon: "server",
      value: t.network.forwarded,
      detail: t.network.forwardedDetail(t.network.hops(view.hops), hopLimit, view.peer),
      tone: "success",
    },
    noPeer: { icon: "circle-x", value: t.network.noPeer, detail: t.network.noPeerDetail, tone: "danger" },
    untrustedForwarding: {
      icon: "shield-alert",
      value: t.network.ignored,
      detail: t.network.ignoredDetail(view.peer),
      tone: "warning",
    },
    proxyWithoutForwardedFor: { icon: "shield-alert", value: t.network.noForwardedFor, detail: t.network.noForwardedForDetail, tone: "warning" },
    invalidForwardedFor: { icon: "shield-alert", value: t.network.invalidForwardedFor, detail: t.network.invalidForwardedForDetail, tone: "warning" },
    hopLimitExceeded: {
      icon: "shield-alert",
      value: t.network.tooMany,
      detail: t.network.tooManyDetail(t.network.hops(view.forwardedFor.length), hopLimit),
      tone: "warning",
    },
  };
  const how = derived[view.reason];
  const mode = modeInfo[view.instanceMode];
  let decision: DecisionStep;
  const decided = t.network.decision;
  if (view.instanceMode === "never") decision = { icon: "lock-open", label: decided, value: t.network.never, detail: t.network.neverDetail(mode.label), tone: "warning" };
  else if (view.instanceMode === "always") decision = { icon: "lock", label: decided, value: t.network.always, detail: t.network.alwaysDetail(mode.label), tone: "info" };
  else if (view.networkException) decision = { icon: "circle-check", label: decided, value: t.network.trusted, detail: t.network.mode(mode.label), tone: "success" };
  else decision = { icon: "fingerprint", label: decided, value: t.network.otherNetwork, detail: t.network.mode(mode.label), tone: "info" };
  return [
    {
      icon: "globe",
      label: t.network.address,
      value: view.address ?? t.network.notDetected,
      detail: view.address ? (view.address.includes(":") ? "IPv6" : "IPv4") : t.network.noReliableAddress,
      tone: view.address ? "info" : "danger",
    },
    { icon: how.icon, label: t.network.howDetected, value: how.value, detail: how.detail, tone: how.tone },
    view.network
      ? { icon: networkIcon(view.network.kind), label: t.network.rule, value: view.network.name, detail: t.network.ruleVersion(view.network.range, view.version), tone: "success" }
      : { icon: "circle-x", label: t.network.rule, value: t.network.noActiveRule, detail: t.network.notTrusted, tone: "neutral" },
    decision,
  ];
}

/** The address tester's answer as the same chain: address → proxy? → rule (and switched-off rules that would match). */
export function testerSteps(result: AddressTestResult): DecisionStep[] {
  if (result.addressError || !result.address) return [{ icon: "circle-x", label: t.network.address, value: t.network.invalidAddress, detail: result.addressError ?? t.network.enterAddress, tone: "danger" }];
  const steps: DecisionStep[] = [{ icon: "globe", label: t.network.address, value: result.address, detail: result.address.includes(":") ? "IPv6" : "IPv4", tone: "info" }];
  if (result.isProxy) steps.push({ icon: "server", label: t.network.proxy, value: t.network.trustedProxy, detail: t.network.proxyDetail, tone: "warning" });
  steps.push(
    result.network
      ? { icon: networkIcon(result.network.kind), label: t.network.rule, value: result.network.name, detail: result.network.range, tone: "success" }
      : {
          icon: "circle-x",
          label: t.network.rule,
          value: t.network.noActiveRule,
          detail: result.inactiveMatches.length ? t.network.disabledNetwork(result.inactiveMatches.map(rule).join(", ")) : t.network.notTrusted,
          tone: result.inactiveMatches.length ? "warning" : "neutral",
        },
  );
  steps.push(
    result.network
      ? { icon: "circle-check", label: t.network.inOutside, value: t.network.never, detail: t.network.outsideAlways, tone: "success" }
      : { icon: "fingerprint", label: t.network.inOutside, value: t.network.newSignIn, detail: t.network.everyIssueAudited, tone: "info" },
  );
  return steps;
}
