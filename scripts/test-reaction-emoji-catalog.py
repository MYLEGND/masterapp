#!/usr/bin/env python3
"""Verify source fidelity and practical shared reaction-search coverage."""

import importlib.util
import unittest

from pathlib import Path

SPEC = importlib.util.spec_from_file_location("catalog", Path(__file__).with_name("generate-reaction-emoji-catalog.py"))
CATALOG = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CATALOG)


class ReactionEmojiCatalogTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.entries = CATALOG.generate()["entries"]
        cls.by_emoji = {entry["emoji"]: entry for entry in cls.entries}

    def test_complete_unique_palette_and_groups(self):
        self.assertEqual(len(self.entries), 3781)
        self.assertEqual(len(self.by_emoji), 3781)
        self.assertEqual(len({entry["group"] for entry in self.entries}), 9)
        self.assertTrue(all(entry["name"] and entry["subgroup"] and entry["keywords"] for entry in self.entries))

    def test_qualified_sequences_preserve_modifiers_and_joins(self):
        for emoji in ["❤️", "👍🏿", "👨‍👩‍👧‍👦", "👩🏽‍💻", "🏳️‍🌈", "🇺🇸", "🫩"]:
            self.assertIn(emoji, self.by_emoji)
        self.assertNotIn("❤", self.by_emoji)
        self.assertNotIn("🏿", self.by_emoji)

    def test_case_diacritic_and_multitoken_search(self):
        examples = {"THUMBS UP": "👍", "côte ivoire": "🇨🇮", "family girl boy": "👨‍👩‍👧‍👦", "thanks": "🙏", "like dark": "👍🏿"}
        for query, expected in examples.items():
            matches = [entry["emoji"] for entry in self.entries if all(
                any(token in keyword for keyword in entry["keywords"])
                for token in CATALOG.tokens(query)
            )]
            self.assertIn(expected, matches, query)

    def test_skin_tone_maps_only_contain_source_listed_sequences(self):
        for entry in self.entries:
            base = self.by_emoji[entry["baseEmoji"]]
            self.assertEqual(base["emoji"], base["baseEmoji"])
            self.assertEqual(entry["skinToneVariants"]["default"], base["emoji"])
            self.assertEqual(entry["skinToneVariants"], base["skinToneVariants"])
            for value in entry["skinToneVariants"].values():
                self.assertIn(value, self.by_emoji)
        self.assertEqual(self.by_emoji["👍"]["skinToneVariants"]["dark"], "👍🏿")
        self.assertEqual(self.by_emoji["👩‍💻"]["skinToneVariants"]["medium"], "👩🏽‍💻")
        self.assertEqual(self.by_emoji["❤️"]["skinToneVariants"], {"default": "❤️"})
        self.assertEqual(self.by_emoji["🇺🇸"]["skinToneVariants"], {"default": "🇺🇸"})

    def test_mixed_tones_and_legacy_zwj_bases_resolve_without_synthesis(self):
        self.assertEqual(self.by_emoji["🫱🏻‍🫲🏿"]["baseEmoji"], "🤝")
        self.assertEqual(self.by_emoji["🫱🏻‍🫲🏿"]["skinToneVariants"]["dark"], "🤝🏿")
        self.assertEqual(self.by_emoji["🧑🏻‍❤️‍💋‍🧑🏿"]["baseEmoji"], "💏")
        self.assertEqual(self.by_emoji["💏"]["skinToneVariants"]["light"], "💏🏻")

    def test_reaction_geometry_keeps_three_quarters_inside(self):
        import json
        contract = json.loads((CATALOG.ROOT / "Legend-Design/legend-design.tokens.json").read_text())
        bubble = contract["messaging"]["reactionBubble"]
        self.assertEqual(bubble["outsideFraction"], 0.25)
        self.assertEqual(bubble["height"] * bubble["outsideFraction"], 8)
        self.assertEqual(bubble["trailingInset"], 0)


if __name__ == "__main__":
    unittest.main()
