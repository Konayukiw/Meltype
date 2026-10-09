#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 0924haruto12
"""実際の virtual_key と定数だけを読み込み、IBus・共有ライブラリなしで文字写像を検証する。"""

import ast
import unittest
from pathlib import Path


class FakeIBus:
    def __init__(self):
        self.constants = {}

    def __getattr__(self, name):
        if not name.startswith("KEY_"):
            raise AttributeError(name)
        return self.constants.setdefault(name, 0x200000 + len(self.constants))

    @staticmethod
    def keyval_to_unicode(keyval):
        return keyval if isinstance(keyval, str) else chr(keyval)


class VirtualKeyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = Path(__file__).resolve().parents[1] / "ibus-engine-meltype"
        parsed = ast.parse(source.read_text(encoding="utf-8"), filename=str(source))
        constants = {"OTHER_CHARACTER_KEY", "SPECIAL_KEYS", "SYMBOL_KEYS"}
        nodes = [node for node in parsed.body if
                 isinstance(node, ast.FunctionDef) and node.name == "virtual_key" or
                 isinstance(node, ast.Assign) and any(isinstance(target, ast.Name) and target.id in constants
                                                      for target in node.targets)]
        namespace = {"IBus": FakeIBus()}
        exec(compile(ast.Module(body=nodes, type_ignores=[]), str(source), "exec"), namespace)
        cls.virtual_key = staticmethod(namespace["virtual_key"])
        cls.ibus = namespace["IBus"]
        cls.special = namespace["SPECIAL_KEYS"]

    def test_lower_ascii(self):
        for c in "az":
            self.assertEqual((ord(c.upper()), ord(c)), self.virtual_key(c))

    def test_upper_ascii(self):
        for c in "AZ":
            self.assertEqual((ord(c), ord(c)), self.virtual_key(c))

    def test_digits(self):
        self.assertEqual((ord("9"), ord("9")), self.virtual_key("9"))

    def test_symbol_key(self):
        self.assertEqual((0xBE, ord(".")), self.virtual_key("."))

    def test_other_ascii(self):
        self.assertEqual((0x07, ord("@")), self.virtual_key("@"))

    def test_turkish_capital_i(self):
        self.assertEqual((0x07, 0x0130), self.virtual_key("İ"))

    def test_non_ascii_letter(self):
        self.assertEqual((0x07, 0x00C9), self.virtual_key("É"))

    def test_supplementary_scalar(self):
        self.assertEqual((0x07, 0x1F60A), self.virtual_key("😊"))

    def test_empty(self):
        self.assertEqual((None, 0), self.virtual_key(""))

    def test_multiple_scalars(self):
        self.assertEqual((None, 0), self.virtual_key("e\u0301"))

    def test_control(self):
        self.assertEqual((None, 0), self.virtual_key("\x01"))

    def test_special_keys(self):
        for keyval, vk in self.special.items():
            self.assertEqual((vk, 0x20 if keyval == self.ibus.KEY_space else 0), self.virtual_key(keyval))


if __name__ == "__main__":
    unittest.main(verbosity=2)
