import time
import tkinter as tk
from tkinter import scrolledtext, messagebox, simpledialog
from pythonosc import udp_client, osc_server, dispatcher
import threading
import os
import ctypes
import json
import math
import sys

# Replace Pygame with PySDL2
import sdl2
import sdl2.ext

# Imports for system tray and image handling
from PIL import Image, ImageTk
import pystray

# Attempt to import keyboard for global key polling
try:
    import keyboard
    KEYBOARD_AVAILABLE = True
except ImportError:
    KEYBOARD_AVAILABLE = False


def resource_path(relative_path):
    try:
        base_path = sys._MEIPASS
    except Exception:
        base_path = os.path.abspath(".")
    return os.path.join(base_path, relative_path)

class ToolTip:
    def __init__(self, widget, text):
        self.widget = widget
        self.text = text
        self.tooltip_window = None
        self.widget.bind("<Enter>", self.show_tooltip)
        self.widget.bind("<Leave>", self.hide_tooltip)

    def show_tooltip(self, event=None):
        x = self.widget.winfo_rootx() + 20
        y = self.widget.winfo_rooty() + 20
        self.tooltip_window = tw = tk.Toplevel(self.widget)
        tw.wm_overrideredirect(True) # Removes window borders
        tw.wm_geometry(f"+{x}+{y}")
        
        label = tk.Label(tw, text=self.text, justify="left",
                         background="#333333", foreground="white", relief="solid", borderwidth=1,
                         font=("Segoe UI", 9, "normal"), padx=5, pady=5)
        label.pack()

    def hide_tooltip(self, event=None):
        if self.tooltip_window:
            self.tooltip_window.destroy()
            self.tooltip_window = None

class OscWheelApp:
    def __init__(self, root):
        self.root = root
        self.root.title("CTRL 2 OSC")
        self.root.geometry("880x780")
        
        # State variables
        self.is_running = False
        self.client = None
        
        # Hardware pools
        self.joystick = None 
        self.active_joysticks = [] 
        
        # FFB Effect IDs and Structures
        self.spring_id = None
        self.spring_effect = None
        self.damper_id = None
        self.damper_effect = None
        self.friction_id = None
        self.friction_effect = None

        self.prev_axes = {}
        self.prev_buttons = {}
        self.prev_hats = {} 
        self.prev_keys = {}
        self.axis_ema = {} 
        self.update_job = None
        self.devices_map = {} 
        
        # Configuration Variables
        self.axis_config = {}
        self.button_vars = {}
        self.button_name_vars = {}        
        self.button_addr_vars = {}        
        self.current_button_map = {i: f"Btn {i}" for i in range(24)} 
        self.hat_vars = {}
        self.hat_addr_vars = {}           
        self.keyboard_vars = {}           
        self.setting_widgets = [] 
        self.config_file = "config.json"
        
        # Profile System
        self.profiles = {"Default": {}}
        self.current_profile_name = tk.StringVar(value="Default")
        
        # UI State Variables
        self.is_wheel = False
        self.ui_indicators = {'axes': {}, 'buttons': {}, 'hats': {}, 'keys': {}}
        
        # Multi-Device Previews
        self.device_previews = []
        
        self.canvas_size = 140
        self.center = self.canvas_size // 2
        self.radius = 60
        
        # 2 way OSC
        self.osc_server_thread = None
        self.server = None
        
        self.img_off = None
        self.img_on = None
        self.icon_off_tk = None
        self.icon_on_tk = None
        self.tray_icon = None

        self._load_icons()

        sdl2.SDL_Init(sdl2.SDL_INIT_JOYSTICK | sdl2.SDL_INIT_HAPTIC)

        self._build_ui()
        self.load_config()
        self.refresh_devices() 
        self._setup_tray_icon()
        
        # Start the continuous UI preview rendering loop
        self._preview_update_loop()
        
        self.root.protocol("WM_DELETE_WINDOW", self.on_closing)
        self.root.bind_all("<MouseWheel>", self._on_mousewheel)

    def _preview_update_loop(self):
        if hasattr(self, 'device_previews') and self.active_joysticks:
            if not self.is_running:
                sdl2.SDL_JoystickUpdate()
            
            for idx, d in enumerate(self.active_joysticks):
                if idx >= len(self.device_previews): break
                joy = d['joy']
                ax_off = d['ax_off']
                p_dict = self.device_previews[idx]
                
                num_axes = sdl2.SDL_JoystickNumAxes(joy)
                
                if p_dict['is_wheel']:
                    if num_axes > 0 and p_dict['wheel_canvas'] and p_dict['wheel_spoke_id']:
                        raw_0 = sdl2.SDL_JoystickGetAxis(joy, 0) / 32767.0
                        val_0 = self.get_axis_value(ax_off + 0, raw_0)
                        
                        current_deg = val_0 * 450.0
                        angle = math.radians(current_deg - 90)
                        
                        end_x = self.center + self.radius * math.cos(angle)
                        end_y = self.center + self.radius * math.sin(angle)
                        
                        p_dict['wheel_canvas'].coords(p_dict['wheel_spoke_id'], self.center, self.center, end_x, end_y)
                        p_dict['wheel_canvas'].itemconfig(p_dict['wheel_text_id'], text=f"{int(current_deg)}°")
                    
                    for i in range(1, num_axes):
                        pidx = i - 1
                        if pidx < len(p_dict['pedal_canvases']):
                            raw_val = sdl2.SDL_JoystickGetAxis(joy, i) / 32767.0
                            val = self.get_axis_value(ax_off + i, raw_val)
                            
                            pct = (val + 1.0) / 2.0
                            
                            p_height = 140
                            y2 = p_height - 4 
                            y1 = y2 - (pct * (p_height - 8)) 
                            
                            p_dict['pedal_canvases'][pidx].coords(p_dict['pedal_rect_ids'][pidx], 4, y1, 30-4, y2)
                else:
                    for i, (ax_x, ax_y) in enumerate(p_dict['std_grid_axes']):
                        if i >= len(p_dict['preview_canvases']):
                            break
                            
                        x_val, y_val = 0.0, 0.0
                        
                        if ax_x < num_axes:
                            raw_x = sdl2.SDL_JoystickGetAxis(joy, ax_x) / 32767.0
                            x_val = self.get_axis_value(ax_off + ax_x, raw_x)
                        if ax_y is not None and ax_y < num_axes:
                            raw_y = sdl2.SDL_JoystickGetAxis(joy, ax_y) / 32767.0
                            y_val = self.get_axis_value(ax_off + ax_y, raw_y)
                            
                        draw_x = self.center + (x_val * self.radius)
                        draw_y = self.center + (y_val * self.radius)
                        
                        dot_r = 6
                        p_dict['preview_canvases'][i].coords(p_dict['preview_dots'][i], draw_x - dot_r, draw_y - dot_r, draw_x + dot_r, draw_y + dot_r)
                    
                    for i, ax in enumerate(p_dict['std_trigger_axes']):
                        if i >= len(p_dict['pedal_canvases']):
                            break
                            
                        raw_val = sdl2.SDL_JoystickGetAxis(joy, ax) / 32767.0
                        val = self.get_axis_value(ax_off + ax, raw_val)
                        
                        pct = (val + 1.0) / 2.0
                        
                        p_height = 140
                        y2 = p_height - 4 
                        y1 = y2 - (pct * (p_height - 8)) 
                        
                        p_dict['pedal_canvases'][i].coords(p_dict['pedal_rect_ids'][i], 4, y1, 30-4, y2)

        self.root.after(20, self._preview_update_loop)

    def _update_preview_labels(self, *args):
        for p_dict in getattr(self, 'device_previews', []):
            for info in p_dict['preview_labels']:
                lbl = info['label']
                if not lbl.winfo_exists():
                    continue
                
                axes = info['axes']
                
                def get_name(idx):
                    if idx in self.axis_config:
                        val = self.axis_config[idx]['name_var'].get().strip()
                        if val: return val
                    return f"Axis {idx}"
                    
                if info['type'] == 'wheel':
                    lbl.config(text=f"{get_name(axes[0])}\n(Steering)")
                elif info['type'] == 'single':
                    lbl.config(text=get_name(axes[0]))
                elif info['type'] == 'pair':
                    ax_x, ax_y = axes[0], axes[1]
                    if ax_y is not None:
                        lbl.config(text=f"{get_name(ax_x)} & {get_name(ax_y)}")
                    else:
                        lbl.config(text=get_name(ax_x))
                elif info['type'] == 'trigger':
                    lbl.config(text=f"{get_name(axes[0])}\n(Trigger)")

    def _load_icons(self):
            try:
                path_off = resource_path("steering-wheel-car_off.png")
                path_on = resource_path("steering-wheel-car_on.png")
                
                if os.path.exists(path_off) and os.path.exists(path_on):
                    self.img_off = Image.open(path_off)
                    self.img_on = Image.open(path_on)
                    
                    self.ui_icon_off = ImageTk.PhotoImage(self.img_off.resize((36, 36), Image.Resampling.LANCZOS))
                    self.ui_icon_on = ImageTk.PhotoImage(self.img_on.resize((36, 36), Image.Resampling.LANCZOS))
                    
                    self.icon_off_tk = ImageTk.PhotoImage(self.img_off)
                    self.icon_on_tk = ImageTk.PhotoImage(self.img_on)
                    self.root.iconphoto(True, self.icon_off_tk)
                else:
                    self.img_off = Image.new('RGB', (24, 24), color='red')
                    self.img_on = Image.new('RGB', (24, 24), color='green')
                    self.ui_icon_off = ImageTk.PhotoImage(self.img_off)
                    self.ui_icon_on = ImageTk.PhotoImage(self.img_on)
            except Exception:
                self.ui_icon_off = None
                self.ui_icon_on = None

    def _setup_tray_icon(self):
        menu = pystray.Menu(
            pystray.MenuItem(
                lambda item: "Stop Streaming" if self.is_running else "Start Streaming", 
                self._tray_toggle_stream
            ),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Show Window", self._show_window),
            pystray.MenuItem("Quit", self.on_closing)
        )
        self.tray_icon = pystray.Icon("OSC_Wheel", self.img_off, "C2O Controller to OSC", menu)
        threading.Thread(target=self.tray_icon.run, daemon=True).start()

    def _tray_toggle_stream(self, *args):
        self.root.after(0, self.toggle_stream)

    def _show_window(self, *args):
        self.root.after(0, lambda: [self.root.deiconify(), self.root.lift()])

    def _build_ui(self):
        self.notebook_frame = tk.Frame(self.root)
        self.notebook_frame.pack(fill="both", expand=True)

        self.tab_buttons_frame = tk.Frame(self.notebook_frame, bg="#d9d9d9", bd=1, relief="raised")
        self.tab_buttons_frame.pack(fill="x")

        self.tab_content_frame = tk.Frame(self.notebook_frame)
        self.tab_content_frame.pack(fill="both", expand=True)

        self.main_tab = tk.Frame(self.tab_content_frame)
        self.settings_tab = tk.Frame(self.tab_content_frame)
        self.help_tab = tk.Frame(self.tab_content_frame)

        self.tabs = {
            "Output Settings": self.main_tab,
            "Input Settings": self.settings_tab,
            "About": self.help_tab
        }

        self.tab_buttons = {}
        for text, frame in self.tabs.items():
            frame.grid(row=0, column=0, sticky="nsew")
            btn = tk.Button(self.tab_buttons_frame, text=text, relief="raised", padx=10, pady=2,
                            command=lambda f=frame, t=text: self.select_tab(f, t))
            btn.pack(side="left")
            self.tab_buttons[text] = btn

        self.tab_content_frame.grid_rowconfigure(0, weight=1)
        self.tab_content_frame.grid_columnconfigure(0, weight=1)

        self.select_tab(self.main_tab, "Output Settings")

        self._build_main_tab()
        self._build_settings_tab()
        self._build_help_tab()

    def select_tab(self, frame, text):
        frame.tkraise()
        self.current_tab = frame
        for t, btn in self.tab_buttons.items():
            if t == text:
                btn.config(relief="sunken", bg="#c0c0c0")
            else:
                btn.config(relief="raised", bg="#e0e0e0")

    def _build_main_tab(self):
        settings_frame = tk.LabelFrame(
            self.main_tab, 
            text="OSC Targeting & Listening", 
            padx=10, pady=10,
            font=("Segoe UI", 10, "bold"), 
            fg="#0052cc", 
            bd=1, relief="solid" 
        )
        settings_frame.pack(fill="x", padx=10, pady=10)

        settings_frame.columnconfigure(1, weight=1)

        tk.Label(settings_frame, text="Target IP:").grid(row=0, column=0, sticky="w", pady=2)
        self.ip_entry = tk.Entry(settings_frame, width=20)
        self.ip_entry.insert(0, "127.0.0.1")
        self.ip_entry.grid(row=0, column=1, pady=2, padx=5, sticky="w")
        self.setting_widgets.append(self.ip_entry)

        tk.Label(settings_frame, text="Target Port (Send):").grid(row=1, column=0, sticky="w", pady=2)
        self.port_entry = tk.Entry(settings_frame, width=10)
        self.port_entry.insert(0, "4041")
        self.port_entry.grid(row=1, column=1, pady=2, padx=5, sticky="w")
        self.setting_widgets.append(self.port_entry)

        tk.Label(settings_frame, text="Listen Port (FFB In):").grid(row=2, column=0, sticky="w", pady=2)
        self.listen_port_entry = tk.Entry(settings_frame, width=10)
        self.listen_port_entry.insert(0, "4042")
        self.listen_port_entry.grid(row=2, column=1, pady=2, padx=5, sticky="w")
        self.setting_widgets.append(self.listen_port_entry)

        tk.Label(settings_frame, text="Base OSC Address:").grid(row=3, column=0, sticky="w", pady=2)
        self.addr_entry = tk.Entry(settings_frame, width=25)
        self.addr_entry.insert(0, "/wheel/input")
        self.addr_entry.grid(row=3, column=1, pady=2, padx=5, sticky="w")
        self.setting_widgets.append(self.addr_entry)

        control_frame = tk.Frame(self.main_tab)
        control_frame.pack(fill="x", padx=10, pady=5)

        self.start_btn = tk.Button(
            control_frame, 
            text="Start Streaming", 
            bg="#28a745", fg="white", activebackground="#218838", activeforeground="white",
            command=self.toggle_stream
        )
        self.start_btn.pack(side="left", expand=True, fill="x", padx=(0, 5))

        self.clear_btn = tk.Button(control_frame, text="Clear Log", command=self.clear_log)
        self.clear_btn.config(
            bg="#cc2900", fg="white", 
            activebackground="#720E0E", activeforeground="white", 
            font=("Segoe UI", 10, "bold"), relief="raised", bd=2, highlightthickness=0
        )
        self.clear_btn.pack(side="right", expand=True, fill="x", padx=(5, 0))

        output_options_frame = tk.Frame(self.main_tab)
        output_options_frame.pack(fill="x", padx=10, pady=(5, 0))
        
        tk.Label(output_options_frame, text="Output Style:").pack(side="left")
        
        self.output_mode = tk.StringVar(value="scroll")
        tk.Radiobutton(output_options_frame, text="Scrolling Log", variable=self.output_mode, value="scroll", command=self.on_mode_change).pack(side="left", padx=5)
        tk.Radiobutton(output_options_frame, text="In-Place Dashboard", variable=self.output_mode, value="inplace", command=self.on_mode_change).pack(side="left", padx=5)

        self.status_icon_label = tk.Label(output_options_frame, image=self.ui_icon_off)
        self.status_icon_label.pack(side="right", padx=20)

        self.log_area = scrolledtext.ScrolledText(self.main_tab, height=25, state='disabled', bg="#1e1e1e", fg="#00ffff", font=("Consolas", 9))
        self.log_area.pack(fill="both", expand=True, padx=10, pady=(5, 10))

    def _build_settings_tab(self):
        self.settings_canvas = tk.Canvas(self.settings_tab, highlightthickness=0)
        
        self.v_scrollbar = tk.Scrollbar(self.settings_tab, orient="vertical", command=self.settings_canvas.yview)
        self.h_scrollbar = tk.Scrollbar(self.settings_tab, orient="horizontal", command=self.settings_canvas.xview)
        
        self.scrollable_frame = tk.Frame(self.settings_canvas)

        self.scrollable_frame.bind("<Configure>", lambda e: self.settings_canvas.configure(scrollregion=self.settings_canvas.bbox("all")))
        self.canvas_window_id = self.settings_canvas.create_window((0, 0), window=self.scrollable_frame, anchor="nw")
        
        self.settings_canvas.configure(yscrollcommand=self.v_scrollbar.set, xscrollcommand=self.h_scrollbar.set)

        def _on_canvas_configure(event):
            req_width = self.scrollable_frame.winfo_reqwidth()
            new_width = max(event.width, req_width)
            self.settings_canvas.itemconfig(self.canvas_window_id, width=new_width)
            
        self.settings_canvas.bind("<Configure>", _on_canvas_configure)

        self.h_scrollbar.pack(side="bottom", fill="x")
        self.v_scrollbar.pack(side="right", fill="y")
        self.settings_canvas.pack(side="left", fill="both", expand=True)

        profile_frame = tk.LabelFrame(
            self.scrollable_frame, 
            text="Input Profile", 
            padx=10, pady=10,
            font=("Segoe UI", 10, "bold"), 
            fg="#0052cc",
            bd=0, relief="solid"
        )
        profile_frame.pack(fill="x", padx=10, pady=(10, 5))

        self.profile_combo = tk.OptionMenu(profile_frame, self.current_profile_name, "Default", command=self.on_profile_selected)
        
        self.profile_combo.config(
            bg="#0052cc", fg="white", 
            activebackground="#003d99", activeforeground="white", 
            font=("Segoe UI", 10, "bold"), relief="raised", bd=2, highlightthickness=0
        )
        self.profile_combo["menu"].config(
            bg="#f8f9fa", fg="black", 
            activebackground="#0052cc", activeforeground="white", 
            font=("Segoe UI", 10)
        )
        self.profile_combo.pack(side="left", fill="x", expand=True, padx=(0, 5))
        self.setting_widgets.append(self.profile_combo)

        self.new_prof_btn = tk.Button(profile_frame, text="New Profile", command=self.new_profile)
        self.new_prof_btn.config(
            bg="#00cc44", fg="white", 
            activebackground="#009952", activeforeground="white", 
            font=("Segoe UI", 10, "bold"), relief="raised", bd=2, highlightthickness=0
        )
        self.new_prof_btn.pack(side="left", padx=2)
        self.setting_widgets.append(self.new_prof_btn)

        self.del_prof_btn = tk.Button(profile_frame, text="Delete", command=self.delete_profile)
        self.del_prof_btn.config(
            bg="#cc2900", fg="white", 
            activebackground="#720E0E", activeforeground="white", 
            font=("Segoe UI", 10, "bold"), relief="raised", bd=2, highlightthickness=0
        )
        self.del_prof_btn.pack(side="left", padx=2)
        self.setting_widgets.append(self.del_prof_btn)

        device_frame = tk.LabelFrame(
            self.scrollable_frame, 
            text="Input Device", 
            padx=10, pady=10,
            font=("Segoe UI", 10, "bold"), 
            fg="#0052cc", 
            bd=0, relief="solid" 
        )
        device_frame.pack(fill="x", padx=10, pady=(10, 5))

        self.device_var = tk.StringVar()
        self.device_dropdown = tk.OptionMenu(device_frame, self.device_var, "")
        
        self.device_dropdown.config(
            bg="#0052cc", fg="white", 
            activebackground="#003d99", activeforeground="white", 
            font=("Segoe UI", 10, "bold"), relief="raised", bd=2, highlightthickness=0
        )
        self.device_dropdown["menu"].config(
            bg="#f8f9fa", fg="black", 
            activebackground="#0052cc", activeforeground="white", 
            font=("Segoe UI", 10)
        )

        self.device_dropdown.pack(side="left", fill="x", expand=True, padx=(0, 5))
        self.setting_widgets.append(self.device_dropdown)

        self.add_device_btn = tk.Button(device_frame, text="Add Device", command=self.add_device)
        self.add_device_btn.config(
            bg="#00cc44", fg="white", 
            activebackground="#009952", activeforeground="white", 
            font=("Segoe UI", 10, "bold"), relief="raised", bd=2, highlightthickness=0
        )
        self.add_device_btn.pack(side="left", padx=2)
        self.setting_widgets.append(self.add_device_btn)
        
        self.clear_device_btn = tk.Button(device_frame, text="Clear Devices", command=self.clear_devices)
        self.clear_device_btn.config(
            bg="#cc2900", fg="white", 
            activebackground="#720E0E", activeforeground="white", 
            font=("Segoe UI", 10, "bold"), relief="raised", bd=2, highlightthickness=0
        )
        self.clear_device_btn.pack(side="left", padx=2)
        self.setting_widgets.append(self.clear_device_btn)

        self.refresh_btn = tk.Button(device_frame, text="Refresh Devices", command=self.refresh_devices)
        self.refresh_btn.config(
            bg="#ccc900", fg="white", 
            activebackground="#8f9900", activeforeground="white", 
            font=("Segoe UI", 10, "bold"), relief="raised", bd=2, highlightthickness=0
        )
        self.refresh_btn.pack(side="right")
        self.setting_widgets.append(self.refresh_btn)

        self.ffb_frame = tk.LabelFrame(self.scrollable_frame, text="Hardware Force Feedback Parameters", padx=10, pady=10)
        
        spring_container = tk.Frame(self.ffb_frame)
        spring_container.pack(fill="x", pady=2)
        tk.Label(spring_container, text="Centering Spring:", width=15, anchor="w").pack(side="left")
        
        self.ffb_spring_var = tk.DoubleVar(value=50.0) 
        spring_lbl = tk.Label(spring_container, text=f"{self.ffb_spring_var.get():.0f}", width=4)
        spring_lbl.pack(side="left")
        
        self.ffb_spring_scale = tk.Scale(
            spring_container, variable=self.ffb_spring_var, from_=0, to=100, orient="horizontal", showvalue=0,
            command=lambda v, lbl=spring_lbl: (lbl.config(text=f"{float(v):.0f}"), self.update_ffb())
        )
        self.ffb_spring_scale.pack(side="left", fill="x", expand=True, padx=5)
        self.setting_widgets.append(self.ffb_spring_scale)

        damper_container = tk.Frame(self.ffb_frame)
        damper_container.pack(fill="x", pady=2)
        tk.Label(damper_container, text="Damper (Weight):", width=15, anchor="w").pack(side="left")
        
        self.ffb_damper_var = tk.DoubleVar(value=20.0) 
        damper_lbl = tk.Label(damper_container, text=f"{self.ffb_damper_var.get():.0f}", width=4)
        damper_lbl.pack(side="left")
        
        self.ffb_damper_scale = tk.Scale(
            damper_container, variable=self.ffb_damper_var, from_=0, to=100, orient="horizontal", showvalue=0,
            command=lambda v, lbl=damper_lbl: (lbl.config(text=f"{float(v):.0f}"), self.update_ffb())
        )
        self.ffb_damper_scale.pack(side="left", fill="x", expand=True, padx=5)
        self.setting_widgets.append(self.ffb_damper_scale)

        friction_container = tk.Frame(self.ffb_frame)
        friction_container.pack(fill="x", pady=2)
        tk.Label(friction_container, text="Static Friction:", width=15, anchor="w").pack(side="left")
        
        self.ffb_friction_var = tk.DoubleVar(value=10.0) 
        friction_lbl = tk.Label(friction_container, text=f"{self.ffb_friction_var.get():.0f}", width=4)
        friction_lbl.pack(side="left")
        
        self.ffb_friction_scale = tk.Scale(
            friction_container, variable=self.ffb_friction_var, from_=0, to=100, orient="horizontal", showvalue=0,
            command=lambda v, lbl=friction_lbl: (lbl.config(text=f"{float(v):.0f}"), self.update_ffb())
        )
        self.ffb_friction_scale.pack(side="left", fill="x", expand=True, padx=5)
        self.setting_widgets.append(self.ffb_friction_scale)

        self.preview_frame = tk.LabelFrame(
            self.scrollable_frame, 
            text="Input Preview", 
            padx=10, pady=10,
            font=("Segoe UI", 10, "bold"), 
            fg="#0052cc",
            bd=0, relief="solid"
        )
        self.preview_frame.pack(fill="x", padx=10, pady=5)
        
        self.axes_frame = tk.LabelFrame(
            self.scrollable_frame, 
            text="Axis Configuration", 
            padx=10, pady=10,
            font=("Segoe UI", 10, "bold"), 
            fg="#0052cc",
            bd=1, relief="solid"
        )
        self.axes_frame.pack(fill="x", padx=10, pady=5)

        buttons_frame = tk.LabelFrame(
            self.scrollable_frame, 
            text="Button Mapping", 
            padx=10, pady=10,
            font=("Segoe UI", 10, "bold"), 
            fg="#0052cc",
            bd=1, relief="solid"
        )
        buttons_frame.pack(fill="both", expand=True, padx=10, pady=(0, 5))

        help_lbl = tk.Label(buttons_frame, text="[?] Hover for help", fg="#4C98AF", cursor="question_arrow")
        help_lbl.pack(anchor="e", pady=(0, 5))
        
        help_text = (
            "About mapping buttons :\n\n"
            "• Button Name : Based on auto-detect, your button map will default to the predetermined button names given by your device's driver. \n You can customize each buttons name if you wish by clicking and typing in the appropriate field. \n\n"
             "• ID / OSC ID : You can customize what OSC ID you want your button to broadcast its message to. This is optional, as C2O sets it up by default in numerical order.\n\n"
            "• Addr : The target OSC Address (e.g., '/avatar/parameters/Jump').\n"
        )
        ToolTip(help_lbl, help_text)

        self.buttons_grid_frame = tk.Frame(buttons_frame)
        self.buttons_grid_frame.pack(expand=True)
        self._populate_buttons_frame()

        hats_frame = tk.LabelFrame(
            self.scrollable_frame, 
            text="D-Pad / Hat Mapping", 
            padx=10, pady=10,
            font=("Segoe UI", 10, "bold"), 
            fg="#0052cc",
            bd=1, relief="solid"
        )
        hats_frame.pack(fill="x", padx=10, pady=(0, 5))
        
        self.hat_grid_frame = tk.Frame(hats_frame)
        self.hat_grid_frame.pack(expand=True)
        self._populate_hats_frame()
        
        keyboard_frame = tk.LabelFrame(
            self.scrollable_frame, 
            text="Global Keyboard Mapping (Key -> OSC Address / ID)", 
            padx=10, pady=10,
            font=("Segoe UI", 10, "bold"), 
            fg="#0052cc",
            bd=1, relief="solid"
        )
        keyboard_frame.pack(fill="x", padx=10, pady=(0, 5))
        
        help_lbl = tk.Label(keyboard_frame, text="[?] Hover for help", fg="#4C98AF", cursor="question_arrow")
        help_lbl.pack(anchor="e", pady=(0, 5))
        
        help_text = (
            "How to bind/map keys:\n\n"
            "• Key : The keyboard key you want to press (e.g., 'w', 'space', 'shift', 'ctrl+c').\n\n"
             "• Key Combinations : You can even type key combinations into that field and they'll work too! (e.g., 'shift+1', 'ctrl+c', 'F3+4', 'w+d+c', and so on.).\n\n"
            "• Addr : The target OSC Address (e.g., '/avatar/parameters/Jump').\n\n"
            "• ID : An optional numeric ID to pass as an argument."
        )
        ToolTip(help_lbl, help_text)

        if KEYBOARD_AVAILABLE:
            k_grid = tk.Frame(keyboard_frame)
            k_grid.pack(expand=True)
            for i in range(12): 
                row = i // 2
                col = (i % 2) * 6
                
                tk.Label(k_grid, text=f"Slot {i+1} Key:").grid(row=row, column=col, sticky="e", pady=2)
                k_var = tk.StringVar()
                k_ent = tk.Entry(k_grid, textvariable=k_var, width=5)
                k_ent.grid(row=row, column=col+1, padx=2, pady=2)
                self.ui_indicators['keys'][i] = k_ent
                
                tk.Label(k_grid, text="-> Addr:").grid(row=row, column=col+2, sticky="e", pady=2)
                a_var = tk.StringVar()
                a_ent = tk.Entry(k_grid, textvariable=a_var, width=12)
                a_ent.grid(row=row, column=col+3, padx=2, pady=2)
                
                tk.Label(k_grid, text="ID:").grid(row=row, column=col+4, sticky="e", pady=2)
                i_var = tk.StringVar()
                i_ent = tk.Entry(k_grid, textvariable=i_var, width=5)
                i_ent.grid(row=row, column=col+5, padx=(2, 20), pady=2)
                
                self.keyboard_vars[i] = {'key': k_var, 'addr': a_var, 'id': i_var}
                self.setting_widgets.extend([k_ent, a_ent, i_ent])
        else:
            tk.Label(keyboard_frame, text="Keyboard module not installed. Run 'pip install keyboard' in your environment to enable.", fg="#e74c3c").pack(pady=5)

        reset_frame = tk.Frame(self.scrollable_frame)
        reset_frame.pack(fill="x", padx=10, pady=20)

        self.reset_btn = tk.Button(reset_frame, text="Reset Mappings", command=self.reset_mappings)
        self.reset_btn.pack(side="right", padx=(5, 0))
        self.setting_widgets.append(self.reset_btn)

        self.save_btn = tk.Button(reset_frame, text="Save Settings", command=self.save_config)
        self.save_btn.pack(side="right")
        self.setting_widgets.append(self.save_btn)

    def _on_mousewheel(self, event):
            if getattr(self, 'current_tab', None) == self.settings_tab:
                if event.widget.winfo_class() not in ('OptionMenu', 'Listbox'):
                    scroll_dir = int(-1*(event.delta/120))
                    self.settings_canvas.yview_scroll(scroll_dir, "units")

    def _create_button_grid(self, parent, offset, count):
        for i in range(count):
            global_i = offset + i
            col = (i // 12) * 5
            row = i % 12
            
            if global_i not in self.button_name_vars:
                self.button_name_vars[global_i] = tk.StringVar(value=f"Btn {global_i}")
            if global_i not in self.button_vars:
                self.button_vars[global_i] = tk.StringVar(value=str(global_i))
            if global_i not in self.button_addr_vars:
                self.button_addr_vars[global_i] = tk.StringVar(value="")
            
            name_var = self.button_name_vars[global_i]
            name_ent = tk.Entry(parent, textvariable=name_var, width=16)
            name_ent.grid(row=row, column=col, sticky="e", pady=2)
            self.setting_widgets.append(name_ent)
            self.ui_indicators['buttons'][global_i] = name_ent

            tk.Label(parent, text="-> ID:").grid(row=row, column=col+1, padx=2)
            
            var = self.button_vars[global_i]
            ent = tk.Entry(parent, textvariable=var, width=5)
            ent.grid(row=row, column=col+2, padx=(0, 5), pady=2)
            self.setting_widgets.append(ent)
            
            tk.Label(parent, text="Addr:").grid(row=row, column=col+3, padx=2)
            
            addr_var = self.button_addr_vars[global_i]
            addr_ent = tk.Entry(parent, textvariable=addr_var, width=12)
            addr_ent.grid(row=row, column=col+4, padx=(0, 20), pady=2)
            self.setting_widgets.append(addr_ent)

    def _populate_buttons_frame(self):
            for widget in self.buttons_grid_frame.winfo_children():
                widget.destroy()

            if not self.active_joysticks:
                tk.Label(self.buttons_grid_frame, text="No devices connected. Default 24 slots shown below:", fg="gray").pack(pady=5)
                # Create a dedicated inner frame for the grid so pack and grid don't mix
                default_grid_frame = tk.Frame(self.buttons_grid_frame)
                default_grid_frame.pack(expand=True)
                self._create_button_grid(default_grid_frame, 0, 24)
                return

            for d in self.active_joysticks:
                dev_frame = tk.LabelFrame(self.buttons_grid_frame, text=f"Device: {d['name'].title()}", fg="#00cc44", bd=1, relief="solid")
                dev_frame.pack(fill="x", padx=5, pady=5)
                inner_frame = tk.Frame(dev_frame)
                inner_frame.pack(expand=True)
                self._create_button_grid(inner_frame, d['btn_off'], d['num_buttons'])

    def _create_hat_grid(self, parent, offset, count):
        for i in range(count):
            global_i = offset + i
            if global_i not in self.hat_vars:
                self.hat_vars[global_i] = tk.StringVar(value=str(global_i))
            if global_i not in self.hat_addr_vars:
                self.hat_addr_vars[global_i] = tk.StringVar(value="")
                
            tk.Label(parent, text=f"Hat {i} -> ID:").grid(row=i, column=0, sticky="e", pady=2)
            var = self.hat_vars[global_i]
            ent = tk.Entry(parent, textvariable=var, width=5)
            ent.grid(row=i, column=1, padx=(0, 5), pady=2)
            self.setting_widgets.append(ent)
            self.ui_indicators['hats'][global_i] = ent
            
            tk.Label(parent, text="Addr:").grid(row=i, column=2, sticky="e", pady=2)
            addr_var = self.hat_addr_vars[global_i]
            addr_ent = tk.Entry(parent, textvariable=addr_var, width=12)
            addr_ent.grid(row=i, column=3, padx=(0, 20), pady=2)
            self.setting_widgets.append(addr_ent)

    def _populate_hats_frame(self):
        for widget in self.hat_grid_frame.winfo_children():
            widget.destroy()
            
        if not self.active_joysticks:
            tk.Label(self.hat_grid_frame, text="No devices connected. Default 4 slots shown below:", fg="gray").pack(pady=5)
            # Create a dedicated inner frame for the hat grid
            default_hat_frame = tk.Frame(self.hat_grid_frame)
            default_hat_frame.pack(expand=True)
            self._create_hat_grid(default_hat_frame, 0, 4)
            return
            
        for d in self.active_joysticks:
            if d['num_hats'] > 0:
                dev_frame = tk.LabelFrame(self.hat_grid_frame, text=f"Device: {d['name'].title()}", fg="#00cc44", bd=1, relief="solid")
                dev_frame.pack(fill="x", padx=5, pady=5)
                inner_frame = tk.Frame(dev_frame)
                inner_frame.pack(expand=True)
                self._create_hat_grid(inner_frame, d['hat_off'], d['num_hats'])

    def _populate_preview_frame(self):
        for widget in self.preview_frame.winfo_children():
            widget.destroy()
            
        self.device_previews = getattr(self, 'device_previews', [])
        self.device_previews.clear()
        
        if not self.active_joysticks:
            tk.Label(self.preview_frame, text="Selected device has no valid axes.", fg="gray").pack(pady=5)
            return

        for d in self.active_joysticks:
            preview_dict = {
                'is_wheel': False,
                'wheel_canvas': None,
                'wheel_spoke_id': None,
                'wheel_text_id': None,
                'pedal_canvases': [],
                'pedal_rect_ids': [],
                'preview_canvases': [],
                'preview_dots': [],
                'std_grid_axes': [],
                'std_trigger_axes': [],
                'preview_labels': []
            }
            
            name = d['name']
            wheel_keywords = ['wheel', 'racing', 'g29', 'g920', 'thrustmaster', 'fanatec', 'moza']
            joy_type = sdl2.SDL_JoystickGetType(d['joy'])
            is_wheel = (joy_type == sdl2.SDL_JOYSTICK_TYPE_WHEEL) or any(kw in name for kw in wheel_keywords)
            preview_dict['is_wheel'] = is_wheel
            
            dev_frame = tk.LabelFrame(self.preview_frame, text=f"Device: {name.title()}", fg="#00cc44", bd=1, relief="solid")
            dev_frame.pack(fill="x", padx=5, pady=5)

            num_axes = d['num_axes']
            ax_off = d['ax_off']

            if num_axes == 0:
                tk.Label(dev_frame, text="No axes to preview.", fg="gray").pack(pady=5)
                self.device_previews.append(preview_dict)
                continue

            if is_wheel:
                container = tk.Frame(dev_frame)
                container.pack(expand=True, pady=10)

                wheel_frame = tk.Frame(container)
                wheel_frame.pack(side="left", padx=20)
                w_lbl = tk.Label(wheel_frame, text="")
                w_lbl.pack(pady=(0, 5))
                preview_dict['preview_labels'].append({'label': w_lbl, 'type': 'wheel', 'axes': [ax_off + 0]})
                
                preview_dict['wheel_canvas'] = tk.Canvas(wheel_frame, width=self.canvas_size, height=self.canvas_size, bg="black", highlightthickness=0)
                preview_dict['wheel_canvas'].pack()
                
                preview_dict['wheel_canvas'].create_oval(
                    self.center - self.radius, self.center - self.radius,
                    self.center + self.radius, self.center + self.radius,
                    outline="#555555", width=4
                )
                preview_dict['wheel_canvas'].create_oval(
                    self.center - 4, self.center - 4,
                    self.center + 4, self.center + 4,
                    fill="#15ff00"
                )
                
                preview_dict['wheel_spoke_id'] = preview_dict['wheel_canvas'].create_line(
                    self.center, self.center,
                    self.center, self.center - self.radius,
                    fill="#15ff00", width=3
                )
                
                preview_dict['wheel_text_id'] = preview_dict['wheel_canvas'].create_text(
                    self.center, self.center + 30, text="0°", fill="white", font=("Segoe UI", 10, "bold")
                )

                if num_axes > 1:
                    pedals_frame = tk.Frame(container)
                    pedals_frame.pack(side="left", padx=20)
                    
                    for i in range(1, num_axes):
                        p_frame = tk.Frame(pedals_frame)
                        p_frame.pack(side="left", padx=10)
                        p_lbl = tk.Label(p_frame, text="")
                        p_lbl.pack(pady=(0, 5))
                        preview_dict['preview_labels'].append({'label': p_lbl, 'type': 'single', 'axes': [ax_off + i]})
                        
                        p_width = 30
                        p_height = 140
                        p_canvas = tk.Canvas(p_frame, width=p_width, height=p_height, bg="black", highlightthickness=0)
                        p_canvas.pack()
                        
                        p_canvas.create_rectangle(2, 2, p_width-2, p_height-2, outline="#555555", width=2)
                        rect_id = p_canvas.create_rectangle(4, p_height-4, p_width-4, p_height-4, fill="#15ff00", outline="")
                        
                        preview_dict['pedal_canvases'].append(p_canvas)
                        preview_dict['pedal_rect_ids'].append(rect_id)
            else:
                container = tk.Frame(dev_frame)
                container.pack(expand=True, pady=5)
                
                grid_axes = [i for i in range(num_axes) if i not in (4, 5)]
                trigger_axes = [i for i in range(num_axes) if i in (4, 5)]
                
                grid_frame = tk.Frame(container)
                grid_frame.pack(side="left")
                
                if trigger_axes:
                    trigger_frame = tk.Frame(container)
                    trigger_frame.pack(side="left", padx=(20 if grid_axes else 0))
                
                num_pairs = math.ceil(len(grid_axes) / 2)
                dot_r = 6
                for i in range(num_pairs):
                    col = i % 3
                    row = i // 3
                    
                    pair_frame = tk.Frame(grid_frame)
                    pair_frame.grid(row=row, column=col, padx=15, pady=5)
                    
                    ax_x = grid_axes[i*2]
                    ax_y = grid_axes[i*2+1] if (i*2+1) < len(grid_axes) else None
                    preview_dict['std_grid_axes'].append((ax_x, ax_y))
                    
                    pair_lbl = tk.Label(pair_frame, text="")
                    pair_lbl.pack()
                    preview_dict['preview_labels'].append({'label': pair_lbl, 'type': 'pair', 'axes': [ax_off + ax_x, ax_off + ax_y if ax_y is not None else None]})
                    
                    canvas = tk.Canvas(pair_frame, width=self.canvas_size, height=self.canvas_size, bg="black", highlightthickness=0)
                    canvas.pack()
                    
                    grid_color = "#222222"
                    for step in range(10, self.canvas_size, 10):
                        canvas.create_line(step, 0, step, self.canvas_size, fill=grid_color)
                        canvas.create_line(0, step, self.canvas_size, step, fill=grid_color)
                        
                    canvas.create_oval(self.center - self.radius, self.center - self.radius,
                                       self.center + self.radius, self.center + self.radius,
                                       outline="#555555", width=2)
                    canvas.create_line(self.center, 0, self.center, self.canvas_size, fill="#666666", width=2)
                    canvas.create_line(0, self.center, self.canvas_size, self.center, fill="#666666", width=2)
                    
                    red_dot = canvas.create_oval(self.center - dot_r, self.center - dot_r,
                                                 self.center + dot_r, self.center + dot_r,
                                                 fill="red", outline="red")
                                                 
                    preview_dict['preview_canvases'].append(canvas)
                    preview_dict['preview_dots'].append(red_dot)
                    
                for ax in trigger_axes:
                    t_frame = tk.Frame(trigger_frame)
                    t_frame.pack(side="left", padx=10)
                    t_lbl = tk.Label(t_frame, text="")
                    t_lbl.pack(pady=(0, 5))
                    preview_dict['preview_labels'].append({'label': t_lbl, 'type': 'trigger', 'axes': [ax_off + ax]})
                    
                    preview_dict['std_trigger_axes'].append(ax)
                    
                    p_width = 30
                    p_height = 140
                    p_canvas = tk.Canvas(t_frame, width=p_width, height=p_height, bg="black", highlightthickness=0)
                    p_canvas.pack()
                    
                    p_canvas.create_rectangle(2, 2, p_width-2, p_height-2, outline="#555555", width=2)
                    rect_id = p_canvas.create_rectangle(4, p_height-4, p_width-4, p_height-4, fill="#15ff00", outline="")
                    
                    preview_dict['pedal_canvases'].append(p_canvas)
                    preview_dict['pedal_rect_ids'].append(rect_id)

            self.device_previews.append(preview_dict)
            
        self._update_preview_labels()

    def _populate_axes_frame(self):
        self.setting_widgets = [w for w in self.setting_widgets if w.winfo_exists()]
        
        for widget in self.axes_frame.winfo_children():
            widget.destroy()
            
        if not self.active_joysticks:
            tk.Label(self.axes_frame, text="Please connect and select a controller.", fg="gray").pack(pady=5)
            return

        profile_data = self.profiles.get(self.current_profile_name.get(), {})
        custom_axes_data = profile_data.get("axes", {})

        for d in self.active_joysticks:
            dev_frame = tk.LabelFrame(self.axes_frame, text=f"Device: {d['name'].title()}", fg="#00cc44", bd=1, relief="solid")
            dev_frame.pack(fill="x", padx=5, pady=5)
            
            num_axes = d['num_axes']
            ax_off = d['ax_off']
            
            for i in range(num_axes):
                axis_idx = ax_off + i
                axis_container = tk.Frame(dev_frame)
                axis_container.pack(fill="x", pady=8)
                
                if axis_idx not in self.axis_config:
                    name_var = tk.StringVar(value=f"Axis {axis_idx}")
                    name_var.trace_add("write", self._update_preview_labels)
                    self.axis_config[axis_idx] = {
                        'name_var': tk.StringVar(value=f"Axis {axis_idx}"),
                        'id_var': tk.StringVar(value=str(axis_idx)),
                        'addr_var': tk.StringVar(value=""),
                        'inv_var': tk.BooleanVar(value=False),
                        'sens_var': tk.DoubleVar(value=1.0),
                        'dead_var': tk.DoubleVar(value=0.0),
                        'curve_var': tk.DoubleVar(value=1.0),
                        'smooth_var': tk.DoubleVar(value=0.0)
                    }
                config = self.axis_config[axis_idx]
                
                saved_name = f"Axis {axis_idx}"
                if str(axis_idx) in custom_axes_data:
                    saved_name = custom_axes_data[str(axis_idx)].get("custom_name", saved_name)
                config['name_var'].set(saved_name)
                
                top_row = tk.Frame(axis_container)
                top_row.pack(fill="x", pady=(0, 5))
                
                name_entry = tk.Entry(top_row, textvariable=config['name_var'], width=15)
                name_entry.pack(side="left")
                self.setting_widgets.append(name_entry)
                self.ui_indicators['axes'][axis_idx] = name_entry
                
                tk.Label(top_row, text="-> ID:").pack(side="left", padx=(15, 2))
                id_entry = tk.Entry(top_row, textvariable=config['id_var'], width=4)
                id_entry.pack(side="left")
                self.setting_widgets.append(id_entry)
                
                tk.Label(top_row, text="Addr:").pack(side="left", padx=(15, 2))
                addr_entry = tk.Entry(top_row, textvariable=config['addr_var'], width=18)
                addr_entry.pack(side="left")
                self.setting_widgets.append(addr_entry)

                bottom_row = tk.Frame(axis_container)
                bottom_row.pack(fill="x")

                third_row = tk.Frame(axis_container)
                third_row.pack(fill="x")

                chk = tk.Checkbutton(top_row, text="Invert   ", variable=config['inv_var'])
                chk.pack(side="left", padx=(20, 0))
                self.setting_widgets.append(chk)

                dz_frame = tk.Frame(bottom_row)
                dz_frame.pack(side="left", expand=True, fill="x", padx=(0, 5))
                tk.Label(dz_frame, text="Deadzone:").pack(side="left")
                dead_val_lbl = tk.Label(dz_frame, text=f"{config['dead_var'].get():.2f}", width=4)
                dead_val_lbl.pack(side="left")
                dead_scale = tk.Scale(
                    dz_frame, variable=config['dead_var'], from_=0.0, to=0.5, orient="horizontal", showvalue=0, resolution=0.01,
                    command=lambda v, lbl=dead_val_lbl: lbl.config(text=f"{float(v):.2f}")
                )
                dead_scale.pack(side="left", fill="x", expand=True)
                self.setting_widgets.append(dead_scale)

                sens_frame = tk.Frame(bottom_row)
                sens_frame.pack(side="left", expand=True, fill="x", padx=5)
                tk.Label(sens_frame, text="Sens:").pack(side="left")
                sens_val_lbl = tk.Label(sens_frame, text=f"{config['sens_var'].get():.1f}", width=3)
                sens_val_lbl.pack(side="left")
                sens_scale = tk.Scale(
                    sens_frame, variable=config['sens_var'], from_=0.1, to=5.0, orient="horizontal", showvalue=0, resolution=0.1,
                    command=lambda v, lbl=sens_val_lbl: lbl.config(text=f"{float(v):.1f}")
                )
                sens_scale.pack(side="left", fill="x", expand=True)
                self.setting_widgets.append(sens_scale)

                curve_frame = tk.Frame(third_row)
                curve_frame.pack(side="left", expand=True, fill="x", padx=5)
                tk.Label(curve_frame, text="Curve:").pack(side="left")
                curve_val_lbl = tk.Label(curve_frame, text=f"{config['curve_var'].get():.1f}", width=3)
                curve_val_lbl.pack(side="left")
                curve_scale = tk.Scale(
                    curve_frame, variable=config['curve_var'], from_=0.1, to=5.0, orient="horizontal", showvalue=0, resolution=0.1,
                    command=lambda v, lbl=curve_val_lbl: lbl.config(text=f"{float(v):.1f}")
                )
                curve_scale.pack(side="left", fill="x", expand=True)
                self.setting_widgets.append(curve_scale)

                smooth_frame = tk.Frame(third_row)
                smooth_frame.pack(side="left", expand=True, fill="x", padx=(5, 0))
                tk.Label(smooth_frame, text="Smooth:").pack(side="left")
                smooth_val_lbl = tk.Label(smooth_frame, text=f"{config['smooth_var'].get():.2f}", width=4)
                smooth_val_lbl.pack(side="left")
                smooth_scale = tk.Scale(
                    smooth_frame, variable=config['smooth_var'], from_=0.0, to=0.99, orient="horizontal", showvalue=0, resolution=0.01,
                    command=lambda v, lbl=smooth_val_lbl: lbl.config(text=f"{float(v):.2f}")
                )
                smooth_scale.pack(side="left", fill="x", expand=True)
                self.setting_widgets.append(smooth_scale)

                if i < num_axes - 1:
                    tk.Frame(dev_frame, height=2, bd=1, relief="sunken").pack(fill="x", pady=(10, 0))
            
    def _build_help_tab(self):
        help_text = scrolledtext.ScrolledText(
            self.help_tab, wrap=tk.WORD, font=("Segoe UI", 10),
            bg="#2b2b2b", fg="#e0e0e0", padx=15, pady=15
        )
        help_text.pack(fill="both", expand=True)

        help_text.tag_configure("h1", font=("Segoe UI", 16, "bold"), foreground="#F6C864", spacing1=10, spacing3=10)
        help_text.tag_configure("h2", font=("Segoe UI", 12, "bold"), foreground="#F6C864", spacing1=15, spacing3=5)
        help_text.tag_configure("bold", font=("Segoe UI", 10, "bold"), foreground="#f0ebcf")
        help_text.tag_configure("code", font=("Consolas", 10), background="#1e1e1e", foreground="#00ff4c")
        help_text.tag_configure("bullet", lmargin1=20, lmargin2=35, spacing1=2, spacing3=2)
        help_text.tag_configure("bold_name", font=("Segoe UI", 10, "bold"), foreground="#21f305")

        content = [
                    ("Controller 2 OSC \n", "h1"),
                    ("Version 2.4.3\n", "code"),
                    ("\n C2O is a lightweight, GUI-driven Python application designed to seamlessly bridge the gap between physical hardware and digital environments. It reads real-time data from connected USB steering wheels, Bluetooth gamepads, joysticks, and keyboards. Capturing everything from continuous analog axes (like pedals and throttles) to discrete button presses and D-pad movements.\n\n", ""),
                    ("The application translates and broadcasts these inputs over a local network using the Open Sound Control (OSC) protocol, ensuring low-latency communication without the need for heavy middleware.\n\n", ""),
                    ("C2O was developed as, and aims to be a versatile solution for mapping physical simulation hardware to Massive Loop.\n\n", ""),
                    ("Programmed by Brandon Withington\n", "code"),

                    ("How to Use\n", "h2"),
                    ("1. Plug in your device: ", "bold"),
                    ("Ensure your USB steering wheel or controller is connected to your PC.\n\n", ""),
                    ("2. Configure the Settings:\n\n", "bold"),
                    ("   • Target IP: ", "bold"),
                    ("The IP address of the receiving machine.\n\n", "bullet"),
                    ("   • Target Port (Send): ", "bold"),
                    ("The network port your receiving application is listening to.\n\n", "bullet"),
                    ("   • Listen Port (FFB In): ", "bold"),
                    ("The port C2O listens on for incoming OSC FFB commands.\n\n", "bullet"),
                    ("   • Base OSC Address: ", "bold"),
                    ("The default OSC endpoint you want to broadcast to (unless overridden by a custom address in the input settings).\n\n", "bullet"),
                    ("3. Start Streaming: ", "bold"),
                    ("Click the Start Streaming button.\n\n", ""),

                    ("Input Profiles\n", "h2"),
                    ("You can save different device layouts and parameters using the Input Profile manager at the top of the settings page. Switch, create, or delete layout maps instantly.\n\n", ""),

                    ("Input Settings\n", "h2"),
                    ("• Deadzone: ", "bold"),
                    ("Allows you to set a center threshold that ignores slight resting inputs (like stick drift). The output will scale smoothly past the deadzone.\n\n", ""),
                    ("• Sensitivity: ", "bold"),
                    ("Acts as a multiplier. If set to 2.0, moving an axis halfway will output the maximum 1.0 value.\n\n", ""),
                    ("• Custom Addresses (Addr): ", "bold"),
                    ("You can override the base OSC address on a per-axis, per-button, or per-key basis. If left blank, it defaults to the Base OSC Address.\n\n", ""),
                    ("Utilize the input preview to determine, visualize, and feel how your inputs are being translated into software in real time!\n\n", ""),

                    ("OSC Payload Format (Output)\n", "h2"),
                    ("• Axes: ", "bold"),
                    ("[Address] \"axis\" [Axis Index] [Float Value]\n", "code"),
                    ("• Buttons: ", "bold"),
                    ("[Address] \"button\" [Button Index] [Int Value]\n", "code"),
                    ("• Keyboard: ", "bold"),
                    ("[Address] \"keyboard\" [Mapped ID] [Int Value]\n", "code"),
                    ("\n", ""),

                    ("OSC Payload Format (Remote FFB Input)\n", "h2"),
                    ("• /ffb/spring [Float 0-100]: ", "bold"),
                    ("Adjust the centering spring resistance dynamically.\n", "code"),
                    ("• /ffb/damper [Float 0-100]: ", "bold"),
                    ("Adjust the wheel weight (damper) dynamically.\n", "code"),
                    ("• /ffb/friction [Float 0-100]: ", "bold"),
                    ("Adjust the static friction dynamically.\n", "code"),
                    ("\n\n", ""),

                    ("Version 2.4.3 QOL Update\n", "h2"),
                    ("• Adding better multi-device support. The input settings dynamically changes based on the controllers / devices you add\n", "bullet"),

                     ("Version 2.4.2 QOL Update\n", "h2"),
                     ("• Reverted back to retro GUI for optimal performance \n", "bullet"),

                    ("Version 2.4.1 QOL Update\n", "h2"),
                    ("• Upgraded Performance to the input polling by utilizing multi-threading : Moved poll_inputs() into its own dedicated thread. \n", "bullet"),
                    ("• Added an input previewer for buttons, keyboard presses, axis, and D-pads when the user presses the button, the corresponding UI should glow\n", "bullet"),
                    ("• Added a dark / light mode switch in the settings tab.\n", "bullet"),
                    ("• Updated axes UI, added ability to change curve and individual axis smoothing \n", "bullet"),
                    ("• Added ability to capture multiple devices at a time, it acts a little strangely but the idea is you'll have multiple *different* devices, not multiple controllers. Although, you can capture multiple controllers at a time. \n", "bullet"),


                    ("Version 2.4.0 Update Log\n", "h2"),
                    ("• Added customizable OSC address routing per axis, button, and hat.\n", "bullet"),
                    ("• Added global Keyboard support (via 'keyboard' library) to map specific keypresses directly to OSC outputs.\n", "bullet"),
                    
                    ("Version 2.3.0 Update Log\n", "h2"),
                    ("• Allowed dynamic renaming of buttons and axes directly from the GUI, saved per profile.\n", "bullet"),

                    ("Version 2.2.1 Update Log\n", "h2"),
                    ("• Added true button names for the devices buttons rather than the generic BTN it was prior.\n", "bullet"),

                    ("Version 2.2.0 Update Log\n", "h2"),
                    ("• Added Ability to remap axis binding\n", "bullet"),
                    ("• Added ability to remap D-pad bindings for steering wheels (not needed for normal controllers)\n", "bullet"),

                    ("Version 2.1.0 Update Log\n", "h2"),
                    ("• Added Profiles: Save unique mappings for different wheels or gamepads.\n", "bullet"),
                    ("• Added Remote FFB: Made C2O two-way, making ML capable of controlling wheel resistance via OSC inputs.\n", "bullet"),

                    ("Version 2.0.0 Update Log\n", "h2"),
                    ("Core Architecture Updates\n", "bold"),
                    ("• PySDL2 Backend: ", "bold"),
                    ("Swapped from Pygame to PySDL2 for direct, low-level access to hardware haptic drivers (universal FFB support).\n", "bullet"),
                    ("• Enhanced Device Discovery: ", "bold"),
                    ("Improved refresh logic to reliably detect XInput controllers without dropping them.\n\n", "bullet"),

                    ("Force Feedback (FFB) Integration\n", "bold"),
                    ("• Hardware FFB Controls: ", "bold"),
                    ("Added real-time haptic controls for steering wheels.\n", "bullet"),
                    ("• Tri-Effect System: ", "bold"),
                    ("New sliders for Centering Spring (Stiffness), Damper (Weight), and Static Friction.\n", "bullet"),
                    ("• Hardware Override: ", "bold"),
                    ("Explicitly disables default firmware auto-centering so C2O has full control over wheel resistance.\n", "bullet"),
                    ("• Dynamic UI: ", "bold"),
                    ("FFB settings are automatically hidden when standard gamepads are connected.\n\n", "bullet"),

                    ("UI & Input Visualization\n", "bold"),
                    ("• Auto-Detecting Visualizers: ", "bold"),
                    ("Previews dynamically change based on device type (steering wheel w/ degree readout vs. standard X/Y grids).\n", "bullet"),
                    ("• Gamepad Triggers: ", "bold"),
                    ("Isolates standard gamepad triggers (Axes 4 & 5) into vertical fill-bars instead of paired 2D grids.\n", "bullet"),

                    ("Version 1.5.0 Update Log\n", "h2"),
                    ("• Integrated overall cleaner GUI elements.\n", "bullet"),
                    ("• Previews dynamically change based on device input, circle grid pattern (steering wheel w/ degree readout vs. standard X/Y grids).\n", "bullet"),
                    ("• Added Bluetooth Gamepad Detection\n", "bullet"),
                    ("• Added ability to switch between controllers\n", "bullet"),
                    ("• Added refresh device button\n", "bullet"),
                    ("• Using Pygame to gather inputs from controllers, will need to change this later for more detailed wheel control\n", "bullet"),

                    ("Version 1.0 Log\n", "h2"),
                    ("• Terminal command, uses Pygame to autodetect the first controller it finds converts and outputs to script defined address & port.\n\n", "bullet"),
                    
                    ("Helpful Resources\n", "h2"),
                    ("• https://pysdl2.readthedocs.io/en/0.9.13/\n", "bullet"),
                    ("• https://pillow.readthedocs.io/en/stable/\n", "bullet"),
                    ("• https://docs.python.org/3/library/tkinter.html\n", "bullet"),
                ]

        for text_chunk, tag_name in content:
            if tag_name:
                help_text.insert(tk.END, text_chunk, tag_name)
            else:
                help_text.insert(tk.END, text_chunk)

        help_text.config(state="disabled")

    def new_profile(self):
        name = simpledialog.askstring("New Profile", "Enter new profile name:")
        if name:
            if name not in self.profiles:
                self.save_current_profile_to_dict() 
                self.profiles[name] = {}
                self.current_profile_name.set(name)
                self.update_profile_combo()
                self.apply_profile(name)
                self.save_config()

    def delete_profile(self):
        name = self.current_profile_name.get()
        if name == "Default":
            messagebox.showwarning("Warning", "Cannot delete the Default profile.")
            return
        if messagebox.askyesno("Confirm Delete", f"Are you sure you want to delete profile '{name}'?"):
            del self.profiles[name]
            self.current_profile_name.set("Default")
            self.update_profile_combo()
            self.apply_profile("Default")
            self.save_config()

    def on_profile_selected(self, value=None):
        self.apply_profile(self.current_profile_name.get())

    def update_profile_combo(self):
        menu = self.profile_combo["menu"]
        menu.delete(0, "end")
        for p in self.profiles.keys():
            menu.add_command(label=p, command=tk._setit(self.current_profile_name, p, self.on_profile_selected))
        
    def save_current_profile_to_dict(self):
        config_data = {
            "ip": self.ip_entry.get(),
            "port": self.port_entry.get(),
            "listen_port": self.listen_port_entry.get(),
            "osc_address": self.addr_entry.get(),
            "ffb_spring": self.ffb_spring_var.get(),
            "ffb_damper": self.ffb_damper_var.get(),
            "ffb_friction": self.ffb_friction_var.get(),
            "axes": {},
            "buttons": {},
            "button_names": {}, 
            "button_addrs": {},
            "hats": {},
            "hat_addrs": {},
            "keyboard": {}
        }
        
        for idx, config in self.axis_config.items():
            config_data["axes"][str(idx)] = {
                "custom_name": config.get('name_var', tk.StringVar(value=f"Axis {idx}")).get(),
                "osc_id": config['id_var'].get(),
                "custom_addr": config['addr_var'].get(),
                "inverted": config['inv_var'].get(),
                "sensitivity": config['sens_var'].get(),
                "deadzone": config['dead_var'].get(),
                "curve": config['curve_var'].get(),
                "smooth": config['smooth_var'].get()
            }
            
        for idx, var in self.button_vars.items():
            config_data["buttons"][str(idx)] = var.get()
            
        for idx, var in self.button_name_vars.items():
            config_data["button_names"][str(idx)] = var.get()
            
        for idx, var in self.button_addr_vars.items():
            config_data["button_addrs"][str(idx)] = var.get()

        for idx, var in self.hat_vars.items():
            config_data["hats"][str(idx)] = var.get()

        for idx, var in self.hat_addr_vars.items():
            config_data["hat_addrs"][str(idx)] = var.get()
            
        for idx, k_vars in self.keyboard_vars.items():
            config_data["keyboard"][str(idx)] = {
                'key': k_vars['key'].get(),
                'addr': k_vars['addr'].get(),
                'id': k_vars['id'].get()
            }

        self.profiles[self.current_profile_name.get()] = config_data

    def apply_profile(self, name):
        config_data = self.profiles.get(name, {})

        if "ip" in config_data:
            self.ip_entry.delete(0, tk.END); self.ip_entry.insert(0, config_data["ip"])
        if "port" in config_data:
            self.port_entry.delete(0, tk.END); self.port_entry.insert(0, config_data["port"])
        if "listen_port" in config_data:
            self.listen_port_entry.delete(0, tk.END); self.listen_port_entry.insert(0, config_data["listen_port"])
        if "osc_address" in config_data:
            self.addr_entry.delete(0, tk.END); self.addr_entry.insert(0, config_data["osc_address"])

        if "ffb_spring" in config_data: self.ffb_spring_var.set(config_data["ffb_spring"])
        elif "ffb_stiffness" in config_data: self.ffb_spring_var.set(config_data["ffb_stiffness"])

        if "ffb_damper" in config_data: self.ffb_damper_var.set(config_data["ffb_damper"])
        if "ffb_friction" in config_data: self.ffb_friction_var.set(config_data["ffb_friction"])

        self.update_ffb()

        if "axes" in config_data:
            for idx_str, data in config_data["axes"].items():
                idx = int(idx_str)
                if idx not in self.axis_config:
                    name_var = tk.StringVar(value=f"Axis {idx}")
                    name_var.trace_add("write", self._update_preview_labels)
                    self.axis_config[idx] = {
                        'name_var': tk.StringVar(value=f"Axis {idx}"),
                        'id_var': tk.StringVar(value=str(idx)),
                        'addr_var': tk.StringVar(value=""),
                        'inv_var': tk.BooleanVar(value=False),
                        'sens_var': tk.DoubleVar(value=1.0),
                        'dead_var': tk.DoubleVar(value=0.0),
                        'curve_var': tk.DoubleVar(value=1.0),
                        'smooth_var': tk.DoubleVar(value=0.0)
                    }
                self.axis_config[idx]['name_var'].set(data.get("custom_name", f"Axis {idx}"))
                self.axis_config[idx]['id_var'].set(data.get("osc_id", str(idx)))
                self.axis_config[idx]['addr_var'].set(data.get("custom_addr", ""))
                self.axis_config[idx]['inv_var'].set(data.get("inverted", False))
                self.axis_config[idx]['sens_var'].set(data.get("sensitivity", 1.0))
                self.axis_config[idx]['dead_var'].set(data.get("deadzone", 0.0))
                self.axis_config[idx]['curve_var'].set(data.get("curve", 1.0))
                self.axis_config[idx]['smooth_var'].set(data.get("smooth", 0.0))

        if "buttons" in config_data:
            for idx_str, value in config_data["buttons"].items():
                idx = int(idx_str)
                if idx in self.button_vars:
                    self.button_vars[idx].set(value)

        if "button_names" in config_data:
            for idx_str, value in config_data["button_names"].items():
                idx = int(idx_str)
                if idx in self.button_name_vars:
                    self.button_name_vars[idx].set(value)
                    
        if "button_addrs" in config_data:
            for idx_str, value in config_data["button_addrs"].items():
                idx = int(idx_str)
                if idx in self.button_addr_vars:
                    self.button_addr_vars[idx].set(value)

        if "hats" in config_data:
            for idx_str, value in config_data["hats"].items():
                idx = int(idx_str)
                if idx in self.hat_vars:
                    self.hat_vars[idx].set(value)

        if "hat_addrs" in config_data:
            for idx_str, value in config_data["hat_addrs"].items():
                idx = int(idx_str)
                if idx in self.hat_addr_vars:
                    self.hat_addr_vars[idx].set(value)
                    
        if "keyboard" in config_data:
            for idx_str, value in config_data["keyboard"].items():
                idx = int(idx_str)
                if idx in self.keyboard_vars:
                    self.keyboard_vars[idx]['key'].set(value.get('key', ''))
                    self.keyboard_vars[idx]['addr'].set(value.get('addr', ''))
                    self.keyboard_vars[idx]['id'].set(value.get('id', ''))

    def _update_button_labels(self):
        profile_data = self.profiles.get(self.current_profile_name.get(), {})
        custom_btn_names = profile_data.get("button_names", {})

        self.current_button_map.clear()
        
        total_buttons = max(24, sum(d['num_buttons'] for d in self.active_joysticks))
        
        if not self.active_joysticks:
            for i in range(total_buttons):
                if str(i) in custom_btn_names and custom_btn_names[str(i)].strip():
                    btn_name = custom_btn_names[str(i)]
                else:
                    btn_name = f"Btn {i}"
                self.current_button_map[i] = btn_name
                if i in self.button_name_vars:
                    self.button_name_vars[i].set(btn_name)
            return
            
        for d in self.active_joysticks:
            name_lower = d['name']
            is_ps = any(x in name_lower for x in ["playstation", "dualshock", "dualsense", "ps4", "ps5"])
            is_nintendo = any(x in name_lower for x in ["nintendo", "switch", "pro controller", "joy-con"])
            is_xbox = any(x in name_lower for x in ["xbox", "xinput"])
            is_g29 = any(x in name_lower for x in ["g29", "g920", "g923"])
            
            for i in range(d['num_buttons']):
                global_i = d['btn_off'] + i
                
                if str(global_i) in custom_btn_names and custom_btn_names[str(global_i)].strip():
                    btn_name = custom_btn_names[str(global_i)]
                else:
                    if is_ps:
                        map_dict = {0: "Cross / A", 1: "Circle / B", 2: "Square / X", 3: "Triangle / Y", 4: "Share", 5: "Playstation Button", 6: "Options", 7: "L Thumbstick Button", 8: "R Thumbstick Button", 9: "L1", 10: "R1", 11: "D-pad UP", 12: "D-pad DOWN", 13:"D-pad LEFT", 14: "D-pad RIGHT", 15: "Pad"}
                        btn_name = map_dict.get(i, f"Btn {i}")
                    elif is_nintendo:
                        map_dict = {0: "B", 1: "A", 2: "Y", 3: "X", 4: "L", 5: "R", 6: "ZL", 7: "ZR", 8: "Minus", 9: "Plus", 10: "L3", 11: "R3", 12: "Home", 13: "Capture"}
                        btn_name = map_dict.get(i, f"Btn {i}")
                    elif is_g29:
                        map_dict = {0: "Cross", 1: "Square", 2: "Circle", 3: "Triangle", 4: "R-Paddle", 5: "L-Paddle", 6: "Options", 7: "Share", 8: "RSB", 9: "LSB", 10: "Center Logo Button", 11: "L3", 12: "Gear 1", 13: "Gear 2", 14: "Gear 3", 15: "Gear 4", 16: "Gear 5", 17: "Gear 6", 18: "Gear R", 19: "Plus", 20: "Minus", 21: "Dial R", 22: "Dial L", 23: "Enter"}
                        btn_name = map_dict.get(i, f"Btn {i}")
                    elif is_xbox or "controller" in name_lower or "gamepad" in name_lower:
                        map_dict = {0: "A", 1: "B", 2: "X", 3: "Y", 4: "LB", 5: "RB", 6: "Back", 7: "Start", 8: "LS", 9: "RS", 10: "Guide"}
                        btn_name = map_dict.get(i, f"Btn {i}")
                    else:
                        btn_name = f"Btn {i}"
                    
                self.current_button_map[global_i] = btn_name
                
                if global_i in self.button_name_vars:
                    self.button_name_vars[global_i].set(btn_name)

    def refresh_devices(self):
        self._close_devices()
        
        sdl2.SDL_QuitSubSystem(sdl2.SDL_INIT_JOYSTICK | sdl2.SDL_INIT_HAPTIC)
        time.sleep(0.1)
        sdl2.SDL_InitSubSystem(sdl2.SDL_INIT_JOYSTICK | sdl2.SDL_INIT_HAPTIC)
        sdl2.SDL_JoystickUpdate()
        
        joystick_count = sdl2.SDL_NumJoysticks()
        self.devices_map = {} 
        
        if joystick_count == 0:
            menu = self.device_dropdown["menu"]
            menu.delete(0, "end")
            self.device_var.set("No devices found")
            menu.add_command(label="No devices found", command=tk._setit(self.device_var, "No devices found"))
            
            self._populate_axes_frame()
            self._populate_buttons_frame()
            self._populate_hats_frame()
            self._populate_preview_frame()
            self._update_button_labels() 
            self.ffb_frame.pack_forget() 
        else:
            dropdown_values = []
            for i in range(joystick_count):
                name = sdl2.SDL_JoystickNameForIndex(i).decode('utf-8', errors='ignore')
                display_name = f"[{i}] {name}"
                
                self.devices_map[display_name] = i
                dropdown_values.append(display_name)
                
            menu = self.device_dropdown["menu"]
            menu.delete(0, "end")
            for val in dropdown_values:
                menu.add_command(label=val, command=tk._setit(self.device_var, val))
            self.device_var.set(dropdown_values[0])

    def _close_devices(self):
        for d in self.active_joysticks:
            if d['haptic']:
                sdl2.SDL_HapticClose(d['haptic'])
            if d['joy']:
                sdl2.SDL_JoystickClose(d['joy'])
                
        self.active_joysticks.clear()
        self.joystick = None
        self.haptic = None
        self.spring_id = None
        self.damper_id = None
        self.friction_id = None
        self.is_wheel = False

    def clear_devices(self):
        self._close_devices()
        self._populate_axes_frame()
        self._populate_buttons_frame()
        self._populate_hats_frame()
        self._populate_preview_frame()
        self._update_button_labels()
        self.ffb_frame.pack_forget()

    def add_device(self):
        selected_device_str = self.device_var.get()
        if selected_device_str == "No devices found" or not selected_device_str:
            return
            
        device_index = self.devices_map.get(selected_device_str)
        if device_index is not None:
            if any(d['index'] == device_index for d in self.active_joysticks):
                return

            joy = sdl2.SDL_JoystickOpen(device_index)
            if not joy: return
            
            num_axes = sdl2.SDL_JoystickNumAxes(joy)
            num_buttons = sdl2.SDL_JoystickNumButtons(joy)
            num_hats = sdl2.SDL_JoystickNumHats(joy)
            
            joy_type = sdl2.SDL_JoystickGetType(joy)
            name = sdl2.SDL_JoystickName(joy).decode('utf-8', errors='ignore').lower()
            
            wheel_keywords = ['wheel', 'racing', 'g29', 'g920', 'thrustmaster', 'fanatec', 'moza']
            is_string_wheel = any(kw in name for kw in wheel_keywords)
            
            if (joy_type == sdl2.SDL_JOYSTICK_TYPE_WHEEL) or is_string_wheel:
                self.is_wheel = True

            haptic = sdl2.SDL_HapticOpenFromJoystick(joy)
            spring_id = damper_id = friction_id = None
            spring_effect = damper_effect = friction_effect = None
            has_ffb = False
            
            if haptic:
                sdl2.SDL_HapticSetAutocenter(haptic, 0) 
                sdl2.SDL_HapticSetGain(haptic, 100)      

                features = sdl2.SDL_HapticQuery(haptic)
                
                if features & sdl2.SDL_HAPTIC_SPRING:
                    spring_effect = sdl2.SDL_HapticEffect()
                    spring_effect.type = sdl2.SDL_HAPTIC_SPRING
                    spring_effect.condition.length = sdl2.SDL_HAPTIC_INFINITY
                    spring_id = sdl2.SDL_HapticNewEffect(haptic, spring_effect)
                    sdl2.SDL_HapticRunEffect(haptic, spring_id, sdl2.SDL_HAPTIC_INFINITY)
                    has_ffb = True
                    
                if features & sdl2.SDL_HAPTIC_DAMPER:
                    damper_effect = sdl2.SDL_HapticEffect()
                    damper_effect.type = sdl2.SDL_HAPTIC_DAMPER
                    damper_effect.condition.length = sdl2.SDL_HAPTIC_INFINITY
                    damper_id = sdl2.SDL_HapticNewEffect(haptic, damper_effect)
                    sdl2.SDL_HapticRunEffect(haptic, damper_id, sdl2.SDL_HAPTIC_INFINITY)
                    has_ffb = True
                    
                if features & sdl2.SDL_HAPTIC_FRICTION:
                    friction_effect = sdl2.SDL_HapticEffect()
                    friction_effect.type = sdl2.SDL_HAPTIC_FRICTION
                    friction_effect.condition.length = sdl2.SDL_HAPTIC_INFINITY
                    friction_id = sdl2.SDL_HapticNewEffect(haptic, friction_effect)
                    sdl2.SDL_HapticRunEffect(haptic, friction_id, sdl2.SDL_HAPTIC_INFINITY)
                    has_ffb = True
                
            ax_off = sum(d['num_axes'] for d in self.active_joysticks)
            btn_off = sum(d['num_buttons'] for d in self.active_joysticks)
            hat_off = sum(d['num_hats'] for d in self.active_joysticks)

            self.active_joysticks.append({
                'index': device_index,
                'joy': joy,
                'haptic': haptic,
                'name': name,
                'num_axes': num_axes,
                'num_buttons': num_buttons,
                'num_hats': num_hats,
                'ax_off': ax_off,
                'btn_off': btn_off,
                'hat_off': hat_off,
                'spring_id': spring_id,
                'spring_effect': spring_effect,
                'damper_id': damper_id,
                'damper_effect': damper_effect,
                'friction_id': friction_id,
                'friction_effect': friction_effect,
                'has_ffb': has_ffb
            })

            self.joystick = self.active_joysticks[0]['joy']

            self.update_ffb()
            if any(d['has_ffb'] for d in self.active_joysticks):
                self.ffb_frame.pack(fill="x", padx=10, pady=5, before=self.preview_frame)
            else:
                self.ffb_frame.pack_forget()
            
            self._populate_axes_frame()
            self._populate_buttons_frame()
            self._populate_hats_frame()
            
            self._populate_preview_frame()
            self._update_button_labels()

    def _apply_condition_effect(self, haptic, effect_id, effect_struct, strength_var):
        if haptic and effect_id is not None and effect_struct:
            strength_pct = strength_var.get() / 100.0
            coeff = int(strength_pct * 32767)
            
            effect_struct.condition.right_sat[0] = 65535
            effect_struct.condition.left_sat[0] = 65535
            effect_struct.condition.right_coeff[0] = coeff
            effect_struct.condition.left_coeff[0] = coeff
            effect_struct.condition.deadband[0] = 0
            effect_struct.condition.center[0] = 0
            
            sdl2.SDL_HapticUpdateEffect(haptic, effect_id, effect_struct)

    def update_ffb(self, *args):
        for d in self.active_joysticks:
            self._apply_condition_effect(d['haptic'], d['spring_id'], d['spring_effect'], self.ffb_spring_var)
            self._apply_condition_effect(d['haptic'], d['damper_id'], d['damper_effect'], self.ffb_damper_var)
            self._apply_condition_effect(d['haptic'], d['friction_id'], d['friction_effect'], self.ffb_friction_var)

    def save_config(self):
        self.save_current_profile_to_dict()
        
        full_data = {
            "active_profile": self.current_profile_name.get(),
            "profiles": self.profiles
        }
            
        try:
            with open(self.config_file, 'w') as f:
                json.dump(full_data, f, indent=4)
            self.save_btn.config(text="Saved!")
            self.root.after(1500, lambda: self.save_btn.config(text="Save Settings"))
        except Exception as e:
            print(f"Error saving config: {e}")

    def load_config(self):
        if not os.path.exists(self.config_file):
            self.profiles = {"Default": {}}
            self.update_profile_combo()
            return
            
        try:
            with open(self.config_file, 'r') as f:
                data = json.load(f)
                
            if "profiles" in data:
                self.profiles = data["profiles"]
                active = data.get("active_profile", "Default")
            else:
                self.profiles = {"Default": data}
                active = "Default"
                
            if active not in self.profiles:
                active = list(self.profiles.keys())[0] if self.profiles else "Default"
                if not self.profiles:
                    self.profiles = {"Default": {}}

            self.current_profile_name.set(active)
            self.update_profile_combo()
            self.apply_profile(active)
                        
        except Exception as e:
            print(f"Error loading config or corrupted file: {e}")
            self.profiles = {"Default": {}}
            self.update_profile_combo()

    def reset_mappings(self):
        if messagebox.askyesno("Confirm Reset", "Are you sure you want to reset all mappings and sensitivities to their defaults?"):
            for axis_idx, config in self.axis_config.items():
                config['name_var'].set(f"Axis {axis_idx}")
                config['id_var'].set(str(axis_idx))
                config['addr_var'].set("")
                config['inv_var'].set(False)
                config['sens_var'].set(1.0)
                config['dead_var'].set(0.0)
                config['curve_var'].set(1.0)
                config['smooth_var'].set(0.0)
            
            for i, var in self.button_vars.items():
                var.set(str(i))
            for i, var in self.button_addr_vars.items():
                var.set("")

            for i, var in self.hat_vars.items():
                var.set(str(i))
            for i, var in self.hat_addr_vars.items():
                var.set("")
                
            for i, k_vars in self.keyboard_vars.items():
                k_vars['key'].set("")
                k_vars['addr'].set("")
                k_vars['id'].set("")
                
            current_profile = self.current_profile_name.get()
            if current_profile in self.profiles:
                if "button_names" in self.profiles[current_profile]:
                    self.profiles[current_profile]["button_names"] = {}
                if "axes" in self.profiles[current_profile]:
                    for _, ax_data in self.profiles[current_profile]["axes"].items():
                        if "custom_name" in ax_data:
                            del ax_data["custom_name"]
            
            self._update_button_labels()

    def get_axis_value(self, index, raw_value):
        if index in self.axis_config:
            config = self.axis_config[index]
            is_inverted = config['inv_var'].get()
            sensitivity = config['sens_var'].get()
            deadzone = config['dead_var'].get()
            curve = config['curve_var'].get()
            smooth = config['smooth_var'].get()
            
            if abs(raw_value) < deadzone:
                val = 0.0
            else:
                sign = 1 if raw_value > 0 else -1
                if deadzone >= 1.0:
                    val = 0.0
                else:
                    val = sign * ((abs(raw_value) - deadzone) / (1.0 - deadzone))

            val_sign = 1 if val >= 0 else -1
            val = val_sign * (abs(val) ** curve)

            val = val * sensitivity
            
            if is_inverted:
                val = -val
            
            val = max(-1.0, min(1.0, val))

            if index not in self.axis_ema:
                self.axis_ema[index] = val
            else:
                alpha = 1.0 - smooth 
                self.axis_ema[index] = (alpha * val) + ((1.0 - alpha) * self.axis_ema[index])

            return round(self.axis_ema[index], 3)
        return raw_value
        
    def get_axis_id(self, raw_index):
        if raw_index in self.axis_config:
            try:
                return int(self.axis_config[raw_index]['id_var'].get())
            except ValueError:
                pass
        return raw_index

    def get_button_id(self, raw_index):
        if raw_index in self.button_vars:
            try:
                return int(self.button_vars[raw_index].get())
            except ValueError:
                pass 
        return raw_index
    
    def get_hat_id(self, raw_index):
        if raw_index in self.hat_vars:
            try:
                return int(self.hat_vars[raw_index].get())
            except ValueError:
                pass 
        return raw_index

    def on_mode_change(self):
        self.clear_log()
        if self.is_running and self.output_mode.get() == "inplace":
            self.redraw_in_place()

    def log(self, message):
        self.log_area.config(state='normal')
        self.log_area.insert(tk.END, message + "\n")
        self.log_area.see(tk.END)
        self.log_area.config(state='disabled')

    def clear_log(self):
        self.log_area.config(state='normal')
        self.log_area.delete(1.0, tk.END)
        self.log_area.config(state='disabled')

    def redraw_in_place(self):
        if not self.is_running:
            return
            
        self.log_area.config(state='normal')
        self.log_area.delete(1.0, tk.END)
        
        name = self.active_joysticks[0]['name'].title() if self.active_joysticks else "Keyboard Only"
        if len(self.active_joysticks) > 1:
            name = f"{name} (+{len(self.active_joysticks)-1} more)"
            
        header = f"--- STREAMING ACTIVE ---\n"
        header += f"Device: {name}\n"
        header += f"Target Output: {self.ip_entry.get()}:{self.port_entry.get()} | Base Addr: {self.osc_address}\n"
        header += f"OSC Input Port (FFB): {self.listen_port_entry.get()}\n"
        header += f"-------------------------\n"
        self.log_area.insert(tk.END, header)
        
        for i in sorted(self.prev_axes.keys()):
            val = self.get_axis_value(i, self.prev_axes[i])
            mapped_i = self.get_axis_id(i)
            c_addr = self.axis_config[i]['addr_var'].get().strip() or self.osc_address
            axis_name = self.axis_config[i]['name_var'].get() if i in self.axis_config else f"Axis {i}"
            self.log_area.insert(tk.END, f"{axis_name} (OSC ID {mapped_i}) [{c_addr}]:   {val:.3f}\n")
            
        for i in sorted(self.prev_buttons.keys()):
            mapped_i = self.get_button_id(i)
            c_addr = self.button_addr_vars[i].get().strip() or self.osc_address
            btn_name = self.button_name_vars[i].get() if i in self.button_name_vars else f"Btn {i}"
            self.log_area.insert(tk.END, f"{btn_name} (OSC ID {mapped_i}) [{c_addr}]: {self.prev_buttons[i]}\n")
            
        for i in sorted(self.prev_hats.keys()):
            mapped_i = self.get_hat_id(i)
            c_addr = self.hat_addr_vars[i].get().strip() or self.osc_address
            self.log_area.insert(tk.END, f"Hat {i} (OSC ID {mapped_i}) [{c_addr}]: {self.prev_hats[i]}\n")
            
        for i in sorted(self.prev_keys.keys()):
            k_vars = self.keyboard_vars.get(i)
            if k_vars and k_vars['key'].get().strip():
                c_addr = k_vars['addr'].get().strip() or self.osc_address
                m_id = k_vars['id'].get().strip() or str(i)
                self.log_area.insert(tk.END, f"Key '{k_vars['key'].get()}' (OSC ID {m_id}) [{c_addr}]: {self.prev_keys[i]}\n")

        self.log_area.config(state='disabled')

    def toggle_stream(self):
        if not self.is_running:
            self.start_streaming()
        else:
            self.stop_streaming()

    def start_streaming(self):
        ip = self.ip_entry.get().strip()
        osc_address = self.addr_entry.get().strip()
        try:
            port = int(self.port_entry.get().strip())
        except ValueError:
            messagebox.showerror("Invalid Input", "Port must be an integer.")
            return

        has_devices = len(self.active_joysticks) > 0
        has_keys = any(v['key'].get().strip() for v in self.keyboard_vars.values()) if KEYBOARD_AVAILABLE else False

        if not has_devices and not has_keys:
            messagebox.showerror("Device Error", "No controller added to pool or keyboard mappings found.")
            return

        if self.ui_icon_on:
            self.status_icon_label.config(image=self.ui_icon_on)

        self.client = udp_client.SimpleUDPClient(ip, port)
        self.osc_address = osc_address
        
        try:
            listen_port = int(self.listen_port_entry.get().strip())
            disp = dispatcher.Dispatcher()
            disp.map("/ffb/spring", self._osc_ffb_spring)
            disp.map("/ffb/damper", self._osc_ffb_damper)
            disp.map("/ffb/friction", self._osc_ffb_friction)

            self.server = osc_server.ThreadingOSCUDPServer(("0.0.0.0", listen_port), disp)
            self.osc_server_thread = threading.Thread(target=self.server.serve_forever)
            self.osc_server_thread.daemon = True
            self.osc_server_thread.start()
        except Exception as e:
            messagebox.showerror("OSC Server Error", f"Could not start OSC server on port {self.listen_port_entry.get()}:\n{e}")
            self.stop_streaming()
            return

        self.is_running = True
        self.start_btn.config(text="Stop Streaming", bg="#dc3545", activebackground="#c82333")
        
        self.setting_widgets = [w for w in self.setting_widgets if w.winfo_exists()]
        for widget in self.setting_widgets:
            widget.config(state="disabled")
        
        if self.icon_on_tk:
            self.root.iconphoto(True, self.icon_on_tk) 
        if self.tray_icon and self.img_on:
            self.tray_icon.icon = self.img_on
        
        name = self.active_joysticks[0]['name'].title() if self.active_joysticks else "Keyboard Only"
        if len(self.active_joysticks) > 1:
            name = f"{name} (+{len(self.active_joysticks)-1} more)"
            
        if self.output_mode.get() == "scroll":
            self.log(f"--- STARTED STREAMING ---")
            self.log(f"Device(s): {name}")
            self.log(f"Sending to: {ip}:{port} | Base Address: {osc_address}")
            self.log(f"Listening for FFB on port: {listen_port}")
            self.log(f"-------------------------")
        else:
            self.clear_log()

        self.prev_axes.clear()
        self.prev_buttons.clear()
        self.prev_hats.clear()
        self.prev_keys.clear()
        self.axis_ema.clear()

        self._main_polling_loop()
        self._ui_log_loop()

    def stop_streaming(self):
        self.is_running = False

        if self.update_job:
            self.root.after_cancel(self.update_job)
            self.update_job = None
            
        if self.server:
            try:
                self.server.shutdown()
                self.server.server_close()
            except:
                pass
            self.server = None
            
        self.start_btn.config(text="Start Streaming")
        self.start_btn.config(text="Start Streaming", bg="#28a745", activebackground="#218838")
        
        if self.ui_icon_off:
            self.status_icon_label.config(image=self.ui_icon_off)
        
        self.setting_widgets = [w for w in self.setting_widgets if w.winfo_exists()]
        for widget in self.setting_widgets:
            widget.config(state="normal")
        
        if self.icon_off_tk:
            self.root.iconphoto(True, self.icon_off_tk)
        if self.tray_icon and self.img_off:
            self.tray_icon.icon = self.img_off
            
        if self.output_mode.get() == "scroll":
            self.log("--- STOPPED STREAMING ---")
        else:
            self.log_area.config(state='normal')
            self.log_area.delete(1.0, tk.END)
            self.log_area.insert(tk.END, "--- STOPPED STREAMING ---\n")
            self.log_area.config(state='disabled')

    def _osc_ffb_spring(self, address, *args):
        if args:
            try:
                val = float(args[0])
                val = max(0.0, min(100.0, val)) 
                self.root.after(0, self._set_ffb_spring, val)
            except ValueError:
                pass

    def _set_ffb_spring(self, val):
        self.ffb_spring_var.set(val)
        self.update_ffb()
        if self.output_mode.get() == "scroll":
            self.log(f"OSC IN: /ffb/spring -> {val:.1f}")

    def _osc_ffb_damper(self, address, *args):
        if args:
            try:
                val = float(args[0])
                val = max(0.0, min(100.0, val))
                self.root.after(0, self._set_ffb_damper, val)
            except ValueError:
                pass

    def _set_ffb_damper(self, val):
        self.ffb_damper_var.set(val)
        self.update_ffb()
        if self.output_mode.get() == "scroll":
            self.log(f"OSC IN: /ffb/damper -> {val:.1f}")

    def _osc_ffb_friction(self, address, *args):
        if args:
            try:
                val = float(args[0])
                val = max(0.0, min(100.0, val))
                self.root.after(0, self._set_ffb_friction, val)
            except ValueError:
                pass

    def _set_ffb_friction(self, val):
        self.ffb_friction_var.set(val)
        self.update_ffb()
        if self.output_mode.get() == "scroll":
            self.log(f"OSC IN: /ffb/friction -> {val:.1f}")

    def sdl_hat_to_tuple(self, hat_val):
        x, y = 0, 0
        if hat_val & sdl2.SDL_HAT_UP:
            y = 1
        elif hat_val & sdl2.SDL_HAT_DOWN:
            y = -1
        if hat_val & sdl2.SDL_HAT_RIGHT:
            x = 1
        elif hat_val & sdl2.SDL_HAT_LEFT:
            x = -1
        return (x, y)
    
    def _ui_log_loop(self):
        if self.is_running:
            if self.output_mode.get() == "inplace":
                self.redraw_in_place()
            # 100ms = 10 FPS UI updates
            self.root.after(100, self._ui_log_loop)
    
    def _main_polling_loop(self):
            if self.is_running:
                self.poll_inputs()
                # 10ms = 100Hz polling securely on the main thread
                self.root.after(10, self._main_polling_loop)

    def poll_inputs(self):
        if not self.is_running:
            return

        if self.active_joysticks:
            sdl2.SDL_JoystickUpdate()
            
            for d in self.active_joysticks:
                joy = d['joy']
                ax_off = d['ax_off']
                btn_off = d['btn_off']
                hat_off = d['hat_off']
                
                num_axes = d['num_axes']
                num_buttons = d['num_buttons']
                num_hats = d['num_hats']

                for i in range(num_axes):
                    global_i = ax_off + i
                    raw_axis_val = round(sdl2.SDL_JoystickGetAxis(joy, i) / 32767.0, 3)
                    
                    if self.prev_axes.get(global_i) != raw_axis_val:
                        self.prev_axes[global_i] = raw_axis_val
                        
                        final_val = self.get_axis_value(global_i, raw_axis_val)
                        mapped_axis_id = self.get_axis_id(global_i)
                        addr = self.axis_config[global_i]['addr_var'].get().strip() or self.osc_address
                        msg_args = ["axis", mapped_axis_id, final_val]
                        
                        self.client.send_message(addr, msg_args)
                        if self.output_mode.get() == "scroll":
                            self.log(f"{addr} {msg_args}")

                for i in range(num_buttons):
                    global_i = btn_off + i
                    raw_btn_val = sdl2.SDL_JoystickGetButton(joy, i)
                    
                    if self.prev_buttons.get(global_i) != raw_btn_val:
                        self.prev_buttons[global_i] = raw_btn_val
                        
                        mapped_id = self.get_button_id(global_i)
                        addr = self.button_addr_vars[global_i].get().strip() or self.osc_address
                        msg_args = ["button", mapped_id, raw_btn_val]
                        
                        self.client.send_message(addr, msg_args)
                        if self.output_mode.get() == "scroll":
                            self.log(f"{addr} {msg_args}")

                for i in range(num_hats):
                    global_i = hat_off + i
                    hat_bitmask = sdl2.SDL_JoystickGetHat(joy, i)
                    hat_tuple = self.sdl_hat_to_tuple(hat_bitmask)
                    
                    if self.prev_hats.get(global_i) != hat_tuple:
                        mapped_id = self.get_hat_id(global_i)
                        addr = self.hat_addr_vars[global_i].get().strip() or self.osc_address
                        msg_args = ["hat", mapped_id, hat_tuple[0], hat_tuple[1]]
                        
                        self.client.send_message(addr, msg_args)
                        if self.output_mode.get() == "scroll":
                            self.log(f"{addr} {msg_args}")
                        
                        self.prev_hats[global_i] = hat_tuple

        if KEYBOARD_AVAILABLE:
            for i, k_vars in self.keyboard_vars.items():
                k = k_vars['key'].get().strip()
                if k:
                    try:
                        is_pressed = keyboard.is_pressed(k)
                    except ValueError:
                        is_pressed = False
                        
                    val = 1 if is_pressed else 0
                    if self.prev_keys.get(i) != val:
                        self.prev_keys[i] = val
                        
                        addr = k_vars['addr'].get().strip() or self.osc_address
                        m_id = k_vars['id'].get().strip() or str(i)
                        
                        try:
                            m_id = int(m_id)
                        except ValueError:
                            pass
                            
                        msg_args = ["keyboard", m_id, val]
                        self.client.send_message(addr, msg_args)
                        
                        if self.output_mode.get() == "scroll":
                            self.log(f"{addr} {msg_args} (Key: {k})")

    def on_closing(self, *args):
        self.root.after(0, self._shutdown)

    def _shutdown(self):
        self.save_config()
        self.stop_streaming()
        self._close_devices()
            
        sdl2.SDL_Quit()
        
        if self.tray_icon:
            self.tray_icon.stop() 
        self.root.destroy()


if __name__ == "__main__":
    try:
        myappid = 'local.oscwheel.controller.1' 
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(myappid)
    except Exception:
        pass 
    
    root = tk.Tk()
    app = OscWheelApp(root)
    root.mainloop()