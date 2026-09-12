"""Generate assets/icon.ico - the arc meter, matching the dashboard gauge.

Qt can write PNG but not ICO, so the PNG frames are packed into an ICO
container by hand (PNG-in-ICO, supported since Vista). No extra dependency.
"""

from __future__ import annotations

import struct
import sys
from pathlib import Path

from PySide6.QtCore import QBuffer, QByteArray, QIODevice, QRectF, Qt
from PySide6.QtGui import QColor, QImage, QPainter, QPen
from PySide6.QtWidgets import QApplication

SIZES = (16, 24, 32, 48, 64, 128, 256)
ACCENT = QColor("#2a78d6")
FILL_FRACTION = 0.68
START_ANGLE = 225
SWEEP = -270


def render(size: int) -> QByteArray:
    image = QImage(size, size, QImage.Format.Format_ARGB32_Premultiplied)
    image.fill(Qt.GlobalColor.transparent)

    painter = QPainter(image)
    painter.setRenderHint(QPainter.RenderHint.Antialiasing)

    radius = size * 0.22
    painter.setPen(Qt.PenStyle.NoPen)
    painter.setBrush(ACCENT)
    painter.drawRoundedRect(QRectF(0, 0, size, size), radius, radius)

    thickness = max(1.6, size * 0.115)
    inset = thickness / 2 + size * 0.20
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


def main() -> int:
    app = QApplication(sys.argv)  # noqa: F841 - QPainter needs a Q(Gui)Application
    target = Path(__file__).resolve().parent.parent / "assets" / "icon.ico"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(pack([(size, render(size)) for size in SIZES]))
    print(f"wrote {target} ({target.stat().st_size:,} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
