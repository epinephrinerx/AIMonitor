"""Regression: the parity report pairs agy/gemini counterparts, lists 2.0-only and
missing-in-2.0 screens, and preserves owner records (SPEC R-005 V, V3/V10/V12/V14)."""
from pathlib import Path
import json
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]

# Exact strings required by Docs/SPEC_R005_EVIDENCE.md (V3, V3.1, V3.2, V10, V12).
CCUSAGE_REASON = "R-014\u2013R-017 ccusage tabs (owner accepted 2026-11-10)"
FALLBACK_REASON = "No 1.3.3 counterpart; owner decision pending"
SUPPLEMENTARY_REASON = "R-010/R-011 supplementary details"
ONLY_SECTION_TITLE = "2.0 only \u2014 not in 1.3.3, not scored for parity"

# V3.3/V12 text oracles. The three new exception bullets are the exact spec text
# (V3.3); the retained bullets, the 36 functional-area names, the five visual
# criteria and the subcriteria total are the unchanged text of the generated base
# file Docs/PARITY_ACCEPTANCE.md at b913799, which V9 establishes was produced by
# the unmodified generator. INDEX_CRITERIA is that base rendering of the five
# criteria as the index page labels each pair's five scoring selects.
STATUS_NEW_REF = "STATUS_2026-11-11.md"
STATUS_OLD_REF = "STATUS_2026-10-14.md"
NEW_EXCEPTION_BULLETS = (
    "- Gemini tab renamed Gemini / AGY and backed by AGY CLI pools (R-012, R-013; owner accepted 2026-10-16).",
    "- The dashboard tab row adds OpenCode, Overview and All agents (R-014\u2013R-017; owner accepted 2026-11-10); paired screens show the extra tab headers.",
    "- Paired dashboard screens are rendered without ccusage; the ccusage rows that R-014 adds under existing gauges are not in the paired evidence.",
)
BASE_EXCEPTION_BULLETS = (
    "- Optional installer download/run differs from the original read-only update-check contract; owner approval pending.",
    "- App version and bundled documents intentionally differ between releases; owner approval pending.",
    "- Installed experimental 2.0 builds with the old separate AppId need their own migration plan; no silent removal.",
    "- Native tray context menu/tooltip/balloon, real printer/preview, title bars, actual multi-monitor/DPI and lock/crash smoke need a disposable Windows environment.",
    "- Clean install/upgrade must cover startup on/off, no duplicate processes/trays and preservation of legacy settings/credentials. Compiler success does not prove these.",
    "- No installer was executed and no real Run key or real credentials were used during local verification.",
    "- Owner sign-off and final R-004 score have not been recorded.",
)
EXPECTED_EXCEPTION_BULLETS = sorted(BASE_EXCEPTION_BULLETS + NEW_EXCEPTION_BULLETS)
FUNCTIONAL_AREAS = (
    "Provider contract", "Detection", "Refresh", "Partial success", "Claude quota",
    "Claude history", "Pricing", "OpenAI selection", "Codex protocol", "OpenAI API",
    "Gemini", "Credential validity", "No invented data", "Dashboard", "Metric/range",
    "Severity", "Reset", "Dashboard mode", "Widget", "Widget navigation",
    "Geometry", "Tray", "Single instance", "Startup", "Close/exit", "Settings",
    "Preview", "Theme", "Secrets", "Reports", "Export/print", "Menus/shortcuts",
    "Updates", "Documents", "Packaging", "Lifecycle",
)
TOTAL_SUBCRITERIA = 106
TOTAL_LINE = ("Total: **106** subcriteria; accepted: **0 recorded**; result: "
              "**pending**, not 0% or 100%.")
VISUAL_CRITERIA_PROSE = ("the five agreed criteria: row structure, complete elements, "
                         "text, colours/signals and proportions/layout")
INDEX_CRITERIA = ("Row structure", "Complete elements", "Text",
                  "Colours and signals", "Proportions and layout")

PNG_PLACEHOLDER = b"\x89PNG\r\n\x1a\n"
SECTION_RE = re.compile(r"<section\b.*?</section>", re.DOTALL)
IMG_SRC_RE = re.compile(r'<img\b[^>]*\bsrc="([^"]*)"')
FIGURE_RE = re.compile(r"<figure\b.*?</figure>", re.DOTALL)
CAPTION_RE = re.compile(r"<figcaption>(.*?)</figcaption>")
SELECT_RE = re.compile(r"<select\b[^>]*>")
CLASS_ATTR_RE = re.compile(r'\bclass="([^"]*)"')
LABEL_TEXT_RE = re.compile(r"<label>(.*?)<select", re.DOTALL)
PAR_ID_RE = re.compile(r"PAR-(\d+)\.\d+")
# V14: the two figure captions the spec fixes for every pair section.
PYTHON_REFERENCE_CAPTION = "Python reference"
WPF_CAPTION = "C# WPF"


def is_score_select(tag):
    """V14: a scoring control is any <select> whose class attribute contains the
    token 'score' (whitespace-split), matching what the page's '.score' JS selects;
    a compound class such as 'score extra' must therefore still count."""
    match = CLASS_ATTR_RE.search(tag)
    return match is not None and "score" in match.group(1).split()


def count_score_selects(html):
    """Number of scoring controls in an HTML chunk, counted over the raw text."""
    return sum(1 for match in SELECT_RE.finditer(html) if is_score_select(match.group(0)))


def figure_sources(chunk):
    """V14: [(figcaption, img src), ...] for every <figure> in the chunk. The list
    keeps document order, which V14 does not specify; callers key it by caption."""
    figures = []
    for figure in FIGURE_RE.findall(chunk):
        caption = CAPTION_RE.search(figure)
        src = IMG_SRC_RE.search(figure)
        figures.append((caption.group(1) if caption else None,
                        src.group(1) if src else None))
    return figures


class ParityReportTests(unittest.TestCase):
    # --- fixture helpers (pattern of the owner-preservation test below) ---

    def setUp(self):
        (ROOT / "artifacts/verification/test-fixtures").mkdir(parents=True, exist_ok=True)

    def make_fixture(self, temporary, wpf_names, legacy_names, wpf_pngs=(), legacy_pngs=()):
        """Temporary repo holding a copy of the script and synthetic manifests.

        Every manifest screen also gets a placeholder PNG on both sides, plus any
        extra PNGs requested, so an implementation that checks file existence (the
        tray pairs and the -agy- counterpart rule) sees consistent evidence."""
        fixture = Path(temporary).resolve()
        self.assertTrue(fixture.is_relative_to(ROOT))
        (fixture / "scripts").mkdir()
        (fixture / "Docs").mkdir()
        names = {"wpf": sorted(set(wpf_names)), "legacy": sorted(set(legacy_names))}
        pngs = {"wpf": sorted(set(wpf_names) | set(wpf_pngs)), "legacy": sorted(set(legacy_names) | set(legacy_pngs))}
        for renderer in ("wpf", "legacy"):
            directory = fixture / "artifacts/verification" / renderer
            directory.mkdir(parents=True)
            (directory / "manifest.json").write_text(json.dumps(names[renderer]), encoding="utf-8")
            for name in pngs[renderer]:
                (directory / f"{name}.png").write_bytes(PNG_PLACEHOLDER)
        shutil.copyfile(ROOT / "scripts/build_parity_report.py", fixture / "scripts/build_parity_report.py")
        return fixture

    def run_generator(self, fixture):
        return subprocess.run(
            [sys.executable, str(fixture / "scripts/build_parity_report.py")],
            capture_output=True, text=True, cwd=fixture)

    def outputs(self, fixture):
        index = (fixture / "artifacts/verification/index.html").read_text(encoding="utf-8")
        acceptance = (fixture / "Docs/PARITY_ACCEPTANCE.md").read_text(encoding="utf-8")
        return index, acceptance

    def reported_pair_count(self, result, acceptance):
        """Exact pair count from stdout and from the acceptance evidence line."""
        generated = re.search(r"Generated (\d+) visual pair", result.stdout)
        self.assertIsNotNone(generated, f"no 'Generated N visual pairs' in stdout: {result.stdout!r}")
        evidence = re.search(r"Evidence: (\d+) matched", acceptance)
        self.assertIsNotNone(evidence, f"no 'Evidence: N matched' line in acceptance: {acceptance!r}")
        self.assertEqual(generated.group(1), evidence.group(1), "stdout and acceptance pair counts differ")
        return int(generated.group(1))

    @staticmethod
    def markdown_rows(section_text):
        rows = []
        for line in section_text.splitlines():
            if not line.startswith("|"):
                continue
            cells = tuple(cell.strip() for cell in line.strip("|").split("|"))
            if all(cell and set(cell) <= {"-", ":"} for cell in cells):
                continue  # header separator row
            rows.append(cells)
        return rows

    def section_text(self, acceptance, heading):
        match = re.search(rf"^## .*{re.escape(heading)}.*$", acceptance, re.MULTILINE)
        if match is None:
            self.fail(f"missing section '## {heading}' in acceptance document")
        body = acceptance[match.end():]
        stop = re.search(r"^## ", body, re.MULTILINE)
        return body[:stop.start()] if stop else body

    def section_rows(self, acceptance, heading):
        return self.markdown_rows(self.section_text(acceptance, heading))

    def visual_rows(self, acceptance):
        return self.section_rows(acceptance, "Visual screens")

    def only_rows(self, acceptance):
        # The section may be absent when no screen is 2.0-only; absence is caught by
        # the exact-set assertion, which then reports the missing entries.
        match = re.search(r"^## .*2\.0-only screens.*$", acceptance, re.MULTILINE)
        if match is None:
            return []
        rows = self.markdown_rows(self.section_text(acceptance, "2.0-only screens"))
        return [row for row in rows if row[0] != "Screen"]

    def exception_bullets(self, acceptance):
        body = self.section_text(acceptance, "Exceptions and remaining evidence")
        return [line.strip() for line in body.splitlines() if line.startswith("- ")]

    def functional_rows(self, acceptance):
        rows = self.section_rows(acceptance, "Functional subcriteria")
        return [row for row in rows if row[0] != "ID"]

    def visual_pair_rows(self, acceptance):
        """Split the Visual screens table into pair rows and 'No 2.0 render' rows."""
        pair, missing = [], []
        for row in self.visual_rows(acceptance):
            if row[0] == "Screen pair":
                continue
            (missing if "No 2.0 render" in row[2] else pair).append(row)
        return pair, missing

    # --- index.html section parsing (V12/V14: assert inside each pair's <section>
    # and, for the images, inside the captioned <figure>) ---

    def parse_sections(self, index):
        sections = []
        for match in SECTION_RE.finditer(index):
            chunk = match.group(0)
            attrs = dict(re.findall(r'([\w-]+)="([^"]*)"', chunk[:chunk.index(">")]))
            heading = re.search(r"<h2>(.*?)</h2>", chunk)
            sections.append({
                "start": match.start(),
                "id": attrs.get("id"),
                "class": attrs.get("class", ""),
                "title": heading.group(1) if heading else None,
                "srcs": IMG_SRC_RE.findall(chunk),
                "figures": figure_sources(chunk),
                "score_selects": count_score_selects(chunk),
                "labels": LABEL_TEXT_RE.findall(chunk),
                "text": " ".join(re.sub(r"<[^>]+>", " ", chunk).split()),
            })
        return sections

    def split_index_sections(self, index):
        """Index sections before the '2.0 only' heading (pairs) and after it (only)."""
        marker = index.find(ONLY_SECTION_TITLE)
        self.assertNotEqual(marker, -1, f"missing heading {ONLY_SECTION_TITLE!r} in index.html")
        pair, only = [], []
        for section in self.parse_sections(index):
            (only if section["start"] > marker else pair).append(section)
        return pair, only

    def assert_index_score_selects(self, index, pair_count):
        """V12/V14: five scoring controls per pair section, zero in every 2.0-only
        section, and 5 x pairs over the WHOLE page. Controls are recognised by the
        'score' class token (not class equality), and the page-wide count is taken
        over the raw HTML so a control outside every <section> cannot escape."""
        pair, only = self.split_index_sections(index)
        self.assertEqual(len(pair), pair_count, "index pair sections differ from the pair count")
        for section in pair:
            self.assertEqual(section["score_selects"], 5,
                             f"pair {section['title']!r} must carry the five visual criteria")
        for section in only:
            self.assertEqual(section["score_selects"], 0,
                             f"2.0-only {section['title']!r} must not be scored")
        inside = sum(section["score_selects"] for section in pair + only)
        page = count_score_selects(index)
        self.assertEqual(inside, 5 * pair_count, "total select.score count is not 5 x pairs")
        self.assertEqual(page, inside, "scoring controls outside <section> escape the count")
        self.assertEqual(page, 5 * pair_count, "page-wide scoring-control count is not 5 x pairs")

    def index_pair_sections(self, index, expected_labels):
        pair, _ = self.split_index_sections(index)
        self.assertEqual(sorted(section["title"] for section in pair), sorted(expected_labels),
                         "index.html pair sections differ from the exact expected set")
        by_title = {section["title"]: section for section in pair}
        self.assertEqual(len(by_title), len(expected_labels), "duplicate pair label in index.html")
        return by_title

    def assert_index_pair_images(self, by_title, label, wpf_name, legacy_name):
        """V12/V14: the pair's <section> holds exactly its legacy and WPF images, and
        each <img src> sits in its own <figure> identified by caption: 'Python
        reference' -> legacy/<legacy>.png, 'C# WPF' -> wpf/<wpf>.png. V14 leaves the
        order of the two figures unspecified, so it is keyed by caption, not position."""
        section = by_title[label]
        self.assertEqual(sorted(section["srcs"]), sorted([f"legacy/{legacy_name}.png", f"wpf/{wpf_name}.png"]),
                         f"pair {label!r} shows the wrong images")
        by_caption = {}
        for caption, src in section["figures"]:
            self.assertIsNotNone(caption, f"pair {label!r} has a figure without a figcaption")
            self.assertIsNotNone(src, f"pair {label!r}: figure {caption!r} has no img src")
            self.assertNotIn(caption, by_caption, f"pair {label!r} repeats figure caption {caption!r}")
            by_caption[caption] = src
        self.assertEqual(sorted(by_caption), sorted([PYTHON_REFERENCE_CAPTION, WPF_CAPTION]),
                         f"pair {label!r} must hold exactly one figure per caption")
        self.assertEqual(by_caption[PYTHON_REFERENCE_CAPTION], f"legacy/{legacy_name}.png",
                         f"pair {label!r}: the 'Python reference' figure must show the legacy render")
        self.assertEqual(by_caption[WPF_CAPTION], f"wpf/{wpf_name}.png",
                         f"pair {label!r}: the 'C# WPF' figure must show the WPF render")

    def assert_index_only_set(self, index, expected):
        """V12: exact 2.0-only section set; each holds its reason and image, unscoped."""
        _, only = self.split_index_sections(index)
        self.assertEqual(sorted(section["title"] for section in only), sorted(expected),
                         "index.html 2.0-only sections differ from the exact expected set")
        for section in only:
            name = section["title"]
            self.assertEqual(section["srcs"], [f"wpf/{name}.png"],
                             f"2.0-only section {name!r} must show its WPF image")
            self.assertIn(expected[name], section["text"],
                          f"2.0-only section {name!r} must state its reason")

    # --- exact acceptance-document row sets (V10/V12) ---

    def assert_pair_rows(self, acceptance, expected_labels):
        pair, _ = self.visual_pair_rows(acceptance)
        self.assertEqual(sorted(row[0] for row in pair), sorted(expected_labels),
                         "Visual screens pair rows differ from the exact expected set")
        for row in pair:
            self.assertEqual(row[1], "Pending", f"pair {row[0]!r} must start unscored")
            self.assertIn("no owner score", row[2].lower(), f"pair {row[0]!r} note changed")

    def assert_only_row_set(self, acceptance, expected):
        """V12: complete 2.0-only row-name set, exact count, and reasons."""
        rows = self.only_rows(acceptance)
        self.assertEqual(sorted(row[0] for row in rows), sorted(expected),
                         "2.0-only rows differ from the exact expected set")
        for row in rows:
            self.assertEqual(row[1], expected[row[0]], f"wrong reason for {row[0]!r}")

    def assert_legacy_only_set(self, acceptance, expected_names):
        """V12: complete 'No 2.0 render' row set and exact count (zero where none)."""
        _, missing = self.visual_pair_rows(acceptance)
        self.assertEqual(sorted(row[0] for row in missing), sorted(expected_names),
                         "legacy-only 'No 2.0 render' rows differ from the exact expected set")
        for row in missing:
            self.assertEqual(row[1], "Pending", f"untested evidence {row[0]!r} must keep the score Pending")

    # --- V10: -agy- WPF screens pair with -gemini- legacy screens, both themes ---

    def test_agy_counterparts_pair_with_gemini_screens_in_both_themes(self):
        wpf = ["dashboard-agy-light", "dashboard-agy-dark", "connect-agy-light", "connect-agy-dark"]
        legacy = ["dashboard-gemini-light", "dashboard-gemini-dark",
                  "connect-gemini-light", "connect-gemini-dark"]
        pairs = {f"{w} \u2194 {l}": (w, l) for w, l in zip(wpf, legacy)}
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/verification/test-fixtures") as temporary:
            fixture = self.make_fixture(temporary, wpf, legacy)
            result = self.run_generator(fixture)
            self.assertEqual(0, result.returncode, result.stderr)
            index, acceptance = self.outputs(fixture)
            self.assertEqual(self.reported_pair_count(result, acceptance), 4)
            self.assert_pair_rows(acceptance, list(pairs))
            by_title = self.index_pair_sections(index, list(pairs))
            for label, (wpf_name, legacy_name) in pairs.items():
                self.assert_index_pair_images(by_title, label, wpf_name, legacy_name)
            # nothing else is unpaired: the full 2.0-only and legacy-only sets are empty
            self.assert_only_row_set(acceptance, {})
            self.assert_index_only_set(index, {})
            self.assert_legacy_only_set(acceptance, [])
            self.assert_index_score_selects(index, 4)

    # --- V10: identical-name pairing keeps working (regression of base behaviour) ---

    def test_identical_name_screens_still_pair(self):
        names = ["dashboard-claude-light", "dashboard-claude-dark"]
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/verification/test-fixtures") as temporary:
            fixture = self.make_fixture(temporary, names, names)
            result = self.run_generator(fixture)
            self.assertEqual(0, result.returncode, result.stderr)
            index, acceptance = self.outputs(fixture)
            self.assertEqual(self.reported_pair_count(result, acceptance), 2)
            self.assert_pair_rows(acceptance, names)
            self.assertNotIn(" \u2194", acceptance, "identical names must keep the plain label")
            by_title = self.index_pair_sections(index, names)
            for name in names:
                self.assert_index_pair_images(by_title, name, name, name)
            self.assert_only_row_set(acceptance, {})
            self.assert_index_only_set(index, {})
            self.assert_legacy_only_set(acceptance, [])
            self.assert_index_score_selects(index, 2)

    # --- V10: a tray icon pairs only when the PNG exists on both sides ---

    def test_tray_icon_pairs_only_when_both_pngs_exist(self):
        names = ["dashboard-claude-light"]
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/verification/test-fixtures") as temporary:
            fixture = self.make_fixture(
                temporary, names, names,
                wpf_pngs=["tray-light", "tray-dark"], legacy_pngs=["tray-light"])
            result = self.run_generator(fixture)
            self.assertEqual(0, result.returncode, result.stderr)
            index, acceptance = self.outputs(fixture)
            self.assertEqual(self.reported_pair_count(result, acceptance), 2)
            self.assert_pair_rows(acceptance, ["dashboard-claude-light", "tray-light"])
            by_title = self.index_pair_sections(index, ["dashboard-claude-light", "tray-light"])
            self.assert_index_pair_images(by_title, "tray-light", "tray-light", "tray-light")
            self.assert_only_row_set(acceptance, {})
            self.assert_index_only_set(index, {})
            self.assert_legacy_only_set(acceptance, [])
            self.assert_index_score_selects(index, 2)

    # --- V3.1 / V10 / V12: every 2.0-only reason class is listed and never paired ---

    def test_two_only_screens_are_listed_with_reasons_and_never_paired(self):
        wpf = ["dashboard-claude-light",
               "supplementary-report-light",
               "dashboard-opencode-light", "dashboard-overview-dark", "dashboard-all-agents-light",
               "custom-panel-dark"]
        legacy = ["dashboard-claude-light"]
        expected = {
            "supplementary-report-light": SUPPLEMENTARY_REASON,
            "dashboard-opencode-light": CCUSAGE_REASON,
            "dashboard-overview-dark": CCUSAGE_REASON,
            "dashboard-all-agents-light": CCUSAGE_REASON,
            "custom-panel-dark": FALLBACK_REASON,
        }
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/verification/test-fixtures") as temporary:
            fixture = self.make_fixture(temporary, wpf, legacy)
            result = self.run_generator(fixture)
            self.assertEqual(0, result.returncode, result.stderr)
            index, acceptance = self.outputs(fixture)
            self.assertEqual(self.reported_pair_count(result, acceptance), 1)
            self.assert_pair_rows(acceptance, ["dashboard-claude-light"])
            by_title = self.index_pair_sections(index, ["dashboard-claude-light"])
            self.assert_index_pair_images(by_title, "dashboard-claude-light",
                                          "dashboard-claude-light", "dashboard-claude-light")
            # V12: complete sets in both outputs, exact reasons, images and no scoring
            self.assert_only_row_set(acceptance, expected)
            self.assert_index_only_set(index, expected)
            self.assert_legacy_only_set(acceptance, [])
            self.assert_index_score_selects(index, 1)

    # --- V3.2 / V10 / V12: a legacy screen without a WPF counterpart is untested ---

    def test_legacy_only_screen_is_listed_as_no_2_0_render(self):
        wpf = ["dashboard-claude-light"]
        legacy = ["dashboard-claude-light", "dashboard-gemini-dark"]
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/verification/test-fixtures") as temporary:
            fixture = self.make_fixture(temporary, wpf, legacy)
            result = self.run_generator(fixture)
            self.assertEqual(0, result.returncode, result.stderr)
            index, acceptance = self.outputs(fixture)
            self.assertEqual(self.reported_pair_count(result, acceptance), 1)
            self.assert_pair_rows(acceptance, ["dashboard-claude-light"])
            self.assert_legacy_only_set(acceptance, ["dashboard-gemini-dark"])
            self.assert_only_row_set(acceptance, {})
            self.assert_index_only_set(index, {})
            self.assert_index_score_selects(index, 1)

    # --- V10: identical matches win; a legacy screen is consumed by at most one pair ---

    def test_identical_match_is_taken_before_counterpart_and_legacy_screen_used_once(self):
        wpf = ["dashboard-gemini-light", "dashboard-agy-light"]
        legacy = ["dashboard-gemini-light"]
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/verification/test-fixtures") as temporary:
            fixture = self.make_fixture(temporary, wpf, legacy)
            result = self.run_generator(fixture)
            self.assertEqual(0, result.returncode, result.stderr)
            index, acceptance = self.outputs(fixture)
            self.assertEqual(self.reported_pair_count(result, acceptance), 1)
            self.assert_pair_rows(acceptance, ["dashboard-gemini-light"])
            by_title = self.index_pair_sections(index, ["dashboard-gemini-light"])
            self.assert_index_pair_images(by_title, "dashboard-gemini-light",
                                          "dashboard-gemini-light", "dashboard-gemini-light")
            # The unmatched agy screen is 2.0-only with the fallback reason, not a pair.
            self.assert_only_row_set(acceptance, {"dashboard-agy-light": FALLBACK_REASON})
            self.assert_index_only_set(index, {"dashboard-agy-light": FALLBACK_REASON})
            self.assert_legacy_only_set(acceptance, [])
            self.assert_index_score_selects(index, 1)

    # --- V3.3 / V12: generated text keeps the STATUS reference, bullets, 36 areas ---

    def test_generated_text_keeps_status_reference_bullets_areas_and_criteria(self):
        wpf = ["dashboard-claude-light", "supplementary-report-light"]
        legacy = ["dashboard-claude-light"]
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/verification/test-fixtures") as temporary:
            fixture = self.make_fixture(temporary, wpf, legacy)
            result = self.run_generator(fixture)
            self.assertEqual(0, result.returncode, result.stderr)
            index, acceptance = self.outputs(fixture)
            self.assertEqual(self.reported_pair_count(result, acceptance), 1)

            # V3.3: the new STATUS file is referenced and the old one is gone.
            self.assertIn(STATUS_NEW_REF, acceptance)
            self.assertNotIn(STATUS_OLD_REF, acceptance)

            # V3.3: the three added bullets plus all retained bullets, each once.
            self.assertEqual(sorted(self.exception_bullets(acceptance)), EXPECTED_EXCEPTION_BULLETS,
                             "exception bullets differ from the retained set plus the three V3.3 bullets")

            # V3.3: exactly 36 functional areas (PAR-001..036) and 106 subcriteria.
            rows = self.functional_rows(acceptance)
            self.assertEqual(len(rows), TOTAL_SUBCRITERIA, "wrong number of functional subcriteria")
            areas_by_id = {}
            for row in rows:
                match = PAR_ID_RE.fullmatch(row[0])
                self.assertIsNotNone(match, f"bad functional id {row[0]!r}")
                self.assertEqual(row[3], "Pending", f"{row[0]} must start unassessed")
                areas_by_id.setdefault(int(match.group(1)), set()).add(row[1])
            self.assertEqual(sorted(areas_by_id), list(range(1, 37)), "functional area numbering changed")
            for number, area in enumerate(FUNCTIONAL_AREAS, 1):
                self.assertEqual(areas_by_id[number], {area}, f"functional area {number} changed")
            self.assertIn(TOTAL_LINE, acceptance, "subcriteria total line changed")

            # V3.3: the five visual criteria, both in the document prose and as the
            # five scoring labels of the pair's index section (V12 count: 5 x pairs).
            self.assertIn(VISUAL_CRITERIA_PROSE, " ".join(acceptance.split()))
            by_title = self.index_pair_sections(index, ["dashboard-claude-light"])
            self.assertEqual(sorted(by_title["dashboard-claude-light"]["labels"]), sorted(INDEX_CRITERIA),
                             "the pair section must label its selects with the five visual criteria")
            self.assert_index_score_selects(index, 1)
            self.assert_only_row_set(acceptance, {"supplementary-report-light": SUPPLEMENTARY_REASON})
            self.assert_index_only_set(index, {"supplementary-report-light": SUPPLEMENTARY_REASON})
            self.assert_legacy_only_set(acceptance, [])

    # --- V4/V9: the owner-score preservation guard keeps working unchanged ---

    def test_regeneration_preserves_owner_results_and_notes(self):
        scratch = ROOT / "artifacts/verification/test-fixtures"
        scratch.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temporary:
            fixture = Path(temporary).resolve()
            self.assertTrue(fixture.is_relative_to(ROOT))
            (fixture / "scripts").mkdir()
            (fixture / "Docs").mkdir()
            for renderer in ("wpf", "legacy"):
                directory = fixture / "artifacts/verification" / renderer
                directory.mkdir(parents=True)
                (directory / "manifest.json").write_text('["fixture-light"]', encoding="utf-8")
            script = fixture / "scripts/build_parity_report.py"
            shutil.copyfile(ROOT / "scripts/build_parity_report.py", script)
            first = subprocess.run([sys.executable, str(script)], capture_output=True, text=True, cwd=fixture)
            self.assertEqual(0, first.returncode, first.stderr)
            acceptance = fixture / "Docs/PARITY_ACCEPTANCE.md"
            owner_text = acceptance.read_text(encoding="utf-8").replace("| Pending |", "| Pass |", 1)
            owner_text += "\nOwner exception: approved version label.\n"
            acceptance.write_text(owner_text, encoding="utf-8")
            second = subprocess.run([sys.executable, str(script)], capture_output=True, text=True, cwd=fixture)
            self.assertNotEqual(0, second.returncode)
            self.assertIn("preserve owner scores", second.stderr)
            self.assertEqual(owner_text, acceptance.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
