"""Per-token list prices, used to value local token usage.

On a Max or Pro subscription nothing here is actually billed - the figures are
an "equivalent API value", i.e. what the same traffic would have cost at list
price on the Claude API. Every surface that shows a dollar amount labels it that
way, so it is never mistaken for a charge.

Rates are USD per million tokens. Cache multipliers follow the published model:
a 5-minute cache write costs 1.25x the input rate, a 1-hour write 2x, and a
cache read 0.1x.
"""

from __future__ import annotations

from dataclasses import dataclass

CACHE_WRITE_5M_MULTIPLIER = 1.25
CACHE_WRITE_1H_MULTIPLIER = 2.0
CACHE_READ_MULTIPLIER = 0.1

MILLION = 1_000_000


@dataclass(frozen=True)
class Rate:
    """USD per million tokens."""

    input: float
    output: float
    display_name: str

    @property
    def cache_write_5m(self) -> float:
        return self.input * CACHE_WRITE_5M_MULTIPLIER

    @property
    def cache_write_1h(self) -> float:
        return self.input * CACHE_WRITE_1H_MULTIPLIER

    @property
    def cache_read(self) -> float:
        return self.input * CACHE_READ_MULTIPLIER


# Exact model ids first; anything else falls back to the prefix table below.
RATES: dict[str, Rate] = {
    "claude-fable-5-1": Rate(10.00, 50.00, "Fable 5.1"),
    "claude-fable-5": Rate(10.00, 50.00, "Fable 5"),
    "claude-mythos-5-1": Rate(10.00, 50.00, "Mythos 5.1"),
    "claude-mythos-5": Rate(10.00, 50.00, "Mythos 5"),
    "claude-opus-5": Rate(5.00, 25.00, "Opus 5"),
    "claude-opus-4-8": Rate(5.00, 25.00, "Opus 4.8"),
    "claude-opus-4-7": Rate(5.00, 25.00, "Opus 4.7"),
    "claude-opus-4-6": Rate(5.00, 25.00, "Opus 4.6"),
    "claude-sonnet-5": Rate(2.00, 10.00, "Sonnet 5"),
    "claude-sonnet-4-6": Rate(3.00, 15.00, "Sonnet 4.6"),
    "claude-haiku-4-5": Rate(1.00, 5.00, "Haiku 4.5"),
}

# Prefix fallbacks for ids this build predates (e.g. a future Opus point release).
_PREFIX_RATES: list[tuple[str, Rate]] = [
    ("claude-fable", Rate(10.00, 50.00, "Fable")),
    ("claude-mythos", Rate(10.00, 50.00, "Mythos")),
    ("claude-opus", Rate(5.00, 25.00, "Opus")),
    ("claude-sonnet", Rate(3.00, 15.00, "Sonnet")),
    ("claude-haiku", Rate(1.00, 5.00, "Haiku")),
]

_UNKNOWN = Rate(0.0, 0.0, "Unknown")


def rate_for(model: str) -> Rate:
    if not model:
        return _UNKNOWN
    if model in RATES:
        return RATES[model]
    # Strip a trailing date snapshot such as -20251001 and retry.
    trimmed = model
    tail = model.rsplit("-", 1)[-1]
    if tail.isdigit() and len(tail) == 8:
        trimmed = model.rsplit("-", 1)[0]
        if trimmed in RATES:
            return RATES[trimmed]
    for prefix, rate in _PREFIX_RATES:
        if trimmed.startswith(prefix):
            return rate
    return _UNKNOWN


def display_name(model: str) -> str:
    rate = rate_for(model)
    if rate is _UNKNOWN:
        return model or "Unknown"
    return rate.display_name


def cost(
    model: str,
    *,
    input_tokens: int = 0,
    output_tokens: int = 0,
    cache_write_5m: int = 0,
    cache_write_1h: int = 0,
    cache_read: int = 0,
) -> float:
    """Equivalent API list-price value in USD for one message's token counts."""
    rate = rate_for(model)
    return (
        input_tokens * rate.input
        + output_tokens * rate.output
        + cache_write_5m * rate.cache_write_5m
        + cache_write_1h * rate.cache_write_1h
        + cache_read * rate.cache_read
    ) / MILLION
