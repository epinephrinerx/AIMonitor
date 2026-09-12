"""Card surface and stat tile.

The stat tile follows the figure contract: a sentence-case label, a compact
semibold value in proportional figures, and an optional secondary line. Values
never wear a series colour - identity, where it is needed, comes from a mark
beside the text.
"""

from __future__ import annotations

from PySide6.QtCore import Qt
from PySide6.QtWidgets import QFrame, QLabel, QSizePolicy, QVBoxLayout, QWidget

from ..theme import Theme, qcolor


class Card(QFrame):
    """A chart surface with a hairline ring and rounded corners."""

    def __init__(self, theme: Theme, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.theme = theme
        self.setObjectName("card")
        self.setFrameShape(QFrame.Shape.NoFrame)
        self.apply_theme(theme)

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        border = qcolor(theme.border)
        self.setStyleSheet(
            f"""
            QFrame#card {{
                background-color: {theme.surface};
                border: 1px solid rgba({border.red()}, {border.green()},
                                       {border.blue()}, {border.alphaF():.2f});
                border-radius: 10px;
            }}
            """
        )
        for child in self.findChildren(QWidget):
            applier = getattr(child, "apply_theme", None)
            if callable(applier) and child is not self:
                applier(theme)


class StatTile(QFrame):
    """label / value / optional detail line."""

    def __init__(
        self,
        theme: Theme,
        label: str,
        value: str = "-",
        detail: str = "",
        parent: QWidget | None = None,
    ) -> None:
        super().__init__(parent)
        self.theme = theme
        self.setFrameShape(QFrame.Shape.NoFrame)
        self.setSizePolicy(QSizePolicy.Policy.Preferred, QSizePolicy.Policy.Fixed)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(2)

        self._label = QLabel(label)
        self._value = QLabel(value)
        self._detail = QLabel(detail)
        self._detail.setVisible(bool(detail))

        for widget in (self._label, self._value, self._detail):
            widget.setTextInteractionFlags(Qt.TextInteractionFlag.NoTextInteraction)
            layout.addWidget(widget)

        self.apply_theme(theme)

    def set_label(self, label: str) -> None:
        self._label.setText(label)

    def set_value(self, value: str, detail: str = "") -> None:
        self._value.setText(value)
        self._detail.setText(detail)
        self._detail.setVisible(bool(detail))

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        self._label.setStyleSheet(
            f"color: {theme.ink_secondary}; font-size: 11px; font-weight: 500;"
        )
        # Proportional figures: a large standalone number reads loose in
        # tabular figures, which are reserved for aligned columns.
        self._value.setStyleSheet(
            f"color: {theme.ink}; font-size: 24px; font-weight: 600;"
        )
        self._detail.setStyleSheet(f"color: {theme.ink_muted}; font-size: 11px;")
