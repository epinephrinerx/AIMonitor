"""Number, duration and date formatting shared by the widgets."""

from __future__ import annotations

import datetime as dt


def compact(value: float) -> str:
    """1,284 / 12.9K / 4.2M - the stat-tile number format."""
    magnitude = abs(value)
    if magnitude >= 1_000_000_000:
        return f"{value / 1_000_000_000:.1f}B".replace(".0B", "B")
    if magnitude >= 1_000_000:
        return f"{value / 1_000_000:.1f}M".replace(".0M", "M")
    if magnitude >= 10_000:
        return f"{value / 1_000:.1f}K".replace(".0K", "K")
    return f"{value:,.0f}"


def money(value: float) -> str:
    if value and abs(value) < 0.01:
        return "<$0.01"
    if abs(value) >= 1000:
        return f"${compact(value)}"
    return f"${value:,.2f}"


def axis_tick(value: float, metric: str) -> str:
    if metric == "Equivalent value":
        if value >= 1000:
            return f"${value / 1000:.0f}K"
        if value >= 10:
            return f"${value:,.0f}"
        return f"${value:,.2f}".rstrip("0").rstrip(".")
    return compact(value)


def metric_label(value: float, metric: str) -> str:
    return money(value) if metric == "Equivalent value" else compact(value)


def duration(delta: dt.timedelta) -> str:
    """Coarse countdown: '2d 8h', '1h 12m', '4m', 'under a minute'."""
    seconds = int(delta.total_seconds())
    if seconds <= 0:
        return "now"
    days, seconds = divmod(seconds, 86_400)
    hours, seconds = divmod(seconds, 3_600)
    minutes = seconds // 60
    if days:
        return f"{days}d {hours}h" if hours else f"{days}d"
    if hours:
        return f"{hours}h {minutes}m" if minutes else f"{hours}h"
    if minutes:
        return f"{minutes}m"
    return "under a minute"


def local_time(moment: dt.datetime) -> str:
    """'17:50' today, otherwise 'Sep 11, 08:00'."""
    local = moment.astimezone()
    if local.date() == dt.datetime.now().astimezone().date():
        return local.strftime("%H:%M")
    if _SUPPORTS_DASH:
        return local.strftime("%b %-d, %H:%M")
    return local.strftime("%b %d, %H:%M").replace(" 0", " ")


def clock(moment: dt.datetime) -> str:
    return moment.astimezone().strftime("%H:%M:%S")


def day_label(day: dt.date) -> str:
    return day.strftime("%b %d").replace(" 0", " ")


def _detect_dash_support() -> bool:
    """`%-d` is glibc-only; Windows uses `%#d`, so detect once and fall back."""
    try:
        return dt.datetime(2020, 1, 5).strftime("%-d") == "5"
    except ValueError:
        return False


_SUPPORTS_DASH = _detect_dash_support()
