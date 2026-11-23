# Atlas Bloom and motion

The four petals are the pages of an open book. Keep the approved geometry and square aspect ratio; do not put it in a solid icon tile.

## Live components

The development gallery at `/?gallery=1#/gallery/brand` imports the real components. It includes replay/pause, 32 px previews, the login composition and a **design-only** second-factor example.

- `components/AtlasBrandLogo.vue`: decorative mark; adjacent text names the application. `motion="ambient"` (default) moves the pages once, then sends a soft light band across them every nine seconds. `motion="hero"` opens the book once, then adds subtle page movement. `motion="static"` keeps supporting marks still. Keep it mounted to preserve continuity.
- `components/auth/LoginLayout.vue`: the approved opening plus constellation stars, followed by gentle movement and reflections. The slot contains the existing login, activation or recovery form. It does not authenticate, name providers or implement 2FA. Providers are supplied by the existing `/auth/methods` flow.
- `components/brand/AtlasMotionMark.vue`: decorative activity illustrations saved for future use, **not yet attached to AI or settings workflows**. Unlike `AtlasIcon` (action icons) and `LoadingSkeleton` (loading content), these illustrate a named activity. Always provide visible status text alongside them.

`AtlasMotionMark` variants:

| Variant | Intended use | Motion with `active` |
| --- | --- | --- |
| `settings` | Entering settings | One adjustment |
| `bookmark` | Successful save | Ribbon settles, then check appears |
| `shelf` | Entering the space library | Books settle once |
| `network` | Finding related knowledge | Staggered incoming light |
| `pages` | Reading sources | Page-turn loop |
| `scan` | Finding a passage | Soft scanning loop |
| `gather` | Composing an answer | Lines assemble in a loop |

Motion is opt-in (`active` defaults to false). Set it only while the represented activity runs; for a fresh one-shot event, remount with an event key. Do not use a success animation before an operation succeeds. Native CSS handles motion; no animation package or timer is needed.

All components stop motion for `prefers-reduced-motion: reduce` and keep the mark legible. A completed bookmark still shows its static check. Gradients adapt to `html[data-mode="dark"]`, and per-instance IDs prevent collisions between several marks.

## Timing details

The logo opens over 2.6 seconds with 120 ms page offsets; the ambient movement starts after 3.2 seconds and the feathered light after 3.6 seconds. The light fades toward the spine so intersecting strokes do not flash there.

The ambient light is one finite pass per `--atlas-brand-wave` (the travelling 46 % of `bloom-wave`), started by the component: a feathered band and the highlight strokes under it move as two composited layers, and nothing in the logo animates between passes, so the sidebar mark costs no frame while a page sits open. The hero keeps the light inside the SVG mask, because its pages move while it passes.

Incoming knowledge paths use normalized lengths and one light segment per path, a 4.8-second cycle and 1.2-second offsets. They reset only while invisible; keep the dash period longer than the entire traversal to avoid a second launch.

## Release marks

Every minor or major release has a codename and a wireframe mark (`pear.svg` is Atlas 3.5 „Performant Pear“, `quince.svg` Atlas 4.0 „Quiet Quince“, `raspberry.svg` Atlas 4.1 „Radiant Raspberry“). `tooling/brand/wireframe-mark.mjs` draws a body of revolution in the Bloom line style with the shared motion (outline, unfolding wireframe, stem and leaf, quickening light, then a slow sway); a release only adds its geometry in `tooling/brand/<mark>-mark.mjs`. The quince is a grid of dots instead of the wires (`dots`) with the quiet finale (`fade`): as three slow lights pass, the dots go out one by one until a sparse trace stays, with no sway. The raspberry (`raspberry.svg`, Atlas 4.1 „Radiant Raspberry“) is a cluster of round drupelets (`drupelets`) with the radiant finale (`radiant`): once they have popped in, ripples of light run from the centre outwards, stronger each time, short rays flicker around the fruit, and the whole mark dissolves into the light until nothing is left (`data-vanishes`: ReleaseMark shows only the hero that way, the changelog watermark and the icon rest as the full mark); reduced motion shows the full mark and only brightens the drupelets once. The strawberry (`strawberry.svg`, „Shared Strawberry“, the release that opens Atlas as open source; its version number stands only in its release notes) is not a body of revolution and has a generator of its own, `tooling/brand/strawberry-mark.mjs`, in the same line style: a construction in the golden ratio whose motif is growth. A golden rectangle cut into squares and its dimensions (φ : 1) draw themselves, the mother strawberry grows in the first square with her seeds popping in one golden angle (137.5°) after another, then the runner shoots along the golden spiral and a smaller strawberry (a fork) grows in each next square, each 1/φ of the one before; the construction lines and the dimensions fade away, the clean mark stays and a soft light runs along the runner now and then. The tangerine (`tangerine.svg`, Atlas 5.1 „Tandem Tangerine“, the release of live collaboration) is a dimensioned technical drawing with a generator of its own, `tooling/brand/tangerine-mark.mjs`: the whole fruit cracks, its two halves, cut crosswise, part and turn their cut faces to the viewer (the peel as two concentric lines, irregular segments, pith fibres in the middle), while the overall length and the chain beneath (the depth of each half, the gap) stretch with them and their numbers count up to 40,7 and 17,3 · 6 · 17,3, and the diameter of the cut (Ø 24) is dimensioned. Then one segment of the right cut lights up, its angle is dimensioned on the tilted cut (an elliptic arc counting up to 43°), the right half fades to a phantom and the segment comes out of it as one tangerine segment seen from the side, a „fish“ whose length counts up to 19. Two co-editors' pointers (Atlas's own, Jana and Anna in the film's colours) come in, take one half each and part the fruit together; Anna then draws the fish out. The 20 px icon leaves the pointers out with the dimensions. The 20 px icon leaves the dimensions out. The release notes name the mark (`mark: pear`); `components/release/ReleaseMark.vue` draws it inline, so its intro starts when it appears, and stops it for reduced motion. The gallery page „Co je nového“ shows the splash, the changelog entry and the notice that use it.

## Standalone artwork

`bloom.svg` and `bloom-white.svg` are the earlier standalone artwork, retained for existing external consumers. Their embedded animation is independent of the live Vue components. `public/favicon.svg` remains a heavier static small mark. The live components and gallery are the source of truth for the new motion, not the standalone SVG animations.
