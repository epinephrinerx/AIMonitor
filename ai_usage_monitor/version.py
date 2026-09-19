"""The app's version, and the GitHub release check behind About > Version.

`__init__.__version__` is the one source of truth. `version_info.txt` (the
Windows file-version resource) and `installer/AIUsageMonitor.iss` carry the
same number for the build and the installer, and `tests/test_version.py`
fails when the three drift apart - a shipped exe whose About box disagrees
with its file properties is how a bug report becomes unreproducible.

The check is read-only and unauthenticated: one GET against the public
releases endpoint, no token, nothing sent but the request itself.
"""

from __future__ import annotations

import json
import re
import ssl
import urllib.error
import urllib.request
from dataclasses import dataclass

from . import __version__

VERSION = __version__
REPO = "epinephrinerx/AIMonitor"
RELEASES_URL = f"https://github.com/{REPO}/releases"
LATEST_API = f"https://api.github.com/repos/{REPO}/releases/latest"
# Fallback for a repository that tags its versions without publishing a
# release for each one. A tag is still a version the user can go and get, and
# "no releases yet" next to a repo full of version tags reads as broken.
TAGS_API = f"https://api.github.com/repos/{REPO}/tags?per_page=100"

# GitHub rejects unidentified callers; this is the app, not the user.
USER_AGENT = f"AIUsageMonitor/{VERSION} (+{RELEASES_URL})"

TIMEOUT = 8.0


class UpdateCheckError(Exception):
    """The check could not complete. The installed version is still valid."""


@dataclass(frozen=True)
class Release:
    tag: str
    name: str
    url: str
    published: str
    newer: bool


def parse(text: str) -> tuple[int, ...]:
    """'v1.2.7' -> (1, 2, 7). Unparseable input sorts lowest, never highest.

    A release tagged with something this does not understand must not be
    announced as an update - silently offering a downgrade is worse than
    missing a release the user can still see on the releases page.
    """
    match = re.match(r"\s*v?(\d+(?:\.\d+)*)", text or "")
    if not match:
        return ()
    return tuple(int(part) for part in match.group(1).split("."))


def is_newer(candidate: str, installed: str = VERSION) -> bool:
    left, right = parse(candidate), parse(installed)
    if not left or not right:
        return False
    # Pad so 1.3 and 1.3.0 compare equal rather than by length.
    width = max(len(left), len(right))
    left += (0,) * (width - len(left))
    right += (0,) * (width - len(right))
    return left > right


PRIVATE_HINT = (
    "GitHub does not show this repository to an anonymous request, so the "
    "update check cannot see its releases. It reports this rather than "
    "claiming you are up to date."
)


def _get(url: str, timeout: float):
    """One GET. Returns None for 404; every other failure is an UpdateCheckError.

    404 is a normal answer here, not a fault: it is what GitHub says both for
    a repository with no published releases and for one it will not show an
    anonymous caller, and the two need different words.
    """
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "User-Agent": USER_AGENT,
        },
    )
    try:
        with urllib.request.urlopen(
            request, timeout=timeout, context=ssl.create_default_context()
        ) as response:
            return json.loads(response.read().decode("utf-8", "replace"))
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return None
        if exc.code in (403, 429):
            raise UpdateCheckError(
                "GitHub is rate-limiting this check. Try again later."
            ) from exc
        raise UpdateCheckError(f"GitHub returned HTTP {exc.code}.") from exc
    except urllib.error.URLError as exc:
        raise UpdateCheckError(f"Could not reach GitHub: {exc.reason}") from exc
    except (ValueError, OSError) as exc:
        raise UpdateCheckError(f"Could not read GitHub's answer: {exc}") from exc


def _latest_tag(timeout: float) -> Release:
    """Highest version tag, for a repository that publishes no releases."""
    payload = _get(TAGS_API, timeout)
    if payload is None:
        # The releases endpoint AND the tags endpoint both deny knowing this
        # repository: it is private, renamed or gone, not merely unreleased.
        raise UpdateCheckError(PRIVATE_HINT)
    if not isinstance(payload, list):
        raise UpdateCheckError("Could not read GitHub's answer.")
    ranked = sorted(
        (parse(name), name)
        for name in (
            str(entry.get("name") or "")
            for entry in payload
            if isinstance(entry, dict)
        )
        if parse(name)
    )
    if not ranked:
        raise UpdateCheckError("This project has no published releases yet.")
    tag = ranked[-1][1]
    return Release(
        tag=tag,
        name=tag,
        url=f"https://github.com/{REPO}/releases/tag/{tag}",
        published="",
        newer=is_newer(tag),
    )


def check_latest(timeout: float = TIMEOUT) -> Release:
    """Ask GitHub for the newest published release. Raises `UpdateCheckError`."""
    payload = _get(LATEST_API, timeout)
    if payload is None:
        # No release published - the version tags are still real versions.
        return _latest_tag(timeout)

    if not isinstance(payload, dict):
        raise UpdateCheckError("Could not read GitHub's answer.")

    tag = str(payload.get("tag_name") or "")
    if not tag:
        raise UpdateCheckError("The newest release carries no version tag.")
    return Release(
        tag=tag,
        name=str(payload.get("name") or tag),
        url=str(payload.get("html_url") or RELEASES_URL),
        published=str(payload.get("published_at") or "")[:10],
        newer=is_newer(tag),
    )
