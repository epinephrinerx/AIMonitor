"""Generate the app icon - the arc meter, matching the dashboard gauge.

Writes three things from one renderer, so the platforms cannot drift apart:

    assets/icon.ico   Windows, and the window icon on any platform
    assets/icon.icns  the macOS .app bundle icon
    assets/icon.png   what the running app loads for its window and Dock tile

Qt can write PNG but not ICO, so the PNG frames are packed into an ICO
container by hand (PNG-in-ICO, supported since Vista). ICNS goes the other way:
`iconutil` is part of the macOS command line tools and does the packing, so we
only lay out the .iconset it expects.
"""

from __future__ import annotations

import struct
import subprocess
import sys
import tempfile
from pathlib import Path

from PySide6.QtCore import QBuffer, QByteArray, QIODevice, QRectF, Qt
from PySide6.QtGui import QColor, QImage, QPainter, QPen
from PySide6.QtWidgets import QApplication

SIZES = (16, 24, 32, 48, 64, 128, 256)

# macOS draws app icons on a grid where the rounded square covers about 80% of
# the tile; a full-bleed icon reads as oversized next to everything else in the
# Dock. Windows has no such convention and wants the full square.
MACOS_MARGIN = 0.10

# The pairs `iconutil` looks for: (file stem, pixel size).
ICONSET = [
    ("icon_16x16", 16), ("icon_16x16@2x", 32),
    ("icon_32x32", 32), ("icon_32x32@2x", 64),
    ("icon_128x128", 128), ("icon_128x128@2x", 256),
    ("icon_256x256", 256), ("icon_256x256@2x", 512),
    ("icon_512x512", 512), ("icon_512x512@2x", 1024),
]
ACCENT = QColor("#2a78d6")
FILL_FRACTION = 0.68
START_ANGLE = 225
SWEEP = -270


def render(size: int, margin: float = 0.0) -> QByteArray:
    image = QImage(size, size, QImage.Format.Format_ARGB32_Premultiplied)
    image.fill(Qt.GlobalColor.transparent)

    painter = QPainter(image)
    painter.setRenderHint(QPainter.RenderHint.Antialiasing)

    pad = size * margin
    tile = size - pad * 2
    radius = tile * 0.22
    painter.setPen(Qt.PenStyle.NoPen)
    painter.setBrush(ACCENT)
    painter.drawRoundedRect(QRectF(pad, pad, tile, tile), radius, radius)

    thickness = max(1.6, tile * 0.115)
    inset = pad + thickness / 2 + tile * 0.20
    box = QRectF(inset, inset, size - inset * 2, size - inset * 2)

    track = QPen(QColor(255, 255, 255, 90), thickness)
    track.setCapStyle(Qt.PenCapStyle.RoundCap)
    painter.setPen(track)
    painter.drawArc(box, START_ANGLE * 16, SWEEP * 16)

    fill = QPen(QColor(255, 255, 255), thickness)
    fill.setCapStyle(Qt.PenCapStyle.RoundCap)
    painter.setPen(fill)
    painter.drawArc(box, START_ANGLE * 16, int(SWEEP * FILL_FRACTION * 16))
    painter.end()

    buffer = QBuffer()
    buffer.open(QIODevice.OpenModeFlag.WriteOnly)
    image.save(buffer, "PNG")
    return buffer.data()


def pack(frames: list[tuple[int, QByteArray]]) -> bytes:
    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = len(header) + 16 * len(frames)
    entries = bytearray()
    payload = bytearray()
    for size, data in frames:
        blob = bytes(data)
        entries += struct.pack(
            "<BBBBHHII",
            size if size < 256 else 0,
            size if size < 256 else 0,
            0,
            0,
            1,
            32,
            len(blob),
            offset,
        )
        payload += blob
        offset += len(blob)
    return header + bytes(entries) + bytes(payload)


def write_icns(target: Path) -> bool:
    """Pack the PNG frames into an .icns. False if this is not a Mac."""
    if sys.platform != "darwin":
        return False
    with tempfile.TemporaryDirectory() as work:
        iconset = Path(work) / "icon.iconset"
        iconset.mkdir()
        for stem, size in ICONSET:
            (iconset / f"{stem}.png").write_bytes(
                bytes(render(size, MACOS_MARGIN))
            )
        done = subprocess.run(
            ["/usr/bin/iconutil", "-c", "icns", str(iconset), "-o", str(target)],
            capture_output=True, text=True,
        )
    if done.returncode != 0:
        raise SystemExit(f"iconutil failed: {done.stderr.strip()}")
    return True


def main() -> int:
    app = QApplication(sys.argv)  # noqa: F841 - QPainter needs a Q(Gui)Application
    assets = Path(__file__).resolve().parent.parent / "assets"
    assets.mkdir(parents=True, exist_ok=True)

    ico = assets / "icon.ico"
    ico.write_bytes(pack([(size, render(size)) for size in SIZES]))
    print(f"wrote {ico} ({ico.stat().st_size:,} bytes)")

    # The runtime window/Dock icon. PNG rather than the platform container, so
    # one file works everywhere Qt runs without depending on an image plugin.
    png = assets / "icon.png"
    png.write_bytes(bytes(render(512, MACOS_MARGIN if sys.platform == "darwin" else 0.0)))
    print(f"wrote {png} ({png.stat().st_size:,} bytes)")

    icns = assets / "icon.icns"
    if write_icns(icns):
        print(f"wrote {icns} ({icns.stat().st_size:,} bytes)")
    else:
        print("skipped icon.icns (needs macOS and iconutil)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
