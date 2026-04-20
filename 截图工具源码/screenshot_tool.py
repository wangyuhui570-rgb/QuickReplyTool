import ctypes
import io
import math
import os
from pathlib import Path
import tkinter as tk
from tkinter import filedialog, messagebox

from PIL import Image, ImageDraw, ImageGrab, ImageTk


APP_TITLE = "轻截"
MIN_SELECTION = 8
DEFAULT_SAVE_DIR = Path.home() / "Pictures"
ARROW_COLOR = "#ff3b30"
RECT_COLOR = "#ffd60a"
ARROW_WIDTH = 5
RECT_WIDTH = 4
PIXELATE_BLOCK = 14


def enable_dpi_awareness() -> None:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


def get_virtual_screen_bounds() -> tuple[int, int, int, int]:
    user32 = ctypes.windll.user32
    left = user32.GetSystemMetrics(76)
    top = user32.GetSystemMetrics(77)
    width = user32.GetSystemMetrics(78)
    height = user32.GetSystemMetrics(79)
    return left, top, width, height


def clamp(value: int, low: int, high: int) -> int:
    return max(low, min(value, high))


def normalize_rect(x1: int, y1: int, x2: int, y2: int) -> tuple[int, int, int, int]:
    return min(x1, x2), min(y1, y2), max(x1, x2), max(y1, y2)


def pil_image_to_clipboard(image: Image.Image) -> None:
    output = io.BytesIO()
    image.convert("RGB").save(output, "BMP")
    data = output.getvalue()[14:]

    GMEM_MOVEABLE = 0x0002
    CF_DIB = 8
    kernel32 = ctypes.windll.kernel32
    user32 = ctypes.windll.user32

    handle = kernel32.GlobalAlloc(GMEM_MOVEABLE, len(data))
    if not handle:
        raise RuntimeError("申请剪贴板内存失败")

    pointer = kernel32.GlobalLock(handle)
    if not pointer:
        kernel32.GlobalFree(handle)
        raise RuntimeError("写入剪贴板失败")

    ctypes.memmove(pointer, data, len(data))
    kernel32.GlobalUnlock(handle)

    if not user32.OpenClipboard(None):
        kernel32.GlobalFree(handle)
        raise RuntimeError("无法打开剪贴板，可能正被其他程序占用")

    try:
        user32.EmptyClipboard()
        if not user32.SetClipboardData(CF_DIB, handle):
            kernel32.GlobalFree(handle)
            raise RuntimeError("设置剪贴板内容失败")
        handle = None
    finally:
        user32.CloseClipboard()


def draw_arrow(draw: ImageDraw.ImageDraw, start: tuple[int, int], end: tuple[int, int]) -> None:
    x1, y1 = start
    x2, y2 = end
    if x1 == x2 and y1 == y2:
        return

    draw.line((x1, y1, x2, y2), fill=ARROW_COLOR, width=ARROW_WIDTH)

    angle = math.atan2(y2 - y1, x2 - x1)
    head_len = max(16, min(30, int(math.dist(start, end) * 0.18)))
    head_angle = math.radians(28)

    left = (
        x2 - head_len * math.cos(angle - head_angle),
        y2 - head_len * math.sin(angle - head_angle),
    )
    right = (
        x2 - head_len * math.cos(angle + head_angle),
        y2 - head_len * math.sin(angle + head_angle),
    )
    draw.polygon([(x2, y2), left, right], fill=ARROW_COLOR)


def pixelate_region(image: Image.Image, rect: tuple[int, int, int, int]) -> None:
    x1, y1, x2, y2 = normalize_rect(*rect)
    x1 = clamp(x1, 0, image.width)
    y1 = clamp(y1, 0, image.height)
    x2 = clamp(x2, 0, image.width)
    y2 = clamp(y2, 0, image.height)

    if x2 - x1 < 2 or y2 - y1 < 2:
        return

    region = image.crop((x1, y1, x2, y2))
    down_w = max(1, region.width // PIXELATE_BLOCK)
    down_h = max(1, region.height // PIXELATE_BLOCK)
    reduced = region.resize((down_w, down_h), Image.Resampling.BILINEAR)
    pixelated = reduced.resize(region.size, Image.Resampling.NEAREST)
    image.paste(pixelated, (x1, y1))


class CaptureOverlay(tk.Toplevel):
    def __init__(self, master, screenshot, bounds, on_done, on_cancel):
        super().__init__(master)
        self.screenshot = screenshot
        self.bounds = bounds
        self.on_done = on_done
        self.on_cancel = on_cancel

        self.start_x = 0
        self.start_y = 0
        self.current_x = 0
        self.current_y = 0
        self.dragging = False
        self.selection_rect = None
        self.mask_items = []
        self.hint_item = None

        self.overrideredirect(True)
        self.attributes("-topmost", True)

        left, top, width, height = bounds
        self.geometry(f"{width}x{height}{left:+d}{top:+d}")

        self.canvas = tk.Canvas(self, width=width, height=height, highlightthickness=0, cursor="crosshair")
        self.canvas.pack(fill="both", expand=True)

        self.photo = ImageTk.PhotoImage(self.screenshot)
        self.canvas.create_image(0, 0, anchor="nw", image=self.photo)

        self.hint_item = self.canvas.create_text(
            18,
            18,
            anchor="nw",
            fill="white",
            font=("Microsoft YaHei UI", 12, "bold"),
            text="拖动鼠标框选截图区域  Esc 取消",
        )

        self.bind("<Escape>", lambda _event: self.cancel())
        self.canvas.bind("<ButtonPress-1>", self.on_press)
        self.canvas.bind("<B1-Motion>", self.on_drag)
        self.canvas.bind("<ButtonRelease-1>", self.on_release)
        self.focus_force()

    def on_press(self, event):
        self.dragging = True
        self.start_x = clamp(event.x, 0, self.screenshot.width)
        self.start_y = clamp(event.y, 0, self.screenshot.height)
        self.current_x = self.start_x
        self.current_y = self.start_y
        self.redraw_selection()

    def on_drag(self, event):
        if not self.dragging:
            return
        self.current_x = clamp(event.x, 0, self.screenshot.width)
        self.current_y = clamp(event.y, 0, self.screenshot.height)
        self.redraw_selection()

    def on_release(self, event):
        if not self.dragging:
            return
        self.dragging = False
        self.current_x = clamp(event.x, 0, self.screenshot.width)
        self.current_y = clamp(event.y, 0, self.screenshot.height)
        x1, y1, x2, y2 = normalize_rect(self.start_x, self.start_y, self.current_x, self.current_y)
        if x2 - x1 < MIN_SELECTION or y2 - y1 < MIN_SELECTION:
            self.clear_selection()
            return

        cropped = self.screenshot.crop((x1, y1, x2, y2))
        self.destroy()
        self.on_done(cropped)

    def clear_selection(self):
        if self.selection_rect is not None:
            self.canvas.delete(self.selection_rect)
            self.selection_rect = None
        for item in self.mask_items:
            self.canvas.delete(item)
        self.mask_items.clear()

    def redraw_selection(self):
        self.clear_selection()
        x1, y1, x2, y2 = normalize_rect(self.start_x, self.start_y, self.current_x, self.current_y)
        width = self.screenshot.width
        height = self.screenshot.height

        if x2 - x1 < 1 or y2 - y1 < 1:
            return

        self.mask_items.extend(
            [
                self.canvas.create_rectangle(0, 0, width, y1, fill="black", stipple="gray50", outline=""),
                self.canvas.create_rectangle(0, y2, width, height, fill="black", stipple="gray50", outline=""),
                self.canvas.create_rectangle(0, y1, x1, y2, fill="black", stipple="gray50", outline=""),
                self.canvas.create_rectangle(x2, y1, width, y2, fill="black", stipple="gray50", outline=""),
            ]
        )
        self.selection_rect = self.canvas.create_rectangle(x1, y1, x2, y2, outline="#00e5ff", width=2)
        self.canvas.tag_raise(self.selection_rect)
        self.canvas.tag_raise(self.hint_item)

    def cancel(self):
        self.destroy()
        self.on_cancel()


class EditorWindow(tk.Toplevel):
    def __init__(self, master, image, on_retake, on_close):
        super().__init__(master)
        self.image = image
        self.on_retake = on_retake
        self.on_close_callback = on_close

        self.tool_var = tk.StringVar(value="arrow")
        self.zoom_mode = tk.StringVar(value="fit")
        self.status_var = tk.StringVar()
        self.undo_stack: list[Image.Image] = []
        self.status_after_id = None
        self.dirty = False

        self.scale = 1.0
        self.display_width = image.width
        self.display_height = image.height

        self.dragging = False
        self.start_x = 0
        self.start_y = 0
        self.preview_item = None

        self.title(f"{APP_TITLE} - 编辑")
        self.configure(bg="#202124")
        self.protocol("WM_DELETE_WINDOW", self.close_window)

        self.create_toolbar()
        self.create_canvas()
        self.update_base_status()
        self.refresh_canvas()
        self.bind_shortcuts()

    def create_toolbar(self):
        toolbar = tk.Frame(self, bg="#202124", padx=10, pady=10)
        toolbar.pack(fill="x")

        buttons = [
            ("重新截图", self.retake),
            ("箭头(A)", lambda: self.select_tool("arrow")),
            ("打码(M)", lambda: self.select_tool("mosaic")),
            ("矩形(R)", lambda: self.select_tool("rect")),
            ("适应窗口", lambda: self.set_zoom_mode("fit")),
            ("100%", lambda: self.set_zoom_mode("actual")),
            ("撤销", self.undo),
            ("复制", self.copy_to_clipboard),
            ("保存", self.save_image),
            ("关闭", self.close_window),
        ]
        for text, command in buttons:
            tk.Button(
                toolbar,
                text=text,
                command=command,
                bg="#2d2f31",
                fg="white",
                activebackground="#3c4043",
                activeforeground="white",
                relief="flat",
                padx=10,
                pady=6,
                font=("Microsoft YaHei UI", 10),
            ).pack(side="left", padx=(0, 8))

        tk.Label(
            self,
            textvariable=self.status_var,
            bg="#202124",
            fg="#d0d0d0",
            anchor="w",
            padx=12,
            pady=4,
            font=("Microsoft YaHei UI", 10),
        ).pack(fill="x")

    def create_canvas(self):
        frame = tk.Frame(self, bg="#202124")
        frame.pack(fill="both", expand=True, padx=12, pady=(0, 12))

        self.canvas = tk.Canvas(frame, bg="#111111", highlightthickness=0, cursor="crosshair")
        x_scroll = tk.Scrollbar(frame, orient="horizontal", command=self.canvas.xview)
        y_scroll = tk.Scrollbar(frame, orient="vertical", command=self.canvas.yview)
        self.canvas.configure(xscrollcommand=x_scroll.set, yscrollcommand=y_scroll.set)

        self.canvas.grid(row=0, column=0, sticky="nsew")
        y_scroll.grid(row=0, column=1, sticky="ns")
        x_scroll.grid(row=1, column=0, sticky="ew")
        frame.grid_rowconfigure(0, weight=1)
        frame.grid_columnconfigure(0, weight=1)

        self.canvas.bind("<ButtonPress-1>", self.on_press)
        self.canvas.bind("<B1-Motion>", self.on_drag)
        self.canvas.bind("<ButtonRelease-1>", self.on_release)

    def bind_shortcuts(self):
        self.bind("<Escape>", lambda _event: self.close_window())
        self.bind("<Control-z>", lambda _event: self.undo())
        self.bind("<Control-c>", lambda _event: self.copy_to_clipboard())
        self.bind("<Control-s>", lambda _event: self.save_image())
        self.bind("<Control-n>", lambda _event: self.retake())
        self.bind("a", lambda _event: self.select_tool("arrow"))
        self.bind("m", lambda _event: self.select_tool("mosaic"))
        self.bind("r", lambda _event: self.select_tool("rect"))

    def refresh_canvas(self):
        if self.zoom_mode.get() == "actual":
            self.scale = 1.0
        else:
            screen_w = max(1200, self.winfo_screenwidth() - 160)
            screen_h = max(800, self.winfo_screenheight() - 220)
            self.scale = min(1.0, screen_w / self.image.width, screen_h / self.image.height)
        self.display_width = max(1, int(self.image.width * self.scale))
        self.display_height = max(1, int(self.image.height * self.scale))

        display_image = self.image.resize((self.display_width, self.display_height), Image.Resampling.LANCZOS)
        self.photo = ImageTk.PhotoImage(display_image)

        viewport_w = min(self.display_width, self.winfo_screenwidth() - 120)
        viewport_h = min(self.display_height, self.winfo_screenheight() - 240)
        self.canvas.config(width=viewport_w, height=viewport_h, scrollregion=(0, 0, self.display_width, self.display_height))
        self.canvas.delete("all")
        self.canvas.create_image(0, 0, anchor="nw", image=self.photo)
        self.geometry(f"{viewport_w + 44}x{viewport_h + 132}+80+60")

    def select_tool(self, tool_name: str):
        self.tool_var.set(tool_name)
        self.update_base_status()

    def set_zoom_mode(self, mode: str):
        self.zoom_mode.set(mode)
        self.refresh_canvas()
        label = "适应窗口" if mode == "fit" else "100%"
        self.flash_status(f"缩放已切换为 {label}")

    def update_base_status(self):
        tool_name = {"arrow": "箭头", "mosaic": "打码", "rect": "矩形"}[self.tool_var.get()]
        zoom_name = "适应窗口" if self.zoom_mode.get() == "fit" else "100%"
        self.status_var.set(
            f"工具：{tool_name} | 缩放：{zoom_name} | A 箭头  M 打码  R 矩形  Ctrl+Z 撤销  Ctrl+C 复制  Ctrl+S 保存"
        )

    def flash_status(self, text: str, duration_ms: int = 2600):
        if self.status_after_id is not None:
            self.after_cancel(self.status_after_id)
            self.status_after_id = None
        self.status_var.set(text)
        self.status_after_id = self.after(duration_ms, self.update_base_status)

    def canvas_to_image(self, x: int, y: int) -> tuple[int, int]:
        image_x = clamp(int(round(x / self.scale)), 0, self.image.width)
        image_y = clamp(int(round(y / self.scale)), 0, self.image.height)
        return image_x, image_y

    def on_press(self, event):
        canvas_x = clamp(int(self.canvas.canvasx(event.x)), 0, self.display_width)
        canvas_y = clamp(int(self.canvas.canvasy(event.y)), 0, self.display_height)
        if not (0 <= canvas_x <= self.display_width and 0 <= canvas_y <= self.display_height):
            return
        self.dragging = True
        self.start_x = canvas_x
        self.start_y = canvas_y
        self.remove_preview()

    def on_drag(self, event):
        if not self.dragging:
            return

        current_x = clamp(int(self.canvas.canvasx(event.x)), 0, self.display_width)
        current_y = clamp(int(self.canvas.canvasy(event.y)), 0, self.display_height)
        tool = self.tool_var.get()
        self.remove_preview()

        if tool == "arrow":
            self.preview_item = self.canvas.create_line(
                self.start_x,
                self.start_y,
                current_x,
                current_y,
                fill=ARROW_COLOR,
                width=ARROW_WIDTH,
                arrow=tk.LAST,
                arrowshape=(16, 18, 6),
            )
        elif tool == "mosaic":
            self.preview_item = self.canvas.create_rectangle(
                self.start_x,
                self.start_y,
                current_x,
                current_y,
                outline="#00e5ff",
                width=2,
                dash=(6, 4),
            )
        else:
            self.preview_item = self.canvas.create_rectangle(
                self.start_x,
                self.start_y,
                current_x,
                current_y,
                outline=RECT_COLOR,
                width=2,
            )

    def on_release(self, event):
        if not self.dragging:
            return

        self.dragging = False
        current_x = clamp(int(self.canvas.canvasx(event.x)), 0, self.display_width)
        current_y = clamp(int(self.canvas.canvasy(event.y)), 0, self.display_height)
        self.remove_preview()

        if abs(current_x - self.start_x) < MIN_SELECTION and abs(current_y - self.start_y) < MIN_SELECTION:
            return

        start = self.canvas_to_image(self.start_x, self.start_y)
        end = self.canvas_to_image(current_x, current_y)

        self.undo_stack.append(self.image.copy())
        draw = ImageDraw.Draw(self.image)
        tool = self.tool_var.get()
        if tool == "arrow":
            draw_arrow(draw, start, end)
            self.flash_status("已添加箭头")
        elif tool == "mosaic":
            pixelate_region(self.image, (*start, *end))
            self.flash_status("已打码")
        else:
            x1, y1, x2, y2 = normalize_rect(*start, *end)
            draw.rectangle((x1, y1, x2, y2), outline=RECT_COLOR, width=RECT_WIDTH)
            self.flash_status("已添加矩形框")

        self.dirty = True
        self.refresh_canvas()

    def remove_preview(self):
        if self.preview_item is not None:
            self.canvas.delete(self.preview_item)
            self.preview_item = None

    def undo(self):
        if not self.undo_stack:
            self.flash_status("没有可撤销的操作")
            return
        self.image = self.undo_stack.pop()
        self.refresh_canvas()
        self.dirty = bool(self.undo_stack)
        self.flash_status("已撤销")

    def copy_to_clipboard(self):
        try:
            pil_image_to_clipboard(self.image)
        except Exception as exc:
            messagebox.showerror(APP_TITLE, str(exc), parent=self)
            return
        self.flash_status("已复制到剪贴板")

    def save_image(self):
        initial_dir = DEFAULT_SAVE_DIR if DEFAULT_SAVE_DIR.exists() else Path.cwd()
        file_path = filedialog.asksaveasfilename(
            parent=self,
            title="保存截图",
            initialdir=initial_dir,
            initialfile="screenshot.png",
            defaultextension=".png",
            filetypes=[("PNG 图片", "*.png"), ("JPEG 图片", "*.jpg;*.jpeg"), ("BMP 图片", "*.bmp")],
        )
        if not file_path:
            return

        extension = Path(file_path).suffix.lower()
        save_format = "PNG"
        image_to_save = self.image
        if extension in {".jpg", ".jpeg"}:
            save_format = "JPEG"
            image_to_save = self.image.convert("RGB")
        elif extension == ".bmp":
            save_format = "BMP"
            image_to_save = self.image.convert("RGB")

        image_to_save.save(file_path, save_format)
        self.dirty = False
        self.flash_status(f"已保存到 {file_path}")

    def retake(self):
        if not self.confirm_discard():
            return
        self.destroy()
        self.on_retake()

    def confirm_discard(self) -> bool:
        if not self.dirty:
            return True
        return messagebox.askyesno(
            APP_TITLE,
            "当前修改还没有保存，确定要丢弃并继续吗？",
            parent=self,
        )

    def close_window(self):
        if not self.confirm_discard():
            return
        self.destroy()
        self.on_close_callback()


class ScreenshotApp(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title(APP_TITLE)
        self.resizable(False, False)
        self.configure(bg="#202124")

        self.capture_overlay = None
        self.editor = None
        self.launcher_status_var = tk.StringVar(value="准备就绪")
        self.launcher_status_after_id = None

        self.protocol("WM_DELETE_WINDOW", self.destroy)
        self.build_launcher()
        self.bind("<Control-n>", lambda _event: self.start_capture())
        self.bind("<Return>", lambda _event: self.start_capture())

    def build_launcher(self):
        wrapper = tk.Frame(self, bg="#202124", padx=20, pady=20)
        wrapper.pack(fill="both", expand=True)

        tk.Label(
            wrapper,
            text=APP_TITLE,
            bg="#202124",
            fg="white",
            font=("Microsoft YaHei UI", 22, "bold"),
        ).pack(anchor="w")

        tk.Label(
            wrapper,
            text="轻量、不常驻后台的截图小工具",
            bg="#202124",
            fg="#c7c7c7",
            font=("Microsoft YaHei UI", 11),
        ).pack(anchor="w", pady=(4, 14))

        tk.Button(
            wrapper,
            text="开始截图",
            command=self.start_capture,
            bg="#0a84ff",
            fg="white",
            activebackground="#409cff",
            activeforeground="white",
            relief="flat",
            padx=20,
            pady=10,
            font=("Microsoft YaHei UI", 12, "bold"),
        ).pack(anchor="w")

        tips = [
            "只在打开时运行，关闭即退出，不驻留系统托盘。",
            "截图后可直接打码、画箭头、画矩形、撤销、复制、保存。",
            "快捷键：Enter/Ctrl+N 开始截图。",
        ]
        for tip in tips:
            tk.Label(
                wrapper,
                text=f"• {tip}",
                bg="#202124",
                fg="#d0d0d0",
                font=("Microsoft YaHei UI", 10),
                justify="left",
            ).pack(anchor="w", pady=2)

        tk.Label(
            wrapper,
            textvariable=self.launcher_status_var,
            bg="#202124",
            fg="#8bd3ff",
            font=("Microsoft YaHei UI", 10),
            anchor="w",
            pady=8,
        ).pack(fill="x")

        self.geometry("+120+120")

    def flash_launcher_status(self, text: str, duration_ms: int = 2200):
        if self.launcher_status_after_id is not None:
            self.after_cancel(self.launcher_status_after_id)
            self.launcher_status_after_id = None
        self.launcher_status_var.set(text)
        self.launcher_status_after_id = self.after(duration_ms, lambda: self.launcher_status_var.set("准备就绪"))

    def start_capture(self):
        self.withdraw()
        self.after(140, self.open_capture_overlay)

    def open_capture_overlay(self):
        try:
            left, top, width, height = get_virtual_screen_bounds()
            bbox = (left, top, left + width, top + height)
            screenshot = ImageGrab.grab(bbox=bbox, all_screens=True)
        except Exception as exc:
            self.deiconify()
            messagebox.showerror(APP_TITLE, f"截图失败：{exc}", parent=self)
            return

        self.capture_overlay = CaptureOverlay(
            self,
            screenshot=screenshot,
            bounds=(left, top, width, height),
            on_done=self.open_editor,
            on_cancel=lambda: self.show_launcher("已取消截图，已返回启动页"),
        )

    def open_editor(self, image: Image.Image):
        self.editor = EditorWindow(
            self,
            image=image,
            on_retake=self.start_capture,
            on_close=self.show_launcher,
        )

    def show_launcher(self, message: str | None = None):
        self.deiconify()
        self.lift()
        self.focus_force()
        if message:
            self.flash_launcher_status(message)


def main():
    enable_dpi_awareness()
    os.environ.setdefault("TK_SILENCE_DEPRECATION", "1")
    app = ScreenshotApp()
    app.mainloop()


if __name__ == "__main__":
    main()
