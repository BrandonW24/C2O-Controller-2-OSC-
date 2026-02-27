import time
import tkinter as tk
from tkinter import ttk, scrolledtext, messagebox, simpledialog
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


def resource_path(relative_path):
    try:
        base_path = sys._MEIPASS
    except Exception:
        base_path = os.path.abspath(".")
    return os.path.join(base_path, relative_path)


class OscWheelApp:
    def __init__(self, root):
        self.root = root
        self.root.title("CTRL 2 OSC")
        self.root.geometry("650x780")
        
        # State variables
        self.is_running = False
        self.client = None
        self.joystick = None
        self.haptic = None
        
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
        self.update_job = None
        self.devices_map = {} 
        
        # Configuration Variables
        self.axis_config = {}
        self.button_vars = {}
        self.button_labels = {}           # NEW: Track button labels to rename them dynamically
        self.current_button_map = {i: f"Btn {i}" for i in range(24)} # NEW: Track active names for logging
        self.hat_vars = {}
        self.setting_widgets = [] 
        self.config_file = "config.json"
        
        # Profile System
        self.profiles = {"Default": {}}
        self.current_profile_name = tk.StringVar(value="Default")
        
        # UI State Variables
        self.is_wheel = False
        
        # Preview Canvas Variables (For Wheel/Pedals)
        self.wheel_canvas = None
        self.wheel_spoke_id = None
        self.wheel_text_id = None
        self.pedal_canvases = []
        self.pedal_rect_ids = []

        # 2 way OSC
        self.osc_server_thread = None
        self.server = None
        
        # Preview Canvas Variables (For Standard Gamepads)
        self.preview_canvases = []
        self.preview_dots = []
        self.std_grid_axes = []      # Maps which axes go to which grids
        self.std_trigger_axes = []   # Maps which axes go to triggers
        self.canvas_size = 140
        self.center = self.canvas_size // 2
        self.radius = 60
        
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
        self._preview_loop()
        
        self.root.protocol("WM_DELETE_WINDOW", self.on_closing)

    def _load_icons(self):
            try:
                path_off = resource_path("steering-wheel-car_off.png")
                path_on = resource_path("steering-wheel-car_on.png")
                
                if os.path.exists(path_off) and os.path.exists(path_on):
                    self.img_off = Image.open(path_off)
                    self.img_on = Image.open(path_on)
                    
                    # Resized versions for the UI dashboard (24x24)
                    self.ui_icon_off = ImageTk.PhotoImage(self.img_off.resize((36, 36), Image.Resampling.LANCZOS))
                    self.ui_icon_on = ImageTk.PhotoImage(self.img_on.resize((36, 36), Image.Resampling.LANCZOS))
                    
                    # Standard versions for tray and window icon
                    self.icon_off_tk = ImageTk.PhotoImage(self.img_off)
                    self.icon_on_tk = ImageTk.PhotoImage(self.img_on)
                    self.root.iconphoto(True, self.icon_off_tk)
                else:
                    # Fallback if files are missing
                    self.img_off = Image.new('RGB', (24, 24), color='red')
                    self.img_on = Image.new('RGB', (24, 24), color='green')
                    self.ui_icon_off = ImageTk.PhotoImage(self.img_off)
                    self.ui_icon_on = ImageTk.PhotoImage(self.img_on)
            except Exception:
                # Final safety fallback
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
        self.notebook = ttk.Notebook(self.root)
        self.notebook.pack(fill="both", expand=True)

        self.main_tab = ttk.Frame(self.notebook)
        self.notebook.add(self.main_tab, text="Output Settings")

        self.settings_tab = ttk.Frame(self.notebook)
        self.notebook.add(self.settings_tab, text="Input Settings")

        self.help_tab = ttk.Frame(self.notebook)
        self.notebook.add(self.help_tab, text="About")

        self._build_main_tab()
        self._build_settings_tab()
        self._build_help_tab()

    def _build_main_tab(self):
        settings_frame = tk.LabelFrame(self.main_tab, text="OSC Targeting & Listening", padx=10, pady=10)
        settings_frame.pack(fill="x", padx=10, pady=10)

        tk.Label(settings_frame, text="Target IP:").grid(row=0, column=0, sticky="e", pady=2)
        self.ip_entry = tk.Entry(settings_frame, width=20)
        self.ip_entry.insert(0, "127.0.0.1")
        self.ip_entry.grid(row=0, column=1, pady=2, padx=5, sticky="w")
        self.setting_widgets.append(self.ip_entry)

        tk.Label(settings_frame, text="Target Port (Send):").grid(row=1, column=0, sticky="e", pady=2)
        self.port_entry = tk.Entry(settings_frame, width=10)
        self.port_entry.insert(0, "4041")
        self.port_entry.grid(row=1, column=1, pady=2, padx=5, sticky="w")
        self.setting_widgets.append(self.port_entry)

        # OSC Input Port
        tk.Label(settings_frame, text="Listen Port (FFB In):").grid(row=2, column=0, sticky="e", pady=2)
        self.listen_port_entry = tk.Entry(settings_frame, width=10)
        self.listen_port_entry.insert(0, "4042")
        self.listen_port_entry.grid(row=2, column=1, pady=2, padx=5, sticky="w")
        self.setting_widgets.append(self.listen_port_entry)

        tk.Label(settings_frame, text="OSC Address:").grid(row=3, column=0, sticky="e", pady=2)
        self.addr_entry = tk.Entry(settings_frame, width=25)
        self.addr_entry.insert(0, "/wheel/input")
        self.addr_entry.grid(row=3, column=1, pady=2, padx=5, sticky="w")
        self.setting_widgets.append(self.addr_entry)

        control_frame = tk.Frame(self.main_tab)
        control_frame.pack(fill="x", padx=10, pady=5)

        self.start_btn = tk.Button(control_frame, text="Start Streaming", bg="#4CAF50", fg="white", font=("Arial", 10, "bold"), command=self.toggle_stream)
        self.start_btn.pack(side="left", expand=True, fill="x", padx=(0, 5))

        self.clear_btn = tk.Button(control_frame, text="Clear Log", command=self.clear_log)
        self.clear_btn.pack(side="right", expand=True, fill="x", padx=(5, 0))

        output_options_frame = tk.Frame(self.main_tab)
        output_options_frame.pack(fill="x", padx=10, pady=(5, 0))
        
        tk.Label(output_options_frame, text="Output Style:").pack(side="left")
        
        self.output_mode = tk.StringVar(value="scroll")
        tk.Radiobutton(output_options_frame, text="Scrolling Log", variable=self.output_mode, value="scroll", command=self.on_mode_change).pack(side="left", padx=5)
        tk.Radiobutton(output_options_frame, text="In-Place Dashboard", variable=self.output_mode, value="inplace", command=self.on_mode_change).pack(side="left", padx=5)

        self.status_icon_label = tk.Label(output_options_frame, image=self.ui_icon_off)
        self.status_icon_label.pack(side="right", padx=20)

        self.log_area = scrolledtext.ScrolledText(self.main_tab, height=25, state='disabled', bg="#1e1e1e", fg="#00ff00", font=("Consolas", 9))
        self.log_area.pack(fill="both", expand=True, padx=10, pady=(5, 10))

    def _build_settings_tab(self):
        self.settings_canvas = tk.Canvas(self.settings_tab, highlightthickness=0)
        self.scrollbar = ttk.Scrollbar(self.settings_tab, orient="vertical", command=self.settings_canvas.yview)
        
        self.scrollable_frame = ttk.Frame(self.settings_canvas)

        self.scrollable_frame.bind("<Configure>", lambda e: self.settings_canvas.configure(scrollregion=self.settings_canvas.bbox("all")))
        self.canvas_window_id = self.settings_canvas.create_window((0, 0), window=self.scrollable_frame, anchor="nw")
        self.settings_canvas.configure(yscrollcommand=self.scrollbar.set)
        self.settings_canvas.bind("<Configure>", lambda e: self.settings_canvas.itemconfig(self.canvas_window_id, width=e.width))

        self.settings_canvas.pack(side="left", fill="both", expand=True)
        self.scrollbar.pack(side="right", fill="y")

        # --- PROFILE MANAGEMENT FRAME ---
        profile_frame = tk.LabelFrame(self.scrollable_frame, text="Input Profile", padx=10, pady=10)
        profile_frame.pack(fill="x", padx=10, pady=(10, 5))

        self.profile_combo = ttk.Combobox(profile_frame, textvariable=self.current_profile_name, state="readonly")
        self.profile_combo.pack(side="left", fill="x", expand=True, padx=(0, 5))
        self.profile_combo.bind("<<ComboboxSelected>>", self.on_profile_selected)
        self.setting_widgets.append(self.profile_combo)

        self.new_prof_btn = tk.Button(profile_frame, text="New Profile", command=self.new_profile)
        self.new_prof_btn.pack(side="left", padx=2)
        self.setting_widgets.append(self.new_prof_btn)

        self.del_prof_btn = tk.Button(profile_frame, text="Delete", command=self.delete_profile)
        self.del_prof_btn.pack(side="left", padx=2)
        self.setting_widgets.append(self.del_prof_btn)

        device_frame = tk.LabelFrame(self.scrollable_frame, text="Input Device", padx=10, pady=10)
        device_frame.pack(fill="x", padx=10, pady=(10, 5))

        self.device_var = tk.StringVar()
        self.device_dropdown = ttk.Combobox(device_frame, textvariable=self.device_var, state="readonly")
        self.device_dropdown.pack(side="left", fill="x", expand=True, padx=(0, 5))
        self.device_dropdown.bind("<<ComboboxSelected>>", self.on_device_selected)
        self.setting_widgets.append(self.device_dropdown)

        self.refresh_btn = tk.Button(device_frame, text="Refresh Devices", command=self.refresh_devices)
        self.refresh_btn.pack(side="right")
        self.setting_widgets.append(self.refresh_btn)

        # --- FORCE FEEDBACK FRAME ---
        self.ffb_frame = tk.LabelFrame(self.scrollable_frame, text="Hardware Force Feedback Parameters", padx=10, pady=10)
        
        # Spring
        spring_container = tk.Frame(self.ffb_frame)
        spring_container.pack(fill="x", pady=2)
        tk.Label(spring_container, text="Centering Spring:", width=15, anchor="w").pack(side="left")
        self.ffb_spring_var = tk.DoubleVar(value=50.0) 
        self.ffb_spring_scale = tk.Scale(spring_container, variable=self.ffb_spring_var, from_=0, to=100, orient="horizontal", command=self.update_ffb)
        self.ffb_spring_scale.pack(side="left", fill="x", expand=True, padx=5)
        self.setting_widgets.append(self.ffb_spring_scale)

        # Damper
        damper_container = tk.Frame(self.ffb_frame)
        damper_container.pack(fill="x", pady=2)
        tk.Label(damper_container, text="Damper (Weight):", width=15, anchor="w").pack(side="left")
        self.ffb_damper_var = tk.DoubleVar(value=20.0) 
        self.ffb_damper_scale = tk.Scale(damper_container, variable=self.ffb_damper_var, from_=0, to=100, orient="horizontal", command=self.update_ffb)
        self.ffb_damper_scale.pack(side="left", fill="x", expand=True, padx=5)
        self.setting_widgets.append(self.ffb_damper_scale)

        # Friction
        friction_container = tk.Frame(self.ffb_frame)
        friction_container.pack(fill="x", pady=2)
        tk.Label(friction_container, text="Static Friction:", width=15, anchor="w").pack(side="left")
        self.ffb_friction_var = tk.DoubleVar(value=10.0) 
        self.ffb_friction_scale = tk.Scale(friction_container, variable=self.ffb_friction_var, from_=0, to=100, orient="horizontal", command=self.update_ffb)
        self.ffb_friction_scale.pack(side="left", fill="x", expand=True, padx=5)
        self.setting_widgets.append(self.ffb_friction_scale)

        self.preview_frame = tk.LabelFrame(self.scrollable_frame, text="Input Preview", padx=10, pady=10)
        self.preview_frame.pack(fill="x", padx=10, pady=5)
        tk.Label(self.preview_frame, text="Waiting for device...", fg="gray").pack(pady=5)

        self.axes_frame = tk.LabelFrame(self.scrollable_frame, text="Axis Configuration (Hardware ID -> OSC ID)", padx=10, pady=10)
        self.axes_frame.pack(fill="x", padx=10, pady=5)
        tk.Label(self.axes_frame, text="Please connect and select a controller.", fg="gray").pack(pady=5)

        buttons_frame = tk.LabelFrame(self.scrollable_frame, text="Button Mapping (Hardware ID -> OSC ID)", padx=10, pady=10)
        buttons_frame.pack(fill="both", expand=True, padx=10, pady=(0, 5))

        grid_frame = tk.Frame(buttons_frame)
        grid_frame.pack(expand=True)

        for i in range(24):
            col = (i // 12) * 2
            row = i % 12
            
            # --- UPDATED: Track UI labels in a dict ---
            lbl = tk.Label(grid_frame, text=f"Btn {i} ->")
            lbl.grid(row=row, column=col, sticky="e", pady=2)
            self.button_labels[i] = lbl 
            
            var = tk.StringVar(value=str(i))
            ent = tk.Entry(grid_frame, textvariable=var, width=5)
            ent.grid(row=row, column=col+1, padx=(0, 20), pady=2)
            self.button_vars[i] = var
            self.setting_widgets.append(ent)

        # --- NEW HAT MAPPING FRAME ---
        hats_frame = tk.LabelFrame(self.scrollable_frame, text="D-Pad / Hat Mapping (Hardware ID -> OSC ID)", padx=10, pady=10)
        hats_frame.pack(fill="x", padx=10, pady=(0, 5))
        
        hat_grid = tk.Frame(hats_frame)
        hat_grid.pack(expand=True)
        
        for i in range(4): # Supports up to 4 D-Pads/Hats
            tk.Label(hat_grid, text=f"Hat {i} ->").grid(row=0, column=i*2, sticky="e", pady=2)
            var = tk.StringVar(value=str(i))
            ent = tk.Entry(hat_grid, textvariable=var, width=5)
            ent.grid(row=0, column=i*2+1, padx=(0, 20), pady=2)
            self.hat_vars[i] = var
            self.setting_widgets.append(ent)
        # -----------------------------

        reset_frame = tk.Frame(self.scrollable_frame)
        reset_frame.pack(fill="x", padx=10, pady=20)

        self.reset_btn = tk.Button(reset_frame, text="Reset Mappings", command=self.reset_mappings)
        self.reset_btn.pack(side="right", padx=(5, 0))
        self.setting_widgets.append(self.reset_btn)

        self.save_btn = tk.Button(reset_frame, text="Save Settings", command=self.save_config)
        self.save_btn.pack(side="right")
        self.setting_widgets.append(self.save_btn)

        self._bind_mousewheel(self.scrollable_frame)

    def _on_mousewheel(self, event):
        if self.notebook.select() == self.settings_tab._w:
            scroll_dir = int(-1*(event.delta/120))
            self.settings_canvas.yview_scroll(scroll_dir, "units")

    def _bind_mousewheel(self, widget):
        widget.bind("<MouseWheel>", self._on_mousewheel)
        for child in widget.winfo_children():
            self._bind_mousewheel(child)

    def _populate_preview_frame(self, num_axes):
        for widget in self.preview_frame.winfo_children():
            widget.destroy()
            
        self.wheel_canvas = None
        self.wheel_spoke_id = None
        self.wheel_text_id = None
        self.pedal_canvases.clear()
        self.pedal_rect_ids.clear()
        
        self.preview_canvases.clear()
        self.preview_dots.clear()
        self.std_grid_axes.clear()
        self.std_trigger_axes.clear()
        
        if num_axes == 0:
            tk.Label(self.preview_frame, text="Selected device has no valid axes.", fg="gray").pack(pady=5)
            return

        if self.is_wheel:
            container = tk.Frame(self.preview_frame)
            container.pack(expand=True, pady=10)

            wheel_frame = tk.Frame(container)
            wheel_frame.pack(side="left", padx=20)
            tk.Label(wheel_frame, text="Axis 0 (Steering)").pack(pady=(0, 5))
            
            self.wheel_size = 140
            self.wheel_center = self.wheel_size // 2
            self.wheel_radius = 60
            
            self.wheel_canvas = tk.Canvas(wheel_frame, width=self.wheel_size, height=self.wheel_size, bg="black", highlightthickness=0)
            self.wheel_canvas.pack()
            
            self.wheel_canvas.create_oval(
                self.wheel_center - self.wheel_radius, self.wheel_center - self.wheel_radius,
                self.wheel_center + self.wheel_radius, self.wheel_center + self.wheel_radius,
                outline="#555555", width=4
            )
            self.wheel_canvas.create_oval(
                self.wheel_center - 4, self.wheel_center - 4,
                self.wheel_center + 4, self.wheel_center + 4,
                fill="#00ff4c"
            )
            
            self.wheel_spoke_id = self.wheel_canvas.create_line(
                self.wheel_center, self.wheel_center,
                self.wheel_center, self.wheel_center - self.wheel_radius,
                fill="#00ff4c", width=3
            )
            
            self.wheel_text_id = self.wheel_canvas.create_text(
                self.wheel_center, self.wheel_center + 30, text="0°", fill="white", font=("Arial", 10, "bold")
            )

            if num_axes > 1:
                pedals_frame = tk.Frame(container)
                pedals_frame.pack(side="left", padx=20)
                
                for i in range(1, num_axes):
                    p_frame = tk.Frame(pedals_frame)
                    p_frame.pack(side="left", padx=10)
                    tk.Label(p_frame, text=f"Axis {i}").pack(pady=(0, 5))
                    
                    p_width = 30
                    p_height = 140
                    p_canvas = tk.Canvas(p_frame, width=p_width, height=p_height, bg="black", highlightthickness=0)
                    p_canvas.pack()
                    
                    p_canvas.create_rectangle(2, 2, p_width-2, p_height-2, outline="#555555", width=2)
                    rect_id = p_canvas.create_rectangle(4, p_height-4, p_width-4, p_height-4, fill="#00ff4c", outline="")
                    
                    self.pedal_canvases.append(p_canvas)
                    self.pedal_rect_ids.append(rect_id)
        else:
            container = tk.Frame(self.preview_frame)
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
                self.std_grid_axes.append((ax_x, ax_y))
                
                lbl_text = f"Axes {ax_x} & {ax_y}" if ax_y is not None else f"Axis {ax_x}"
                tk.Label(pair_frame, text=lbl_text).pack()
                
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
                                             
                self.preview_canvases.append(canvas)
                self.preview_dots.append(red_dot)
                
            for ax in trigger_axes:
                t_frame = tk.Frame(trigger_frame)
                t_frame.pack(side="left", padx=10)
                tk.Label(t_frame, text=f"Axis {ax}\n(Trigger)").pack(pady=(0, 5))
                
                self.std_trigger_axes.append(ax)
                
                p_width = 30
                p_height = 140
                p_canvas = tk.Canvas(t_frame, width=p_width, height=p_height, bg="black", highlightthickness=0)
                p_canvas.pack()
                
                p_canvas.create_rectangle(2, 2, p_width-2, p_height-2, outline="#555555", width=2)
                rect_id = p_canvas.create_rectangle(4, p_height-4, p_width-4, p_height-4, fill="#00ff4c", outline="")
                
                self.pedal_canvases.append(p_canvas)
                self.pedal_rect_ids.append(rect_id)
            
        self._bind_mousewheel(self.preview_frame)

    def _populate_axes_frame(self, num_axes):
        self.setting_widgets = [w for w in self.setting_widgets if w.winfo_exists()]
        
        for widget in self.axes_frame.winfo_children():
            widget.destroy()
            
        if num_axes == 0:
            tk.Label(self.axes_frame, text="Selected device has no valid axes.", fg="gray").pack(pady=5)
            return

        for axis_idx in range(num_axes):
            row_frame = tk.Frame(self.axes_frame)
            row_frame.pack(fill="x", pady=5)
            
            tk.Label(row_frame, text=f"Axis {axis_idx}:", width=6, anchor="w").pack(side="left")
            
            if axis_idx not in self.axis_config:
                self.axis_config[axis_idx] = {
                    'id_var': tk.StringVar(value=str(axis_idx)),
                    'inv_var': tk.BooleanVar(value=False),
                    'sens_var': tk.DoubleVar(value=1.0),
                    'dead_var': tk.DoubleVar(value=0.0)
                }
            config = self.axis_config[axis_idx]
            
            tk.Label(row_frame, text="-> OSC ID:").pack(side="left")
            id_entry = tk.Entry(row_frame, textvariable=config['id_var'], width=4)
            id_entry.pack(side="left", padx=(2, 10))
            self.setting_widgets.append(id_entry)
            
            chk = tk.Checkbutton(row_frame, text="Invert", variable=config['inv_var'])
            chk.pack(side="left", padx=2)
            self.setting_widgets.append(chk)
            
            tk.Label(row_frame, text="Deadzone:").pack(side="left", padx=(10, 0))
            dead_scale = tk.Scale(row_frame, variable=config['dead_var'], from_=0.0, to=0.5, resolution=0.01, orient="horizontal", length=100)
            dead_scale.pack(side="left", padx=2)
            self.setting_widgets.append(dead_scale)
            
            tk.Label(row_frame, text="Sens:").pack(side="left", padx=(10, 0))
            sens_scale = tk.Scale(row_frame, variable=config['sens_var'], from_=0.1, to=5.0, resolution=0.1, orient="horizontal")
            sens_scale.pack(side="left", fill="x", expand=True, padx=2)
            self.setting_widgets.append(sens_scale)
            
        self._bind_mousewheel(self.axes_frame)

    def _preview_loop(self):
        if self.joystick:
            if not self.is_running:
                sdl2.SDL_JoystickUpdate()
            
            num_axes = sdl2.SDL_JoystickNumAxes(self.joystick)
            
            if self.is_wheel:
                if num_axes > 0 and self.wheel_canvas and self.wheel_spoke_id:
                    raw_0 = sdl2.SDL_JoystickGetAxis(self.joystick, 0) / 32767.0
                    val_0 = self.get_axis_value(0, raw_0)
                    
                    current_deg = val_0 * 450.0
                    angle = math.radians(current_deg - 90)
                    
                    end_x = self.wheel_center + self.wheel_radius * math.cos(angle)
                    end_y = self.wheel_center + self.wheel_radius * math.sin(angle)
                    
                    self.wheel_canvas.coords(self.wheel_spoke_id, self.wheel_center, self.wheel_center, end_x, end_y)
                    self.wheel_canvas.itemconfig(self.wheel_text_id, text=f"{int(current_deg)}°")
                
                for i in range(1, num_axes):
                    idx = i - 1
                    if idx < len(self.pedal_canvases):
                        raw_val = sdl2.SDL_JoystickGetAxis(self.joystick, i) / 32767.0
                        val = self.get_axis_value(i, raw_val)
                        
                        pct = (val + 1.0) / 2.0
                        
                        p_height = 140
                        y2 = p_height - 4 
                        y1 = y2 - (pct * (p_height - 8)) 
                        
                        self.pedal_canvases[idx].coords(self.pedal_rect_ids[idx], 4, y1, 30-4, y2)
            else:
                for i, (ax_x, ax_y) in enumerate(self.std_grid_axes):
                    if i >= len(self.preview_canvases):
                        break
                        
                    x_val, y_val = 0.0, 0.0
                    
                    if ax_x < num_axes:
                        raw_x = sdl2.SDL_JoystickGetAxis(self.joystick, ax_x) / 32767.0
                        x_val = self.get_axis_value(ax_x, raw_x)
                    if ax_y is not None and ax_y < num_axes:
                        raw_y = sdl2.SDL_JoystickGetAxis(self.joystick, ax_y) / 32767.0
                        y_val = self.get_axis_value(ax_y, raw_y)
                        
                    draw_x = self.center + (x_val * self.radius)
                    draw_y = self.center + (y_val * self.radius)
                    
                    dot_r = 6
                    self.preview_canvases[i].coords(self.preview_dots[i], draw_x - dot_r, draw_y - dot_r, draw_x + dot_r, draw_y + dot_r)
                
                for i, ax in enumerate(self.std_trigger_axes):
                    if i >= len(self.pedal_canvases):
                        break
                        
                    raw_val = sdl2.SDL_JoystickGetAxis(self.joystick, ax) / 32767.0
                    val = self.get_axis_value(ax, raw_val)
                    
                    pct = (val + 1.0) / 2.0
                    
                    p_height = 140
                    y2 = p_height - 4 
                    y1 = y2 - (pct * (p_height - 8)) 
                    
                    self.pedal_canvases[i].coords(self.pedal_rect_ids[i], 4, y1, 30-4, y2)
            
        self.root.after(20, self._preview_loop) 

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
                    ("Version 2.2.1\n", "code"),
                    ("\n C2O is a lightweight, GUI-driven Python application designed to seamlessly bridge the gap between physical hardware and digital environments. It reads real-time data from connected USB steering wheels, Bluetooth gamepads, and joysticks. Capturing everything from continuous analog axes (like pedals and throttles) to discrete button presses and D-pad movements.\n\n", ""),
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
                    ("   • OSC Address: ", "bold"),
                    ("The specific OSC endpoint you want to broadcast to.\n\n", "bullet"),
                    ("3. Start Streaming: ", "bold"),
                    ("Click the Start Streaming button.\n\n", ""),

                    ("Input Profiles\n", "h2"),
                    ("You can save different device layouts and parameters using the Input Profile manager at the top of the settings page. Switch, create, or delete layout maps instantly.\n\n", ""),

                    ("Input Settings\n", "h2"),
                    ("• Deadzone: ", "bold"),
                    ("Allows you to set a center threshold that ignores slight resting inputs (like stick drift). The output will scale smoothly past the deadzone.\n\n", ""),
                    ("• Sensitivity: ", "bold"),
                    ("Acts as a multiplier. If set to 2.0, moving an axis halfway will output the maximum 1.0 value.\n\n", ""),
                    ("Utilize the input preview to determine, visualize, and feel how your inputs are being translated into software in real time!\n\n", ""),

                    ("OSC Payload Format (Output)\n", "h2"),
                    ("• Axes: ", "bold"),
                    ("[Address] \"axis\" [Axis Index] [Float Value]\n", "code"),
                    ("• Buttons: ", "bold"),
                    ("[Address] \"button\" [Button Index] [Int Value]\n", "code"),
                    ("\n", ""),

                    ("OSC Payload Format (Remote FFB Input)\n", "h2"),
                    ("• /ffb/spring [Float 0-100]: ", "bold"),
                    ("Adjust the centering spring resistance dynamically.\n", "code"),
                    ("• /ffb/damper [Float 0-100]: ", "bold"),
                    ("Adjust the wheel weight (damper) dynamically.\n", "code"),
                    ("• /ffb/friction [Float 0-100]: ", "bold"),
                    ("Adjust the static friction dynamically.\n", "code"),
                    ("\n\n", ""),

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

    # --- Profile Management Functions ---
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

    def on_profile_selected(self, event=None):
        self.apply_profile(self.current_profile_name.get())

    def update_profile_combo(self):
        self.profile_combo['values'] = list(self.profiles.keys())
        
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
            "hats": {} # new
        }
        
        for idx, config in self.axis_config.items():
            config_data["axes"][str(idx)] = {
                "osc_id": config['id_var'].get(),
                "inverted": config['inv_var'].get(),
                "sensitivity": config['sens_var'].get(),
                "deadzone": config['dead_var'].get()
            }
            
        for idx, var in self.button_vars.items():
            config_data["buttons"][str(idx)] = var.get()

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
                    self.axis_config[idx] = {
                        'id_var': tk.StringVar(value=str(idx)),
                        'inv_var': tk.BooleanVar(value=False),
                        'sens_var': tk.DoubleVar(value=1.0),
                        'dead_var': tk.DoubleVar(value=0.0)
                    }
                self.axis_config[idx]['id_var'].set(data.get("osc_id", str(idx)))
                self.axis_config[idx]['inv_var'].set(data.get("inverted", False))
                self.axis_config[idx]['sens_var'].set(data.get("sensitivity", 1.0))
                self.axis_config[idx]['dead_var'].set(data.get("deadzone", 0.0))

        if "buttons" in config_data:
            for idx_str, value in config_data["buttons"].items():
                idx = int(idx_str)
                if idx in self.button_vars:
                    self.button_vars[idx].set(value)

        if "hats" in config_data:
            for idx_str, value in config_data["hats"].items():
                idx = int(idx_str)
                if idx in self.hat_vars:
                    self.hat_vars[idx].set(value)

    def _update_button_labels(self, name):
        name_lower = name.lower()
        
        is_ps = any(x in name_lower for x in ["playstation", "dualshock", "dualsense", "ps4", "ps5"])
        is_nintendo = any(x in name_lower for x in ["nintendo", "switch", "pro controller", "joy-con"])
        is_xbox = any(x in name_lower for x in ["xbox", "xinput"])
        is_g29 = any(x in name_lower for x in ["g29", "g920", "g923"])
        
        self.current_button_map.clear()
        
        for i in range(24):
            btn_name = f"Btn {i}"
            
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
                # Default to XInput map for generics
                map_dict = {0: "A", 1: "B", 2: "X", 3: "Y", 4: "LB", 5: "RB", 6: "Back", 7: "Start", 8: "LS", 9: "RS", 10: "Guide"}
                btn_name = map_dict.get(i, f"Btn {i}")
                
            self.current_button_map[i] = btn_name
            
            # Apply to GUI
            if i in self.button_labels:
                self.button_labels[i].config(text=f"{btn_name} ->")

    def refresh_devices(self):
        self._close_devices()
        
        sdl2.SDL_QuitSubSystem(sdl2.SDL_INIT_JOYSTICK | sdl2.SDL_INIT_HAPTIC)
        time.sleep(0.1)
        sdl2.SDL_InitSubSystem(sdl2.SDL_INIT_JOYSTICK | sdl2.SDL_INIT_HAPTIC)
        sdl2.SDL_JoystickUpdate()
        
        joystick_count = sdl2.SDL_NumJoysticks()
        self.devices_map = {} 
        
        if joystick_count == 0:
            self.device_dropdown['values'] = ["No devices found"]
            self.device_dropdown.current(0)
            self._populate_axes_frame(0)
            self._populate_preview_frame(0)
            self._update_button_labels("none") # Reset visual maps
            self.ffb_frame.pack_forget() 
        else:
            dropdown_values = []
            for i in range(joystick_count):
                name = sdl2.SDL_JoystickNameForIndex(i).decode('utf-8', errors='ignore')
                display_name = f"[{i}] {name}"
                
                self.devices_map[display_name] = i
                dropdown_values.append(display_name)
                
            self.device_dropdown['values'] = dropdown_values
            self.device_dropdown.current(0)
            self.on_device_selected()

    def _close_devices(self):
        if self.haptic:
            sdl2.SDL_HapticClose(self.haptic)
            self.haptic = None
            self.spring_id = None
            self.damper_id = None
            self.friction_id = None
            
        if self.joystick:
            sdl2.SDL_JoystickClose(self.joystick)
            self.joystick = None

    def on_device_selected(self, event=None):
        selected_device_str = self.device_var.get()
        if selected_device_str == "No devices found" or not selected_device_str:
            return
            
        device_index = self.devices_map.get(selected_device_str)
        if device_index is not None:
            self._close_devices()

            self.joystick = sdl2.SDL_JoystickOpen(device_index)
            num_axes = sdl2.SDL_JoystickNumAxes(self.joystick)
            
            joy_type = sdl2.SDL_JoystickGetType(self.joystick)
            is_type_wheel = (joy_type == sdl2.SDL_JOYSTICK_TYPE_WHEEL)
            
            name = sdl2.SDL_JoystickName(self.joystick).decode('utf-8', errors='ignore').lower()
            wheel_keywords = ['wheel', 'racing', 'g29', 'g920', 'thrustmaster', 'fanatec', 'moza']
            is_string_wheel = any(kw in name for kw in wheel_keywords)
            
            self.is_wheel = is_type_wheel or is_string_wheel

            # Execute dynamic labels
            self._update_button_labels(name)

            self.haptic = sdl2.SDL_HapticOpenFromJoystick(self.joystick)
            has_ffb = False
            
            if self.haptic:
                sdl2.SDL_HapticSetAutocenter(self.haptic, 0) 
                sdl2.SDL_HapticSetGain(self.haptic, 100)      

                features = sdl2.SDL_HapticQuery(self.haptic)
                
                if features & sdl2.SDL_HAPTIC_SPRING:
                    self.spring_effect = sdl2.SDL_HapticEffect()
                    self.spring_effect.type = sdl2.SDL_HAPTIC_SPRING
                    self.spring_effect.condition.length = sdl2.SDL_HAPTIC_INFINITY
                    self.spring_id = sdl2.SDL_HapticNewEffect(self.haptic, self.spring_effect)
                    sdl2.SDL_HapticRunEffect(self.haptic, self.spring_id, sdl2.SDL_HAPTIC_INFINITY)
                    has_ffb = True
                    
                if features & sdl2.SDL_HAPTIC_DAMPER:
                    self.damper_effect = sdl2.SDL_HapticEffect()
                    self.damper_effect.type = sdl2.SDL_HAPTIC_DAMPER
                    self.damper_effect.condition.length = sdl2.SDL_HAPTIC_INFINITY
                    self.damper_id = sdl2.SDL_HapticNewEffect(self.haptic, self.damper_effect)
                    sdl2.SDL_HapticRunEffect(self.haptic, self.damper_id, sdl2.SDL_HAPTIC_INFINITY)
                    has_ffb = True
                    
                if features & sdl2.SDL_HAPTIC_FRICTION:
                    self.friction_effect = sdl2.SDL_HapticEffect()
                    self.friction_effect.type = sdl2.SDL_HAPTIC_FRICTION
                    self.friction_effect.condition.length = sdl2.SDL_HAPTIC_INFINITY
                    self.friction_id = sdl2.SDL_HapticNewEffect(self.haptic, self.friction_effect)
                    sdl2.SDL_HapticRunEffect(self.haptic, self.friction_id, sdl2.SDL_HAPTIC_INFINITY)
                    has_ffb = True
                
                self.update_ffb()
                
            if self.is_wheel and has_ffb:
                self.ffb_frame.pack(fill="x", padx=10, pady=5, before=self.preview_frame)
            else:
                self.ffb_frame.pack_forget()
            
            self._populate_axes_frame(num_axes)
            self._populate_preview_frame(num_axes)

    def _apply_condition_effect(self, effect_id, effect_struct, strength_var):
        if self.haptic and effect_id is not None and effect_struct:
            strength_pct = strength_var.get() / 100.0
            coeff = int(strength_pct * 32767)
            
            effect_struct.condition.right_sat[0] = 65535
            effect_struct.condition.left_sat[0] = 65535
            effect_struct.condition.right_coeff[0] = coeff
            effect_struct.condition.left_coeff[0] = coeff
            effect_struct.condition.deadband[0] = 0
            effect_struct.condition.center[0] = 0
            
            sdl2.SDL_HapticUpdateEffect(self.haptic, effect_id, effect_struct)

    def update_ffb(self, *args):
        self._apply_condition_effect(self.spring_id, self.spring_effect, self.ffb_spring_var)
        self._apply_condition_effect(self.damper_id, self.damper_effect, self.ffb_damper_var)
        self._apply_condition_effect(self.friction_id, self.friction_effect, self.ffb_friction_var)

    def save_config(self):
        self.save_current_profile_to_dict()
        
        full_data = {
            "active_profile": self.current_profile_name.get(),
            "profiles": self.profiles
        }
            
        try:
            with open(self.config_file, 'w') as f:
                json.dump(full_data, f, indent=4)
            self.save_btn.config(text="Saved!", bg="#4CAF50", fg="white")
            self.root.after(1500, lambda: self.save_btn.config(text="Save Settings", bg="SystemButtonFace", fg="black"))
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
                # Legacy format migration
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
                config['id_var'].set(str(axis_idx))
                config['inv_var'].set(False)
                config['sens_var'].set(1.0)
                config['dead_var'].set(0.0)
            
            for i, var in self.button_vars.items():
                var.set(str(i))

            for i, var in self.hat_vars.items():
                var.set(str(i))

    def get_axis_value(self, index, raw_value):
        if index in self.axis_config:
            config = self.axis_config[index]
            is_inverted = config['inv_var'].get()
            sensitivity = config['sens_var'].get()
            deadzone = config['dead_var'].get()
            
            if abs(raw_value) < deadzone:
                val = 0.0
            else:
                sign = 1 if raw_value > 0 else -1
                if deadzone >= 1.0:
                    val = 0.0
                else:
                    val = sign * ((abs(raw_value) - deadzone) / (1.0 - deadzone))
            
            val = val * sensitivity
            if is_inverted:
                val = -val
            
            val = max(-1.0, min(1.0, val))
            return round(val, 3)
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
        
        name = sdl2.SDL_JoystickName(self.joystick).decode('utf-8', errors='ignore') if self.joystick else "Unknown"
        header = f"--- STREAMING ACTIVE ---\n"
        header += f"Device: {name}\n"
        header += f"Target Output: {self.ip_entry.get()}:{self.port_entry.get()} | Addr: {self.osc_address}\n"
        header += f"OSC Input Port (FFB): {self.listen_port_entry.get()}\n"
        header += f"-------------------------\n"
        self.log_area.insert(tk.END, header)
        
        for i in sorted(self.prev_axes.keys()):
            val = self.get_axis_value(i, self.prev_axes[i])
            mapped_i = self.get_axis_id(i)
            self.log_area.insert(tk.END, f"Axis {i} (OSC ID {mapped_i}):   {val:.3f}\n")
            
        for i in sorted(self.prev_buttons.keys()):
            mapped_i = self.get_button_id(i)
            # Fetch mapped name for terminal display
            btn_name = self.current_button_map.get(i, f"Btn {i}")
            self.log_area.insert(tk.END, f"{btn_name} (OSC ID {mapped_i}): {self.prev_buttons[i]}\n")
            
        for i in sorted(self.prev_hats.keys()):
            mapped_i = self.get_hat_id(i)
            self.log_area.insert(tk.END, f"Hat {i} (OSC ID {mapped_i}): {self.prev_hats[i]}\n")
            
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

        selected_device_str = self.device_var.get()
        if selected_device_str == "No devices found" or not selected_device_str:
            messagebox.showerror("Device Error", "No controller selected or found. Please connect a device and click Refresh.")
            return

        if not self.joystick:
            self.on_device_selected()

        if self.ui_icon_on:
            self.status_icon_label.config(image=self.ui_icon_on)

        # OSC Out Client
        self.client = udp_client.SimpleUDPClient(ip, port)
        self.osc_address = osc_address
        
        # OSC IN Server (Remote FFB Control)
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
        self.start_btn.config(text="Stop Streaming", bg="#f44336")
        
        self.setting_widgets = [w for w in self.setting_widgets if w.winfo_exists()]
        for widget in self.setting_widgets:
            widget.config(state="disabled")
        
        if self.icon_on_tk:
            self.root.iconphoto(True, self.icon_on_tk) 
        if self.tray_icon and self.img_on:
            self.tray_icon.icon = self.img_on
        
        name = sdl2.SDL_JoystickName(self.joystick).decode('utf-8', errors='ignore')
        if self.output_mode.get() == "scroll":
            self.log(f"--- STARTED STREAMING ---")
            self.log(f"Device: {name}")
            self.log(f"Sending to: {ip}:{port} | Address: {osc_address}")
            self.log(f"Listening for FFB on port: {listen_port}")
            self.log(f"-------------------------")
        else:
            self.clear_log()

        self.prev_axes.clear()
        self.prev_buttons.clear()
        self.prev_hats.clear()

        self.poll_joystick()

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
            
        self.start_btn.config(text="Start Streaming", bg="#4CAF50")

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

    # --- OSC Input Handlers ---
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

    # --- Joystick Polling ---
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

    def poll_joystick(self):
        if not self.is_running:
            return

        sdl2.SDL_JoystickUpdate()
        num_axes = sdl2.SDL_JoystickNumAxes(self.joystick)
        num_buttons = sdl2.SDL_JoystickNumButtons(self.joystick)
        num_hats = sdl2.SDL_JoystickNumHats(self.joystick)

        state_changed = False

        for i in range(num_axes):
            raw_axis_val = round(sdl2.SDL_JoystickGetAxis(self.joystick, i) / 32767.0, 3)
            if self.prev_axes.get(i) != raw_axis_val:
                self.prev_axes[i] = raw_axis_val
                
                final_val = self.get_axis_value(i, raw_axis_val)
                mapped_axis_id = self.get_axis_id(i)
                msg_args = ["axis", mapped_axis_id, final_val]
                
                self.client.send_message(self.osc_address, msg_args)
                if self.output_mode.get() == "scroll":
                    self.log(f"{self.osc_address} {msg_args}")
                
                state_changed = True

        for i in range(num_buttons):
            raw_btn_val = sdl2.SDL_JoystickGetButton(self.joystick, i)
            if self.prev_buttons.get(i) != raw_btn_val:
                self.prev_buttons[i] = raw_btn_val
                
                mapped_id = self.get_button_id(i)
                msg_args = ["button", mapped_id, raw_btn_val]
                
                self.client.send_message(self.osc_address, msg_args)
                if self.output_mode.get() == "scroll":
                    # Optionally use dynamic name here, but raw OSC payload remains unchanged!
                    self.log(f"{self.osc_address} {msg_args}")
                
                state_changed = True

        for i in range(num_hats):
            hat_bitmask = sdl2.SDL_JoystickGetHat(self.joystick, i)
            hat_tuple = self.sdl_hat_to_tuple(hat_bitmask)
            
            if self.prev_hats.get(i) != hat_tuple:
                mapped_id = self.get_hat_id(i)
                msg_args = ["hat", mapped_id, hat_tuple[0], hat_tuple[1]]
                self.client.send_message(self.osc_address, msg_args)
                
                if self.output_mode.get() == "scroll":
                    self.log(f"{self.osc_address} {msg_args}")
                
                self.prev_hats[i] = hat_tuple
                state_changed = True

        if state_changed and self.output_mode.get() == "inplace":
            self.redraw_in_place()

        self.update_job = self.root.after(10, self.poll_joystick)

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