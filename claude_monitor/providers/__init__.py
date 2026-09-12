"""Provider registry.

Adding a service means adding a module here and one entry to `PROVIDERS` - the
tabs, settings cards, and worker loop are all driven off this list.
"""

from .base import (
    HistoryView,
    Meter,
    Provider,
    ProviderSnapshot,
    Stat,
    ThroughputView,
)
from .claude_provider import ClaudeProvider
from .gemini_provider import GeminiProvider
from .openai_provider import OpenAIProvider

PROVIDER_CLASSES = (ClaudeProvider, OpenAIProvider, GeminiProvider)


def build_all() -> list[Provider]:
    return [cls() for cls in PROVIDER_CLASSES]


__all__ = [
    "HistoryView",
    "Meter",
    "Provider",
    "ProviderSnapshot",
    "Stat",
    "ThroughputView",
    "ClaudeProvider",
    "OpenAIProvider",
    "GeminiProvider",
    "PROVIDER_CLASSES",
    "build_all",
]
