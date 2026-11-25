/*
 * Frozen baseline of src/motionLiterals.guard.test.ts (motion unification plan §5 M0, 2026-10-05): every literal
 * duration, delay, easing and @keyframes outside motion.css and styles/ai.css as it was when the guard came.
 * 0 entries (116 at M0; M1, M2 and M4 converted them all, the remaining @keyframes are allowlisted with reasons).
 *
 * A package that converts a line deletes it here; the test fails on a new literal and on a line that no longer
 * exists, so this list only shrinks. Keys are `file :: declaration` (spaces collapsed), not line numbers, so
 * unrelated edits in the same file do not touch it. A duplicate key means the declaration repeats in the file.
 */

/** Literals that stay, each with its reason (plan §1.4: keep: funkční / smyčka práce / podpis). `match` is a substring of the finding. */
export const MOTION_LITERAL_ALLOWLIST: readonly { file: string; match: string; reason: string }[] = [
  // M2 (nastavení a průvodci)
  {
    file: "src/components/settings/ConfirmWithReauth.vue",
    match: "@keyframes confirm-reauth-shake",
    reason: "keep: funkční. Short sideways shake of a failed re-authentication box, a state signal outside the prototype's vocabulary; distances are --atlas-lift tokens, so reduced motion makes it still (and the border colour change carries the state).",
  },
  // M4 (značka)
  { file: "src/components/AtlasBrandLogo.vue", match: "@keyframes", reason: "Brand signature (plan O2): the approved choreography of the logo, login scene and activity illustrations; its timing is --atlas-brand-* in motion.css, the keyframes shape only the motion itself." },
  { file: "src/components/auth/LoginLayout.vue", match: "@keyframes", reason: "Brand signature (plan O2): the approved choreography of the logo, login scene and activity illustrations; its timing is --atlas-brand-* in motion.css, the keyframes shape only the motion itself." },
  { file: "src/components/brand/AtlasMotionMark.vue", match: "@keyframes", reason: "Brand signature (plan O2): the approved choreography of the logo, login scene and activity illustrations; its timing is --atlas-brand-* in motion.css, the keyframes shape only the motion itself." },
  { file: "src/components/release/ReleaseSplash.vue", match: "@keyframes", reason: "Brand signature of a major release (owner, 4.0): the halo, ring, aurora and sheen of the splash after the update, slow ambient loops loaded only with the lazy What's new chunk (tokens in the eager motion.css would cost the eager budget); they stand still under reduced motion, the system one and Atlas own." },
  { file: "src/components/release/ReleaseSplash.vue", match: "animation: release-", reason: "Brand signature of a major release (owner, 4.0): the halo, ring, aurora and sheen of the splash after the update, slow ambient loops loaded only with the lazy What's new chunk (tokens in the eager motion.css would cost the eager budget); they stand still under reduced motion, the system one and Atlas own." },
  // 5.1
  { file: "src/components/release/demos/graph.vue", match: "@keyframes demo-graph-", reason: "Brand signature of a major release (owner, 5.1): the knowledge-map demo of the splash draws its links, runs a light along them and pops its nodes; loaded only with the lazy demo chunk, timing from the motion tokens, still under reduced motion and [data-still]." },
  // 5.2
  { file: "src/components/comments/PageDiscussionPeek.vue", match: "@keyframes atlas-peek-", reason: "keep: functional (5.2): the peek's bubble drops in from the avatars' corner and flies toward the dock; loaded only with the lazy peek (in the eager motion.css it cost the first-screen budget), timing and distances are the motion tokens, only fades under reduced motion." },
  { file: "src/components/dev/CommentsExample.vue", match: "@keyframes st-slide-in", reason: "Gallery only (5.2): the stand-in dock of the comments example slides in from its edge; kept out of the eager motion.css, timing and distance are the motion tokens, only the fade under reduced motion." },
  { file: "src/contacts/contactQr.css", match: "@keyframes atlas-qr-", reason: "keep: podpis (owner, 5.1): the QR code of a contact card unfolds ring by ring from its centre; the shape of the motion only, its timing is the motion tokens, a plain fade under reduced motion and none under [data-still]." },
];

export const MOTION_LITERAL_BASELINE: string[] = [
  // M1 (shell, navigace, dialogy, hledání): 0 (the last keyframe, SpacePageList's publish bar, now unfolds through InlineReveal)
  // M2 (nastavení a průvodci): 0 (3 keyframes stay in the allowlist)
  // M4 (značka): 0 (its @keyframes stay in the allowlist as brand signature)
];
