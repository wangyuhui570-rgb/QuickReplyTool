from __future__ import annotations

import ctypes
import io
import json
import math
import os
import socket
import sys
import threading
import time
from ctypes import wintypes
from pathlib import Path

from PIL import Image, ImageGrab
from PySide6.QtCore import QBuffer, QByteArray, QIODevice, QObject, QPoint, QRect, Qt, QTimer, Signal
from PySide6.QtGui import QAction, QColor, QCursor, QGuiApplication, QIcon, QImage, QKeySequence, QPainter, QPen, QPixmap
from PySide6.QtWidgets import (
    QApplication,
    QDialog,
    QDialogButtonBox,
    QFileDialog,
    QFrame,
    QHBoxLayout,
    QLabel,
    QKeySequenceEdit,
    QMenu,
    QMessageBox,
    QSystemTrayIcon,
    QToolButton,
    QVBoxLayout,
    QWidget,
)
import win32con
import win32gui
import win32process

try:
    from pywinauto.uia_element_info import UIAElementInfo
except Exception:
    UIAElementInfo = None


APP_TITLE = "LiteShot 轻截图"
APPDATA_ROOT = Path(os.getenv("APPDATA") or str(Path.home()))
SETTINGS_DIR = APPDATA_ROOT / "QuickReplyTool" / "ScreenshotTool"
SETTINGS_PATH = SETTINGS_DIR / "lite_shot_settings.json"
LEGACY_SETTINGS_PATH = Path(__file__).with_name("lite_shot_settings.json")
DEFAULT_HOTKEY = "Alt+A"
MIN_SELECTION = 10
HOVER_CLICK_TOLERANCE = 6
HOTKEY_ID = 0xB100
SINGLE_INSTANCE_PORT = 47625
LOG_DIR = APPDATA_ROOT / "QuickReplyTool" / "logs" / "screenshot"
WM_POWERBROADCAST = 0x0218
PBT_APMRESUMEAUTOMATIC = 0x0012
PBT_APMRESUMESUSPEND = 0x0007
WM_WTSSESSION_CHANGE = 0x02B1
WTS_SESSION_UNLOCK = 0x8
WTS_CONSOLE_CONNECT = 0x1
HOTKEY_SELF_HEAL_INTERVAL_MS = 30_000
HEARTBEAT_INTERVAL_MS = 2_000
ERROR_HOTKEY_ALREADY_REGISTERED = 1409

MOD_ALT = 0x0001
MOD_CONTROL = 0x0002
MOD_SHIFT = 0x0004
MOD_WIN = 0x0008
WM_HOTKEY = 0x0312

ARROW_COLOR = QColor("#ff3b30")
RECT_COLOR = QColor("#ffd60a")
MASK_COLOR = QColor(0, 0, 0, 110)
SELECTION_BORDER = QColor("#00c2ff")
MOSAIC_OUTLINE = QColor("#00c2ff")
COLOR_OPTIONS = [
    ("#ff3b30", "红色"),
    ("#ffd60a", "黄色"),
    ("#34c759", "绿色"),
    ("#0a84ff", "蓝色"),
    ("#ffffff", "白色"),
]
IGNORED_WINDOW_CLASSES = {"Progman", "WorkerW"}

USER32 = ctypes.windll.user32
USER32.RealChildWindowFromPoint.argtypes = [wintypes.HWND, wintypes.POINT]
USER32.RealChildWindowFromPoint.restype = wintypes.HWND
USER32.GetPhysicalCursorPos.argtypes = [ctypes.POINTER(wintypes.POINT)]
USER32.GetPhysicalCursorPos.restype = wintypes.BOOL
USER32.OpenClipboard.argtypes = [wintypes.HWND]
USER32.OpenClipboard.restype = wintypes.BOOL
USER32.EmptyClipboard.argtypes = []
USER32.EmptyClipboard.restype = wintypes.BOOL
USER32.SetClipboardData.argtypes = [wintypes.UINT, wintypes.HANDLE]
USER32.SetClipboardData.restype = wintypes.HANDLE
USER32.CloseClipboard.argtypes = []
USER32.CloseClipboard.restype = wintypes.BOOL

KERNEL32 = ctypes.windll.kernel32
KERNEL32.GlobalAlloc.argtypes = [wintypes.UINT, ctypes.c_size_t]
KERNEL32.GlobalAlloc.restype = wintypes.HGLOBAL
KERNEL32.GlobalLock.argtypes = [wintypes.HGLOBAL]
KERNEL32.GlobalLock.restype = wintypes.LPVOID
KERNEL32.GlobalUnlock.argtypes = [wintypes.HGLOBAL]
KERNEL32.GlobalUnlock.restype = wintypes.BOOL
KERNEL32.GlobalFree.argtypes = [wintypes.HGLOBAL]
KERNEL32.GlobalFree.restype = wintypes.HGLOBAL


def log_event(event: str, **fields) -> None:
    """Append one JSON line to logs/screenshot/liteshot-YYYYMMDD.jsonl. Never raises."""
    try:
        LOG_DIR.mkdir(parents=True, exist_ok=True)
        record = {"ts": time.strftime("%Y-%m-%dT%H:%M:%S%z"), "source": "lite_shot", "pid": os.getpid(), "event": event}
        record.update(fields)
        path = LOG_DIR / f"liteshot-{time.strftime('%Y%m%d')}.jsonl"
        with path.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(record, ensure_ascii=False) + "\n")
    except Exception:
        pass


def install_exception_logging() -> None:
    def hook(exc_type, exc, tb):
        import traceback
        log_event("unhandled_exception", error=f"{exc_type.__name__}: {exc}", traceback="".join(traceback.format_exception(exc_type, exc, tb))[-4000:])
    sys.excepthook = hook
    threading.excepthook = lambda args: hook(args.exc_type, args.exc_value, args.exc_traceback)


def default_save_dir() -> str:
    desktop = Path.home() / "Desktop"
    return str(desktop if desktop.exists() else Path.home())


def enable_dpi_awareness() -> None:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


def load_settings() -> dict:
    for candidate in (SETTINGS_PATH, LEGACY_SETTINGS_PATH):
        if not candidate.exists():
            continue
        try:
            data = json.loads(candidate.read_text(encoding="utf-8"))
            hotkey = data.get("hotkey", DEFAULT_HOTKEY)
            save_dir = data.get("save_dir", default_save_dir())
            if isinstance(hotkey, str) and hotkey.strip():
                return {"hotkey": hotkey.strip(), "save_dir": str(save_dir)}
        except Exception:
            pass
    return {"hotkey": DEFAULT_HOTKEY, "save_dir": default_save_dir()}


def save_settings(settings: dict) -> None:
    SETTINGS_DIR.mkdir(parents=True, exist_ok=True)
    SETTINGS_PATH.write_text(json.dumps(settings, ensure_ascii=False, indent=2), encoding="utf-8")


def get_physical_cursor_qpoint() -> QPoint:
    point = wintypes.POINT()
    if USER32.GetPhysicalCursorPos(ctypes.byref(point)):
        return QPoint(point.x, point.y)
    return QCursor.pos()


def normalize_rect(start: QPoint, end: QPoint) -> QRect:
    return QRect(start, end).normalized()


def clamp_point(point: QPoint, bounds: QRect) -> QPoint:
    return QPoint(
        max(bounds.left(), min(point.x(), bounds.right())),
        max(bounds.top(), min(point.y(), bounds.bottom())),
    )


def qimage_from_pil(image) -> QImage:
    rgba = image.convert("RGBA")
    data = rgba.tobytes("raw", "RGBA")
    qimage = QImage(data, rgba.width, rgba.height, QImage.Format_RGBA8888)
    return qimage.copy()


def qpixmap_to_pil_image(pixmap: QPixmap) -> Image.Image:
    buffer_bytes = QByteArray()
    buffer = QBuffer(buffer_bytes)
    if not buffer.open(QIODevice.WriteOnly):
        raise RuntimeError("无法创建截图缓冲区")

    try:
        if not pixmap.save(buffer, "PNG"):
            raise RuntimeError("截图导出失败")
    finally:
        buffer.close()

    return Image.open(io.BytesIO(bytes(buffer_bytes))).copy()


def pil_image_to_clipboard(image: Image.Image) -> None:
    output = io.BytesIO()
    image.convert("RGB").save(output, "BMP")
    data = output.getvalue()[14:]

    gmem_movable = 0x0002
    cf_dib = 8

    handle = KERNEL32.GlobalAlloc(gmem_movable, len(data))
    if not handle:
        raise RuntimeError("申请剪贴板内存失败")

    pointer = KERNEL32.GlobalLock(handle)
    if not pointer:
        KERNEL32.GlobalFree(handle)
        raise RuntimeError("写入剪贴板失败")

    try:
        ctypes.memmove(pointer, data, len(data))
    finally:
        KERNEL32.GlobalUnlock(handle)

    for _ in range(10):
        if USER32.OpenClipboard(None):
            break
        time.sleep(0.05)
    else:
        KERNEL32.GlobalFree(handle)
        raise RuntimeError("无法打开剪贴板，可能正被其他程序占用")

    try:
        USER32.EmptyClipboard()
        if not USER32.SetClipboardData(cf_dib, handle):
            KERNEL32.GlobalFree(handle)
            raise RuntimeError("设置剪贴板内容失败")
        handle = None
    finally:
        USER32.CloseClipboard()


def create_app_icon() -> QIcon:
    pixmap = QPixmap(64, 64)
    pixmap.fill(Qt.transparent)
    painter = QPainter(pixmap)
    painter.setRenderHint(QPainter.Antialiasing)
    painter.setBrush(QColor("#0a84ff"))
    painter.setPen(Qt.NoPen)
    painter.drawRoundedRect(6, 6, 52, 52, 14, 14)
    painter.setPen(QPen(Qt.white, 4))
    painter.drawLine(20, 18, 20, 46)
    painter.drawLine(32, 18, 32, 46)
    painter.drawLine(44, 18, 44, 46)
    painter.drawLine(18, 20, 46, 20)
    painter.drawLine(18, 32, 46, 32)
    painter.drawLine(18, 44, 46, 44)
    painter.end()
    return QIcon(pixmap)


def parse_hotkey(text: str) -> tuple[int, int]:
    tokens = [token.strip() for token in text.split("+") if token.strip()]
    if not tokens:
        raise ValueError("快捷键不能为空")

    modifiers = 0
    key_token = None
    for token in tokens:
        upper = token.upper()
        if upper in {"CTRL", "CONTROL"}:
            modifiers |= MOD_CONTROL
        elif upper == "ALT":
            modifiers |= MOD_ALT
        elif upper == "SHIFT":
            modifiers |= MOD_SHIFT
        elif upper in {"META", "WIN", "WINDOWS"}:
            modifiers |= MOD_WIN
        else:
            key_token = upper

    if not modifiers:
        raise ValueError("至少需要一个修饰键，例如 Ctrl 或 Alt")
    if not key_token:
        raise ValueError("缺少主按键")

    if len(key_token) == 1 and "A" <= key_token <= "Z":
        return modifiers, ord(key_token)
    if len(key_token) == 1 and "0" <= key_token <= "9":
        return modifiers, ord(key_token)
    if key_token.startswith("F") and key_token[1:].isdigit():
        number = int(key_token[1:])
        if 1 <= number <= 24:
            return modifiers, 0x70 + number - 1

    mapping = {"SPACE": 0x20, "TAB": 0x09, "ESC": 0x1B, "ESCAPE": 0x1B, "ENTER": 0x0D, "RETURN": 0x0D}
    if key_token in mapping:
        return modifiers, mapping[key_token]

    raise ValueError("仅支持字母、数字、F1-F24、Space、Tab、Esc、Enter")


def request_existing_instance_shutdown() -> bool:
    try:
        with socket.create_connection(("127.0.0.1", SINGLE_INSTANCE_PORT), timeout=0.4) as client:
            client.sendall(b"shutdown")
        return True
    except OSError:
        return False


def wait_for_existing_instance_shutdown(timeout_sec: float = 3.0) -> None:
    deadline = time.time() + timeout_sec
    while time.time() < deadline:
        try:
            with socket.create_connection(("127.0.0.1", SINGLE_INSTANCE_PORT), timeout=0.2):
                time.sleep(0.12)
        except OSError:
            return


class SingleInstanceServer(threading.Thread):
    def __init__(self, on_shutdown, on_reload, on_status=None):
        super().__init__(daemon=True)
        self.on_shutdown = on_shutdown
        self.on_reload = on_reload
        self.on_status = on_status
        self.stop_event = threading.Event()
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind(("127.0.0.1", SINGLE_INSTANCE_PORT))
        self.sock.listen(1)
        self.sock.settimeout(0.5)

    def run(self) -> None:
        while not self.stop_event.is_set():
            try:
                conn, _addr = self.sock.accept()
            except OSError:
                continue
            with conn:
                try:
                    data = conn.recv(64)
                except OSError:
                    data = b""
                command = data.strip().lower()
                try:
                    if command == b"shutdown":
                        conn.sendall(b"ok")
                        self.on_shutdown()
                        break
                    if command == b"reload":
                        conn.sendall(b"ok")
                        self.on_reload()
                    elif command == b"status" and self.on_status is not None:
                        conn.sendall(json.dumps(self.on_status(), ensure_ascii=False).encode("utf-8"))
                    elif command:
                        conn.sendall(b"unsupported")
                except OSError:
                    pass

    def close(self) -> None:
        self.stop_event.set()
        try:
            with socket.create_connection(("127.0.0.1", SINGLE_INSTANCE_PORT), timeout=0.2):
                pass
        except OSError:
            pass
        try:
            self.sock.close()
        except OSError:
            pass
        if self.is_alive() and threading.current_thread() is not self:
            self.join(timeout=1.0)
        try:
            self.sock.close()
        except OSError:
            pass


class HotkeyWindow(QWidget):
    hotkey_pressed = Signal()
    refresh_requested = Signal(str)

    def __init__(self):
        super().__init__(None)
        self.setWindowFlags(Qt.Tool)
        self.setAttribute(Qt.WA_NativeWindow)
        self.hide()
        self.winId()
        self.registered = False
        self.registered_hotkey = ""
        self.last_error = ""
        self.last_error_code = 0
        try:
            # Receive WM_WTSSESSION_CHANGE (lock/unlock, RDP reconnect) for this window.
            ctypes.windll.wtsapi32.WTSRegisterSessionNotification(int(self.winId()), 0)
        except Exception:
            pass

    def register_hotkey(self, hotkey: str) -> None:
        """Register `hotkey`. On failure the previously working hotkey is restored, so a failed
        reload never leaves the tool without any hotkey."""
        modifiers, vk = parse_hotkey(hotkey)
        previous = self.registered_hotkey if self.registered else ""
        self.unregister_hotkey()
        ctypes.windll.kernel32.SetLastError(0)
        ok = ctypes.windll.user32.RegisterHotKey(int(self.winId()), HOTKEY_ID, modifiers, vk)
        if ok:
            self.registered = True
            self.registered_hotkey = hotkey
            self.last_error = ""
            self.last_error_code = 0
            log_event("hotkey_registered", hotkey=hotkey)
            return

        code = ctypes.windll.kernel32.GetLastError()
        self.last_error_code = int(code)
        if code == ERROR_HOTKEY_ALREADY_REGISTERED:
            self.last_error = f"快捷键 {hotkey} 已被其他软件占用（如微信/企业微信/QQ 的截图快捷键）"
        else:
            self.last_error = f"注册快捷键失败：{hotkey}（错误码 {code}）"
        log_event("hotkey_register_failed", hotkey=hotkey, error_code=int(code), error=self.last_error)
        self.registered_hotkey = hotkey
        if previous and previous != hotkey:
            prev_mod, prev_vk = parse_hotkey(previous)
            if ctypes.windll.user32.RegisterHotKey(int(self.winId()), HOTKEY_ID, prev_mod, prev_vk):
                self.registered = True
                self.registered_hotkey = previous
                log_event("hotkey_restored_previous", hotkey=previous)
        raise RuntimeError(self.last_error)

    def unregister_hotkey(self) -> None:
        if self.registered:
            ctypes.windll.user32.UnregisterHotKey(int(self.winId()), HOTKEY_ID)
            self.registered = False

    def nativeEvent(self, event_type, message):
        msg = wintypes.MSG.from_address(int(message))
        if msg.message == WM_HOTKEY and msg.wParam == HOTKEY_ID:
            self.hotkey_pressed.emit()
            return True, 0
        if msg.message == WM_POWERBROADCAST and msg.wParam in (PBT_APMRESUMEAUTOMATIC, PBT_APMRESUMESUSPEND):
            self.refresh_requested.emit("resume")
        elif msg.message == WM_WTSSESSION_CHANGE and msg.wParam in (WTS_SESSION_UNLOCK, WTS_CONSOLE_CONNECT):
            self.refresh_requested.emit("session_unlock")
        return False, 0


class SettingsDialog(QDialog):
    def __init__(self, current_hotkey: str, current_save_dir: str, parent=None):
        super().__init__(parent)
        self.setWindowTitle("快捷键设置")
        self.setModal(True)
        self.setMinimumWidth(460)

        layout = QVBoxLayout(self)
        layout.addWidget(QLabel("修改全局截图快捷键"))
        layout.addWidget(QLabel("建议保留 Ctrl / Alt 等修饰键，避免影响正常输入。"))

        self.editor = QKeySequenceEdit(self)
        self.editor.setKeySequence(QKeySequence(current_hotkey))
        layout.addWidget(self.editor)

        self.error_label = QLabel("")
        self.error_label.setStyleSheet("color:#d93025;")
        layout.addWidget(self.error_label)

        layout.addWidget(QLabel("默认保存目录"))
        path_row = QHBoxLayout()
        self.save_dir_label = QLabel(current_save_dir)
        self.save_dir_label.setWordWrap(True)
        self.save_dir_label.setStyleSheet("background:#f3f4f6; border:1px solid #d0d7de; padding:8px; border-radius:6px;")
        path_row.addWidget(self.save_dir_label, 1)

        browse_button = QToolButton(self)
        browse_button.setText("浏览")
        browse_button.clicked.connect(self.pick_save_dir)
        path_row.addWidget(browse_button)
        layout.addLayout(path_row)

        buttons = QDialogButtonBox(QDialogButtonBox.Save | QDialogButtonBox.Cancel, self)
        buttons.accepted.connect(self.validate_and_accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

    def validate_and_accept(self):
        hotkey = self.editor.keySequence().toString(QKeySequence.PortableText)
        try:
            parse_hotkey(hotkey)
        except ValueError as exc:
            self.error_label.setText(str(exc))
            return
        self.accept()

    def pick_save_dir(self):
        directory = QFileDialog.getExistingDirectory(self, "选择默认保存目录", self.save_dir_label.text() or default_save_dir())
        if directory:
            self.save_dir_label.setText(directory)

    def hotkey(self) -> str:
        return self.editor.keySequence().toString(QKeySequence.PortableText)

    def save_dir(self) -> str:
        return self.save_dir_label.text() or default_save_dir()


class ToolButton(QToolButton):
    def __init__(self, text: str, on_click, tooltip: str = "", checkable: bool = False):
        super().__init__()
        self.setText(text)
        self.clicked.connect(on_click)
        self.setCheckable(checkable)
        self.setToolTip(tooltip)
        self.setCursor(Qt.PointingHandCursor)
        self.setFixedSize(40, 40)
        self.setStyleSheet(
            """
            QToolButton {
                color: white;
                background: #242628;
                border: 1px solid #3c4043;
                border-radius: 8px;
                padding: 0px;
                font-size: 16px;
                font-weight: 600;
            }
            QToolButton:hover {
                background: #2f3336;
            }
            QToolButton:checked {
                background: #0a84ff;
                border-color: #5fb1ff;
            }
            """
        )


class ColorButton(QToolButton):
    def __init__(self, color: str, tooltip: str, on_click):
        super().__init__()
        self.color = color
        self.clicked.connect(on_click)
        self.setCheckable(True)
        self.setToolTip(tooltip)
        self.setCursor(Qt.PointingHandCursor)
        self.setFixedSize(24, 24)
        self.setStyleSheet(
            f"""
            QToolButton {{
                background: {color};
                border: 2px solid rgba(255,255,255,0.35);
                border-radius: 12px;
            }}
            QToolButton:hover {{
                border-color: rgba(255,255,255,0.8);
            }}
            QToolButton:checked {{
                border-color: white;
                border-width: 3px;
            }}
            """
        )


class CaptureOverlay(QWidget):
    finished = Signal()

    def __init__(self, tray: QSystemTrayIcon | None, settings: dict):
        super().__init__(None)
        self.tray = tray
        self.settings = settings
        self.setWindowFlags(Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool)
        self.setAttribute(Qt.WA_DeleteOnClose)
        self.setMouseTracking(True)

        self.desktop_rect = QGuiApplication.primaryScreen().virtualGeometry()
        self.setGeometry(self.desktop_rect)

        self.full_pixmap = self.capture_desktop()
        self.selection_rect = QRect()
        self.start_point: QPoint | None = None
        self.preview_end: QPoint | None = None
        self.mode = "select"
        self.tool: str | None = None
        self.shape_color = QColor("#ff3b30")
        self.dragging = False
        self.annotations: list[dict] = []
        self.undo_stack: list[dict] = []
        self.drag_action: str | None = None
        self.active_handle: str | None = None
        self.base_rect = QRect()
        self.move_anchor = QPoint()
        self.hover_rect = QRect()
        self.hover_kind = ""
        self.hover_hwnd = 0
        self.toast_label = QLabel(self)
        self.toast_label.hide()
        self.toast_label.setStyleSheet(
            """
            QLabel {
                color: white;
                background: rgba(18, 18, 18, 230);
                border: 1px solid rgba(255,255,255,35);
                border-radius: 10px;
                padding: 10px 14px;
                font-size: 14px;
            }
            """
        )

        self.toolbar = self.build_toolbar()
        self.update_tool_ui()
        self.toolbar.hide()
        self.showFullScreen()
        self.activateWindow()
        self.raise_()
        QTimer.singleShot(0, self.prime_hover_selection)

    def capture_desktop(self) -> QPixmap:
        bbox = (
            self.desktop_rect.left(),
            self.desktop_rect.top(),
            self.desktop_rect.right() + 1,
            self.desktop_rect.bottom() + 1,
        )
        image = ImageGrab.grab(bbox=bbox, all_screens=True)
        return QPixmap.fromImage(qimage_from_pil(image))

    def build_toolbar(self) -> QFrame:
        bar = QFrame(self)
        bar.setStyleSheet(
            """
            QFrame {
                background: rgba(18, 18, 18, 230);
                border: 1px solid rgba(255,255,255,30);
                border-radius: 12px;
            }
            """
        )
        layout = QHBoxLayout(bar)
        layout.setContentsMargins(10, 8, 10, 8)
        layout.setSpacing(8)
        self.tool_buttons: dict[str, ToolButton] = {}
        for tool, label, tooltip in [
            ("arrow", "➜", "箭头"),
            ("rect", "□", "矩形"),
            ("circle", "○", "圆形"),
            ("mosaic", "▦", "打码"),
        ]:
            button = ToolButton(label, lambda checked=False, name=tool: self.set_tool(name), tooltip=tooltip, checkable=True)
            self.tool_buttons[tool] = button
            layout.addWidget(button)

        for label, callback, tooltip in [
            ("⎘", self.copy_and_close, "复制（空格）"),
            ("⬇", self.save_selection, "保存"),
        ]:
            layout.addWidget(ToolButton(label, callback, tooltip=tooltip))

        self.color_buttons: dict[str, ColorButton] = {}
        for color, tooltip in COLOR_OPTIONS:
            button = ColorButton(color, tooltip, lambda checked=False, value=color: self.set_shape_color(value))
            self.color_buttons[color] = button
            layout.addWidget(button)

        bar.adjustSize()
        return bar

    def set_tool(self, tool: str) -> None:
        self.tool = tool
        self.update_tool_ui()
        self.update()

    def set_shape_color(self, color: str) -> None:
        self.shape_color = QColor(color)
        self.update_tool_ui()
        self.update()

    def update_tool_ui(self) -> None:
        for name, button in self.tool_buttons.items():
            button.setChecked(name == self.tool)
        show_colors = self.tool in {"rect", "circle"}
        for color, button in self.color_buttons.items():
            button.setVisible(show_colors)
            button.setChecked(show_colors and self.shape_color.name().lower() == color.lower())
        self.toolbar.adjustSize()

    def prime_hover_selection(self) -> None:
        cursor = get_physical_cursor_qpoint()
        point = QPoint(cursor.x() - self.desktop_rect.left(), cursor.y() - self.desktop_rect.top())
        if self.rect().contains(point):
            self.update_hover_selection(point)

    def screen_point(self, point: QPoint) -> tuple[int, int]:
        return point.x() + self.desktop_rect.left(), point.y() + self.desktop_rect.top()

    def rect_contains_screen_point(self, rect: tuple[int, int, int, int], x: int, y: int) -> bool:
        left, top, right, bottom = rect
        return left <= x < right and top <= y < bottom

    def rect_tuple_to_qrect(self, rect: tuple[int, int, int, int]) -> QRect:
        left, top, right, bottom = rect
        return QRect(
            left - self.desktop_rect.left(),
            top - self.desktop_rect.top(),
            max(0, right - left),
            max(0, bottom - top),
        )

    def is_own_window(self, hwnd: int) -> bool:
        if not hwnd:
            return False
        try:
            return win32process.GetWindowThreadProcessId(hwnd)[1] == os.getpid()
        except Exception:
            return False

    def is_valid_target_window(self, hwnd: int, screen_x: int, screen_y: int) -> bool:
        if not hwnd or not win32gui.IsWindow(hwnd) or not win32gui.IsWindowVisible(hwnd) or win32gui.IsIconic(hwnd):
            return False
        if self.is_own_window(hwnd):
            return False
        try:
            class_name = win32gui.GetClassName(hwnd)
            rect = win32gui.GetWindowRect(hwnd)
        except Exception:
            return False
        if class_name in IGNORED_WINDOW_CLASSES:
            return False
        if rect[2] - rect[0] < MIN_SELECTION or rect[3] - rect[1] < MIN_SELECTION:
            return False
        return self.rect_contains_screen_point(rect, screen_x, screen_y)

    def find_underlying_top_window(self, screen_x: int, screen_y: int) -> int:
        point = (screen_x, screen_y)
        try:
            hovered = win32gui.WindowFromPoint(point)
        except Exception:
            hovered = 0
        if hovered and not self.is_own_window(hovered):
            try:
                root = win32gui.GetAncestor(hovered, win32con.GA_ROOT)
            except Exception:
                root = hovered
            if self.is_valid_target_window(root, screen_x, screen_y):
                return root

        current = win32gui.GetAncestor(int(self.winId()), win32con.GA_ROOT)
        seen: set[int] = set()
        while current:
            current = win32gui.GetWindow(current, win32con.GW_HWNDNEXT)
            if not current or current in seen:
                break
            seen.add(current)
            if self.is_valid_target_window(current, screen_x, screen_y):
                return current
        return 0

    def find_deepest_child_window(self, hwnd: int, screen_x: int, screen_y: int) -> int:
        current = hwnd
        while current:
            try:
                client_x, client_y = win32gui.ScreenToClient(current, (screen_x, screen_y))
                child = USER32.RealChildWindowFromPoint(current, wintypes.POINT(client_x, client_y))
            except Exception:
                break
            if not child or child == current or self.is_own_window(child):
                break
            current = child
        return current

    def uia_rect_for_point(self, hwnd: int, screen_x: int, screen_y: int) -> QRect:
        if UIAElementInfo is None or not hwnd:
            return QRect()
        try:
            current = UIAElementInfo(hwnd, cache_enable=True)
            best = current
            best_area = (current.rectangle.right - current.rectangle.left) * (current.rectangle.bottom - current.rectangle.top)
            while True:
                next_best = None
                next_area = best_area
                for child in current.iter_children():
                    if not child.visible:
                        continue
                    rect = child.rectangle
                    width = rect.right - rect.left
                    height = rect.bottom - rect.top
                    if width < MIN_SELECTION or height < MIN_SELECTION:
                        continue
                    if not self.rect_contains_screen_point((rect.left, rect.top, rect.right, rect.bottom), screen_x, screen_y):
                        continue
                    area = width * height
                    if area <= next_area:
                        next_best = child
                        next_area = area
                if next_best is None:
                    break
                best = next_best
                current = next_best
                best_area = next_area
            rect = best.rectangle
            return self.rect_tuple_to_qrect((rect.left, rect.top, rect.right, rect.bottom))
        except Exception:
            return QRect()

    def update_hover_selection(self, point: QPoint) -> None:
        screen_x, screen_y = self.screen_point(point)
        hwnd = self.find_underlying_top_window(screen_x, screen_y)
        if not hwnd:
            self.hover_rect = QRect()
            self.hover_kind = ""
            self.hover_hwnd = 0
            self.selection_rect = QRect()
            self.update()
            return

        target_hwnd = self.find_deepest_child_window(hwnd, screen_x, screen_y)
        target_rect = QRect()
        target_kind = "窗口"
        if target_hwnd:
            try:
                target_rect = self.rect_tuple_to_qrect(win32gui.GetWindowRect(target_hwnd))
            except Exception:
                target_rect = QRect()
            if target_hwnd != hwnd:
                target_kind = "控件"

        uia_rect = self.uia_rect_for_point(hwnd, screen_x, screen_y)
        if uia_rect.isValid():
            if not target_rect.isValid() or (uia_rect.width() * uia_rect.height()) <= (target_rect.width() * target_rect.height()):
                target_rect = uia_rect
                target_kind = "控件"

        if not target_rect.isValid() or not target_rect.intersects(self.rect()):
            self.hover_rect = QRect()
            self.hover_kind = ""
            self.hover_hwnd = 0
            self.selection_rect = QRect()
            self.update()
            return

        bounded = target_rect.intersected(self.rect())
        if bounded == self.hover_rect and self.hover_kind == target_kind and self.hover_hwnd == target_hwnd:
            return
        self.hover_rect = bounded
        self.hover_kind = target_kind
        self.hover_hwnd = target_hwnd
        self.selection_rect = QRect(bounded)
        self.update()

    def enter_edit_mode(self) -> None:
        self.mode = "edit"
        self.tool = None
        self.hover_kind = ""
        self.hover_hwnd = 0
        self.undo_stack.clear()
        self.update_tool_ui()
        self.position_toolbar()

    def selected_pixmap(self) -> QPixmap | None:
        if not self.selection_rect.isValid():
            return None
        return self.full_pixmap.copy(self.selection_rect)

    def snapshot_state(self) -> dict:
        return {
            "selection_rect": QRect(self.selection_rect),
            "annotations": [item.copy() for item in self.annotations],
            "tool": self.tool,
            "shape_color": self.shape_color.name(),
        }

    def push_undo_state(self) -> None:
        self.undo_stack.append(self.snapshot_state())

    def push_snapshot(self, snapshot: dict) -> None:
        self.undo_stack.append(
            {
                "selection_rect": QRect(snapshot["selection_rect"]),
                "annotations": [item.copy() for item in snapshot["annotations"]],
                "tool": snapshot["tool"],
                "shape_color": snapshot["shape_color"],
            }
        )

    def undo_last(self) -> None:
        if not self.undo_stack:
            return
        snapshot = self.undo_stack.pop()
        self.selection_rect = QRect(snapshot["selection_rect"])
        self.annotations = [item.copy() for item in snapshot["annotations"]]
        self.tool = snapshot["tool"]
        self.shape_color = QColor(snapshot["shape_color"])
        self.update_tool_ui()
        self.position_toolbar()
        self.update()

    def selection_local_pos(self, point: QPoint) -> QPoint:
        return point - self.selection_rect.topLeft()

    def position_toolbar(self) -> None:
        if not self.selection_rect.isValid():
            return
        self.toolbar.adjustSize()
        margin = 12
        x = max(margin, min(self.selection_rect.right() - self.toolbar.width(), self.width() - self.toolbar.width() - margin))
        y = self.selection_rect.bottom() + margin
        if y + self.toolbar.height() > self.height() - margin:
            y = self.selection_rect.top() - self.toolbar.height() - margin
        self.toolbar.move(x, max(margin, y))
        self.toolbar.show()

    def render_selected_pixmap(self) -> QPixmap | None:
        base = self.selected_pixmap()
        if base is None:
            return None

        painter = QPainter(base)
        painter.setRenderHint(QPainter.Antialiasing)
        for item in self.annotations:
            self.draw_annotation(painter, item)

        if self.dragging and self.mode == "edit" and self.start_point and self.preview_end:
            preview = {
                "tool": self.tool,
                "start": self.selection_local_pos(self.start_point),
                "end": self.selection_local_pos(self.preview_end),
            }
            if self.tool in {"rect", "circle"}:
                preview["color"] = self.shape_color.name()
            self.draw_annotation(painter, preview, preview_only=True)
        painter.end()
        return base

    def show_toast(self, message: str, is_error: bool = False, duration_ms: int = 2200) -> None:
        border_color = "rgba(255,255,255,35)"
        if is_error:
            background = "rgba(139, 26, 26, 238)"
            border_color = "rgba(255,180,180,90)"
        else:
            background = "rgba(18, 18, 18, 230)"

        self.toast_label.setStyleSheet(
            f"""
            QLabel {{
                color: white;
                background: {background};
                border: 1px solid {border_color};
                border-radius: 10px;
                padding: 10px 14px;
                font-size: 14px;
            }}
            """
        )
        self.toast_label.setText(message)
        self.toast_label.adjustSize()
        x = max(16, (self.width() - self.toast_label.width()) // 2)
        y = max(16, self.selection_rect.top() - self.toast_label.height() - 20) if self.selection_rect.isValid() else 24
        self.toast_label.move(x, y)
        self.toast_label.show()
        self.toast_label.raise_()
        QTimer.singleShot(duration_ms, self.toast_label.hide)

    def close_after_operation(self, message: str | None = None) -> None:
        if message and self.tray is not None:
            self.tray.showMessage(APP_TITLE, message, QSystemTrayIcon.Information, 1800)
        QTimer.singleShot(120, self.close)

    def save_pixmap_to_path(self, pixmap: QPixmap, target: Path, image_format: str) -> None:
        target.parent.mkdir(parents=True, exist_ok=True)
        image = pixmap.toImage()
        if image_format in {"JPEG", "BMP"}:
            image = image.convertToFormat(QImage.Format_RGB32)

        output = QPixmap.fromImage(image)
        if not output.save(str(target), image_format):
            raise RuntimeError(f"保存失败：{target}")

    def handle_rects(self) -> dict[str, QRect]:
        if not self.selection_rect.isValid():
            return {}
        size = 10
        half = size // 2
        rect = self.selection_rect
        points = {
            "tl": rect.topLeft(),
            "tm": QPoint(rect.center().x(), rect.top()),
            "tr": rect.topRight(),
            "ml": QPoint(rect.left(), rect.center().y()),
            "mr": QPoint(rect.right(), rect.center().y()),
            "bl": rect.bottomLeft(),
            "bm": QPoint(rect.center().x(), rect.bottom()),
            "br": rect.bottomRight(),
        }
        return {name: QRect(point.x() - half, point.y() - half, size, size) for name, point in points.items()}

    def hit_handle(self, point: QPoint) -> str | None:
        for name, rect in self.handle_rects().items():
            if rect.contains(point):
                return name
        return None

    def moved_rect(self, origin: QRect, delta: QPoint) -> QRect:
        rect = QRect(origin)
        rect.translate(delta)
        if rect.left() < 0:
            rect.moveLeft(0)
        if rect.top() < 0:
            rect.moveTop(0)
        if rect.right() > self.rect().right():
            rect.moveRight(self.rect().right())
        if rect.bottom() > self.rect().bottom():
            rect.moveBottom(self.rect().bottom())
        return rect

    def resized_rect(self, origin: QRect, handle: str, point: QPoint) -> QRect:
        left = origin.left()
        right = origin.right()
        top = origin.top()
        bottom = origin.bottom()

        if "l" in handle:
            left = point.x()
        if "r" in handle:
            right = point.x()
        if "t" in handle:
            top = point.y()
        if "b" in handle:
            bottom = point.y()
        if handle == "tm":
            top = point.y()
        elif handle == "bm":
            bottom = point.y()
        elif handle == "ml":
            left = point.x()
        elif handle == "mr":
            right = point.x()

        rect = QRect(QPoint(left, top), QPoint(right, bottom)).normalized()
        rect = rect.intersected(self.rect())
        if rect.width() < MIN_SELECTION:
            if handle in {"tl", "ml", "bl"}:
                rect.setLeft(rect.right() - MIN_SELECTION)
            else:
                rect.setRight(rect.left() + MIN_SELECTION)
        if rect.height() < MIN_SELECTION:
            if handle in {"tl", "tm", "tr"}:
                rect.setTop(rect.bottom() - MIN_SELECTION)
            else:
                rect.setBottom(rect.top() + MIN_SELECTION)
        return rect.intersected(self.rect())

    def draw_annotation(self, painter: QPainter, item: dict, preview_only: bool = False) -> None:
        tool = item["tool"]
        start = item["start"]
        end = item["end"]
        item_color = QColor(item.get("color", "#ff3b30"))

        if tool == "arrow":
            painter.setPen(QPen(ARROW_COLOR, 5, Qt.SolidLine, Qt.RoundCap, Qt.RoundJoin))
            painter.drawLine(start, end)
            dx = end.x() - start.x()
            dy = end.y() - start.y()
            if dx == 0 and dy == 0:
                return
            angle = math.atan2(dy, dx)
            head_len = max(18, min(34, int(math.dist((start.x(), start.y()), (end.x(), end.y())) * 0.16)))
            head_angle = math.radians(28)
            p1 = QPoint(int(end.x() - head_len * math.cos(angle - head_angle)), int(end.y() - head_len * math.sin(angle - head_angle)))
            p2 = QPoint(int(end.x() - head_len * math.cos(angle + head_angle)), int(end.y() - head_len * math.sin(angle + head_angle)))
            painter.setBrush(ARROW_COLOR)
            painter.drawPolygon([end, p1, p2])
            return

        rect = QRect(start, end).normalized()
        if rect.width() < 2 or rect.height() < 2:
            return

        if tool == "rect":
            painter.setBrush(Qt.NoBrush)
            painter.setPen(QPen(item_color, 4))
            painter.drawRect(rect)
            return

        if tool == "circle":
            painter.setBrush(Qt.NoBrush)
            painter.setPen(QPen(item_color, 4))
            painter.drawEllipse(rect)
            return

        if tool == "mosaic":
            device = painter.device()
            if isinstance(device, QPixmap):
                source = device.copy(rect)
                tiny = source.scaled(max(1, rect.width() // 14), max(1, rect.height() // 14), Qt.IgnoreAspectRatio, Qt.SmoothTransformation)
                mosaic = tiny.scaled(rect.size(), Qt.IgnoreAspectRatio, Qt.FastTransformation)
                painter.drawPixmap(rect.topLeft(), mosaic)
            if preview_only:
                painter.setPen(QPen(MOSAIC_OUTLINE, 2, Qt.DashLine))
                painter.setBrush(Qt.NoBrush)
                painter.drawRect(rect)

    def copy_and_close(self) -> None:
        rendered = self.render_selected_pixmap()
        if rendered is None:
            return
        try:
            pil_image_to_clipboard(qpixmap_to_pil_image(rendered))
        except Exception as exc:
            try:
                QApplication.clipboard().setImage(rendered.toImage())
                QApplication.processEvents()
            except Exception as fallback_exc:
                self.show_toast(f"复制失败：{exc}; 兜底复制也失败：{fallback_exc}", is_error=True, duration_ms=3200)
                return
        self.close_after_operation("截图已复制到剪贴板")

    def save_selection(self) -> None:
        rendered = self.render_selected_pixmap()
        if rendered is None:
            return
        save_dir = Path(self.settings.get("save_dir") or default_save_dir())
        target = save_dir / "liteshot_capture.png"
        counter = 1
        while target.exists():
            target = save_dir / f"liteshot_capture_{counter}.png"
            counter += 1
        try:
            self.save_pixmap_to_path(rendered, target, "PNG")
        except Exception as exc:
            self.show_toast(f"保存失败：{exc}", is_error=True, duration_ms=2600)
            return
        self.close_after_operation(f"截图已保存到 {target}")

    def mousePressEvent(self, event) -> None:
        if event.button() != Qt.LeftButton:
            return
        point = clamp_point(event.position().toPoint(), self.rect())
        if self.mode == "select":
            self.start_point = point
            self.base_rect = QRect(self.hover_rect)
            self.dragging = True
            if self.hover_rect.isValid() and self.hover_rect.contains(point):
                self.selection_rect = QRect(self.hover_rect)
                self.drag_action = "hover-click"
            else:
                self.selection_rect = QRect(point, point)
                self.drag_action = "select"
        else:
            handle = self.hit_handle(point)
            if handle and not self.annotations:
                self.start_point = point
                self.base_rect = QRect(self.selection_rect)
                self.active_handle = handle
                self.drag_action = "resize"
                self.dragging = True
            elif self.selection_rect.contains(point):
                if self.tool is None and not self.annotations:
                    self.start_point = point
                    self.base_rect = QRect(self.selection_rect)
                    self.move_anchor = point - self.selection_rect.topLeft()
                    self.drag_action = "move"
                    self.dragging = True
                elif self.tool is not None:
                    self.start_point = point
                    self.preview_end = point
                    self.dragging = True
                    self.drag_action = "annotate"
        if self.drag_action in {"move", "resize"}:
            self.toolbar.hide()
        self.update()

    def mouseMoveEvent(self, event) -> None:
        point = clamp_point(event.position().toPoint(), self.rect())
        if self.mode == "select" and not self.dragging:
            self.update_hover_selection(point)
            return
        if not self.dragging or self.start_point is None:
            return
        if self.drag_action == "select":
            self.selection_rect = normalize_rect(self.start_point, point)
        elif self.drag_action == "hover-click":
            if (point - self.start_point).manhattanLength() > HOVER_CLICK_TOLERANCE:
                self.drag_action = "select"
                self.selection_rect = normalize_rect(self.start_point, point)
        elif self.drag_action == "move":
            top_left = point - self.move_anchor
            self.selection_rect = self.moved_rect(self.base_rect, top_left - self.base_rect.topLeft())
        elif self.drag_action == "resize" and self.active_handle:
            self.selection_rect = self.resized_rect(self.base_rect, self.active_handle, point)
        elif self.drag_action == "annotate":
            self.preview_end = point
        self.update()

    def mouseReleaseEvent(self, event) -> None:
        if event.button() != Qt.LeftButton or not self.dragging or self.start_point is None:
            return
        point = clamp_point(event.position().toPoint(), self.rect())
        self.dragging = False
        if self.drag_action == "hover-click":
            self.selection_rect = QRect(self.base_rect)
            if self.selection_rect.isValid():
                self.enter_edit_mode()
        elif self.drag_action == "select":
            self.selection_rect = normalize_rect(self.start_point, point)
            if self.selection_rect.width() < MIN_SELECTION or self.selection_rect.height() < MIN_SELECTION:
                self.selection_rect = QRect()
                self.hover_rect = QRect()
                self.hover_kind = ""
                self.hover_hwnd = 0
                self.start_point = None
                self.drag_action = None
                self.update()
                return
            self.enter_edit_mode()
        elif self.drag_action in {"move", "resize"}:
            if self.selection_rect != self.base_rect:
                self.push_snapshot(
                    {
                        "selection_rect": QRect(self.base_rect),
                        "annotations": [item.copy() for item in self.annotations],
                        "tool": self.tool,
                        "shape_color": self.shape_color.name(),
                    }
                )
            self.position_toolbar()
        elif self.drag_action == "annotate":
            if self.preview_end and self.selection_rect.contains(self.preview_end):
                self.push_undo_state()
                annotation = {
                    "tool": self.tool,
                    "start": self.selection_local_pos(self.start_point),
                    "end": self.selection_local_pos(self.preview_end),
                }
                if self.tool in {"rect", "circle"}:
                    annotation["color"] = self.shape_color.name()
                self.annotations.append(annotation)
            self.preview_end = None
        self.start_point = None
        self.drag_action = None
        self.active_handle = None
        if self.mode == "select":
            self.update_hover_selection(point)
        self.update()

    def keyPressEvent(self, event) -> None:
        if event.key() == Qt.Key_Escape:
            self.close()
            return
        if self.mode != "edit":
            return
        if event.matches(QKeySequence.Undo):
            self.undo_last()
        elif event.key() == Qt.Key_A:
            self.set_tool("arrow")
        elif event.key() == Qt.Key_R:
            self.set_tool("rect")
        elif event.key() == Qt.Key_C:
            self.set_tool("circle")
        elif event.key() == Qt.Key_M:
            self.set_tool("mosaic")
        elif event.key() in {Qt.Key_Space, Qt.Key_Return, Qt.Key_Enter}:
            self.copy_and_close()
        elif event.matches(QKeySequence.Copy):
            self.copy_and_close()

    def paintEvent(self, _event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.SmoothPixmapTransform)
        painter.drawPixmap(self.rect(), self.full_pixmap)
        painter.fillRect(self.rect(), MASK_COLOR)

        if not self.selection_rect.isValid():
            painter.setPen(QPen(Qt.white, 2))
            painter.drawText(24, 36, "移动鼠标识别窗口或控件，单击确认，拖动手动框选  Esc 取消")
            painter.end()
            return

        selected = self.render_selected_pixmap() if self.mode == "edit" else self.full_pixmap.copy(self.selection_rect)
        painter.drawPixmap(self.selection_rect.topLeft(), selected)
        painter.setPen(QPen(SELECTION_BORDER, 2))
        painter.setBrush(Qt.NoBrush)
        painter.drawRect(self.selection_rect)
        if self.mode == "edit" and not self.annotations:
            painter.setBrush(Qt.white)
            painter.setPen(Qt.NoPen)
            for handle_rect in self.handle_rects().values():
                painter.drawRect(handle_rect)

        painter.setPen(QPen(Qt.white, 2))
        if self.mode == "select":
            painter.drawText(24, 36, "移动鼠标识别窗口或控件，单击确认，拖动手动框选  Esc 取消")
            if self.hover_kind:
                label_rect = QRect(self.selection_rect.left(), max(12, self.selection_rect.top() - 28), 86, 24)
                painter.fillRect(label_rect, QColor(18, 18, 18, 220))
                painter.drawText(label_rect.adjusted(8, 0, -8, 0), Qt.AlignVCenter | Qt.AlignLeft, self.hover_kind)
        else:
            painter.drawText(self.selection_rect.left(), max(30, self.selection_rect.top() - 12), "A 箭头  R 矩形  C 圆形  M 打码  Ctrl+C 复制  Esc 取消")
        painter.end()

    def closeEvent(self, event) -> None:
        self.finished.emit()
        super().closeEvent(event)


class TrayController(QObject):
    takeover_requested = Signal()
    reload_requested = Signal()

    def __init__(self, app: QApplication, managed_mode: bool = False):
        super().__init__()
        self.app = app
        self.managed_mode = managed_mode
        self.settings = load_settings()
        self.overlay: CaptureOverlay | None = None
        self.cleaned_up = False
        self.takeover_requested.connect(self.quit)
        self.reload_requested.connect(self.reload_settings_from_disk)
        self.hotkey_window = HotkeyWindow()
        self.hotkey_window.hotkey_pressed.connect(self.queue_capture)
        self.hotkey_window.refresh_requested.connect(self.refresh_hotkey)
        self.last_heartbeat = time.monotonic()
        self.instance_server = SingleInstanceServer(self.takeover_requested.emit, self.reload_requested.emit, self.status_snapshot)
        self.instance_server.start()
        # Main-thread heartbeat: the host app reads it via "status" to detect a hung UI thread.
        self.heartbeat_timer = QTimer(self)
        self.heartbeat_timer.timeout.connect(self.beat)
        self.heartbeat_timer.start(HEARTBEAT_INTERVAL_MS)
        # Self-heal: retry registration periodically if another app held the hotkey at startup.
        self.self_heal_timer = QTimer(self)
        self.self_heal_timer.timeout.connect(self.self_heal)
        self.self_heal_timer.start(HOTKEY_SELF_HEAL_INTERVAL_MS)
        log_event("started", managed=managed_mode, hotkey=self.settings.get("hotkey"))

        self.icon = create_app_icon()
        self.tray = None
        self.menu = None
        if not self.managed_mode:
            self.tray = QSystemTrayIcon(self.icon, self.app)
            self.tray.setToolTip(APP_TITLE)
            self.menu = QMenu()

            capture_action = QAction("立即截图", self.menu)
            capture_action.triggered.connect(self.queue_capture)
            self.menu.addAction(capture_action)

            settings_action = QAction("快捷键设置", self.menu)
            settings_action.triggered.connect(self.open_settings)
            self.menu.addAction(settings_action)

            self.menu.addSeparator()
            quit_action = QAction("退出", self.menu)
            quit_action.triggered.connect(self.quit)
            self.menu.addAction(quit_action)

            self.tray.setContextMenu(self.menu)
            self.tray.activated.connect(self.on_tray_activated)
            self.tray.show()

        try:
            self.register_hotkey(self.settings["hotkey"])
            if self.tray is not None:
                self.tray.showMessage(APP_TITLE, f"已在后台运行，快捷键：{self.settings['hotkey']}", QSystemTrayIcon.Information, 2400)
        except Exception as exc:
            # Do not exit in managed mode: stay alive, report via "status", and keep retrying.
            if self.tray is not None:
                self.tray.showMessage(APP_TITLE, f"{exc}，请右键托盘图标修改。", QSystemTrayIcon.Warning, 3200)

    def beat(self) -> None:
        self.last_heartbeat = time.monotonic()

    def status_snapshot(self) -> dict:
        window = self.hotkey_window
        return {
            "hotkey": self.settings.get("hotkey") or DEFAULT_HOTKEY,
            "registered": bool(window.registered),
            "registered_hotkey": window.registered_hotkey if window.registered else "",
            "error": window.last_error,
            "error_code": window.last_error_code,
            "heartbeat_age_ms": int((time.monotonic() - self.last_heartbeat) * 1000),
            "pid": os.getpid(),
        }

    def self_heal(self) -> None:
        desired = self.settings.get("hotkey") or DEFAULT_HOTKEY
        window = self.hotkey_window
        if window.registered and window.registered_hotkey == desired:
            return
        try:
            self.register_hotkey(desired)
            log_event("hotkey_self_healed", hotkey=desired)
        except Exception:
            pass

    def refresh_hotkey(self, reason: str) -> None:
        """Re-register after resume/unlock: some systems silently drop hotkeys across these."""
        desired = self.settings.get("hotkey") or DEFAULT_HOTKEY
        log_event("hotkey_refresh", reason=reason, hotkey=desired)
        try:
            self.register_hotkey(desired)
        except Exception:
            pass

    def on_tray_activated(self, reason):
        if self.tray is not None and reason == QSystemTrayIcon.Trigger:
            self.queue_capture()

    def queue_capture(self) -> None:
        QTimer.singleShot(120, self.start_capture)

    def unregister_hotkey(self) -> None:
        self.hotkey_window.unregister_hotkey()

    def register_hotkey(self, hotkey: str) -> None:
        self.hotkey_window.register_hotkey(hotkey)

    def start_capture(self) -> None:
        if self.overlay is not None:
            self.overlay.close()
        try:
            self.overlay = CaptureOverlay(self.tray, self.settings)
            self.overlay.finished.connect(self.clear_overlay)
        except Exception as exc:
            self.overlay = None
            log_event("capture_failed", error=f"{type(exc).__name__}: {exc}")
            if self.tray is not None:
                self.tray.showMessage(APP_TITLE, f"截图启动失败：{exc}", QSystemTrayIcon.Warning, 3500)
            else:
                QMessageBox.warning(None, APP_TITLE, f"截图启动失败：{exc}")

    def clear_overlay(self) -> None:
        self.overlay = None

    def open_settings(self) -> None:
        if self.managed_mode:
            return
        dialog = SettingsDialog(self.settings["hotkey"], self.settings.get("save_dir") or default_save_dir())
        if dialog.exec() != QDialog.Accepted:
            return
        hotkey = dialog.hotkey()
        try:
            self.register_hotkey(hotkey)
        except Exception as exc:
            QMessageBox.warning(None, APP_TITLE, str(exc))
            return
        self.settings["hotkey"] = hotkey
        self.settings["save_dir"] = dialog.save_dir()
        save_settings(self.settings)
        if self.tray is not None:
            self.tray.showMessage(APP_TITLE, f"设置已更新，快捷键：{hotkey}", QSystemTrayIcon.Information, 2000)

    def reload_settings_from_disk(self) -> None:
        settings = load_settings()
        hotkey = settings.get("hotkey") or DEFAULT_HOTKEY
        self.settings = settings
        try:
            self.register_hotkey(hotkey)
        except Exception as exc:
            # Previously this raised inside a Qt slot and left no hotkey registered at all.
            log_event("reload_register_failed", hotkey=hotkey, error=str(exc))

    def quit(self) -> None:
        self.cleanup()
        if self.tray is not None:
            self.tray.hide()
        self.app.quit()

    def cleanup(self) -> None:
        if self.cleaned_up:
            return
        self.cleaned_up = True
        if self.overlay is not None:
            self.overlay.close()
            self.overlay = None
        self.hotkey_window.unregister_hotkey()
        self.instance_server.close()


def main() -> None:
    install_exception_logging()
    enable_dpi_awareness()
    os.environ.setdefault("QT_ENABLE_HIGHDPI_SCALING", "1")
    if request_existing_instance_shutdown():
        wait_for_existing_instance_shutdown()
    managed_mode = "--managed" in sys.argv
    app = QApplication(sys.argv)
    app.setQuitOnLastWindowClosed(False)
    app.setWindowIcon(create_app_icon())
    controller = TrayController(app, managed_mode=managed_mode)
    app.aboutToQuit.connect(controller.cleanup)
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
