#!/usr/bin/env python3
"""Calibrate structural CIR assertions independently of compiler output."""
import json
import pathlib
import subprocess
import unittest

class PatternTests(unittest.TestCase):
    def matches(self, actual, pattern):
        query = 'include "lowering-pattern"; ir_matches(' + json.dumps(pattern) + ')'
        result = subprocess.run(['jq', '-L', str(pathlib.Path(__file__).parent), '-e', query],
                                input=json.dumps(actual), text=True, capture_output=True)
        self.assertIn(result.returncode, (0, 1), result.stderr)
        return result.returncode == 0

    def test_fields_share_an_object_but_not_an_order(self):
        self.assertTrue(self.matches({'value': 7, 'extra': 1, 'k': 'return'}, {'k': 'return', 'value': 7}))
        self.assertFalse(self.matches({'left': {'k': 'return'}, 'right': {'value': 7}}, {'k': 'return', 'value': 7}))

    def test_arrays_retain_order_and_complete_length(self):
        self.assertTrue(self.matches([1, {'k': 'local', 'name': 'x'}], [1, {'name': 'x'}]))
        for value in ([2, 1], [1, 2, 3], [1], None):
            self.assertFalse(self.matches(value, [1, 2]))
        self.assertFalse(self.matches([1], []))
        self.assertTrue(self.matches([], []))

    def test_prefix_is_explicit_and_presence_is_not_null(self):
        self.assertTrue(self.matches([1, 2], {'$prefix': [1]}))
        self.assertFalse(self.matches([2, 1], {'$prefix': [1]}))
        self.assertTrue(self.matches({'x': None}, {'x': {'$any': True}}))
        self.assertFalse(self.matches({}, {'x': {'$any': True}}))
        self.assertFalse(self.matches({}, {'x': None}))

if __name__ == '__main__':
    unittest.main()
