#!/usr/bin/env python3
"""Approval popup for the AI Agent Monitor OpenDeck plugin (Qt; PyQt6, PySide6 or PyQt5).

Reads one JSON object from stdin:
  {"provider": "Claude Code", "project": "AcmeShop", "tool": "Bash", "command": "git push …",
   "description": "…", "hold_seconds": 30, "received_at": "2026-08-28T15:51:49Z", "screen": "center"}
Exit code: 0 = approve, 1 = deny, 2 = decide in the app (button, window closed, or hold expired).

The window stays on top of everything but never takes keyboard focus, so an Enter you were typing
cannot answer it. "screen" picks the monitor: center (middle one by x position), primary, or mouse.
"""
import json
import os
import sys
from datetime import datetime, timezone

try:
    from PyQt6 import QtCore, QtGui, QtWidgets
    QT = 6
except ImportError:
    try:
        from PySide6 import QtCore, QtGui, QtWidgets
        QT = 6
    except ImportError:
        from PyQt5 import QtCore, QtGui, QtWidgets
        QT = 5

Qt = QtCore.Qt


def flag(name):
    """Qt enum member that works for both the scoped (Qt6) and flat (Qt5) enum styles."""
    for holder in (getattr(Qt, "WindowType", None), Qt):
        if holder is not None and hasattr(holder, name):
            return getattr(holder, name)
    raise AttributeError(name)


def pick_screen(app, which):
    screens = app.screens()
    if not screens:
        return None
    if which == "primary":
        return app.primaryScreen()
    if which == "mouse":
        pos = QtGui.QCursor.pos()
        for s in screens:
            if s.geometry().contains(pos):
                return s
        return app.primaryScreen()
    ordered = sorted(screens, key=lambda s: (s.geometry().x(), s.geometry().y()))
    return ordered[len(ordered) // 2]


STYLE = """
QDialog { background: #0F1115; }
QLabel { color: #ECEFF4; }
QLabel#chip { background: %(accent)s; color: #0F1115; border-radius: 6px; padding: 3px 10px; font-weight: bold; font-size: 12pt; }
QLabel#title { font-size: 16pt; font-weight: bold; }
QLabel#project { color: #8A93A6; font-size: 12pt; }
QLabel#tool { color: #F59E0B; font-size: 13pt; font-weight: bold; }
QLabel#desc { color: #B8BFCC; font-size: 11pt; }
QLabel#hint { color: #8A93A6; font-size: 10pt; }
QPlainTextEdit { background: #1B1F27; color: #ECEFF4; border: 1px solid #2B3140; border-radius: 8px; padding: 10px; font-family: "Liberation Mono", "DejaVu Sans Mono", monospace; font-size: 12pt; selection-background-color: #3B4252; }
QPushButton { border-radius: 8px; padding: 10px 22px; font-size: 12pt; font-weight: bold; color: #ECEFF4; background: #2B3140; }
QPushButton:hover { background: #3A4152; }
QPushButton#approve { background: #1F7A3F; } QPushButton#approve:hover { background: #22C55E; color: #0F1115; }
QPushButton#deny { background: #7A2626; } QPushButton#deny:hover { background: #EF4444; }
"""


class Dialog(QtWidgets.QDialog):
    def __init__(self, req):
        super().__init__(None, flag("Dialog") | flag("WindowStaysOnTopHint") | flag("WindowDoesNotAcceptFocus"))
        self.result_code = 2
        provider = req.get("provider", "Agent")
        accent = "#E0865F" if "claude" in provider.lower() else "#3DD0A4"
        self.setWindowTitle(f"{provider} asks permission — {req.get('project', '')}")
        self.setStyleSheet(STYLE % {"accent": accent})
        self.setMinimumWidth(760)

        layout = QtWidgets.QVBoxLayout(self)
        layout.setContentsMargins(22, 20, 22, 18)
        layout.setSpacing(12)

        head = QtWidgets.QHBoxLayout()
        chip = QtWidgets.QLabel(provider); chip.setObjectName("chip")
        title = QtWidgets.QLabel("asks permission"); title.setObjectName("title")
        project = QtWidgets.QLabel(req.get("project", "")); project.setObjectName("project")
        head.addWidget(chip); head.addSpacing(10); head.addWidget(title); head.addStretch(1); head.addWidget(project)
        layout.addLayout(head)

        tool = QtWidgets.QLabel(req.get("tool", "")); tool.setObjectName("tool")
        layout.addWidget(tool)

        body = req.get("command") or ""
        box = QtWidgets.QPlainTextEdit(body)
        box.setReadOnly(True)
        box.setLineWrapMode(QtWidgets.QPlainTextEdit.LineWrapMode.WidgetWidth if QT == 6 else QtWidgets.QPlainTextEdit.WidgetWidth)
        box.setFocusPolicy(flag("NoFocus") if hasattr(Qt, "NoFocus") or hasattr(Qt, "FocusPolicy") else Qt.NoFocus)
        lines = max(3, min(14, body.count("\n") + 2 + len(body) // 90))
        box.setFixedHeight(int(lines * 24 + 24))
        layout.addWidget(box)

        if req.get("description"):
            desc = QtWidgets.QLabel("— " + req["description"]); desc.setObjectName("desc"); desc.setWordWrap(True)
            layout.addWidget(desc)

        self.hint = QtWidgets.QLabel(); self.hint.setObjectName("hint"); self.hint.setWordWrap(True)
        layout.addWidget(self.hint)

        buttons = QtWidgets.QHBoxLayout()
        later = QtWidgets.QPushButton("Decide in the app")
        deny = QtWidgets.QPushButton("✕  Deny"); deny.setObjectName("deny")
        approve = QtWidgets.QPushButton("✓  Approve"); approve.setObjectName("approve")
        for b in (later, deny, approve):
            b.setFocusPolicy(flag("NoFocus") if hasattr(Qt, "FocusPolicy") else Qt.NoFocus)
            b.setCursor(QtGui.QCursor(flag("PointingHandCursor") if hasattr(Qt, "CursorShape") else Qt.PointingHandCursor))
        later.clicked.connect(lambda: self.finish(2))
        deny.clicked.connect(lambda: self.finish(1))
        approve.clicked.connect(lambda: self.finish(0))
        buttons.addWidget(later); buttons.addStretch(1); buttons.addWidget(deny); buttons.addWidget(approve)
        layout.addLayout(buttons)

        # countdown to the moment the request goes back to the app
        self.hold = int(req.get("hold_seconds", 30))
        self.started = datetime.now(timezone.utc)
        try:
            self.started = datetime.fromisoformat(req["received_at"].replace("Z", "+00:00"))
        except Exception:
            pass
        self.timer = QtCore.QTimer(self)
        self.timer.timeout.connect(self.tick)
        self.timer.start(500)
        self.tick()

    def tick(self):
        left = self.hold - (datetime.now(timezone.utc) - self.started).total_seconds()
        if left <= 0:
            self.finish(2)
            return
        self.hint.setText(f"Approve or deny here or on the deck.  Nothing happens by itself: in {int(left)} s the app shows its own prompt instead.")

    def finish(self, code):
        self.result_code = code
        self.timer.stop()
        self.accept()

    def closeEvent(self, event):
        self.timer.stop()
        super().closeEvent(event)


def main():
    try:
        req = json.loads(sys.stdin.read() or "{}")
    except Exception:
        req = {}
    app = QtWidgets.QApplication(sys.argv)
    dlg = Dialog(req)
    dlg.adjustSize()
    screen = pick_screen(app, req.get("screen", "center"))
    if screen is not None:
        g = screen.availableGeometry()
        dlg.move(g.center().x() - dlg.width() // 2, g.center().y() - dlg.height() // 2)
    dlg.show()
    shot = os.environ.get("AIAM_DIALOG_SCREENSHOT")   # for docs / tests: save a picture of the dialog once it is up
    if shot:
        QtCore.QTimer.singleShot(800, lambda: dlg.grab().save(shot))
    if QT == 6:
        app.exec()
    else:
        app.exec_()
    sys.exit(dlg.result_code)


if __name__ == "__main__":
    main()
