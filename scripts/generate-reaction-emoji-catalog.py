#!/usr/bin/env python3
"""Generate the shared offline emoji palette from vendored Unicode 16.0 data."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import unicodedata
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "Legend-Design/unicode/emoji-test-16.0.txt"
OUTPUT = ROOT / "Legend-Design/legend-reaction-emoji.json"
SOURCE_URL = "https://www.unicode.org/Public/emoji/16.0/emoji-test.txt"

ANNOTATIONS = [
    (ROOT / "Legend-Design/unicode/annotations-en-46.xml", "https://raw.githubusercontent.com/unicode-org/cldr/release-46/common/annotations/en.xml"),
    (ROOT / "Legend-Design/unicode/annotations-derived-en-46.xml", "https://raw.githubusercontent.com/unicode-org/cldr/release-46/common/annotationsDerived/en.xml"),
]


def tokens(value):
    folded = unicodedata.normalize("NFKD", value).lower()
    folded = "".join(c for c in folded if not unicodedata.combining(c))
    return re.findall(r"[^\W_]+", folded, flags=re.UNICODE)


def add_skin_tone_mappings(entries):
    """Resolve complete source-listed sequences, including Unicode legacy bases.

    Unicode uses compact neutral bases for handshake, kiss and couple-with-heart
    but expanded ZWJ sequences for some toned forms. Name matching connects these
    source records without ever manufacturing a modified Unicode string.
    """
    tone_keys = {0x1F3FB: "light", 0x1F3FC: "mediumLight", 0x1F3FD: "medium",
                 0x1F3FE: "mediumDark", 0x1F3FF: "dark"}
    neutral = {entry["name"]: entry for entry in entries
               if not any(ord(c) in tone_keys for c in entry["emoji"])}
    # CLDR explicitly names both neutral people in expanded mixed-tone forms.
    neutral_names = {"kiss: person, person": "kiss",
                     "couple with heart: person, person": "couple with heart"}
    mappings = {entry["emoji"]: {"default": entry["emoji"]} for entry in neutral.values()}
    for entry in entries:
        modifiers = {ord(c) for c in entry["emoji"] if ord(c) in tone_keys}
        name = re.sub(r"(?:light|medium-light|medium|medium-dark|dark) skin tone(?:, )?",
                      "", entry["name"]).rstrip(" :,")
        name = neutral_names.get(name, name)
        if name not in neutral:
            raise ValueError(f"No canonical source-listed base for {entry['name']}")
        base = neutral[name]["emoji"]
        entry["baseEmoji"] = base
        if len(modifiers) == 1:
            key = tone_keys[next(iter(modifiers))]
            if key in mappings[base] and mappings[base][key] != entry["emoji"]:
                raise ValueError(f"Ambiguous uniform skin tone for {entry['name']}")
            mappings[base][key] = entry["emoji"]
    for entry in entries:
        entry["skinToneVariants"] = mappings[entry["baseEmoji"]]


def generate():
    source = SOURCE.read_bytes()
    text = source.decode("utf-8")
    if "# Version: 16.0" not in text:
        raise ValueError("Unexpected Unicode emoji source version")
    annotations = {}
    for path, _ in ANNOTATIONS:
        for entry in ET.parse(path).getroot().iter("annotation"):
            key = entry.attrib["cp"].replace("\ufe0f", "")
            annotations.setdefault(key, []).append(entry.text or "")
    entries = []
    group = subgroup = None
    for line in text.splitlines():
        if line.startswith("# group: "):
            group = line.removeprefix("# group: ")
        elif line.startswith("# subgroup: "):
            subgroup = line.removeprefix("# subgroup: ")
        else:
            match = re.fullmatch(
                r"([0-9A-F ]+)\s*;\s*fully-qualified\s*#\s*(\S+)\s+E[\d.]+\s+(.+)", line
            )
            if not match:
                continue
            emoji = "".join(chr(int(point, 16)) for point in match[1].split())
            if emoji != match[2] or not group or not subgroup:
                raise ValueError(f"Invalid emoji source line: {line}")
            name = match[3]
            search_terms = " ".join(annotations.get(emoji.replace("\ufe0f", ""), []))
            keywords = sorted(set(tokens(" ".join((name, group, subgroup, search_terms)))))
            entries.append(dict(emoji=emoji, name=name, group=group, subgroup=subgroup, keywords=keywords))
    if len(entries) != 3781 or len({entry["emoji"] for entry in entries}) != len(entries):
        raise ValueError("Unicode 16.0 fully-qualified palette must contain 3781 unique entries")
    add_skin_tone_mappings(entries)
    return {
        "schemaVersion": 1,
        "unicodeVersion": "16.0",
        "source": {
            "url": SOURCE_URL,
            "sha256": hashlib.sha256(source).hexdigest(),
            "license": "Unicode-3.0",
            "licenseFile": "unicode/LICENSE.txt",
            "licenseText": (ROOT / "Legend-Design/unicode/LICENSE.txt").read_text(),
            "cldrVersion": "46",
            "annotations": [
                {"url": url, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                for path, url in ANNOTATIONS
            ],
        },
        "entries": entries,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Verify generated catalog is current without writing")
    args = parser.parse_args()
    serialized = json.dumps(generate(), ensure_ascii=False, indent=2) + "\n"
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_text() != serialized:
            raise SystemExit("Emoji catalog is stale; run scripts/generate-reaction-emoji-catalog.py")
        print("Unicode 16.0 emoji catalog is current (3781 fully-qualified entries).")
    else:
        OUTPUT.write_text(serialized)
        print(f"Generated {OUTPUT.relative_to(ROOT)} (3781 fully-qualified entries).")


if __name__ == "__main__":
    main()
