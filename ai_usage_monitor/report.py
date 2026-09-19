"""The usage report behind File > Save to Log and File > Print Report.

One report model, three renderings - on-screen HTML, CSV for a spreadsheet,
and Markdown for a text file - so the window you read, the file you save and
the page you print can never disagree about the numbers.

The report is built from the snapshots the dashboard is already showing. It
never fetches anything of its own: a report is a record of what was on screen,
and a second round of network calls would quietly produce a document that
differs from the window it was printed from.

The value column follows the dashboard's metric rather than assuming tokens.
`Total tokens` is the default and the usual case, but a report printed while
the window is showing `Equivalent value` says so in its heading instead of
labelling dollars as tokens.
"""

from __future__ import annotations

import csv
import datetime as dt
import io
from dataclasses import dataclass, field
from html import escape

from . import fonts
from .detection import STATE_WORDS
from .providers import Provider, ProviderSnapshot

MONEY_METRIC = "Equivalent value"


def _value(value: float, metric: str) -> str:
    if metric == MONEY_METRIC:
        return f"${value:,.2f}"
    return f"{value:,.0f}"


@dataclass
class ReportRow:
    day: dt.date
    value: float
    per_model: dict[str, float] = field(default_factory=dict)


@dataclass
class ReportSection:
    """One service: who is signed in, on what plan, and what it used."""

    provider_id: str
    provider_name: str
    account: str = ""          # "Apichart Chantanis - Max plan"
    source: str = ""           # "Claude Code login"
    status: str = ""           # "Connected" / "Expired" / ...
    note: str = ""             # why there are no rows, when there are none
    rows: list[ReportRow] = field(default_factory=list)

    @property
    def total(self) -> float:
        return sum(row.value for row in self.rows)

    @property
    def active_days(self) -> int:
        return sum(1 for row in self.rows if row.value)


@dataclass
class Report:
    generated_at: dt.datetime
    metric: str
    days: int
    sections: list[ReportSection] = field(default_factory=list)

    @property
    def value_heading(self) -> str:
        return self.metric


def build(
    providers: list[Provider],
    snapshots: dict[str, ProviderSnapshot],
    detections: dict[str, object],
    days: int,
    metric: str,
    generated_at: dt.datetime | None = None,
) -> Report:
    """Fold the current snapshots into one report, in tab order."""
    report = Report(
        generated_at=generated_at or dt.datetime.now(dt.timezone.utc),
        metric=metric,
        days=days,
    )
    for provider in providers:
        snapshot = snapshots.get(provider.id)
        # The snapshot's own detection was taken during that fetch, so prefer
        # it over the page's copy; the page's copy is what a service with no
        # snapshot at all still has to go on.
        found = getattr(snapshot, "detection", None) or detections.get(provider.id)
        section = ReportSection(
            provider_id=provider.id,
            provider_name=provider.display_name,
        )
        if found is not None:
            section.account = getattr(found, "account", "") or ""
            section.source = getattr(found, "source_label", "") or ""
            section.status = STATE_WORDS.get(getattr(found, "state", ""), "")

        if snapshot is None:
            section.note = "Not monitored."
            report.sections.append(section)
            continue

        # The provider's own account line is richer than the detection's when
        # it has one - it carries the plan and rate-limit tier, which is
        # exactly what this report is asked to show.
        if snapshot.account:
            section.account = snapshot.account

        if snapshot.error:
            section.note = snapshot.error
        elif not snapshot.configured:
            section.note = "Not connected."

        history = snapshot.history
        if history is not None and history.buckets:
            for bucket in history.buckets:
                section.rows.append(
                    ReportRow(
                        day=bucket.day,
                        value=float(bucket.total),
                        per_model=dict(bucket.per_model),
                    )
                )
            section.rows.sort(key=lambda row: row.day)

        # A history problem is worth saying in a log whose whole subject is
        # the history, even when rows did arrive: partial totals that look
        # complete are the kind of thing someone quotes in a report later.
        if snapshot.history_error and not section.note:
            section.note = snapshot.history_error
        elif not section.rows and not section.note:
            section.note = "This service reports no daily history."
        report.sections.append(section)
    return report


# -- renderings ---------------------------------------------------------

def _subtitle(section: ReportSection) -> str:
    """'Max plan - Claude Code login - Connected', skipping what is missing."""
    parts = (section.account, section.source, section.status)
    return "  ·  ".join(part for part in parts if part)


def to_markdown(report: Report) -> str:
    when = report.generated_at.astimezone().strftime("%Y-%m-%d %H:%M")
    out = [
        "# AI Usage Monitor - usage log",
        "",
        f"Generated {when}  ·  last {report.days} days  ·  {report.metric}",
        "",
    ]
    for section in report.sections:
        out.append(f"## {section.provider_name}")
        subtitle = _subtitle(section)
        if subtitle:
            out += ["", subtitle]
        if section.note:
            out += ["", f"_{section.note}_"]
        if section.rows:
            out += ["", f"| Date | {report.value_heading} |", "| --- | ---: |"]
            for row in section.rows:
                value = _value(row.value, report.metric)
                out.append(f"| {row.day.isoformat()} | {value} |")
            total = _value(section.total, report.metric)
            out.append(f"| **Total** | **{total}** |")
        out.append("")
    return "\n".join(out).rstrip() + "\n"


def to_csv(report: Report) -> str:
    """One flat table, because that is what a spreadsheet can actually use."""
    buffer = io.StringIO(newline="")
    writer = csv.writer(buffer, lineterminator="\n")
    writer.writerow(
        ["date", "provider", "account", "source", "status", "metric", "value"]
    )
    for section in report.sections:
        for row in section.rows:
            writer.writerow([
                row.day.isoformat(),
                section.provider_name,
                section.account,
                section.source,
                section.status,
                report.metric,
                f"{row.value:.2f}" if report.metric == MONEY_METRIC
                else f"{row.value:.0f}",
            ])
    return buffer.getvalue()


def to_html(report: Report, dark: bool = False) -> str:
    """The on-screen view and the printed page.

    `dark` only ever applies on screen; printing passes the light palette, so
    a dark-themed window still prints black on white instead of pouring ink
    over the page.
    """
    ink = "#e8e8ea" if dark else "#1b1b1f"
    muted = "#9a9aa2" if dark else "#63636b"
    rule = "#3a3a42" if dark else "#d9d9e0"
    # Qt's rich-text engine colours table cells from the palette rather than
    # from `body`, which came out maroon against both themes. Every cell says
    # what colour it is.
    cell = f"border-top:1px solid {rule}; color:{ink};"
    when = escape(report.generated_at.astimezone().strftime("%Y-%m-%d %H:%M"))

    out = [
        f"<body style='color:{ink}; font-family:{fonts.css_family()};'>",
        "<h1 style='font-size:19px; margin:0 0 2px 0;'>"
        "AI Usage Monitor &mdash; usage log</h1>",
        f"<p style='color:{muted}; font-size:11px; margin:0 0 18px 0;'>"
        f"Generated {when} &nbsp;&middot;&nbsp; last {report.days} days"
        f" &nbsp;&middot;&nbsp; {escape(report.metric)}</p>",
    ]
    for section in report.sections:
        out.append(
            "<h2 style='font-size:15px; margin:18px 0 2px 0;'>"
            f"{escape(section.provider_name)}</h2>"
        )
        subtitle = _subtitle(section)
        if subtitle:
            out.append(
                f"<p style='color:{muted}; font-size:11px; margin:0 0 8px 0;'>"
                f"{escape(subtitle)}</p>"
            )
        if section.note:
            out.append(
                f"<p style='color:{muted}; font-size:12px; margin:0 0 8px 0;'>"
                f"{escape(section.note)}</p>"
            )
        if not section.rows:
            continue
        out.append(
            "<table cellspacing='0' cellpadding='4' width='100%' "
            f"style='font-size:12px; border-top:1px solid {rule};'>"
            f"<tr><th align='left' style='color:{muted};'>Date</th>"
            f"<th align='right' style='color:{muted};'>"
            f"{escape(report.value_heading)}</th></tr>"
        )
        for row in section.rows:
            out.append(
                f"<tr><td style='{cell}'>{row.day.strftime('%Y-%m-%d (%a)')}</td>"
                f"<td align='right' style='{cell}'>"
                f"{_value(row.value, report.metric)}</td></tr>"
            )
        out.append(
            f"<tr><td style='{cell} font-weight:600;'>Total "
            f"<span style='color:{muted}; font-weight:400;'>"
            f"({section.active_days} active of {len(section.rows)} days)"
            "</span></td>"
            f"<td align='right' style='{cell} font-weight:600;'>"
            f"{_value(section.total, report.metric)}</td></tr>"
            "</table>"
        )
    out.append("</body>")
    return "\n".join(out)
