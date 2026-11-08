#!/usr/bin/env python3
"""Run directly: python3 .github/scripts/release-notes.test.py"""

import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("release_notes", Path(__file__).with_name("release-notes.py"))
notes = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = notes
spec.loader.exec_module(notes)


class ReleaseNotesTests(unittest.TestCase):
    def setUp(self):
        environment = patch.dict(os.environ, {"GITHUB_OUTPUT": "", "GITHUB_STEP_SUMMARY": ""})
        environment.start()
        self.addCleanup(environment.stop)
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        previous = Path.cwd()
        os.chdir(self.scratch.name)
        self.addCleanup(os.chdir, previous)
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.name", "test")
        self.git("config", "user.email", "test@example.com")
        self.git("config", "commit.gpgsign", "false")
        self.git("config", "tag.gpgsign", "false")
        self.git("config", "core.hooksPath", "/dev/null")
        self.initial = self.commit("Old release")
        self.git("tag", "-a", "v0.2.0", "-m", "Old release")
        self.first = self.commit("Merge pull request #1 from test/feature/one")
        self.git("tag", "-a", "v0.2.1", "-m", "Intermediate tag")
        self.head = self.commit("Merge pull request #2 from test/fix/two")
        self.git("tag", "-a", "v0.2.2", "-m", "Target tag")
        self.releases = [self.release("v0.2.0", "2026-11-05T00:00:00Z")]
        self.pulls = [
            self.pull(1, self.first, "Add routing", "feature/one"),
            self.pull(2, self.head, "Fix notes | releases", "fix/two"),
        ]
        self.directory = Path(self.scratch.name) / "notes"
        self.api_patch = patch.object(notes, "api_pages", side_effect=self.api)
        self.api_patch.start()
        self.addCleanup(self.api_patch.stop)

    def git(self, *args):
        return subprocess.check_output(["git", *args], text=True, stderr=subprocess.PIPE).strip()

    def commit(self, subject):
        self.git("commit", "-q", "--allow-empty", "-m", subject)
        return self.git("rev-parse", "HEAD")

    @staticmethod
    def release(tag, date, draft=False):
        return {"tag_name": tag, "published_at": date, "draft": draft}

    @staticmethod
    def pull(number, merge, title, branch):
        return {
            "number": number, "merge_commit_sha": merge, "merged_at": "2026-11-08T00:00:00Z",
            "title": title, "html_url": f"https://github.com/test/repo/pull/{number}",
            "head": {"ref": branch},
            "body": f"## Summary\n\n{title}\n\n## Verification\n\nDo not include this verification.",
        }

    def api(self, endpoint):
        if "/releases?" in endpoint:
            return self.releases
        sha = endpoint.split("/commits/")[1].split("/")[0]
        return [pull for pull in self.pulls if pull["merge_commit_sha"] == sha]

    def data(self):
        return notes.inventory("test/repo", "v0.2.2", self.releases)

    @staticmethod
    def response(**sections):
        return json.dumps({
            "summary": [{"text": "Bluestone improves routing.", "sources": ["PR#1"]}],
            "highlights": [{"text": "Added routing.", "sources": ["PR#1"]}],
            **sections,
        })

    def test_range_starts_at_published_release_not_intermediate_tag(self):
        data = self.data()
        self.assertEqual("v0.2.0", data["base"])
        self.assertEqual(2, data["total"])
        self.assertEqual([1, 2], [pull["number"] for pull in data["pulls"]])
        self.assertEqual(["v0.2.1", "v0.2.2"], [pull["version"] for pull in data["pulls"]])
        self.assertEqual([self.first, self.head], [commit["sha"] for commit in data["commits"]])

    def test_drafts_do_not_change_baseline(self):
        self.releases.insert(0, self.release("v0.2.1", None, draft=True))
        self.assertEqual("v0.2.0", self.data()["base"])

    def test_prereleases_are_valid_baselines(self):
        self.releases[0]["prerelease"] = True
        self.assertEqual("v0.2.0", self.data()["base"])

    def test_lightweight_and_annotated_tags_both_map_to_versions(self):
        self.git("tag", "-d", "v0.2.2")
        self.git("tag", "v0.2.2")
        self.assertEqual(["v0.2.1", "v0.2.2"], [pull["version"] for pull in self.data()["pulls"]])

    def test_newer_release_outside_target_history_is_ignored(self):
        self.git("switch", "-q", "-c", "other", "v0.2.0")
        self.commit("Different branch")
        self.git("tag", "v9.0.0")
        self.git("switch", "-q", "main")
        self.releases.insert(0, self.release("v9.0.0", "2026-11-09T00:00:00Z"))
        self.assertEqual("v0.2.0", self.data()["base"])

    def test_releasing_an_older_tag_does_not_compare_against_a_newer_release(self):
        self.releases.insert(0, self.release("v0.2.2", "2026-11-08T00:00:00Z"))
        data = notes.inventory("test/repo", "v0.2.1", self.releases)
        self.assertEqual("v0.2.0", data["base"])
        self.assertEqual(1, data["total"])

    def test_first_release_includes_all_history_without_comparison(self):
        data = notes.inventory("test/repo", "v0.2.2", [])
        self.assertIsNone(data["base"])
        self.assertEqual(3, data["total"])
        rendered = notes.render(data, notes.fallback(data), False)
        self.assertIn("First published release.", rendered)
        self.assertNotIn("/compare/", rendered)
        self.assertIn("/commits/v0.2.2", rendered)

    def test_existing_release_preserves_files_and_skips_generation(self):
        self.releases.append(self.release("v0.2.2", "2026-11-08T00:00:00Z"))
        self.directory.mkdir()
        original = self.directory / "release-notes.md"
        original.write_text("Hand-edited notes")
        with patch.object(notes, "inventory") as collect, patch.object(notes, "output") as output:
            notes.prepare("test/repo", "v0.2.2", self.directory)
        collect.assert_not_called()
        output.assert_called_once_with("existing", "true")
        self.assertEqual("Hand-edited notes", original.read_text())

    def test_existing_notes_are_regenerated_only_when_explicitly_requested(self):
        self.releases.append(self.release("v0.2.2", "2026-11-08T00:00:00Z"))
        notes.prepare("test/repo", "v0.2.2", self.directory, regenerate=True)
        data = json.loads((self.directory / "data.json").read_text())
        self.assertEqual("v0.2.0", data["base"])
        self.assertEqual(2, data["total"])

    def test_pulls_are_deduplicated_and_future_merges_are_excluded(self):
        extra = self.pull(3, "a" * 40, "Future change", "feature/future")
        with patch.object(notes, "api_pages", return_value=self.pulls + [extra]):
            data = self.data()
        self.assertEqual([1, 2], [pull["number"] for pull in data["pulls"]])

    def test_nested_squashed_pull_is_included_with_its_containing_merge(self):
        self.git("switch", "-q", "-c", "feature")
        inner = self.commit("Squash nested pull request #3")
        self.git("switch", "-q", "main")
        self.git("merge", "-q", "--no-ff", "feature", "-m", "Merge pull request #4")
        outer = self.git("rev-parse", "HEAD")
        self.git("tag", "-a", "v0.2.3", "-m", "Nested release")
        self.pulls += [
            self.pull(3, inner, "Nested feature", "feature/nested"),
            self.pull(4, outer, "Combined feature", "feature"),
        ]
        self.assertNotIn(inner, self.git("rev-list", "--first-parent", "v0.2.0..v0.2.3").splitlines())
        data = notes.inventory("test/repo", "v0.2.3", self.releases)
        self.assertEqual([1, 2, 3, 4], [pull["number"] for pull in data["pulls"]])
        self.assertIn("PR#3", data["sources"])
        self.assertEqual(4, data["total"])
        self.assertEqual(1, data["merges"])

    def test_missing_published_tag_is_an_error_not_a_silent_empty_range(self):
        self.releases.insert(0, self.release("v8.0.0", "2026-11-09T00:00:00Z"))
        with self.assertRaises(notes.NotesError):
            self.data()

    def test_github_error_is_not_converted_to_empty_history(self):
        with patch.object(notes, "api_pages", side_effect=notes.NotesError("API permission denied")):
            with self.assertRaisesRegex(notes.NotesError, "permission denied"):
                notes.prepare("test/repo", "v0.2.2", self.directory)
        self.assertFalse(self.directory.exists())

    def test_api_pages_rejects_unexpected_shape(self):
        self.api_patch.stop()
        with patch.object(notes, "command", return_value='{"message":"bad response"}'):
            with self.assertRaises(notes.NotesError):
                notes.api_pages("repos/test/repo/releases")

    def test_summary_prefers_release_notes_and_omits_verification_comments(self):
        body = "## Summary\n\nImplementation detail\n\n## Release notes\n\n<!-- guidance -->\nUser change\n\n## Verification\n\nTests"
        self.assertEqual("User change", notes.pull_summary(body))
        self.assertEqual("Implementation detail", notes.pull_summary(body.replace("User change", "")))
        self.assertEqual("Simple change", notes.pull_summary("Simple change\n\nOther details"))

    def test_prompt_contains_all_changes_but_no_verification(self):
        notes.prepare("test/repo", "v0.2.2", self.directory)
        prompt = (self.directory / "prompt.txt").read_text()
        self.assertIn("PR#1", prompt)
        self.assertIn("PR#2", prompt)
        self.assertIn(f"commit:{self.head[:7]}", prompt)
        self.assertNotIn("Do not include this verification", prompt)
        self.assertIn("untrusted data", prompt)

    def test_context_budget_skips_ai_without_losing_history(self):
        with patch.object(notes, "MAX_PROMPT_BYTES", 1), patch.object(notes, "output") as output, patch("sys.stderr", new_callable=io.StringIO) as errors:
            notes.prepare("test/repo", "v0.2.2", self.directory)
        self.assertIn("::warning::", errors.getvalue())
        self.assertIn(unittest.mock.call("summarize", "false"), output.call_args_list)
        self.assertFalse((self.directory / "prompt.txt").exists())
        data = json.loads((self.directory / "data.json").read_text())
        self.assertEqual(2, len(data["commits"]))
        self.assertEqual(2, len(data["pulls"]))

    def test_valid_narrative_uses_known_sources_and_fixed_sections(self):
        data = self.data()
        response = self.response(features=[{"text": "Added routing.", "sources": ["PR#1", "PR#1"]}])
        prose = notes.narrative(response, data["sources"])
        self.assertEqual(("PR#1",), prose["features"][0].sources)
        rendered = notes.render(data, prose, True)
        self.assertIn("## New features", rendered)
        self.assertIn("Added routing.", rendered)
        self.assertIn("[#1](https://github.com/test/repo/pull/1)", rendered)
        self.assertIn("## Highlights", rendered)
        self.assertNotIn("Narrative summary unavailable", rendered)
        self.assertIn("| v0.2.1 | [#1]", rendered)
        self.assertIn("Fix notes \\| releases", rendered)
        self.assertEqual(2, rendered.count("/commit/"))

    def test_fenced_json_is_accepted(self):
        response = self.response(fixes=[{"text": "Fixed notes.", "sources": ["PR#2"]}])
        prose = notes.narrative(f"```json\n{response}\n```", self.data()["sources"])
        self.assertEqual("Fixed notes.", prose["fixes"][0].text)

    def test_commit_citations_link_to_the_owning_pull_request(self):
        data = self.data()
        response = self.response(fixes=[{"text": "Fixed releases.", "sources": [f"commit:{self.head[:7]}", "PR#2"]}])
        rendered = notes.render(data, notes.narrative(response, data["sources"]), True)
        self.assertIn("Fixed releases. ([#2](https://github.com/test/repo/pull/2))", rendered)
        self.assertNotIn(f"[commit:{self.head[:7]}]", rendered)

    def test_verified_citation_labels_urls_and_full_hashes_are_canonicalized(self):
        data = self.data()
        for reference in ("#2", "PR 2", "PR #2", self.pulls[1]["html_url"], self.head[:7], f"commit:{self.head}"):
            with self.subTest(reference=reference):
                prose = notes.narrative(
                    self.response(fixes=[{"text": "Fixed releases.", "sources": [reference]}]),
                    data["sources"],
                )
                key = prose["fixes"][0].sources[0]
                self.assertIn(key, ("PR#2", f"commit:{self.head[:7]}"))

    def test_ambiguous_citation_labels_are_rejected(self):
        data = self.data()
        data["sources"]["PR#1"]["label"] = "shared"
        data["sources"]["PR#2"]["label"] = "shared"
        with self.assertRaisesRegex(notes.NotesError, "unknown or ambiguous"):
            notes.narrative(self.response(fixes=[{"text": "Fixed notes.", "sources": ["shared"]}]), data["sources"])

    def test_one_correction_can_recover_invalid_ai_without_losing_history(self):
        notes.prepare("test/repo", "v0.2.2", self.directory)
        (self.directory / "summary.json").write_text(self.response(fixes=[{"text": "Bad citation.", "sources": ["PR#999"]}]))
        corrected = self.response(fixes=[{"text": "Fixed notes.", "sources": ["PR#2"]}])
        with patch.object(notes.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, corrected, "")) as run, patch("sys.stderr", new_callable=io.StringIO):
            notes.compose(self.directory, repair=True)
        run.assert_called_once()
        self.assertEqual(90, run.call_args.kwargs["timeout"])
        self.assertIn("PR#999", run.call_args.kwargs["input"])
        self.assertIn("--deny-tool", run.call_args.args[0])
        rendered = (self.directory / "release-notes.md").read_text()
        self.assertIn("Fixed notes.", rendered)
        self.assertNotIn("Narrative summary unavailable", rendered)
        self.assertEqual(2, rendered.count("/commit/"))

    def test_failed_or_invalid_correction_is_bounded_and_falls_back(self):
        notes.prepare("test/repo", "v0.2.2", self.directory)
        (self.directory / "summary.json").write_text("{}")
        for result in (subprocess.CompletedProcess([], 0, "{}", ""), subprocess.CompletedProcess([], 1, "", "unavailable")):
            with self.subTest(result=result), patch.object(notes.subprocess, "run", return_value=result) as run, patch("sys.stderr", new_callable=io.StringIO):
                notes.compose(self.directory, repair=True)
                run.assert_called_once()
                self.assertIn("Narrative summary unavailable", (self.directory / "release-notes.md").read_text())
        with patch.object(notes.subprocess, "run", side_effect=subprocess.TimeoutExpired("copilot", 90)) as run, patch("sys.stderr", new_callable=io.StringIO):
            notes.compose(self.directory, repair=True)
            run.assert_called_once()
            self.assertIn("Narrative summary unavailable", (self.directory / "release-notes.md").read_text())

    def test_missing_ai_does_not_trigger_a_correction(self):
        notes.prepare("test/repo", "v0.2.2", self.directory)
        with patch.object(notes.subprocess, "run") as run, patch("sys.stderr", new_callable=io.StringIO):
            notes.compose(self.directory, repair=True)
        run.assert_not_called()

    def test_invalid_narratives_are_rejected(self):
        cases = [
            "", "{}", '{"features":[]}', '{"invented":[]}',
            '{"features":"text"}',
            '{"features":[{"text":"","sources":["PR#1"]}]}',
            '{"features":[{"text":"Claim.","sources":["PR#999"]}]}',
            '{"features":[{"text":"Claim.","sources":[]}]}',
            '{"features":[{"text":"Claim.","sources":"PR#1"}]}',
            '{"features":[{"text":"Claim.","sources":["PR#1"],"extra":true}]}',
            '{"features":[{"text":"See https://example.com","sources":["PR#1"]}]}',
            '{"features":[{"text":"<script>bad</script>","sources":["PR#1"]}]}',
            '{"features":[{"text":"Line one\\nLine two","sources":["PR#1"]}]}',
            '{"features":[{"text":"unfinished","sources":["PR#1"]}',
        ]
        for response in cases:
            with self.subTest(response=response):
                with self.assertRaises((notes.NotesError, json.JSONDecodeError)):
                    notes.narrative(response, self.data()["sources"])

    def test_missing_or_invalid_ai_produces_complete_fallback_and_warning(self):
        notes.prepare("test/repo", "v0.2.2", self.directory)
        for response in (None, '{"features":[{"text":"Invented.","sources":["PR#999"]}]}'):
            with self.subTest(response=response):
                if response is not None:
                    (self.directory / "summary.json").write_text(response)
                summary = self.directory / "job-summary.md"
                with patch.dict(os.environ, {"GITHUB_STEP_SUMMARY": str(summary)}), patch("sys.stderr", new_callable=io.StringIO) as errors:
                    notes.compose(self.directory)
                rendered = (self.directory / "release-notes.md").read_text()
                self.assertIn("::warning::", errors.getvalue())
                self.assertIn("Release notes warning", summary.read_text())
                self.assertIn("Narrative summary unavailable", rendered)
                self.assertIn("## New features", rendered)
                self.assertIn("## Improvements and fixes", rendered)
                self.assertNotIn("Invented.", rendered)
                self.assertEqual(2, rendered.count("/commit/"))
                self.assertIn("/compare/v0.2.0...v0.2.2", rendered)

    def test_output_limit_is_an_error_and_history_is_not_truncated(self):
        data = self.data()
        with patch.object(notes, "MAX_NOTES_CHARACTERS", 20):
            with self.assertRaisesRegex(notes.NotesError, "not truncated"):
                notes.render(data, notes.fallback(data), False)

    def test_oversized_ai_falls_back_when_deterministic_notes_fit(self):
        notes.prepare("test/repo", "v0.2.2", self.directory)
        data = json.loads((self.directory / "data.json").read_text())
        fallback = notes.render(data, notes.fallback(data), False)
        response = self.response(features=[{"text": "x" * 2000, "sources": ["PR#1"]}])
        (self.directory / "summary.json").write_text(response)
        with patch.object(notes, "MAX_NOTES_CHARACTERS", len(fallback)), patch("sys.stderr", new_callable=io.StringIO) as errors:
            notes.compose(self.directory)
        self.assertIn("Copilot narrative rejected", errors.getvalue())
        self.assertEqual(fallback, (self.directory / "release-notes.md").read_text())

    def test_invalid_repository_and_tag_are_errors(self):
        with self.assertRaises(notes.NotesError):
            notes.prepare("../not-a-repository", "v0.2.2", self.directory)
        with self.assertRaises(notes.NotesError):
            notes.prepare("test/repo", "missing-tag", self.directory)


if __name__ == "__main__":
    unittest.main()
