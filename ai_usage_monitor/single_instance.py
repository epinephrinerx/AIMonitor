"""One running copy per user.

A second launch - from the Start Menu, the desktop shortcut, or the installer's
"Launch" checkbox - must not start a second monitor. Two copies would mean two
tray icons, two pollers hitting the usage endpoint at the same cadence (a good
way to earn an HTTP 429), and two writers racing on the same window geometry in
HKCU.

The guard is a named local socket, which on Windows is a named pipe. Whoever
listens first owns the app; anyone who can connect knows a copy is already
running, asks it to show itself, and exits quietly. A pipe dies with the
process that owns it, so a crashed instance leaves nothing to clean up.
"""

from __future__ import annotations

import getpass
import hashlib
import os

from PySide6.QtCore import QObject, Signal
from PySide6.QtNetwork import QLocalServer, QLocalSocket

from .settings import APP, SCOPE_ENV_VAR

#: Long enough to cross a pipe on a busy machine, short enough that a stale name
#: never makes a launch feel hung.
CONNECT_MS = 400
WRITE_MS = 400

#: Any payload would do; the arrival of the connection is the whole message.
PING = b"show\n"


def server_name() -> str:
    """A pipe name unique to this user and settings scope.

    Named pipes live in one machine-wide namespace, so the user has to be part
    of the name or two people signed in at once would each see the other's copy
    and neither could start one. The settings scope is in there too, so a test
    run under `AI_USAGE_MONITOR_SETTINGS_SCOPE` never collides with the real
    app the user has open.

    Hashed because usernames may contain spaces or characters a pipe name will
    not carry.
    """
    scope = os.environ.get(SCOPE_ENV_VAR, "").strip()
    try:
        who = getpass.getuser()
    except Exception:  # no USERNAME and no password database to fall back on
        who = ""
    raw = f"{APP}|{who}|{scope}"
    digest = hashlib.sha1(raw.encode("utf-8", "replace")).hexdigest()[:16]
    return f"AIUsageMonitor-{digest}"


class SingleInstance(QObject):
    """Owns the lock for this process, and hears about later launch attempts."""

    #: Someone tried to start a second copy; show the window we already have.
    activated = Signal()

    def __init__(self, parent: QObject | None = None) -> None:
        super().__init__(parent)
        self.name = server_name()
        self._server: QLocalServer | None = None

    # -- acquiring ---------------------------------------------------------

    def try_acquire(self) -> bool:
        """True if this process owns the app; False if another copy has it.

        On False the running copy has already been asked to come to the front,
        so the caller's only job is to exit.
        """
        if self._notify_existing():
            return False
        if self._listen():
            return True
        # Lost a start-up race: between the failed connect and the failed
        # listen, another copy claimed the name. Hand off to it.
        if self._notify_existing():
            return False
        # Neither connectable nor listenable - the pipe is unusable for a
        # reason we cannot fix here (locked-down namespace, exhausted handles).
        # Refusing to start over a broken guard would be worse than the
        # duplicate it prevents, so run unguarded.
        return True

    def _notify_existing(self) -> bool:
        """Ask an already-running copy to show itself. True if one answered."""
        socket = QLocalSocket()
        socket.connectToServer(self.name)
        if not socket.waitForConnected(CONNECT_MS):
            socket.abort()
            return False
        socket.write(PING)
        socket.waitForBytesWritten(WRITE_MS)
        socket.disconnectFromServer()
        # A disconnected socket needs no wait; an still-connected one gets a
        # brief one so the ping is not dropped by our own exit.
        if socket.state() != QLocalSocket.LocalSocketState.UnconnectedState:
            socket.waitForDisconnected(WRITE_MS)
        return True

    def _listen(self) -> bool:
        server = QLocalServer(self)
        # Windows: sets the pipe ACL to this user. Also the POSIX behaviour we
        # want if this ever runs anywhere else.
        server.setSocketOptions(QLocalServer.SocketOption.UserAccessOption)
        if not server.listen(self.name):
            # Nothing is listening (we just failed to connect), so a name still
            # in the namespace is a leftover rather than a live owner.
            QLocalServer.removeServer(self.name)
            if not server.listen(self.name):
                server.deleteLater()
                return False
        server.newConnection.connect(self._on_connection)
        self._server = server
        return True

    # -- serving -----------------------------------------------------------

    def _on_connection(self) -> None:
        if self._server is None:
            return
        while True:
            socket = self._server.nextPendingConnection()
            if socket is None:
                break
            # The payload is never read: one connection means one launch
            # attempt, which is the entire protocol.
            socket.disconnected.connect(socket.deleteLater)
            socket.close()
            self.activated.emit()

    def release(self) -> None:
        """Give up the name. Safe to call more than once."""
        if self._server is None:
            return
        self._server.close()
        self._server.deleteLater()
        self._server = None
        QLocalServer.removeServer(self.name)
