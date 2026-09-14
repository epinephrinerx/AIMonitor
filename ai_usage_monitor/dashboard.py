"""One provider's full dashboard page - the content of a single tab."""

from __future__ import annotations

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import (
    QFrame,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QScrollArea,
    QSizePolicy,
    QVBoxLayout,
    QWidget,
)

from .providers import Provider, ProviderSnapshot
from .theme import Theme, qcolor
from .widgets import Card, HorizontalBarChart, QuotaGauge, StackedColumnChart, StatTile


class ProviderPage(QWidget):
    """Gauges, stat tiles and charts for one provider."""

    configure_requested = Signal(str)

    def __init__(self, provider: Provider, theme: Theme, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.provider = provider
        self.theme = theme
        self._gauges: list[QuotaGauge] = []
        self._tiles: list[StatTile] = []
        self.snapshot: ProviderSnapshot | None = None

        outer = QVBoxLayout(self)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(10)

        self.banner = QLabel("")
        self.banner.setWordWrap(True)
        self.banner.setVisible(False)
        self.banner.setContentsMargins(12, 9, 12, 9)
        outer.addWidget(self.banner)

        self.scroll = QScrollArea()
        self.scroll.setWidgetResizable(True)
        self.scroll.setFrameShape(QFrame.Shape.NoFrame)
        content = QWidget()
        self.scroll.setWidget(content)
        outer.addWidget(self.scroll, 1)

        body = QVBoxLayout(content)
        body.setContentsMargins(0, 0, 6, 0)
        body.setSpacing(12)

        self.setup_card = self._build_setup_card()
        body.addWidget(self.setup_card)

        self.gauge_row = QHBoxLayout()
        self.gauge_row.setSpacing(12)
        body.addLayout(self.gauge_row)

        self.tiles_card = Card(theme)
        self.tiles_row = QHBoxLayout(self.tiles_card)
        self.tiles_row.setContentsMargins(18, 14, 18, 14)
        self.tiles_row.setSpacing(24)
        body.addWidget(self.tiles_card)

        self.chart_card = Card(theme)
        chart_layout = QVBoxLayout(self.chart_card)
        chart_layout.setContentsMargins(18, 14, 18, 14)
        chart_layout.setSpacing(10)
        self.chart_title = QLabel("Usage per day")
        chart_layout.addWidget(self.chart_title)
        self.daily_chart = StackedColumnChart(theme)
        chart_layout.addWidget(self.daily_chart, 1)
        body.addWidget(self.chart_card, 1)

        self.breakdown_row = QWidget()
        row = QHBoxLayout(self.breakdown_row)
        row.setContentsMargins(0, 0, 0, 0)
        row.setSpacing(12)

        self.model_card = Card(theme)
        model_layout = QVBoxLayout(self.model_card)
        model_layout.setContentsMargins(18, 14, 18, 14)
        model_layout.setSpacing(10)
        self.model_title = QLabel("By model")
        model_layout.addWidget(self.model_title)
        self.model_chart = HorizontalBarChart(theme, coloured=True)
        model_layout.addWidget(self.model_chart)
        model_layout.addStretch(1)

        self.project_card = Card(theme)
        project_layout = QVBoxLayout(self.project_card)
        project_layout.setContentsMargins(18, 14, 18, 14)
        project_layout.setSpacing(10)
        self.project_title = QLabel("By project")
        project_layout.addWidget(self.project_title)
        self.project_chart = HorizontalBarChart(theme, coloured=False)
        project_layout.addWidget(self.project_chart)
        project_layout.addStretch(1)

        row.addWidget(self.model_card, 1)
        row.addWidget(self.project_card, 1)
        body.addWidget(self.breakdown_row)

        self.note = QLabel("")
        self.note.setWordWrap(True)
        body.addWidget(self.note)

        # Absorbs the slack when charts are hidden, so the cards that are
        # visible sit at their natural height instead of stretching to fill.
        body.addStretch(1)

        self.apply_theme(theme)

    def _build_setup_card(self) -> Card:
        card = Card(self.theme)
        card.setSizePolicy(QSizePolicy.Policy.Preferred, QSizePolicy.Policy.Maximum)
        layout = QVBoxLayout(card)
        layout.setContentsMargins(18, 16, 18, 16)
        layout.setSpacing(10)
        self.setup_title = QLabel(f"{self.provider.display_name} is not set up yet")
        self.setup_body = QLabel(self.provider.setup_hint)
        self.setup_body.setWordWrap(True)
        self.setup_body.setTextFormat(Qt.TextFormat.RichText)
        layout.addWidget(self.setup_title)
        layout.addWidget(self.setup_body)
        if self.provider.needs_key:
            button = QPushButton(f"Configure {self.provider.display_name}…")
            button.setCursor(Qt.CursorShape.PointingHandCursor)
            button.setProperty("accent", True)
            button.clicked.connect(
                lambda: self.configure_requested.emit(self.provider.id)
            )
            row = QHBoxLayout()
            row.addWidget(button)
            row.addStretch(1)
            layout.addLayout(row)
        card.setVisible(False)
        return card

    # -- rendering --------------------------------------------------------

    def render(self, snapshot: ProviderSnapshot | None, show_history: bool) -> None:
        self.snapshot = snapshot
        if snapshot is None:
            return

        configured = snapshot.configured
        self.setup_card.setVisible(not configured)
        self.setup_body.setText(snapshot.setup_hint or self.provider.setup_hint)

        meters = snapshot.meters
        while len(self._gauges) < len(meters):
            gauge = QuotaGauge(self.theme)
            self._gauges.append(gauge)
            self.gauge_row.addWidget(gauge, 1)
        for index, gauge in enumerate(self._gauges):
            if index < len(meters):
                gauge.set_meter(meters[index])
                gauge.setVisible(True)
            else:
                gauge.setVisible(False)

        stats = snapshot.stats
        while len(self._tiles) < len(stats):
            tile = StatTile(self.theme, "")
            self._tiles.append(tile)
            self.tiles_row.addWidget(tile, 1)
        for index, tile in enumerate(self._tiles):
            if index < len(stats):
                stat = stats[index]
                tile.set_label(stat.label)
                tile.set_value(stat.value, stat.detail)
                tile.setVisible(True)
            else:
                tile.setVisible(False)
        self.tiles_card.setVisible(bool(stats))

        history = snapshot.history
        has_history = show_history and history is not None and history.buckets
        self.chart_card.setVisible(bool(has_history))
        self.breakdown_row.setVisible(bool(has_history))
        if has_history:
            self.chart_title.setText(f"Usage per day · last {history.days} days")
            self.daily_chart.set_data(history.buckets, history.series, history.metric)
            self.model_chart.set_data(history.by_model, history.metric)
            self.model_card.setVisible(bool(history.by_model))
            self.model_title.setText(f"By model · last {history.days} days")
            self.project_card.setVisible(bool(history.by_project))
            if history.by_project:
                self.project_chart.set_data(history.by_project, history.metric)
                self.project_title.setText(
                    f"{history.project_label} · last {history.days} days"
                )

        self.note.setText(snapshot.value_note)
        self.note.setVisible(bool(snapshot.value_note) and configured)
        self._render_banner(snapshot)

    def release_charts(self) -> None:
        """Drop chart data when this page is not on screen (widget mode)."""
        self.daily_chart.set_data([], [], "Total tokens")
        self.model_chart.set_data([], "Total tokens")
        self.project_chart.set_data([], "Total tokens")

    def _render_banner(self, snapshot: ProviderSnapshot) -> None:
        if not snapshot.error or not snapshot.configured:
            self.banner.setVisible(False)
            return
        severity = "critical" if snapshot.unauthorized else "warning"
        colour = qcolor(Theme.status(severity))
        self.banner.setText(f"⚠ {snapshot.error}")
        self.banner.setStyleSheet(
            f"color: {self.theme.ink}; font-size: 12px;"
            f"background-color: rgba({colour.red()}, {colour.green()},"
            f" {colour.blue()}, 0.12);"
            f"border: 1px solid rgba({colour.red()}, {colour.green()},"
            f" {colour.blue()}, 0.45); border-radius: 8px;"
        )
        self.banner.setVisible(True)

    def refresh_countdowns(self) -> None:
        for gauge in self._gauges:
            if gauge.isVisible():
                gauge.refresh_countdown()

    def apply_theme(self, theme: Theme) -> None:
        self.theme = theme
        for widget in self.findChildren(QWidget):
            applier = getattr(widget, "apply_theme", None)
            if callable(applier) and widget is not self:
                applier(theme)
        for label in (self.chart_title, self.model_title, self.project_title,
                      self.setup_title):
            label.setStyleSheet(
                f"color: {theme.ink}; font-size: 13px; font-weight: 600;"
            )
        self.setup_body.setStyleSheet(
            f"color: {theme.ink_secondary}; font-size: 12px;"
        )
        self.note.setStyleSheet(f"color: {theme.ink_muted}; font-size: 11px;")
        if self.snapshot is not None:
            self._render_banner(self.snapshot)
