#!/usr/bin/env python3
"""Tests of the il2cppfilter invariant in check_repo.py (#320): the try shapes IL2CPP (Unity 2021.3)
miscompiles are flagged, the shapes it compiles correctly are not. The verdict for each shape was
observed on an IL2CPP player (x86_64 emulator, API 33, Stripping High)."""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_repo  # noqa: E402


def hazards(body):
    return check_repo.il2cpp_filter_hazards("class C {\n void M() {\n" + body + "\n }\n}\n")


class Il2CppFilterCheckTests(unittest.TestCase):
    def test_filtered_rethrow_before_catch_all_is_flagged(self):
        # Http2Connection.DispatchAsync before #320: the rethrow reached catch (Exception).
        self.assertEqual(1, len(hazards(
            "try { Run(); } catch (OperationCanceledException) when (Fired()) { throw; } "
            "catch (Exception ex) { Map(ex); }")))

    def test_unfiltered_rethrow_before_a_later_filter_is_flagged(self):
        # MockTransport before #320.
        found = hazards(
            "try { Run(); }\n"
            "catch (OperationCanceledException) { throw; }\n"
            "catch (UHttpException ex) when (ex.HttpError.Type == UHttpErrorType.Cancelled) { throw new X(ex); }\n"
            "catch (Exception ex) { Report(ex); }")
        self.assertEqual([4, 5], [line for line, _ in found])

    def test_filtered_throw_new_and_dispatch_info_throw_are_flagged(self):
        self.assertEqual(1, len(hazards(
            "try { Run(); } catch (Exception ex) when (Fired()) { throw new OperationCanceledException(\"x\", ex); } "
            "catch (Exception ex) { Fail(ex); }")))
        self.assertEqual(1, len(hazards(
            "try { Run(); } catch (HandlerCallbackException ex) { ExceptionDispatchInfo.Capture(ex).Throw(); } "
            "catch (IOException) when (Fired()) { } catch (Exception) { }")))

    def test_try_without_filter_is_not_flagged(self):
        self.assertEqual([], hazards(
            "try { Run(); } catch (OperationCanceledException) { throw; } catch (Exception ex) { throw Map(ex); }"))

    def test_only_the_last_clause_throwing_is_not_flagged(self):
        self.assertEqual([], hazards(
            "try { Run(); } catch (IOException) when (Fired()) { Log(); } catch (Exception ex) { throw Map(ex); }"))
        self.assertEqual([], hazards(
            "try { Run(); } catch (Exception ex) when (!(ex is OperationCanceledException)) { throw Map(ex); }"))

    def test_clause_that_awaits_is_not_flagged(self):
        # The compiler runs the body of a catch clause that awaits after the try statement.
        self.assertEqual([], hazards(
            "try { Run(); } catch (Exception ex) when (Fired()) { await Close(); throw; } "
            "catch (Exception ex) { Fail(ex); }"))

    def test_keywords_in_strings_and_comments_are_ignored(self):
        self.assertEqual([], hazards(
            "try { Run(); } catch (IOException) when (Fired()) { Log(\"throw; } catch {\"); /* throw */ }\n"
            "// throw\n"
            "catch (Exception) { Log($\"{x} throw\"); }"))


if __name__ == "__main__":
    unittest.main()
