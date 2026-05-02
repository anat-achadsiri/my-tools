"""
Screenshot Tool - Windows App
จับภาพหน้าจอ บันทึกไฟล์ พรีวิว และคัดลอก Full Path ส่งให้ AI
"""

import tkinter as tk
from tkinter import ttk, filedialog, messagebox
import os
import sys
import json
from datetime import datetime

from PIL import Image, ImageTk, ImageGrab

# ── Config path (works for both .pyw and .exe) ─────────────────────
if getattr(sys, 'frozen', False):
    APP_DIR = os.path.dirname(sys.executable)
else:
    APP_DIR = os.path.dirname(os.path.abspath(__file__))

CONFIG_FILE = os.path.join(APP_DIR, "screenshot_config.json")
DEFAULT_SAVE_DIR = os.path.join(APP_DIR, "Screenshots")


def load_config():
    if os.path.exists(CONFIG_FILE):
        try:
            with open(CONFIG_FILE, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return {"save_dir": DEFAULT_SAVE_DIR}


def save_config(cfg):
    with open(CONFIG_FILE, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)


# ── Main App ────────────────────────────────────────────────────────
class ScreenshotApp:
    BG = "#1e1e2e"
    PANEL = "#2a2a3e"
    ACCENT = "#FF5722"
    GREEN = "#4CAF50"
    BLUE = "#2196F3"
    TEXT = "#e0e0e0"
    DIM = "#888888"

    def __init__(self, root):
        self.root = root
        self.root.title("Screenshot Tool")
        self.root.geometry("920x680")
        self.root.minsize(640, 480)
        self.root.configure(bg=self.BG)

        self.config = load_config()
        self.save_dir = self.config.get("save_dir", DEFAULT_SAVE_DIR)
        os.makedirs(self.save_dir, exist_ok=True)

        self.thumb_refs = []
        self.preview_win = None

        self._build_ui()
        self._refresh_files()

        self.root.bind("<F5>", lambda e: self.do_capture_full())
        self.root.bind("<Escape>", lambda e: self.root.quit())

    # ── UI layout ───────────────────────────────────────────────────
    def _build_ui(self):
        # ── Top bar ─────────────────────────────────────────────────
        top = tk.Frame(self.root, bg=self.BG)
        top.pack(fill="x", padx=12, pady=(10, 6))

        tk.Label(top, text="Screenshot Tool", bg=self.BG, fg="#fff",
                 font=("Segoe UI", 18, "bold")).pack(side="left")
        tk.Label(top, text="F5 = Capture  |  คัดลอก path ส่งให้ AI",
                 bg=self.BG, fg=self.DIM, font=("Segoe UI", 9)).pack(side="right")

        # ── Folder bar ──────────────────────────────────────────────
        fbar = tk.Frame(self.root, bg=self.PANEL)
        fbar.pack(fill="x", padx=12, pady=(0, 6), ipady=6)

        tk.Label(fbar, text=" Folder:", bg=self.PANEL, fg=self.TEXT,
                 font=("Segoe UI", 10)).pack(side="left", padx=(10, 4))

        self.path_var = tk.StringVar(value=self.save_dir)
        self.path_entry = tk.Entry(fbar, textvariable=self.path_var, bg="#181828",
                                   fg="#4ade80", font=("Consolas", 10),
                                   insertbackground="#4ade80", bd=0,
                                   highlightthickness=1, highlightcolor=self.ACCENT)
        self.path_entry.pack(side="left", fill="x", expand=True, padx=4, ipady=4)

        tk.Button(fbar, text="Browse", bg=self.ACCENT, fg="#fff",
                  font=("Segoe UI", 9, "bold"), bd=0, padx=12, pady=2,
                  cursor="hand2", command=self._browse).pack(side="left", padx=(2, 4))

        tk.Button(fbar, text="Open", bg="#455a64", fg="#fff",
                  font=("Segoe UI", 9), bd=0, padx=12, pady=2,
                  cursor="hand2", command=self._open_folder).pack(side="left", padx=(0, 10))

        # ── Capture buttons ─────────────────────────────────────────
        cbar = tk.Frame(self.root, bg=self.BG)
        cbar.pack(fill="x", padx=12, pady=(0, 6))

        tk.Button(cbar, text="  Capture ทั้งจอ  (F5)", bg=self.GREEN, fg="#fff",
                  font=("Segoe UI", 12, "bold"), bd=0, padx=20, pady=8,
                  cursor="hand2", activebackground="#66BB6A",
                  command=self.do_capture_full).pack(side="left", padx=(0, 8))

        tk.Button(cbar, text="  เลือกพื้นที่", bg=self.BLUE, fg="#fff",
                  font=("Segoe UI", 12, "bold"), bd=0, padx=20, pady=8,
                  cursor="hand2", activebackground="#42A5F5",
                  command=self.do_capture_region).pack(side="left")

        self.status_var = tk.StringVar(value="พร้อมใช้งาน")
        tk.Label(cbar, textvariable=self.status_var, bg=self.BG, fg="#60a5fa",
                 font=("Segoe UI", 9)).pack(side="right")

        # ── File list header ────────────────────────────────────────
        fhdr = tk.Frame(self.root, bg=self.BG)
        fhdr.pack(fill="x", padx=12, pady=(4, 2))
        tk.Label(fhdr, text="ไฟล์ที่บันทึก", bg=self.BG, fg="#fff",
                 font=("Segoe UI", 13, "bold")).pack(side="left")
        self.count_var = tk.StringVar(value="0 ไฟล์")
        tk.Label(fhdr, textvariable=self.count_var, bg=self.BG, fg=self.DIM,
                 font=("Segoe UI", 9)).pack(side="right")

        # ── Scrollable file grid ────────────────────────────────────
        wrap = tk.Frame(self.root, bg=self.BG)
        wrap.pack(fill="both", expand=True, padx=12, pady=(0, 10))

        self.canvas = tk.Canvas(wrap, bg=self.BG, highlightthickness=0)
        sb = tk.Scrollbar(wrap, orient="vertical", command=self.canvas.yview)
        self.inner = tk.Frame(self.canvas, bg=self.BG)

        self.inner.bind("<Configure>",
                        lambda e: self.canvas.configure(scrollregion=self.canvas.bbox("all")))
        self.cwin = self.canvas.create_window((0, 0), window=self.inner, anchor="nw")
        self.canvas.configure(yscrollcommand=sb.set)
        self.canvas.bind("<Configure>",
                         lambda e: self.canvas.itemconfig(self.cwin, width=e.width))

        self.canvas.pack(side="left", fill="both", expand=True)
        sb.pack(side="right", fill="y")

        # mousewheel
        def _on_wheel(e):
            self.canvas.yview_scroll(int(-1 * (e.delta / 120)), "units")
        self.canvas.bind_all("<MouseWheel>", _on_wheel)

    # ── Folder actions ──────────────────────────────────────────────
    def _apply_dir(self):
        d = self.path_var.get().strip()
        if d:
            self.save_dir = d
            self.config["save_dir"] = d
            save_config(self.config)
            os.makedirs(d, exist_ok=True)

    def _browse(self):
        d = filedialog.askdirectory(initialdir=self.save_dir,
                                    title="เลือก Folder บันทึก Screenshot")
        if d:
            self.path_var.set(d)
            self._apply_dir()
            self._refresh_files()

    def _open_folder(self):
        self._apply_dir()
        if os.path.isdir(self.save_dir):
            os.startfile(self.save_dir)

    # ── Capture ─────────────────────────────────────────────────────
    def do_capture_full(self):
        self._apply_dir()
        self.root.iconify()
        self.root.update_idletasks()
        self.root.after(400, self._grab_full)

    def _grab_full(self):
        try:
            img = ImageGrab.grab(all_screens=True)
            fp = self._save(img)
            self.root.deiconify()
            self.root.lift()
            self._refresh_files()
            self._copy_to_clip(fp)
            self.status_var.set(f"บันทึกแล้ว + คัดลอก path: {os.path.basename(fp)}")
        except Exception as ex:
            self.root.deiconify()
            self.status_var.set(f"Error: {ex}")

    def do_capture_region(self):
        self._apply_dir()
        self.root.iconify()
        self.root.update_idletasks()
        self.root.after(300, self._grab_region)

    def _grab_region(self):
        try:
            full = ImageGrab.grab(all_screens=True)
            self._region_select(full)
        except Exception as ex:
            self.root.deiconify()
            self.status_var.set(f"Error: {ex}")

    def _region_select(self, full_img):
        sel = tk.Toplevel()
        sel.overrideredirect(True)
        sw = sel.winfo_screenwidth()
        sh = sel.winfo_screenheight()
        sel.geometry(f"{sw}x{sh}+0+0")
        sel.attributes("-topmost", True)
        sel.configure(cursor="crosshair", bg="#000")

        # show screenshot + dim overlay
        bg = full_img.copy()
        dim = Image.new("RGBA", bg.size, (0, 0, 0, 80))
        bg.paste(dim, mask=dim)
        bg_tk = ImageTk.PhotoImage(bg.resize((sw, sh), Image.LANCZOS))

        c = tk.Canvas(sel, width=sw, height=sh, highlightthickness=0)
        c.pack()
        c.create_image(0, 0, anchor="nw", image=bg_tk)
        c._ref = bg_tk

        c.create_text(sw // 2, 30, text="ลากเมาส์เลือกพื้นที่  |  ESC = ยกเลิก",
                      fill="#fff", font=("Segoe UI", 14, "bold"))

        st = {"x0": 0, "y0": 0, "rect": None}

        def press(e):
            st["x0"], st["y0"] = e.x, e.y

        def drag(e):
            if st["rect"]:
                c.delete(st["rect"])
            st["rect"] = c.create_rectangle(st["x0"], st["y0"], e.x, e.y,
                                            outline="#4ade80", width=2, dash=(6, 4))

        def release(e):
            sel.destroy()
            iw, ih = full_img.size
            rx, ry = iw / sw, ih / sh
            x0 = int(min(st["x0"], e.x) * rx)
            y0 = int(min(st["y0"], e.y) * ry)
            x1 = int(max(st["x0"], e.x) * rx)
            y1 = int(max(st["y0"], e.y) * ry)
            if x1 - x0 < 10 or y1 - y0 < 10:
                self.root.deiconify()
                self.status_var.set("พื้นที่เล็กเกินไป")
                return
            crop = full_img.crop((x0, y0, x1, y1))
            fp = self._save(crop)
            self.root.deiconify()
            self.root.lift()
            self._refresh_files()
            self._copy_to_clip(fp)
            self.status_var.set(f"บันทึกแล้ว + คัดลอก path: {os.path.basename(fp)}")

        def cancel(e):
            sel.destroy()
            self.root.deiconify()

        c.bind("<ButtonPress-1>", press)
        c.bind("<B1-Motion>", drag)
        c.bind("<ButtonRelease-1>", release)
        sel.bind("<Escape>", cancel)

    # ── Save / clipboard ────────────────────────────────────────────
    def _save(self, img):
        ts = datetime.now().strftime("%Y%m%d_%H%M%S")
        fp = os.path.join(self.save_dir, f"screenshot_{ts}.png")
        img.save(fp, "PNG")
        return fp

    def _copy_to_clip(self, text):
        self.root.clipboard_clear()
        self.root.clipboard_append(text)

    # ── File grid ───────────────────────────────────────────────────
    def _refresh_files(self):
        for w in self.inner.winfo_children():
            w.destroy()
        self.thumb_refs.clear()

        if not os.path.isdir(self.save_dir):
            return

        files = []
        for fn in os.listdir(self.save_dir):
            if fn.lower().endswith((".png", ".jpg", ".jpeg", ".bmp", ".webp")):
                fp = os.path.join(self.save_dir, fn)
                files.append((fp, os.path.getmtime(fp)))
        files.sort(key=lambda x: x[1], reverse=True)

        self.count_var.set(f"{len(files)} ไฟล์")

        if not files:
            tk.Label(self.inner, text="ยังไม่มีภาพ - กด F5 เพื่อ Capture",
                     bg=self.BG, fg=self.DIM, font=("Segoe UI", 11)).pack(pady=50)
            return

        COLS = 3
        row = None
        for i, (fp, mt) in enumerate(files):
            if i % COLS == 0:
                row = tk.Frame(self.inner, bg=self.BG)
                row.pack(fill="x", padx=2, pady=3)
                for c in range(COLS):
                    row.columnconfigure(c, weight=1, uniform="col")
            self._card(row, fp, mt, i % COLS)

    def _card(self, parent, filepath, mtime, col):
        card = tk.Frame(parent, bg=self.PANEL, highlightthickness=1,
                        highlightbackground="#3a3f5c")
        card.grid(row=0, column=col, padx=4, pady=2, sticky="nsew")

        # thumbnail
        try:
            img = Image.open(filepath)
            img.thumbnail((260, 150), Image.LANCZOS)
            tk_img = ImageTk.PhotoImage(img)
            self.thumb_refs.append(tk_img)
            lbl = tk.Label(card, image=tk_img, bg="#000", cursor="hand2")
            lbl.pack(fill="x", padx=3, pady=(3, 0))
            lbl.bind("<Button-1>", lambda e, f=filepath: self._preview(f))
        except Exception:
            tk.Label(card, text="[err]", bg=self.PANEL, fg="#f44",
                     font=("Segoe UI", 14), height=4).pack(fill="x")

        # filename + time
        fname = os.path.basename(filepath)
        tk.Label(card, text=fname, bg=self.PANEL, fg=self.TEXT,
                 font=("Consolas", 9), anchor="w").pack(fill="x", padx=8, pady=(4, 0))

        tstr = datetime.fromtimestamp(mtime).strftime("%d/%m/%Y %H:%M")
        tk.Label(card, text=tstr, bg=self.PANEL, fg=self.DIM,
                 font=("Segoe UI", 8), anchor="w").pack(fill="x", padx=8)

        # full path display
        tk.Label(card, text=filepath, bg=self.PANEL, fg="#666",
                 font=("Consolas", 8), anchor="w", wraplength=260,
                 justify="left").pack(fill="x", padx=8, pady=(2, 0))

        # ── Copy Path button (big, blue, prominent) ─────────────
        copy_btn = tk.Button(card, text="  Copy Full Path", bg=self.BLUE, fg="#fff",
                             font=("Segoe UI", 9, "bold"), bd=0, pady=4,
                             cursor="hand2", activebackground="#42A5F5")
        copy_btn.pack(fill="x", padx=6, pady=(4, 2))
        copy_btn.configure(command=lambda f=filepath, b=copy_btn: self._copy_btn(f, b))

        # small action buttons
        arow = tk.Frame(card, bg=self.PANEL)
        arow.pack(fill="x", padx=6, pady=(0, 6))

        tk.Button(arow, text="Preview", bg="#455a64", fg="#fff",
                  font=("Segoe UI", 8), bd=0, padx=8, pady=2, cursor="hand2",
                  command=lambda f=filepath: self._preview(f)).pack(side="left", padx=(0, 3))

        tk.Button(arow, text="Open Folder", bg="#455a64", fg="#fff",
                  font=("Segoe UI", 8), bd=0, padx=8, pady=2, cursor="hand2",
                  command=lambda f=filepath: self._open_file_folder(f)).pack(side="right")

    def _copy_btn(self, filepath, btn):
        self._copy_to_clip(filepath)
        self.status_var.set(f"คัดลอกแล้ว: {filepath}")
        orig = btn.cget("text")
        btn.config(text="  Copied!", bg=self.GREEN)
        self.root.after(1500, lambda: btn.config(text=orig, bg=self.BLUE))

    # ── Preview ─────────────────────────────────────────────────────
    def _preview(self, filepath):
        if self.preview_win and self.preview_win.winfo_exists():
            self.preview_win.destroy()

        pw = tk.Toplevel(self.root)
        pw.title(os.path.basename(filepath))
        pw.configure(bg="#000")
        pw.attributes("-topmost", True)
        self.preview_win = pw

        try:
            img = Image.open(filepath)
            sw, sh = self.root.winfo_screenwidth(), self.root.winfo_screenheight()
            ratio = min((sw * 0.8) / img.width, (sh * 0.8) / img.height, 1.0)
            nw, nh = int(img.width * ratio), int(img.height * ratio)
            resized = img.resize((nw, nh), Image.LANCZOS)
            tk_img = ImageTk.PhotoImage(resized)
            pw._ref = tk_img

            pw.geometry(f"{nw + 20}x{nh + 110}+{(sw - nw) // 2}+{(sh - nh) // 2}")

            # ── Close button at top-right ───────────────────────────
            top_bar = tk.Frame(pw, bg="#111")
            top_bar.pack(fill="x")

            tk.Button(top_bar, text="  X  ปิด", bg="#c62828", fg="#fff",
                      font=("Segoe UI", 10, "bold"), bd=0, padx=14, pady=4,
                      cursor="hand2", activebackground="#f44336",
                      command=pw.destroy).pack(side="right", padx=6, pady=4)

            tk.Label(top_bar, text=os.path.basename(filepath), bg="#111", fg="#aaa",
                     font=("Segoe UI", 9)).pack(side="left", padx=10)

            # ── Image ───────────────────────────────────────────────
            tk.Label(pw, image=tk_img, bg="#000").pack(padx=10, pady=(4, 4))

            # ── Bottom bar: path + copy ─────────────────────────────
            bot = tk.Frame(pw, bg="#1a1a2e")
            bot.pack(fill="x", padx=10, pady=(0, 8))

            tk.Label(bot, text=filepath, bg="#1a1a2e", fg="#4ade80",
                     font=("Consolas", 9), anchor="w").pack(side="left", fill="x", expand=True)

            tk.Button(bot, text="  Copy Path", bg=self.BLUE, fg="#fff",
                      font=("Segoe UI", 10, "bold"), bd=0, padx=14, pady=4,
                      cursor="hand2",
                      command=lambda: self._copy_to_clip(filepath)).pack(side="right")

            # ── Keyboard & window close ─────────────────────────────
            pw.bind("<Escape>", lambda e: pw.destroy())
            pw.protocol("WM_DELETE_WINDOW", pw.destroy)

        except Exception as ex:
            tk.Label(pw, text=f"Error: {ex}", bg="#000", fg="#f44",
                     font=("Segoe UI", 12)).pack(pady=40)

    # ── Open file in explorer (select) ─────────────────────────────
    def _open_file_folder(self, filepath):
        import subprocess
        subprocess.Popen(f'explorer /select,"{filepath}"')


# ── Run ─────────────────────────────────────────────────────────────
if __name__ == "__main__":
    root = tk.Tk()
    ScreenshotApp(root)
    root.mainloop()
