"""
Interactive MFD slot measurement tool.

- Open a 1024x512 canvas dump PNG (from F9 in-game).
- Drag to draw a rectangle over each physical MFD region.
- On release, the box's canvas coordinates (PosX, PosY, Width, Height)
  are automatically copied to clipboard.
- Save boxes with names; export all as BuiltinLayouts C# or config .cfg format.

Run:
    python measure_gui.py                 # file dialog
    python measure_gui.py <path_to.png>   # direct open
"""

import tkinter as tk
from tkinter import filedialog, simpledialog
import os
import sys
import json

CANVAS_W, CANVAS_H = 1024, 512


class MeasureApp:
    def __init__(self, root, img_path):
        self.root = root
        self.img_path = img_path
        self.root.title(f"MFD Slot Measurer — {os.path.basename(img_path)}")

        self.photo = tk.PhotoImage(file=img_path)
        self.iw = self.photo.width()
        self.ih = self.photo.height()
        if (self.iw, self.ih) != (CANVAS_W, CANVAS_H):
            print(f"WARN: image is {self.iw}x{self.ih}, expected {CANVAS_W}x{CANVAS_H}. Coords may be off.")

        self.canvas = tk.Canvas(root, width=self.iw, height=self.ih, bg='black',
                                cursor='cross', highlightthickness=0)
        self.canvas.create_image(0, 0, anchor='nw', image=self.photo)
        self.canvas.pack()

        self.live = tk.Label(root, text="Drag to measure. Release = copy to clipboard.",
                             font=('Consolas', 11), anchor='w', fg='blue')
        self.live.pack(fill='x')

        self.mouse_info = tk.Label(root, text="", font=('Consolas', 9), anchor='w')
        self.mouse_info.pack(fill='x')

        list_frame = tk.Frame(root)
        list_frame.pack(fill='both', expand=False)
        tk.Label(list_frame, text="Saved slots:", anchor='w').pack(fill='x')
        self.listbox = tk.Listbox(list_frame, height=6, font=('Consolas', 9))
        self.listbox.pack(fill='x')

        btns = tk.Frame(root)
        btns.pack(fill='x', pady=3)
        tk.Button(btns, text="Save last box as slot...", command=self.save_slot).pack(side='left')
        tk.Button(btns, text="Delete selected", command=self.delete_slot).pack(side='left')
        tk.Button(btns, text="Copy BuiltinLayouts (C#)", command=self.export_builtin).pack(side='left')
        tk.Button(btns, text="Copy config .cfg", command=self.export_config).pack(side='left')
        tk.Button(btns, text="Save session (.json)", command=self.save_json).pack(side='left')

        self.canvas.bind('<Button-1>', self.on_click)
        self.canvas.bind('<B1-Motion>', self.on_drag)
        self.canvas.bind('<ButtonRelease-1>', self.on_release)
        self.canvas.bind('<Motion>', self.on_mouse_move)

        self.start = None
        self.rect_id = None
        self.last_box = None       # (x1, y1, x2, y2) in pixel coords
        self.slots = {}            # name -> (x1, y1, x2, y2)
        self.saved_rect_ids = []
        self.aircraft_name = self._guess_aircraft(img_path)

        # Try load previous session
        sess = img_path + ".slots.json"
        if os.path.exists(sess):
            try:
                with open(sess) as f:
                    data = json.load(f)
                self.aircraft_name = data.get('aircraft', self.aircraft_name)
                for name, box in data.get('slots', {}).items():
                    self.slots[name] = tuple(box)
                self.refresh_list()
                self.redraw_saved()
            except Exception as e:
                print(f"Could not load session: {e}")

    def _guess_aircraft(self, path):
        base = os.path.basename(path)
        # e.g. mfd_KR-67_Ifrit_20260418_021228.png
        if base.startswith("mfd_"):
            rest = base[4:]
            idx = rest.rfind("_20")  # date stamp
            if idx > 0:
                return rest[:idx]
        return os.path.splitext(base)[0]

    def px_to_canvas(self, px, py):
        return (px - self.iw / 2, self.ih / 2 - py)

    def compute(self, x1, y1, x2, y2):
        x1, x2 = sorted((x1, x2))
        y1, y2 = sorted((y1, y2))
        cx1, cy1 = self.px_to_canvas(x1, y1)
        cx2, cy2 = self.px_to_canvas(x2, y2)
        pos_x = (cx1 + cx2) / 2
        pos_y = (cy1 + cy2) / 2
        w = x2 - x1
        h = y2 - y1
        return pos_x, pos_y, w, h

    def on_mouse_move(self, e):
        if 0 <= e.x < self.iw and 0 <= e.y < self.ih:
            cx, cy = self.px_to_canvas(e.x, e.y)
            self.mouse_info.config(text=f"Pixel: ({e.x}, {e.y})   Canvas: ({cx:.0f}, {cy:.0f})")

    def on_click(self, e):
        self.start = (e.x, e.y)
        if self.rect_id:
            self.canvas.delete(self.rect_id)
        self.rect_id = self.canvas.create_rectangle(e.x, e.y, e.x, e.y,
                                                     outline='red', width=2)

    def on_drag(self, e):
        if not self.start or not self.rect_id:
            return
        x1, y1 = self.start
        self.canvas.coords(self.rect_id, x1, y1, e.x, e.y)
        pos_x, pos_y, w, h = self.compute(x1, y1, e.x, e.y)
        self.live.config(
            text=f"Px: ({min(x1,e.x)},{min(y1,e.y)})~({max(x1,e.x)},{max(y1,e.y)})  "
                 f"→  PosX={pos_x:.0f}  PosY={pos_y:.0f}  Width={w:.0f}  Height={h:.0f}"
        )

    def on_release(self, e):
        if not self.start:
            return
        x1, y1 = self.start
        x2, y2 = e.x, e.y
        x1, x2 = sorted((x1, x2))
        y1, y2 = sorted((y1, y2))
        if (x2 - x1) < 3 or (y2 - y1) < 3:
            self.start = None
            return
        self.last_box = (x1, y1, x2, y2)
        pos_x, pos_y, w, h = self.compute(x1, y1, x2, y2)
        text = f"PosX={pos_x:.0f}  PosY={pos_y:.0f}  Width={w:.0f}  Height={h:.0f}"
        self._copy(text)
        self.live.config(text=f"COPIED → {text}")
        self.start = None

    def save_slot(self):
        if not self.last_box:
            self.live.config(text="No box to save. Drag one first.")
            return
        name = simpledialog.askstring("Save slot",
                                      "Slot name (e.g. Screen1_Main):",
                                      parent=self.root)
        if not name:
            return
        self.slots[name] = self.last_box
        self.refresh_list()
        self.redraw_saved()
        self.live.config(text=f"Saved slot '{name}'")

    def delete_slot(self):
        sel = self.listbox.curselection()
        if not sel:
            return
        name = list(self.slots.keys())[sel[0]]
        self.slots.pop(name, None)
        self.refresh_list()
        self.redraw_saved()

    def refresh_list(self):
        self.listbox.delete(0, tk.END)
        for name, (x1, y1, x2, y2) in self.slots.items():
            pos_x, pos_y, w, h = self.compute(x1, y1, x2, y2)
            self.listbox.insert(
                tk.END,
                f"{name.ljust(20)} px {x1},{y1}~{x2},{y2}   "
                f"→ X={pos_x:.0f} Y={pos_y:.0f} W={w:.0f} H={h:.0f}"
            )

    def redraw_saved(self):
        for rid in self.saved_rect_ids:
            self.canvas.delete(rid)
        self.saved_rect_ids.clear()
        for name, (x1, y1, x2, y2) in self.slots.items():
            rid = self.canvas.create_rectangle(x1, y1, x2, y2,
                                               outline='lime', width=2)
            tid = self.canvas.create_text(x1 + 4, y1 + 10, anchor='nw',
                                          text=name, fill='lime',
                                          font=('Consolas', 10, 'bold'))
            self.saved_rect_ids.extend([rid, tid])

    def export_builtin(self):
        if not self.slots:
            self.live.config(text="No slots to export")
            return
        lines = [f'["{self.aircraft_name}"] = new Dictionary<string, (float, float, float, float)>']
        lines.append("{")
        for name, box in self.slots.items():
            pos_x, pos_y, w, h = self.compute(*box)
            lines.append(f'    ["{name}"] = ({pos_x:.0f}f, {pos_y:.0f}f, {w:.0f}f, {h:.0f}f),')
        lines.append("},")
        self._copy("\n".join(lines))
        self.live.config(text=f"BuiltinLayouts for '{self.aircraft_name}' copied ({len(self.slots)} slots)")

    def export_config(self):
        if not self.slots:
            return
        lines = []
        for name, box in self.slots.items():
            pos_x, pos_y, w, h = self.compute(*box)
            lines.append(f"[Layout.{self.aircraft_name}.{name}]")
            lines.append(f"PosX = {pos_x:.0f}")
            lines.append(f"PosY = {pos_y:.0f}")
            lines.append(f"Width = {w:.0f}")
            lines.append(f"Height = {h:.0f}")
            lines.append("Enabled = true")
            lines.append("")
        self._copy("\n".join(lines))
        self.live.config(text=f"Config section for '{self.aircraft_name}' copied ({len(self.slots)} slots)")

    def save_json(self):
        out = self.img_path + ".slots.json"
        data = {'aircraft': self.aircraft_name, 'slots': {n: list(b) for n, b in self.slots.items()}}
        with open(out, 'w') as f:
            json.dump(data, f, indent=2)
        self.live.config(text=f"Session saved: {out}")

    def _copy(self, text):
        self.root.clipboard_clear()
        self.root.clipboard_append(text)
        self.root.update()


def main():
    if len(sys.argv) >= 2:
        img = sys.argv[1]
    else:
        root = tk.Tk()
        root.withdraw()
        img = filedialog.askopenfilename(
            title="Select MFD canvas dump PNG (1024x512)",
            filetypes=[("PNG", "*.png"), ("All files", "*.*")],
            initialdir=r"C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option\BepInEx\plugins\MFDVideoPlayer"
        )
        root.destroy()
        if not img:
            return

    root = tk.Tk()
    MeasureApp(root, img)
    root.mainloop()


if __name__ == "__main__":
    main()
