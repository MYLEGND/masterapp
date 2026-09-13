# Shared reaction emoji catalog

`../legend-reaction-emoji.json` is the offline reaction palette for iOS,
Android, and web. Consumers bundle or serve this file directly rather than
maintaining platform-specific emoji lists. It contains all 3,781 fully-qualified
Unicode Emoji 16.0 sequences in the source's recommended CLDR palette order.
Rendering uses the platform emoji font; newer symbols may require an updated OS.

The vendored `emoji-test-16.0.txt` comes from
https://www.unicode.org/Public/emoji/16.0/emoji-test.txt and is distributed under
the Unicode License v3 in `LICENSE.txt`, downloaded from
https://www.unicode.org/license.txt. The generated file records source SHA-256 hashes and embeds the complete license
so bundled native and web copies carry attribution. Official CLDR release 46
English annotations and derived annotations provide search synonyms; the source
URLs are recorded in the catalog.

Regenerate offline with `python3 scripts/generate-reaction-emoji-catalog.py`.
Validate with `python3 scripts/generate-reaction-emoji-catalog.py --check` and
`python3 scripts/test-reaction-emoji-catalog.py`.

Schema version 1 exposes `entries`, each with `emoji`, `name`, `group`,
`subgroup`, and `keywords`. Select and send `emoji` unchanged, including variation
selectors, skin modifiers, regional indicators, and zero-width joiners. `name`
is the Unicode English accessible label. Search keywords include the name,
group/subgroup, and official CLDR English annotations. Keywords are lowercase NFKD text
with combining marks removed, split into alphanumeric words. Normalize a query
the same way; every query token must match a substring of at least one keyword.
An empty query displays the full palette. A query containing an emoji may also
match the unchanged `emoji` value. Search and selection require no network call.

Each entry also carries `baseEmoji` and `skinToneVariants`. The latter maps
`default` to the source-listed neutral base, with optional `light`,
`mediumLight`, `medium`, `mediumDark`, and `dark` keys. Every mapped value is an
actual fully-qualified entry from the vendored Unicode source. No consumer
should insert modifiers or alter ZWJ strings. Select the preferred tone's mapped
value, falling back to `baseEmoji` if that tone is unsupported. Persist the tone
key once per picker preference; apply it consistently to all eligible entries.
The visible base palette can filter `emoji == baseEmoji`. Keep the full catalog
available to identify existing toned or mixed-tone reactions. Applying a global
tone to a mixed-tone entry selects the valid uniform-tone variant of its base.
Emoji without tone variants (for example hearts and flags) remain unchanged.

The shared `legend-design.tokens.json` contract defines
`messaging.reactionBubble`: height 32, horizontal padding 8, item spacing 4,
border width 1, outside fraction 0.25, and trailing inset 0 (logical points/dp/CSS
pixels). Anchor the pill at the physical bottom-right of its exact text bubble
or attachment container, not the timestamp or outer conversation row. Its right
edge aligns with the content's right edge and its vertical extent is 75% inside
the content and 25% below it. Reserve the outside height (8 at the standard
height) before timestamp or next-row content. If accessibility causes the pill
height to grow, preserve the same fraction using its measured height. Use the
existing capsule radius and shared surface/gold/separator colors for styling.
The same reaction contract sets emoji size 20, own-reaction fill color
`chatTimestamp` at opacity 0.16, other-reaction fill `surfaceElevated`, and border
color `gold` at opacity 0.35. Color names reference the existing shared color
tokens, including their light/dark variants; consumers should resolve these
references instead of declaring platform-specific colors or opacity values.
