"""
SoftSnap — by SoftSuite
จับภาพหน้าจอ บันทึกไฟล์ พรีวิว และคัดลอก Full Path ส่งให้ AI
Built with customtkinter for modern UI
"""

import customtkinter as ctk
from tkinter import filedialog, messagebox, simpledialog
import tkinter as tk
import os
import sys
import json
from datetime import datetime

from PIL import Image, ImageTk, ImageGrab, ImageDraw

# ── Config ─────────────────────────────────────────────────────────
if getattr(sys, 'frozen', False):
    APP_DIR = os.path.dirname(sys.executable)
else:
    APP_DIR = os.path.dirname(os.path.abspath(__file__))

CONFIG_FILE = os.path.join(APP_DIR, "softsnap_config.json")
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


# ── Rounded thumbnail ──────────────────────────────────────────────
def make_thumb(img, size=(180, 180), radius=16, bg=(30, 30, 30)):
    img = img.copy()
    img.thumbnail(size, Image.LANCZOS)
    sq = Image.new("RGB", size, bg)
    ox = (size[0] - img.width) // 2
    oy = (size[1] - img.height) // 2
    sq.paste(img, (ox, oy))
    mask = Image.new("L", size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size[0]-1, size[1]-1],
                                            radius=radius, fill=255)
    result = Image.new("RGBA", size, (0, 0, 0, 0))
    result.paste(sq, mask=mask)
    return result


# ── Main App ────────────────────────────────────────────────────────
class SoftSnap(ctk.CTk):
    THUMB = (180, 180)
    COLS = 3

    def __init__(self):
        super().__init__()
        self.title("SoftSnap")
        self.geometry("860x860")
        self.minsize(560, 520)

        # Set window icon
        icon_path = os.path.join(APP_DIR, "softsnap_logo_v4.ico")
        if os.path.exists(icon_path):
            self.iconbitmap(icon_path)

        self.cfg = load_config()
        self.save_dir = self.cfg.get("save_dir", DEFAULT_SAVE_DIR)
        theme = self.cfg.get("theme", "dark")
        ctk.set_appearance_mode(theme)
        ctk.set_default_color_theme("blue")

        # Albums
        if "albums" not in self.cfg:
            self.cfg["albums"] = {"Screenshots": self.save_dir}
            save_config(self.cfg)
        self.albums = self.cfg["albums"]
        self.current_album = self.cfg.get("current_album", "Screenshots")
        if self.current_album in self.albums:
            self.save_dir = self.albums[self.current_album]
        os.makedirs(self.save_dir, exist_ok=True)

        self.thumb_refs = []
        self.preview_win = None
        self.files = []
        self.selected = set()
        self.check_vars = {}

        self._build()
        self._load_files()

        self.bind("<F5>", lambda e: self.capture_full())
        self.bind("<Escape>", lambda e: self.quit())
        self.bind("<Delete>", lambda e: self._delete_sel())
        self.bind("<Control-v>", lambda e: self.paste_clipboard())
        self.bind("<Control-V>", lambda e: self.paste_clipboard())

    # ── Build UI ────────────────────────────────────────────────────
    def _build(self):
        # ── Header bar (colored) ────────────────────────────────────
        hbar = ctk.CTkFrame(self, fg_color=("#5856d6", "#2d2b7c"), corner_radius=0, height=56)
        hbar.pack(fill="x")
        hbar.pack_propagate(False)
        hp = ctk.CTkFrame(hbar, fg_color="transparent")
        hp.pack(fill="both", expand=True, padx=16)

        ctk.CTkLabel(hp, text="SoftSnap",
                     font=ctk.CTkFont("Segoe UI", 22, "bold"),
                     text_color="#ffffff").pack(side="left")
        ctk.CTkLabel(hp, text="by SoftSuite",
                     font=ctk.CTkFont("Segoe UI", 9),
                     text_color="#c4c3ff").pack(side="left", padx=(8, 0), pady=(4, 0))

        self.theme_btn = ctk.CTkButton(hp, text="\u263d", width=32, height=32,
                                        corner_radius=16,
                                        fg_color="#4845b0",
                                        hover_color="#6360c9",
                                        text_color="#fff",
                                        font=ctk.CTkFont(size=15),
                                        command=self._toggle_theme)
        self.theme_btn.pack(side="right")

        self.status_label = ctk.CTkLabel(hp, text="\u2713 Ready",
                                          font=ctk.CTkFont("Segoe UI", 10),
                                          text_color="#a5f3a0")
        self.status_label.pack(side="right", padx=(0, 12))

        self.count_label = ctk.CTkLabel(hp, text="0",
                                         font=ctk.CTkFont("Segoe UI", 10, "bold"),
                                         text_color="#fff")
        self.count_label.pack(side="right", padx=(0, 8))

        # ── Action bar (compact) ────────────────────────────────────
        act = ctk.CTkFrame(self, fg_color="transparent")
        act.pack(fill="x", padx=16, pady=(10, 4))

        ctk.CTkButton(act, text="\u25cf Capture (F5)", height=34,
                       fg_color="#10b981", hover_color="#059669",
                       corner_radius=17, font=ctk.CTkFont("Segoe UI", 12, "bold"),
                       command=self.capture_full).pack(side="left", padx=(0, 6))
        ctk.CTkButton(act, text="\u25a1 \u0e40\u0e25\u0e37\u0e2d\u0e01\u0e1e\u0e37\u0e49\u0e19\u0e17\u0e35\u0e48", height=34,
                       fg_color=("#6366f1", "#4f46e5"), hover_color="#7c3aed",
                       corner_radius=17, font=ctk.CTkFont("Segoe UI", 12, "bold"),
                       command=self.capture_region).pack(side="left", padx=(0, 6))
        ctk.CTkButton(act, text="\U0001f4cb Paste", height=34,
                       fg_color="#f59e0b", hover_color="#d97706",
                       corner_radius=17, font=ctk.CTkFont("Segoe UI", 12, "bold"),
                       command=self.paste_clipboard).pack(side="left")

        # ── Album bar ───────────────────────────────────────────────
        alb = ctk.CTkFrame(self, fg_color="transparent")
        alb.pack(fill="x", padx=16, pady=(2, 2))

        self.album_var = ctk.StringVar(value=self.current_album)
        self.album_menu = ctk.CTkOptionMenu(alb, variable=self.album_var,
                                             values=list(self.albums.keys()),
                                             command=lambda v: self._switch_album(),
                                             corner_radius=8, height=30,
                                             font=ctk.CTkFont("Segoe UI", 11),
                                             width=160,
                                             fg_color=("#6366f1", "#4f46e5"),
                                             button_color=("#4f46e5", "#3730a3"),
                                             button_hover_color=("#7c3aed", "#5b21b6"))
        self.album_menu.pack(side="left", padx=(0, 6))

        for txt, cmd, clr, hclr in [
            ("+ New", self._add_album, ("#e0e7ff", "#1e1b4b"), ("#c7d2fe", "#312e81")),
            ("\u2715", self._remove_album, ("#fee2e2", "#450a0a"), ("#fecaca", "#7f1d1d")),
            ("\U0001f4c2", self._open_folder, ("#e0e7ff", "#1e1b4b"), ("#c7d2fe", "#312e81")),
        ]:
            ctk.CTkButton(alb, text=txt, width=50 if len(txt) < 3 else 70, height=30,
                           corner_radius=8, fg_color=clr, hover_color=hclr,
                           text_color=("#4f46e5", "#c4b5fd"),
                           font=ctk.CTkFont("Segoe UI", 11),
                           command=cmd).pack(side="left", padx=(0, 3))

        self.path_label = ctk.CTkLabel(alb, text=self.save_dir,
                                        font=ctk.CTkFont("Consolas", 9),
                                        text_color="gray50", anchor="w")
        self.path_label.pack(side="left", padx=(10, 0))

        # ── Gallery ─────────────────────────────────────────────────
        self.gallery = ctk.CTkScrollableFrame(self,
                                               fg_color=("gray92", "#0c0a1a"),
                                               corner_radius=12)
        self.gallery.pack(fill="both", expand=True, padx=12, pady=(6, 0))
        for c in range(self.COLS):
            self.gallery.columnconfigure(c, weight=1, uniform="col")

        # ── Bottom toolbar ──────────────────────────────────────────
        bar = ctk.CTkFrame(self, corner_radius=0, height=52,
                            fg_color=("#ffffff", "#111122"))
        bar.pack(fill="x", side="bottom")
        bar.pack_propagate(False)

        bp = ctk.CTkFrame(bar, fg_color="transparent")
        bp.pack(fill="both", expand=True, padx=16)

        self.sel_all_var = ctk.BooleanVar(value=False)
        ctk.CTkCheckBox(bp, text="\u0e40\u0e25\u0e37\u0e2d\u0e01\u0e17\u0e31\u0e49\u0e07\u0e2b\u0e21\u0e14",
                         variable=self.sel_all_var,
                         font=ctk.CTkFont("Segoe UI", 11),
                         checkbox_width=20, checkbox_height=20,
                         corner_radius=10, border_width=2,
                         fg_color=("#6366f1", "#818cf8"),
                         command=self._toggle_all).pack(side="left")

        self.sel_info = ctk.CTkLabel(bp, text="", font=ctk.CTkFont("Segoe UI", 10),
                                      text_color="gray50")
        self.sel_info.pack(side="left", padx=(10, 0))

        ctk.CTkButton(bp, text="\U0001f5d1 Delete", fg_color="#ef4444", hover_color="#dc2626",
                       corner_radius=17, font=ctk.CTkFont("Segoe UI", 12, "bold"),
                       height=34, width=100, command=self._delete_sel).pack(side="right")

        ctk.CTkButton(bp, text="\U0001f4cb Copy", fg_color=("#6366f1", "#4f46e5"),
                       hover_color=("#818cf8", "#6366f1"),
                       corner_radius=17, font=ctk.CTkFont("Segoe UI", 12, "bold"),
                       height=34, width=100, command=self._copy_sel
                       ).pack(side="right", padx=(0, 8))

    # ── Theme ───────────────────────────────────────────────────────
    def _toggle_theme(self):
        current = self.cfg.get("theme", "dark")
        new = "light" if current == "dark" else "dark"
        ctk.set_appearance_mode(new)
        self.cfg["theme"] = new
        save_config(self.cfg)
        icon = "\u263d" if new == "light" else "\u2600"
        self.theme_btn.configure(text=icon)

    # ── File loading ────────────────────────────────────────────────
    def _load_files(self):
        for w in self.gallery.winfo_children():
            w.destroy()
        self.thumb_refs.clear()
        self.check_vars.clear()

        if not os.path.isdir(self.save_dir):
            return

        self.files = []
        for fn in os.listdir(self.save_dir):
            if fn.lower().endswith((".png", ".jpg", ".jpeg", ".bmp", ".webp")):
                fp = os.path.join(self.save_dir, fn)
                self.files.append((fp, os.path.getmtime(fp)))
        self.files.sort(key=lambda x: x[1], reverse=True)

        valid = {fp for fp, _ in self.files}
        self.selected &= valid
        self.count_label.configure(text=f"{len(self.files)} \u0e23\u0e39\u0e1b")

        if not self.files:
            ctk.CTkLabel(self.gallery, text="\U0001f4f7\n\u0e22\u0e31\u0e07\u0e44\u0e21\u0e48\u0e21\u0e35\u0e20\u0e32\u0e1e\n\u0e01\u0e14 F5 \u0e40\u0e1e\u0e37\u0e48\u0e2d Capture",
                         font=ctk.CTkFont("Segoe UI", 16),
                         text_color="gray50").grid(row=0, column=0,
                                                    columnspan=self.COLS, pady=80)
            return

        # Determine thumb bg
        mode = ctk.get_appearance_mode()
        tbg = (30, 30, 30) if mode == "Dark" else (240, 240, 245)

        for i, (fp, mt) in enumerate(self.files):
            r, c = divmod(i, self.COLS)
            self._card(r, c, fp, mt, tbg)

        self._update_bar()

    # ── Card ────────────────────────────────────────────────────────
    def _card(self, row, col, filepath, mtime, tbg):
        is_sel = filepath in self.selected
        border = ("#6366f1", "#818cf8") if is_sel else ("gray82", "#2a2a3e")
        card_fg = ("#ffffff", "#1a1a2e")

        card = ctk.CTkFrame(self.gallery, corner_radius=14,
                             border_width=2 if is_sel else 1,
                             border_color=border, fg_color=card_fg)
        card.grid(row=row, column=col, padx=5, pady=5, sticky="nsew")

        # Thumbnail
        try:
            img = Image.open(filepath)
            thumb = make_thumb(img, self.THUMB, 12, tbg)
            tk_img = ImageTk.PhotoImage(thumb)
            self.thumb_refs.append(tk_img)

            img_label = ctk.CTkLabel(card, image=tk_img, text="", cursor="hand2")
            img_label.pack(padx=6, pady=(6, 3))
            img_label.bind("<Button-1>", lambda e, f=filepath: self._preview(f))
        except Exception:
            ctk.CTkLabel(card, text="\u26a0", font=ctk.CTkFont(size=32),
                         text_color="#ef4444").pack(padx=6, pady=(6, 3))

        # Copy Path — compact
        ctk.CTkButton(card, text="\U0001f4cb Copy Path", height=26,
                       corner_radius=13,
                       fg_color=("#6366f1", "#4f46e5"),
                       hover_color=("#818cf8", "#6366f1"),
                       font=ctk.CTkFont("Segoe UI", 10, "bold"),
                       command=lambda f=filepath: self._copy_one(f)
                       ).pack(padx=6, pady=(1, 3))

        # Bottom: checkbox + name + date
        bot = ctk.CTkFrame(card, fg_color="transparent", height=22)
        bot.pack(fill="x", padx=6, pady=(0, 6))

        cv = ctk.BooleanVar(value=is_sel)
        self.check_vars[filepath] = cv
        ctk.CTkCheckBox(bot, text="", variable=cv, width=20, height=20,
                         checkbox_width=20, checkbox_height=20,
                         corner_radius=10, border_width=2,
                         fg_color=("#6366f1", "#818cf8"),
                         command=lambda f=filepath: self._toggle(f)).pack(side="left")

        fname = os.path.basename(filepath)
        short = fname[:14] + ".." if len(fname) > 16 else fname
        ctk.CTkLabel(bot, text=short, font=ctk.CTkFont("Segoe UI", 8),
                     text_color=("gray45", "gray55")).pack(side="left", padx=(3, 0))

        tstr = datetime.fromtimestamp(mtime).strftime("%d/%m %H:%M")
        ctk.CTkLabel(bot, text=tstr, font=ctk.CTkFont("Segoe UI", 8),
                     text_color=("gray45", "gray55")).pack(side="right")

    def _copy_one(self, filepath):
        self.clipboard_clear()
        self.clipboard_append(filepath)
        self._status("\u0e04\u0e31\u0e14\u0e25\u0e2d\u0e01\u0e41\u0e25\u0e49\u0e27: " + os.path.basename(filepath))

    # ── Selection ───────────────────────────────────────────────────
    def _toggle(self, filepath):
        cv = self.check_vars.get(filepath)
        if cv:
            if cv.get():
                self.selected.add(filepath)
            else:
                self.selected.discard(filepath)
        self.sel_all_var.set(len(self.selected) == len(self.files) and len(self.files) > 0)
        self._update_bar()

    def _toggle_all(self):
        if self.sel_all_var.get():
            self.selected = {fp for fp, _ in self.files}
        else:
            self.selected.clear()
        self._load_files()

    def _update_bar(self):
        n = len(self.selected)
        self.sel_info.configure(text=f"\u0e40\u0e25\u0e37\u0e2d\u0e01\u0e41\u0e25\u0e49\u0e27 {n} \u0e23\u0e32\u0e22\u0e01\u0e32\u0e23" if n else "")

    # ── Batch actions ───────────────────────────────────────────────
    def _delete_sel(self):
        if not self.selected:
            self._status("\u0e44\u0e21\u0e48\u0e44\u0e14\u0e49\u0e40\u0e25\u0e37\u0e2d\u0e01\u0e23\u0e39\u0e1b", "err")
            return
        n = len(self.selected)
        if not messagebox.askyesno("\u0e22\u0e37\u0e19\u0e22\u0e31\u0e19", f"\u0e25\u0e1a {n} \u0e44\u0e1f\u0e25\u0e4c?"):
            return
        ok = 0
        for fp in list(self.selected):
            try:
                os.remove(fp)
                ok += 1
            except Exception:
                pass
        self.selected.clear()
        self.sel_all_var.set(False)
        self._load_files()
        self._status(f"\u0e25\u0e1a\u0e41\u0e25\u0e49\u0e27 {ok} \u0e44\u0e1f\u0e25\u0e4c")

    def _copy_sel(self):
        if not self.selected:
            self._status("\u0e44\u0e21\u0e48\u0e44\u0e14\u0e49\u0e40\u0e25\u0e37\u0e2d\u0e01\u0e23\u0e39\u0e1b", "err")
            return
        paths = "\n".join(sorted(self.selected))
        self.clipboard_clear()
        self.clipboard_append(paths)
        self._status(f"\u0e04\u0e31\u0e14\u0e25\u0e2d\u0e01 {len(self.selected)} path")

    # ── Status ──────────────────────────────────────────────────────
    def _status(self, text, kind="ok"):
        color = ("#34c759", "#30d158") if kind == "ok" else ("#ff3b30", "#ff453a")
        self.status_label.configure(text=text, text_color=color)

    # ── Paste clipboard ─────────────────────────────────────────────
    def paste_clipboard(self):
        os.makedirs(self.save_dir, exist_ok=True)
        try:
            img = ImageGrab.grabclipboard()
            if img is None:
                try:
                    clip = self.clipboard_get()
                    if clip and os.path.isfile(clip.strip().strip('"')):
                        src = clip.strip().strip('"')
                        if src.lower().endswith((".png",".jpg",".jpeg",".bmp",".webp",".gif")):
                            img = Image.open(src)
                except Exception:
                    pass
            if img is None:
                self._status("\u0e44\u0e21\u0e48\u0e1e\u0e1a\u0e23\u0e39\u0e1b\u0e43\u0e19 Clipboard", "err")
                return
            if isinstance(img, list):
                imported = 0
                for src in img:
                    if src.lower().endswith((".png",".jpg",".jpeg",".bmp",".webp",".gif")):
                        pil_img = Image.open(src)
                        self._save_img(pil_img)
                        imported += 1
                if imported:
                    self._load_files()
                    self._status(f"\u0e27\u0e32\u0e07\u0e41\u0e25\u0e49\u0e27 {imported} \u0e44\u0e1f\u0e25\u0e4c")
                else:
                    self._status("\u0e44\u0e21\u0e48\u0e1e\u0e1a\u0e23\u0e39\u0e1b\u0e43\u0e19 Clipboard", "err")
                return
            fp = self._save_img(img)
            self._load_files()
            self.clipboard_clear()
            self.clipboard_append(fp)
            self._status("\u0e27\u0e32\u0e07\u0e23\u0e39\u0e1b\u0e08\u0e32\u0e01 Clipboard + \u0e04\u0e31\u0e14\u0e25\u0e2d\u0e01 path")
        except Exception as ex:
            self._status(f"Paste error: {ex}", "err")

    # ── Album ───────────────────────────────────────────────────────
    def _switch_album(self):
        name = self.album_var.get()
        if name in self.albums:
            self.save_dir = self.albums[name]
            self.current_album = name
            self.cfg["save_dir"] = self.save_dir
            self.cfg["current_album"] = name
            save_config(self.cfg)
            self.path_label.configure(text=self.save_dir)
            os.makedirs(self.save_dir, exist_ok=True)
            self.selected.clear()
            self._load_files()
            self._status(f"Album: {name}")

    def _add_album(self):
        d = filedialog.askdirectory(initialdir=self.save_dir,
                                    title="\u0e40\u0e25\u0e37\u0e2d\u0e01 Folder \u0e2a\u0e33\u0e2b\u0e23\u0e31\u0e1a Album \u0e43\u0e2b\u0e21\u0e48")
        if not d:
            return
        name = simpledialog.askstring("\u0e15\u0e31\u0e49\u0e07\u0e0a\u0e37\u0e48\u0e2d Album", "\u0e0a\u0e37\u0e48\u0e2d Album:",
                                      initialvalue=os.path.basename(d), parent=self)
        if not name:
            return
        self.albums[name] = d
        self.cfg["albums"] = self.albums
        self.cfg["save_dir"] = d
        self.cfg["current_album"] = name
        save_config(self.cfg)

        self.save_dir = d
        self.current_album = name
        self.path_label.configure(text=d)
        os.makedirs(d, exist_ok=True)
        self.selected.clear()
        self.album_menu.configure(values=list(self.albums.keys()))
        self.album_var.set(name)
        self._load_files()
        self._status(f"\u0e40\u0e1e\u0e34\u0e48\u0e21 Album: {name}")

    def _remove_album(self):
        name = self.album_var.get()
        if len(self.albums) <= 1:
            self._status("\u0e15\u0e49\u0e2d\u0e07\u0e21\u0e35\u0e2d\u0e22\u0e48\u0e32\u0e07\u0e19\u0e49\u0e2d\u0e22 1 Album", "err")
            return
        if not messagebox.askyesno("\u0e22\u0e37\u0e19\u0e22\u0e31\u0e19",
                                   f"\u0e25\u0e1a Album \"{name}\"?\n(\u0e44\u0e21\u0e48\u0e25\u0e1a\u0e44\u0e1f\u0e25\u0e4c\u0e08\u0e23\u0e34\u0e07)"):
            return
        del self.albums[name]
        first = list(self.albums.keys())[0]
        self.current_album = first
        self.save_dir = self.albums[first]
        self.cfg["albums"] = self.albums
        self.cfg["save_dir"] = self.save_dir
        self.cfg["current_album"] = first
        save_config(self.cfg)

        self.path_label.configure(text=self.save_dir)
        self.album_menu.configure(values=list(self.albums.keys()))
        self.album_var.set(first)
        self.selected.clear()
        self._load_files()
        self._status(f"\u0e25\u0e1a Album: {name}")

    def _open_folder(self):
        os.makedirs(self.save_dir, exist_ok=True)
        if os.path.isdir(self.save_dir):
            os.startfile(self.save_dir)

    # ── Capture ─────────────────────────────────────────────────────
    def capture_full(self):
        os.makedirs(self.save_dir, exist_ok=True)
        self.iconify()
        self.update_idletasks()
        self.after(400, self._grab_full)

    def _grab_full(self):
        try:
            img = ImageGrab.grab(all_screens=True)
            fp = self._save_img(img)
            self.deiconify(); self.lift()
            self._load_files()
            self.clipboard_clear()
            self.clipboard_append(fp)
            self._status("\u0e1a\u0e31\u0e19\u0e17\u0e36\u0e01 + \u0e04\u0e31\u0e14\u0e25\u0e2d\u0e01 path \u0e41\u0e25\u0e49\u0e27")
        except Exception as ex:
            self.deiconify()
            self._status(f"Error: {ex}", "err")

    def capture_region(self):
        os.makedirs(self.save_dir, exist_ok=True)
        self.iconify()
        self.update_idletasks()
        self.after(300, self._grab_region)

    def _grab_region(self):
        try:
            full = ImageGrab.grab(all_screens=True)
            self._region_sel(full)
        except Exception as ex:
            self.deiconify()
            self._status(f"Error: {ex}", "err")

    def _region_sel(self, full_img):
        sel = tk.Toplevel()
        sel.overrideredirect(True)
        sw, sh = sel.winfo_screenwidth(), sel.winfo_screenheight()
        sel.geometry(f"{sw}x{sh}+0+0")
        sel.attributes("-topmost", True)
        sel.configure(cursor="crosshair", bg="#000")

        bg = full_img.copy()
        dim = Image.new("RGBA", bg.size, (0, 0, 0, 80))
        bg.paste(dim, mask=dim)
        bg_tk = ImageTk.PhotoImage(bg.resize((sw, sh), Image.LANCZOS))

        c = tk.Canvas(sel, width=sw, height=sh, highlightthickness=0)
        c.pack()
        c.create_image(0, 0, anchor="nw", image=bg_tk)
        c._ref = bg_tk
        c.create_text(sw//2, 30,
                      text="\u0e25\u0e32\u0e01\u0e40\u0e21\u0e32\u0e2a\u0e4c\u0e40\u0e25\u0e37\u0e2d\u0e01\u0e1e\u0e37\u0e49\u0e19\u0e17\u0e35\u0e48  |  ESC = \u0e22\u0e01\u0e40\u0e25\u0e34\u0e01",
                      fill="#fff", font=("Segoe UI", 14, "bold"))

        st = {"x0": 0, "y0": 0, "rect": None}
        def press(e): st["x0"], st["y0"] = e.x, e.y
        def drag(e):
            if st["rect"]: c.delete(st["rect"])
            st["rect"] = c.create_rectangle(st["x0"], st["y0"], e.x, e.y,
                                            outline="#4ade80", width=2, dash=(6,4))
        def release(e):
            sel.destroy()
            iw, ih = full_img.size
            rx, ry = iw/sw, ih/sh
            x0, y0 = int(min(st["x0"],e.x)*rx), int(min(st["y0"],e.y)*ry)
            x1, y1 = int(max(st["x0"],e.x)*rx), int(max(st["y0"],e.y)*ry)
            if x1-x0 < 10 or y1-y0 < 10:
                self.deiconify()
                self._status("\u0e1e\u0e37\u0e49\u0e19\u0e17\u0e35\u0e48\u0e40\u0e25\u0e47\u0e01\u0e40\u0e01\u0e34\u0e19\u0e44\u0e1b", "err")
                return
            crop = full_img.crop((x0, y0, x1, y1))
            fp = self._save_img(crop)
            self.deiconify(); self.lift()
            self._load_files()
            self.clipboard_clear()
            self.clipboard_append(fp)
            self._status("\u0e1a\u0e31\u0e19\u0e17\u0e36\u0e01 + \u0e04\u0e31\u0e14\u0e25\u0e2d\u0e01 path \u0e41\u0e25\u0e49\u0e27")
        def cancel(e): sel.destroy(); self.deiconify()

        c.bind("<ButtonPress-1>", press)
        c.bind("<B1-Motion>", drag)
        c.bind("<ButtonRelease-1>", release)
        sel.bind("<Escape>", cancel)

    def _save_img(self, img):
        ts = datetime.now().strftime("%Y%m%d_%H%M%S")
        fp = os.path.join(self.save_dir, f"screenshot_{ts}.png")
        img.save(fp, "PNG")
        return fp

    # ── Preview ─────────────────────────────────────────────────────
    def _preview(self, filepath):
        if self.preview_win and self.preview_win.winfo_exists():
            self.preview_win.destroy()

        pw = ctk.CTkToplevel(self)
        pw.title(os.path.basename(filepath))
        pw.attributes("-topmost", True)
        self.preview_win = pw

        try:
            img = Image.open(filepath)
            sw, sh = self.winfo_screenwidth(), self.winfo_screenheight()
            ratio = min((sw*0.85)/img.width, (sh*0.85)/img.height, 1.0)
            nw, nh = int(img.width*ratio), int(img.height*ratio)
            resized = img.resize((nw, nh), Image.LANCZOS)
            tk_img = ImageTk.PhotoImage(resized)
            pw._ref = tk_img

            pw.geometry(f"{nw+20}x{nh+100}+{(sw-nw)//2}+{(sh-nh)//2}")

            # Top bar
            top = ctk.CTkFrame(pw, fg_color="transparent")
            top.pack(fill="x", padx=12, pady=(8, 4))

            ctk.CTkLabel(top, text=os.path.basename(filepath),
                         font=ctk.CTkFont("Segoe UI", 10),
                         text_color="gray50").pack(side="left")

            ctk.CTkButton(top, text="\u2715 \u0e1b\u0e34\u0e14", width=70, height=30,
                           corner_radius=15, fg_color="#ff3b30", hover_color="#ff6961",
                           font=ctk.CTkFont("Segoe UI", 11),
                           command=pw.destroy).pack(side="right")

            ctk.CTkButton(top, text="\U0001f5d1 \u0e25\u0e1a", width=70, height=30,
                           corner_radius=15, fg_color="gray50", hover_color="gray40",
                           font=ctk.CTkFont("Segoe UI", 11),
                           command=lambda: self._preview_del(pw, filepath)
                           ).pack(side="right", padx=(0, 6))

            # Image
            img_label = ctk.CTkLabel(pw, image=tk_img, text="")
            img_label.pack(padx=10, pady=4)

            # Bottom
            bot = ctk.CTkFrame(pw, fg_color="transparent")
            bot.pack(fill="x", padx=12, pady=(4, 8))

            ctk.CTkLabel(bot, text=filepath,
                         font=ctk.CTkFont("Consolas", 9),
                         text_color=("#34c759", "#30d158"),
                         anchor="w").pack(side="left", fill="x", expand=True)

            ctk.CTkButton(bot, text="Copy Path", width=100, height=30,
                           corner_radius=15, fg_color="#007aff", hover_color="#3395ff",
                           font=ctk.CTkFont("Segoe UI", 11, "bold"),
                           command=lambda: self._copy_one(filepath)).pack(side="right")

            pw.bind("<Escape>", lambda e: pw.destroy())
            pw.protocol("WM_DELETE_WINDOW", pw.destroy)

        except Exception as ex:
            ctk.CTkLabel(pw, text=f"Error: {ex}", text_color="#ff3b30",
                         font=ctk.CTkFont("Segoe UI", 13)).pack(pady=40)

    def _preview_del(self, pw, filepath):
        pw.destroy()
        fn = os.path.basename(filepath)
        if not messagebox.askyesno("\u0e22\u0e37\u0e19\u0e22\u0e31\u0e19", f"\u0e25\u0e1a {fn}?"):
            return
        try:
            os.remove(filepath)
            self.selected.discard(filepath)
            self._load_files()
            self._status(f"\u0e25\u0e1a\u0e41\u0e25\u0e49\u0e27: {fn}")
        except Exception as ex:
            messagebox.showerror("Error", str(ex))


# ── Run ─────────────────────────────────────────────────────────────
if __name__ == "__main__":
    app = SoftSnap()
    app.mainloop()
