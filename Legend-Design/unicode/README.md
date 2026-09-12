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
