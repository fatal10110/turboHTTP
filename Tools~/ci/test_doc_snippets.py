#!/usr/bin/env python3
"""Tests for the documentation snippet harness (doc_snippets.py) and the stale-name check (check_repo.py docs)."""
import importlib.util
import os
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import doc_snippets  # noqa: E402


def load_check_repo():
    spec = importlib.util.spec_from_file_location("check_repo", os.path.join(HERE, "check_repo.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def snippets_of(markdown):
    with tempfile.TemporaryDirectory() as tmp:
        path = os.path.join(tmp, "doc.md")
        with open(path, "w", encoding="utf-8") as f:
            f.write(markdown)
        old = doc_snippets.ROOT
        doc_snippets.ROOT = tmp
        try:
            return doc_snippets.extract("doc.md")
        finally:
            doc_snippets.ROOT = old


class ExtractTests(unittest.TestCase):
    def test_only_csharp_blocks_are_collected(self):
        sn, problems = snippets_of("```json\n{}\n```\n```csharp\nvar x = 1;\n```\n```cs\nvar y = 2;\n```\n")
        self.assertEqual([s.lines for s in sn], [["var x = 1;"], ["var y = 2;"]])
        self.assertEqual(problems, [])

    def test_skip_needs_a_reason(self):
        _, problems = snippets_of("<!-- snippet: skip -->\n```csharp\nx();\n```\n")
        self.assertTrue(any("needs a reason" in p for p in problems))

    def test_skip_and_with_apply_to_the_next_block_only(self):
        sn, _ = snippets_of("<!-- snippet: skip needs a package -->\n```csharp\na();\n```\n"
                            "<!-- snippet: with int n = 0; -->\n```csharp\nb(n);\n```\n```csharp\nc();\n```\n")
        self.assertEqual(sn[0].skip, "needs a package")
        self.assertEqual(sn[1].with_decls, ["int n = 0;"])
        self.assertIsNone(sn[2].skip)
        self.assertEqual(sn[2].with_decls, [])

    def test_directive_before_other_block_is_reported(self):
        _, problems = snippets_of("<!-- snippet: skip why -->\n```json\n{}\n```\n")
        self.assertTrue(problems)


class RenderTests(unittest.TestCase):
    def test_fragment_is_wrapped_and_keeps_its_usings(self):
        sn, _ = snippets_of("```csharp\nusing TurboHTTP.JSON;\n\nvar r = await Foo();\n```\n")
        code = doc_snippets.render(1, sn[0])
        self.assertIn("using TurboHTTP.JSON;", code)
        self.assertIn("static async Task Run()", code)
        self.assertLess(code.index("using TurboHTTP.JSON;"), code.index("static class Snippet"))
        self.assertIn("using TurboHTTP.Core;", code)  # ambient prelude

    def test_declaration_block_is_compiled_as_written(self):
        sn, _ = snippets_of("```csharp\nusing System;\npublic sealed class A { }\n```\n")
        self.assertTrue(sn[0].is_declaration)
        code = doc_snippets.render(1, sn[0])
        self.assertNotIn("static async Task Run()", code)
        self.assertNotIn("using TurboHTTP.Core;", code)  # a complete file carries its own usings

    def test_statement_using_is_not_hoisted(self):
        sn, _ = snippets_of("```csharp\nusing var r = await Foo();\n```\n")
        code = doc_snippets.render(1, sn[0])
        self.assertGreater(code.index("using var r"), code.index("static async Task Run()"))


class StaleNameTests(unittest.TestCase):
    def setUp(self):
        self.check = load_check_repo()

    def hits(self, line):
        return [m.group(0) for m in self.check.STALE_DOC_NAME.finditer(self.check.STALE_DOC_ALLOWED.sub("", line))]

    def test_removed_names_are_flagged(self):
        for line in ("new LoggingMiddleware()", "options.Middlewares.Add(x)", "await c.Get(u).SendAsync();",
                     "| `SendAsync(request)` | Send |"):
            self.assertTrue(self.hits(line), line)

    def test_module_name_and_current_api_are_allowed(self):
        for line in ("using TurboHTTP.Middleware;", "the `TurboHTTP.Middleware` assembly",
                     "await ws.SendAsync(\"hi\");", "await c.Get(u).SendBufferedAsync();",
                     "the old type (formerly AuthMiddleware) was removed"):
            self.assertFalse(self.hits(line), line)


if __name__ == "__main__":
    unittest.main()
