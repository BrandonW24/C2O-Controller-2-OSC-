using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Windows.Threading;
using System.Windows.Input;
using System.Runtime.InteropServices;
using CoreOSC;
using CoreOSC.IO;
using SDL2;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.IO;
using System.Text;

namespace C2O
{
    // --- Data Models for Serialization ---
    public class AppConfig
    {
        public string ActiveProfile { get; set; } = "Default";
        public Dictionary<string, ProfileData> Profiles { get; set; } = new Dictionary<string, ProfileData>();
    }

    public class ProfileData
    {
        public string Ip { get; set; } = "127.0.0.1";
        public string Port { get; set; } = "4041";
        public string ListenIp { get; set; } = "127.0.0.1";
        public string ListenPort { get; set; } = "4042";
        public string BaseAddress { get; set; } = "/wheel/input";
        
        // FFB Configs
        public bool FfbEnabled { get; set; } = true;
        public float FfbGain { get; set; } = 1.0f;
        
        // Motion configs
        public bool MotionEnabled { get; set; } = false;
        public string MotionIp { get; set; } = "127.0.0.1";
        public string MotionPort { get; set; } = "20777";
        public string MotionProtocol { get; set; } = "Raw OSC";
        
        public bool SafetyEnabled { get; set; } = true;
        public int SafetyTimeoutMs { get; set; } = 500;

        public Dictionary<string, MotionAxisConfig> MotionAxes { get; set; } = new Dictionary<string, MotionAxisConfig>();

        public Dictionary<int, AxisConfig> Axes { get; set; } = new Dictionary<int, AxisConfig>();
        public Dictionary<int, ButtonConfig> Buttons { get; set; } = new Dictionary<int, ButtonConfig>();
        public Dictionary<int, HatConfig> Hats { get; set; } = new Dictionary<int, HatConfig>();
        public Dictionary<int, KeyConfig> Keys { get; set; } = new Dictionary<int, KeyConfig>();
    }

    public class MotionAxisConfig
    {
        public float Gain { get; set; } = 1.0f;
        public float Smooth { get; set; } = 0.0f;
        public bool WashoutEnabled { get; set; } = false;
        public float WashoutStrength { get; set; } = 0.99f;
    }

    public class AxisConfig
    {
        public string CustomName { get; set; } = "";
        public int OscId { get; set; }
        public string CustomAddress { get; set; } = "";
        public bool IsInverted { get; set; } = false;
        public bool IsActive { get; set; } = true;
        public float Sensitivity { get; set; } = 1.0f;
        public float Deadzone { get; set; } = 0.0f;
        public float Curve { get; set; } = 1.0f;
        public float Smooth { get; set; } = 0.0f;
    }

    public class ButtonConfig
    {
        public string CustomName { get; set; } = "";
        public int OscId { get; set; }
        public string CustomAddress { get; set; } = "";
    }

    public class HatConfig
    {
        public int OscId { get; set; }
        public string CustomAddress { get; set; } = "";
    }

    public class KeyConfig
    {
        public string KeyName { get; set; } = "";
        public int OscId { get; set; }
        public string CustomAddress { get; set; } = "";
    }

    public class DeviceInfo
    {
        public IntPtr Joystick { get; set; }
        public IntPtr Haptic { get; set; } 
        
        // Multi-Effect FFB Array Management
        public Dictionary<ushort, int> EffectIds { get; set; } = new Dictionary<ushort, int>();
        public bool IsRumbleInitialized { get; set; } = false;

        public string Name { get; set; } = "";
        public int NumAxes { get; set; }
        public int NumButtons { get; set; }
        public int NumHats { get; set; }
        public int AxisOffset { get; set; }
        public int ButtonOffset { get; set; }
        public int HatOffset { get; set; }
    }

    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        private bool _isRunning = false;
        private volatile bool _isScrollMode = true;
        private CancellationTokenSource? _pollingCancellationToken;
        private UdpClient? _oscSender;
        private UdpClient? _oscListener;
        private UdpClient? _motionSender;
        private string _baseAddr = "";

        // Profile Management
        private AppConfig _appConfig = new AppConfig();
        private string _currentProfileName = "Default";
        private bool _isUpdatingProfiles = false;

        private List<DeviceInfo> _activeDevices = new List<DeviceInfo>();

        // Working Dictionaries
        private Dictionary<int, AxisConfig> _axisConfigs = new Dictionary<int, AxisConfig>();
        private Dictionary<int, ButtonConfig> _buttonConfigs = new Dictionary<int, ButtonConfig>();
        private Dictionary<int, HatConfig> _hatConfigs = new Dictionary<int, HatConfig>();
        private Dictionary<int, KeyConfig> _keyConfigs = new Dictionary<int, KeyConfig>();
        
        private Dictionary<int, float> _axisEmaHistory = new Dictionary<int, float>();
        
        // Motion Management
        private ConcurrentDictionary<string, float> _motionEmaHistory = new ConcurrentDictionary<string, float>();
        private ConcurrentDictionary<string, float> _motionWashoutPrevInput = new ConcurrentDictionary<string, float>();
        private ConcurrentDictionary<string, float> _motionWashoutPrevOutput = new ConcurrentDictionary<string, float>();
        
        private bool _motionKilled = false;
        private DateTime _lastMotionReceiveTime = DateTime.UtcNow;
        private bool _hasZeroedMotion = true; // Start true so we don't zero immediately on boot

        // Output and Previews
        private ConcurrentDictionary<int, float> _prevAxes = new ConcurrentDictionary<int, float>();
        private ConcurrentDictionary<int, byte> _prevButtons = new ConcurrentDictionary<int, byte>();
        private ConcurrentDictionary<int, (int x, int y)> _prevHats = new ConcurrentDictionary<int, (int, int)>();
        private ConcurrentDictionary<int, bool> _prevKeys = new ConcurrentDictionary<int, bool>();

        private Dictionary<int, ProgressBar> _axisPreviewBars = new Dictionary<int, ProgressBar>();
        private Dictionary<int, System.Windows.Shapes.Ellipse> _buttonPreviewDots = new Dictionary<int, System.Windows.Shapes.Ellipse>();
        private DispatcherTimer _uiTimer;

        // Dynamic Icons
        private BitmapImage? _iconOn;
        private BitmapImage? _iconOff;

        // Shared Theme Colors
        private SolidColorBrush _bgDark = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2F353D"));
        private SolidColorBrush _inputBoxColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#717171"));
        private SolidColorBrush _labelColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DFBB3F"));
        
        // FFB / Incoming UI queue mechanism
        private ConcurrentQueue<string> _incomingLogQueue = new ConcurrentQueue<string>();
        private ConcurrentDictionary<string, string> _latestIncomingOsc = new ConcurrentDictionary<string, string>();
        private volatile bool _isIncomingLoggingEnabled = false;
        private volatile bool _isIncomingScrollMode = true;

        public MainWindow()
        {
            InitializeComponent();
            LoadAppIcons();
            
            // Core FFB Slider Events
            SliderFfbGain.ValueChanged += (s, e) => { TxtFfbGain.Text = e.NewValue.ToString("0.0"); };

            // Custom FFB Test Tool Slider Events
            SliderTestStrength.ValueChanged += (s, e) => { TxtTestStrength.Text = $"{e.NewValue} %"; };
            SliderTestDuration.ValueChanged += (s, e) => { TxtTestDuration.Text = $"{e.NewValue} ms"; };
            SliderTestPulses.ValueChanged += (s, e) => { TxtTestPulses.Text = $"{e.NewValue}"; };

            LoadConfig();
            
            SDL.SDL_Init(SDL.SDL_INIT_JOYSTICK | SDL.SDL_INIT_HAPTIC);
            RefreshDevices();

            _uiTimer = new DispatcherTimer();
            _uiTimer.Interval = TimeSpan.FromMilliseconds(50); 
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();
        }

        // --- App Lifecycle & Icons ---

        private void LoadAppIcons()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();

                using (var streamOn = assembly.GetManifestResourceStream("C2O.steering-wheel-car_on.png"))
                {
                    if (streamOn != null)
                    {
                        _iconOn = new BitmapImage();
                        _iconOn.BeginInit();
                        _iconOn.StreamSource = streamOn;
                        _iconOn.CacheOption = BitmapCacheOption.OnLoad;
                        _iconOn.EndInit();
                    }
                }

                using (var streamOff = assembly.GetManifestResourceStream("C2O.steering-wheel-car_off.png"))
                {
                    if (streamOff != null)
                    {
                        _iconOff = new BitmapImage();
                        _iconOff.BeginInit();
                        _iconOff.StreamSource = streamOff;
                        _iconOff.CacheOption = BitmapCacheOption.OnLoad;
                        _iconOff.EndInit();
                    }
                }
                
                if (_iconOff != null)
                {
                    this.Icon = _iconOff;
                    StatusIcon.Source = _iconOff;
                }
            }
            catch (Exception ex) 
            { 
                LogScroll($"[WARNING] Failed to load icons via stream: {ex.Message}");
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopStreaming();
            SaveConfig();
            foreach (var device in _activeDevices)
            {
                if (device.Haptic != IntPtr.Zero) SDL.SDL_HapticClose(device.Haptic);
                if (device.Joystick != IntPtr.Zero) SDL.SDL_JoystickClose(device.Joystick);
            }
            SDL.SDL_Quit();
        }

        // --- Profile Management & Serialization ---

        private void LoadConfig()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    _appConfig = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
                catch { _appConfig = new AppConfig(); }
            }

            if (_appConfig.Profiles.Count == 0)
                _appConfig.Profiles["Default"] = new ProfileData();

            if (!_appConfig.Profiles.ContainsKey(_appConfig.ActiveProfile))
                _appConfig.ActiveProfile = _appConfig.Profiles.Keys.First();

            _currentProfileName = _appConfig.ActiveProfile;
            ApplyProfileToUI(_currentProfileName);
            UpdateProfileDropdown();
        }

        private void SaveConfig()
        {
            SyncUIToProfile();
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_appConfig, options);
                File.WriteAllText(path, json);
            }
            catch { }
        }

        private void SyncUIToProfile()
        {
            if (!_appConfig.Profiles.ContainsKey(_currentProfileName))
                _appConfig.Profiles[_currentProfileName] = new ProfileData();

            var prof = _appConfig.Profiles[_currentProfileName];
            prof.Ip = TargetIpEntry.Text;
            prof.Port = TargetPortEntry.Text;
            prof.ListenIp = ListenIpEntry.Text;
            prof.ListenPort = ListenPortEntry.Text;
            prof.BaseAddress = BaseAddrEntry.Text;

            prof.FfbEnabled = ChkFfbEnable.IsChecked == true;
            prof.FfbGain = (float)SliderFfbGain.Value;
            
            prof.MotionEnabled = ChkMotionEnable.IsChecked == true;
            prof.MotionIp = MotionIpEntry.Text;
            prof.MotionPort = MotionPortEntry.Text;
            prof.MotionProtocol = CmbMotionProtocol.Text;
            
            prof.SafetyEnabled = ChkSafetyEnable.IsChecked == true;
            if (int.TryParse(TxtSafetyTimeout.Text, out int timeout)) prof.SafetyTimeoutMs = timeout;

            // MotionAxes are synced directly via their event handlers in BuildMotionUI
            prof.Axes = _axisConfigs;
            prof.Buttons = _buttonConfigs;
            prof.Hats = _hatConfigs;
            prof.Keys = _keyConfigs;
        }

        private void ApplyProfileToUI(string profileName)
        {
            if (!_appConfig.Profiles.ContainsKey(profileName)) return;
            var prof = _appConfig.Profiles[profileName];
            
            TargetIpEntry.Text = prof.Ip;
            TargetPortEntry.Text = prof.Port;
            ListenIpEntry.Text = string.IsNullOrEmpty(prof.ListenIp) ? "0.0.0.0" : prof.ListenIp;
            ListenPortEntry.Text = prof.ListenPort;
            BaseAddrEntry.Text = prof.BaseAddress;

            ChkFfbEnable.IsChecked = prof.FfbEnabled;
            SliderFfbGain.Value = prof.FfbGain;
            TxtFfbGain.Text = prof.FfbGain.ToString("0.0");
            
            ChkMotionEnable.IsChecked = prof.MotionEnabled;
            MotionIpEntry.Text = prof.MotionIp;
            MotionPortEntry.Text = prof.MotionPort;
            CmbMotionProtocol.Text = string.IsNullOrEmpty(prof.MotionProtocol) ? "Raw OSC" : prof.MotionProtocol;
            
            ChkSafetyEnable.IsChecked = prof.SafetyEnabled;
            TxtSafetyTimeout.Text = prof.SafetyTimeoutMs.ToString();

            if (prof.MotionAxes == null) prof.MotionAxes = new Dictionary<string, MotionAxisConfig>();

            _axisConfigs = prof.Axes ?? new Dictionary<int, AxisConfig>();
            _buttonConfigs = prof.Buttons ?? new Dictionary<int, ButtonConfig>();
            _hatConfigs = prof.Hats ?? new Dictionary<int, HatConfig>();
            _keyConfigs = prof.Keys ?? new Dictionary<int, KeyConfig>();

            RebuildDynamicUI();
        }

        private void UpdateProfileDropdown()
        {
            _isUpdatingProfiles = true;
            ProfileDropdown.Items.Clear();
            foreach (var p in _appConfig.Profiles.Keys)
            {
                ProfileDropdown.Items.Add(p);
            }
            ProfileDropdown.SelectedItem = _currentProfileName;
            _isUpdatingProfiles = false;
        }

        private void ProfileDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingProfiles || ProfileDropdown.SelectedItem == null) return;
            
            string selected = ProfileDropdown.SelectedItem.ToString()!;
            if (selected != _currentProfileName)
            {
                SyncUIToProfile(); 
                _currentProfileName = selected;
                _appConfig.ActiveProfile = _currentProfileName;
                ApplyProfileToUI(_currentProfileName);
                SaveConfig();
            }
        }

        private void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
        {
            SaveConfig();
            BtnSaveConfig.Content = "Saved!";
            Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() => BtnSaveConfig.Content = "Save Configs Now"));
        }

        private void BtnNewProfile_Click(object sender, RoutedEventArgs e)
        {
            string newName = NewProfileNameEntry.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName))
            {
                MessageBox.Show("Please enter a name for the new profile in the text box.");
                return;
            }
            if (_appConfig.Profiles.ContainsKey(newName))
            {
                MessageBox.Show("A profile with this name already exists.");
                return;
            }

            SyncUIToProfile();
            
            var cloneJson = JsonSerializer.Serialize(_appConfig.Profiles[_currentProfileName]);
            var clone = JsonSerializer.Deserialize<ProfileData>(cloneJson);
            
            _appConfig.Profiles[newName] = clone!;
            _currentProfileName = newName;
            _appConfig.ActiveProfile = newName;
            
            NewProfileNameEntry.Text = "";
            UpdateProfileDropdown();
            ApplyProfileToUI(newName);
            SaveConfig();
        }

        private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_appConfig.Profiles.Count <= 1)
            {
                MessageBox.Show("You cannot delete your only remaining profile.");
                return;
            }
            
            var result = MessageBox.Show($"Are you sure you want to delete the '{_currentProfileName}' profile?", "Confirm Delete", MessageBoxButton.YesNo);
            if (result == MessageBoxResult.Yes)
            {
                _appConfig.Profiles.Remove(_currentProfileName);
                _currentProfileName = _appConfig.Profiles.Keys.First();
                _appConfig.ActiveProfile = _currentProfileName;
                
                UpdateProfileDropdown();
                ApplyProfileToUI(_currentProfileName);
                SaveConfig();
            }
        }

        private void BtnExportProfile_Click(object sender, RoutedEventArgs e)
        {
            SyncUIToProfile();
            var prof = _appConfig.Profiles[_currentProfileName];
            var dialog = new Microsoft.Win32.SaveFileDialog {
                Filter = "JSON Profile (*.json)|*.json",
                FileName = $"{_currentProfileName}_c2o.json"
            };
            
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var json = JsonSerializer.Serialize(prof, options);
                    File.WriteAllText(dialog.FileName, json);
                    MessageBox.Show("Profile exported successfully!");
                }
                catch (Exception ex) { MessageBox.Show("Error exporting: " + ex.Message); }
            }
        }

        private void BtnImportProfile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog {
                Filter = "JSON Profile (*.json)|*.json"
            };
            
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var json = File.ReadAllText(dialog.FileName);
                    var prof = JsonSerializer.Deserialize<ProfileData>(json);
                    if (prof != null)
                    {
                        string baseName = Path.GetFileNameWithoutExtension(dialog.FileName).Replace("_c2o", "");
                        string finalName = baseName;
                        int count = 1;
                        while (_appConfig.Profiles.ContainsKey(finalName))
                        {
                            finalName = $"{baseName}_{count++}";
                        }
                        
                        _appConfig.Profiles[finalName] = prof;
                        _currentProfileName = finalName;
                        _appConfig.ActiveProfile = finalName;
                        
                        UpdateProfileDropdown();
                        ApplyProfileToUI(finalName);
                        SaveConfig();
                        MessageBox.Show($"Profile '{finalName}' imported successfully!");
                    }
                }
                catch (Exception ex) { MessageBox.Show("Error importing profile. Ensure it is a valid C2O config.\n" + ex.Message); }
            }
        }

        private void RebuildDynamicUI()
        {
            DynamicSettingsPanel.Children.Clear();
            KeyboardSettingsPanel.Children.Clear();
            _axisPreviewBars.Clear();
            _buttonPreviewDots.Clear();

            foreach (var dev in _activeDevices)
            {
                BuildDeviceUI(dev);
            }
            BuildKeyboardUI();
            BuildMotionUI();
        }

        private void BuildMotionUI()
        {
            MotionAxesPanel.Children.Clear();
            string[] axes = { "pitch", "roll", "yaw", "surge", "sway", "heave" };

            var prof = _appConfig.Profiles[_currentProfileName];
            if (prof.MotionAxes == null) prof.MotionAxes = new Dictionary<string, MotionAxisConfig>();

            foreach (string axis in axes)
            {
                if (!prof.MotionAxes.ContainsKey(axis))
                {
                    prof.MotionAxes[axis] = new MotionAxisConfig();
                }

                var cfg = prof.MotionAxes[axis];

                var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
                
                // Axis Label
                rowPanel.Children.Add(new TextBlock { Text = axis.ToUpper(), Width = 60, Foreground = _labelColor, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });

                // Gain
                rowPanel.Children.Add(new TextBlock { Text = "Gain:", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 5, 0) });
                var gainSlider = new Slider { Minimum = 0.1, Maximum = 5.0, Value = cfg.Gain, Width = 80, TickFrequency = 0.1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
                var gainTxt = new TextBlock { Text = cfg.Gain.ToString("0.0"), Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), Width = 30 };
                gainSlider.ValueChanged += (s, e) => { cfg.Gain = (float)e.NewValue; gainTxt.Text = cfg.Gain.ToString("0.0"); };
                rowPanel.Children.Add(gainSlider);
                rowPanel.Children.Add(gainTxt);

                // Smooth
                rowPanel.Children.Add(new TextBlock { Text = "Smooth:", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(15, 0, 5, 0) });
                var smoothSlider = new Slider { Minimum = 0.0, Maximum = 0.99, Value = cfg.Smooth, Width = 80, TickFrequency = 0.01, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
                var smoothTxt = new TextBlock { Text = cfg.Smooth.ToString("0.00"), Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), Width = 35 };
                smoothSlider.ValueChanged += (s, e) => { cfg.Smooth = (float)e.NewValue; smoothTxt.Text = cfg.Smooth.ToString("0.00"); };
                rowPanel.Children.Add(smoothSlider);
                rowPanel.Children.Add(smoothTxt);

                // Washout Enable
                var washChk = new CheckBox { Content = "Washout", IsChecked = cfg.WashoutEnabled, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 5, 0) };
                washChk.Checked += (s, e) => cfg.WashoutEnabled = true;
                washChk.Unchecked += (s, e) => cfg.WashoutEnabled = false;
                rowPanel.Children.Add(washChk);

                // Washout Strength (Alpha)
                rowPanel.Children.Add(new TextBlock { Text = "Strength:", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 5, 0) });
                var washSlider = new Slider { Minimum = 0.50, Maximum = 0.99, Value = cfg.WashoutStrength, Width = 80, TickFrequency = 0.01, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
                var washTxt = new TextBlock { Text = cfg.WashoutStrength.ToString("0.00"), Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), Width = 35 };
                washSlider.ValueChanged += (s, e) => { cfg.WashoutStrength = (float)e.NewValue; washTxt.Text = cfg.WashoutStrength.ToString("0.00"); };
                rowPanel.Children.Add(washSlider);
                rowPanel.Children.Add(washTxt);

                MotionAxesPanel.Children.Add(rowPanel);
            }
        }

        // --- Hardware Polling & OSC ---

        private async Task HardwarePollingLoop(CancellationToken token)
        {
            try 
            {
                while (!token.IsCancellationRequested)
                {
                    SDL.SDL_JoystickUpdate();

                    foreach (var device in _activeDevices)
                    {
                        // Axes
                        for (int i = 0; i < device.NumAxes; i++)
                        {
                            int globalIdx = device.AxisOffset + i;
                            if (!_axisConfigs.ContainsKey(globalIdx) || !_axisConfigs[globalIdx].IsActive) continue;

                            short rawAxis = SDL.SDL_JoystickGetAxis(device.Joystick, i);
                            float normalizedRawAxis = rawAxis / 32767.0f;
                            float finalProcessedValue = GetAxisValue(globalIdx, normalizedRawAxis);

                            if (!_prevAxes.ContainsKey(globalIdx) || Math.Abs(_prevAxes[globalIdx] - finalProcessedValue) > 0.001f)
                            {
                                _prevAxes[globalIdx] = finalProcessedValue;
                                if (_oscSender != null)
                                {
                                    int mappedId = _axisConfigs[globalIdx].OscId;
                                    string customAddr = !string.IsNullOrWhiteSpace(_axisConfigs[globalIdx].CustomAddress) ? _axisConfigs[globalIdx].CustomAddress : _baseAddr;
                                    
                                    var msg = new OscMessage(new Address(customAddr), new object[] { "axis", mappedId, finalProcessedValue });
                                    await _oscSender.SendMessageAsync(msg);
                                    LogScroll($"[{customAddr}] axis {mappedId}: {finalProcessedValue}");
                                }
                            }
                        }

                        // Buttons
                        for (int i = 0; i < device.NumButtons; i++)
                        {
                            int globalIdx = device.ButtonOffset + i;
                            byte btnState = SDL.SDL_JoystickGetButton(device.Joystick, i);
                            
                            if (!_prevButtons.ContainsKey(globalIdx) || _prevButtons[globalIdx] != btnState)
                            {
                                _prevButtons[globalIdx] = btnState;
                                if (_oscSender != null)
                                {
                                    int mappedId = _buttonConfigs.ContainsKey(globalIdx) ? _buttonConfigs[globalIdx].OscId : globalIdx;
                                    string customAddr = _buttonConfigs.ContainsKey(globalIdx) && !string.IsNullOrWhiteSpace(_buttonConfigs[globalIdx].CustomAddress) ? _buttonConfigs[globalIdx].CustomAddress : _baseAddr;

                                    var msg = new OscMessage(new Address(customAddr), new object[] { "button", mappedId, (int)btnState });
                                    await _oscSender.SendMessageAsync(msg);
                                    LogScroll($"[{customAddr}] button {mappedId}: {btnState}");
                                }
                            }
                        }

                        // Hats
                        for (int i = 0; i < device.NumHats; i++)
                        {
                            int globalIdx = device.HatOffset + i;
                            byte hatState = SDL.SDL_JoystickGetHat(device.Joystick, i);
                            var hatTuple = ParseHatState(hatState);

                            if (!_prevHats.ContainsKey(globalIdx) || _prevHats[globalIdx] != hatTuple)
                            {
                                _prevHats[globalIdx] = hatTuple;
                                if (_oscSender != null)
                                {
                                    int mappedId = _hatConfigs.ContainsKey(globalIdx) ? _hatConfigs[globalIdx].OscId : globalIdx;
                                    string customAddr = _hatConfigs.ContainsKey(globalIdx) && !string.IsNullOrWhiteSpace(_hatConfigs[globalIdx].CustomAddress) ? _hatConfigs[globalIdx].CustomAddress : _baseAddr;

                                    var msg = new OscMessage(new Address(customAddr), new object[] { "hat", mappedId, hatTuple.x, hatTuple.y });
                                    await _oscSender.SendMessageAsync(msg);
                                    LogScroll($"[{customAddr}] hat {mappedId}: {hatTuple.x}, {hatTuple.y}");
                                }
                            }
                        }
                    }

                    // Keyboard
                    foreach (var kvp in _keyConfigs)
                    {
                        int idx = kvp.Key;
                        var cfg = kvp.Value;
                        if (string.IsNullOrWhiteSpace(cfg.KeyName)) continue;

                        if (Enum.TryParse(cfg.KeyName, true, out Key wpfKey))
                        {
                            int vKey = KeyInterop.VirtualKeyFromKey(wpfKey);
                            bool isPressed = (GetAsyncKeyState(vKey) & 0x8000) != 0;

                            if (!_prevKeys.ContainsKey(idx) || _prevKeys[idx] != isPressed)
                            {
                                _prevKeys[idx] = isPressed;
                                if (_oscSender != null)
                                {
                                    string customAddr = !string.IsNullOrWhiteSpace(cfg.CustomAddress) ? cfg.CustomAddress : _baseAddr;
                                    int val = isPressed ? 1 : 0;
                                    var msg = new OscMessage(new Address(customAddr), new object[] { "keyboard", cfg.OscId, val });
                                    await _oscSender.SendMessageAsync(msg);
                                    LogScroll($"[{customAddr}] keyboard {cfg.OscId} (Key: {cfg.KeyName}): {val}");
                                }
                            }
                        }
                    }

                    await Task.Delay(10, token); 
                }
            }
            catch (Exception ex)
            {
                Dispatcher.InvokeAsync(() => 
                {
                    LogArea.AppendText($"\n--- HARDWARE POLLING CRASH ---\n{ex.Message}\n");
                    StopStreaming();
                });
            }
        }

        // --- Motion Safety Loop ---
        private async Task MotionSafetyLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (_isRunning && _motionSender != null)
                {
                    bool motionEnabled = false;
                    bool safetyEnabled = false;
                    int timeout = 500;

                    Dispatcher.Invoke(() => {
                        motionEnabled = ChkMotionEnable.IsChecked == true;
                        safetyEnabled = ChkSafetyEnable.IsChecked == true;
                        int.TryParse(TxtSafetyTimeout.Text, out timeout);
                    });

                    if (motionEnabled && safetyEnabled && !_hasZeroedMotion)
                    {
                        if ((DateTime.UtcNow - _lastMotionReceiveTime).TotalMilliseconds > timeout)
                        {
                            _hasZeroedMotion = true;
                            
                            string protocol = "";
                            Dispatcher.Invoke(() => protocol = ((ComboBoxItem)CmbMotionProtocol.SelectedItem)?.Content?.ToString() ?? "");
                            string[] axes = { "pitch", "roll", "yaw", "surge", "sway", "heave" };

                            foreach (var axis in axes)
                            {
                                string fAddr = $"/motion/{axis}";
                                _motionEmaHistory[fAddr] = 0f;
                                _motionWashoutPrevInput[fAddr] = 0f;
                                _motionWashoutPrevOutput[fAddr] = 0f;

                                if (protocol == "SimTools String")
                                {
                                    string payload = $"<{axis}>0.00</{axis}>";
                                    byte[] bytes = Encoding.UTF8.GetBytes(payload);
                                    await _motionSender.SendAsync(bytes, bytes.Length);
                                }
                                else
                                {
                                    var msg = new OscMessage(new Address(fAddr), new object[] { 0f });
                                    await _motionSender.SendMessageAsync(msg);
                                }
                            }
                            LogScroll($"[MOTION SAFETY] No OSC data received for {timeout}ms. Axes zeroed.");
                        }
                    }
                }
                await Task.Delay(100, token);
            }
        }

        // --- OSC Listener Loop (FFB & Motion Forwarding) ---
        private async Task OscListenerLoop(CancellationToken token)
        {
            string listenIpStr = "127.0.0.1";
            int listenPort = 4042;
            
            // Safely get UI values before starting the background loop
            Dispatcher.Invoke(() => {
                listenIpStr = ListenIpEntry.Text;
                int.TryParse(ListenPortEntry.Text, out listenPort);
            });

            try
            {
                if (!System.Net.IPAddress.TryParse(listenIpStr, out System.Net.IPAddress listenIp)) 
                    listenIp = System.Net.IPAddress.Any;

                _oscListener = new UdpClient(new System.Net.IPEndPoint(listenIp, listenPort));

                while (!token.IsCancellationRequested)
                {
                    try 
                    {
                        var msg = await _oscListener.ReceiveMessageAsync();
                        string addr = msg.Address.Value;
                        var args = msg.Arguments.ToArray();

                        // --- Queue log if enabled ---
                        if (_isIncomingLoggingEnabled)
                        {
                            string logVal = (args.Length > 0 && args[0] != null) ? args[0].ToString() : "null";
                            
                            if (_isIncomingScrollMode)
                            {
                                _incomingLogQueue.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {addr} : {logVal}");
                                if (_incomingLogQueue.Count > 1000) _incomingLogQueue.TryDequeue(out _);
                            }
                            else
                            {
                                _latestIncomingOsc[addr] = logVal;
                            }
                        }

                        // Gather enabled toggles once per tick to avoid bottlenecking
                        bool isMotionEnabled = false;
                        bool isFfbEnabled = false;
                        Dispatcher.Invoke(() => {
                            isMotionEnabled = ChkMotionEnable.IsChecked == true;
                            isFfbEnabled = ChkFfbEnable.IsChecked == true;
                        });

                        // --- Motion Platform Forwarding ---
                        if (addr.Contains("/motion") && isMotionEnabled && _motionSender != null)
                        {
                            _lastMotionReceiveTime = DateTime.UtcNow;
                            _hasZeroedMotion = false;

                            if (_motionKilled) continue;

                            if (args.Length > 0 && args[0] is float rawVal)
                            {
                                string axisName = addr.Split('/').Last().ToLower();
                                
                                var prof = _appConfig.Profiles[_currentProfileName];
                                if (!prof.MotionAxes.TryGetValue(axisName, out var axisConfig))
                                {
                                    axisConfig = new MotionAxisConfig(); 
                                }

                                // 1. Apply Per-Axis Gain
                                float scaledVal = rawVal * axisConfig.Gain;
                                
                                // 2. Apply Per-Axis EMA Smooth
                                if (!_motionEmaHistory.ContainsKey(addr)) _motionEmaHistory[addr] = scaledVal;
                                else
                                {
                                    float alpha = 1.0f - axisConfig.Smooth;
                                    _motionEmaHistory[addr] = (alpha * scaledVal) + ((1.0f - alpha) * _motionEmaHistory[addr]);
                                }
                                float smoothedVal = _motionEmaHistory[addr];

                                // 3. Apply Per-Axis High-Pass Washout Filter
                                float finalOutputVal = smoothedVal;

                                if (axisConfig.WashoutEnabled)
                                {
                                    if (!_motionWashoutPrevInput.ContainsKey(addr))
                                    {
                                        _motionWashoutPrevInput[addr] = smoothedVal;
                                        _motionWashoutPrevOutput[addr] = smoothedVal;
                                    }

                                    float alphaW = axisConfig.WashoutStrength;
                                    finalOutputVal = alphaW * (_motionWashoutPrevOutput[addr] + smoothedVal - _motionWashoutPrevInput[addr]);
                                    
                                    _motionWashoutPrevInput[addr] = smoothedVal;
                                    _motionWashoutPrevOutput[addr] = finalOutputVal;
                                }

                                // Update the EMA history preview dictionary with the final value for the UI progress bars
                                _motionEmaHistory[addr] = finalOutputVal; 

                                // Protocol Translation
                                string protocol = (string)Dispatcher.Invoke(() => ((ComboBoxItem)CmbMotionProtocol.SelectedItem).Content);
                                
                                if (protocol == "SimTools String")
                                {
                                    string payload = $"<{axisName}>{finalOutputVal:0.00}</{axisName}>";
                                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                                    await _motionSender.SendAsync(bytes, bytes.Length);
                                }
                                else 
                                {
                                    // Raw OSC Forward
                                    var motionMsg = new OscMessage(new Address(addr), new object[] { finalOutputVal });
                                    await _motionSender.SendMessageAsync(motionMsg);
                                }
                            }
                        }

                        // --- Force Feedback Processing ---
                        if (isFfbEnabled && addr.StartsWith("/ffb/"))
                        {
                            float val = 0f;

                            if (args.Length > 0 && args[0] is float parsedFloat)
                            {
                                val = parsedFloat;
                            }
                            
                            float gainMultiplier = (float)Dispatcher.Invoke(() => SliderFfbGain.Value);
                            float scaledVal = val * gainMultiplier;

                            // FFB Clipping Monitor Trigger
                            if (scaledVal > 100f)
                            {
                                Dispatcher.InvokeAsync(() => FfbClipIndicator.Fill = Brushes.Red);
                                Task.Delay(100).ContinueWith(_ => Dispatcher.InvokeAsync(() => FfbClipIndicator.Fill = _bgDark));
                            }

                            scaledVal = Math.Clamp(scaledVal, 0, 100);

                            switch (addr)
                            {
                                case "/ffb/force":
                                    ApplyHapticForce(scaledVal, SDL.SDL_HAPTIC_CONSTANT);
                                    break;
                                case "/ffb/spring":
                                    ApplyHapticCondition(scaledVal, SDL.SDL_HAPTIC_SPRING);
                                    break;
                                case "/ffb/damper":
                                    ApplyHapticCondition(scaledVal, SDL.SDL_HAPTIC_DAMPER);
                                    break;
                                case "/ffb/friction":
                                    ApplyHapticCondition(scaledVal, SDL.SDL_HAPTIC_FRICTION);
                                    break;
                                case "/ffb/rumble":
                                    ApplyHapticRumble(scaledVal);
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogScroll($"[OSC IN ERROR] {ex.Message}");
                    }
                }
            }
            catch (Exception e) when (!(e is ObjectDisposedException))
            {
                LogScroll($"[LISTENER ERROR] {e.Message}");
            }
        }

        // --- Hardware Effect Routing Methods ---

        private unsafe void ApplyHapticForce(float strengthPercentage, ushort effectType)
        {
            short scaledForce = (short)((strengthPercentage / 100f) * 32767);

            foreach (var dev in _activeDevices)
            {
                if (dev.Haptic != IntPtr.Zero)
                {
                    SDL.SDL_HapticEffect effect = new SDL.SDL_HapticEffect();
                    effect.type = effectType;
                    effect.constant.direction.type = SDL.SDL_HAPTIC_CARTESIAN;
                    effect.constant.direction.dir[0] = 0; 
                    effect.constant.length = SDL.SDL_HAPTIC_INFINITY;
                    effect.constant.level = scaledForce;

                    ProcessEffect(dev, effectType, ref effect);
                }
            }
        }

        private unsafe void ApplyHapticCondition(float strengthPercentage, ushort effectType)
        {
            ushort level = (ushort)((strengthPercentage / 100f) * 32767);

            foreach (var dev in _activeDevices)
            {
                if (dev.Haptic != IntPtr.Zero)
                {
                    SDL.SDL_HapticEffect effect = new SDL.SDL_HapticEffect();
                    effect.type = effectType;
                    effect.condition.length = SDL.SDL_HAPTIC_INFINITY;

                    // Condition effects (Spring/Damper/Friction) utilize these coefficients across axes
                    effect.condition.right_coeff[0] = (short)level;
                    effect.condition.left_coeff[0] = (short)level;
                    effect.condition.right_sat[0] = 0xFFFF;
                    effect.condition.left_sat[0] = 0xFFFF;

                    ProcessEffect(dev, effectType, ref effect);
                }
            }
        }

        private void ProcessEffect(DeviceInfo dev, ushort effectType, ref SDL.SDL_HapticEffect effect)
        {
            if (!dev.EffectIds.ContainsKey(effectType))
            {
                int newId = SDL.SDL_HapticNewEffect(dev.Haptic, ref effect);
                if (newId >= 0)
                {
                    dev.EffectIds[effectType] = newId;
                    SDL.SDL_HapticRunEffect(dev.Haptic, newId, 1);
                }
            }
            else
            {
                int existingId = dev.EffectIds[effectType];
                SDL.SDL_HapticUpdateEffect(dev.Haptic, existingId, ref effect);
                SDL.SDL_HapticRunEffect(dev.Haptic, existingId, 1);
            }
        }

        private void ApplyHapticRumble(float strengthPercentage)
        {
            ushort scaledForce = (ushort)((strengthPercentage / 100f) * 65535);

            foreach (var dev in _activeDevices)
            {
                if (dev.IsRumbleInitialized)
                {
                    SDL.SDL_HapticRumblePlay(dev.Haptic, (float)(strengthPercentage / 100f), (uint)100);
                }
            }
        }

        // --- Motion Kill Switch ---

        private async void BtnMotionKill_Click(object sender, RoutedEventArgs e)
        {
            _motionKilled = !_motionKilled;
            
            if (_motionKilled)
            {
                BtnMotionKill.Background = Brushes.Gray;
                BtnMotionKill.Content = "MOTION KILLED (CLICK TO RESUME)";
                
                // Send zeroed telemetry to stop rig movement
                if (_motionSender != null)
                {
                    string protocol = ((ComboBoxItem)CmbMotionProtocol.SelectedItem)?.Content?.ToString() ?? "";
                    string[] axes = { "pitch", "roll", "yaw", "surge", "sway", "heave" };
                    
                    foreach(var axis in axes) 
                    {
                        if (protocol == "SimTools String")
                        {
                            string payload = $"<{axis}>0.00</{axis}>";
                            byte[] bytes = Encoding.UTF8.GetBytes(payload);
                            await _motionSender.SendAsync(bytes, bytes.Length); 
                        }
                        else
                        {
                            var motionMsg = new OscMessage(new Address($"/motion/{axis}"), new object[] { 0f });
                            await _motionSender.SendMessageAsync(motionMsg);
                        }
                    }
                }
            }
            else
            {
                BtnMotionKill.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B30000"));
                BtnMotionKill.Content = "MOTION KILL SWITCH";
            }
        }

        // --- Axis Processing ---

        private float GetAxisValue(int axisIndex, float rawValue)
        {
            if (!_axisConfigs.ContainsKey(axisIndex)) return rawValue;

            var config = _axisConfigs[axisIndex];
            float val = 0.0f;

            if (Math.Abs(rawValue) >= config.Deadzone)
            {
                float sign = rawValue > 0 ? 1.0f : -1.0f;
                if (config.Deadzone >= 1.0f) val = 0.0f;
                else val = sign * ((Math.Abs(rawValue) - config.Deadzone) / (1.0f - config.Deadzone));
            }

            float valSign = val >= 0 ? 1.0f : -1.0f;
            val = valSign * (float)Math.Pow(Math.Abs(val), config.Curve);
            val *= config.Sensitivity;
            if (config.IsInverted) val = -val;

            val = Math.Max(-1.0f, Math.Min(1.0f, val));

            if (!_axisEmaHistory.ContainsKey(axisIndex)) _axisEmaHistory[axisIndex] = val;
            else
            {
                float alpha = 1.0f - config.Smooth;
                _axisEmaHistory[axisIndex] = (alpha * val) + ((1.0f - alpha) * _axisEmaHistory[axisIndex]);
            }

            return (float)Math.Round(_axisEmaHistory[axisIndex], 3);
        }

        private (int x, int y) ParseHatState(byte hatVal)
        {
            int x = 0, y = 0;
            if ((hatVal & SDL.SDL_HAT_UP) != 0) y = 1;
            else if ((hatVal & SDL.SDL_HAT_DOWN) != 0) y = -1;
            
            if ((hatVal & SDL.SDL_HAT_RIGHT) != 0) x = 1;
            else if ((hatVal & SDL.SDL_HAT_LEFT) != 0) x = -1;
            return (x, y);
        }

        // --- UI Updates & Stream Controls ---

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            foreach (var kvp in _prevAxes)
                if (_axisPreviewBars.TryGetValue(kvp.Key, out var bar)) bar.Value = kvp.Value;

            foreach (var kvp in _prevButtons)
                if (_buttonPreviewDots.TryGetValue(kvp.Key, out var dot)) dot.Fill = kvp.Value == 1 ? Brushes.LimeGreen : Brushes.White;

            // Update Motion Previews
            if (_motionEmaHistory.Count > 0)
            {
                if (_motionEmaHistory.TryGetValue("/motion/pitch", out float p)) BarPitch.Value = p;
                if (_motionEmaHistory.TryGetValue("/motion/roll", out float r)) BarRoll.Value = r;
                if (_motionEmaHistory.TryGetValue("/motion/yaw", out float y)) BarYaw.Value = y;
                if (_motionEmaHistory.TryGetValue("/motion/surge", out float su)) BarSurge.Value = su;
                if (_motionEmaHistory.TryGetValue("/motion/sway", out float sw)) BarSway.Value = sw;
                if (_motionEmaHistory.TryGetValue("/motion/heave", out float h)) BarHeave.Value = h;
            }

            if (_isRunning && !_isScrollMode) 
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("--- STREAMING ACTIVE (IN-PLACE DASHBOARD) ---");
                sb.AppendLine($"Devices Connected: {_activeDevices.Count}");
                sb.AppendLine("---------------------------------------------");

                foreach (var kvp in _prevAxes.OrderBy(k => k.Key))
                    sb.AppendLine($"Axis {kvp.Key}: {kvp.Value:0.000}");

                foreach (var kvp in _prevButtons.OrderBy(k => k.Key))
                    sb.AppendLine($"Btn  {kvp.Key}: {kvp.Value}");

                foreach (var kvp in _prevHats.OrderBy(k => k.Key))
                    sb.AppendLine($"Hat  {kvp.Key}: {kvp.Value.x}, {kvp.Value.y}");

                foreach (var kvp in _prevKeys.Where(k => _keyConfigs.ContainsKey(k.Key) && !string.IsNullOrWhiteSpace(_keyConfigs[k.Key].KeyName)))
                    sb.AppendLine($"Key  '{_keyConfigs[kvp.Key].KeyName}': {(kvp.Value ? 1 : 0)}");

                LogArea.Text = sb.ToString();
            }

            if (_isIncomingLoggingEnabled)
            {
                if (_isIncomingScrollMode)
                {
                    if (!_incomingLogQueue.IsEmpty)
                    {
                        var sb = new StringBuilder();
                        while (_incomingLogQueue.TryDequeue(out string logLine))
                        {
                            sb.AppendLine(logLine);
                        }
                        IncomingLogArea.AppendText(sb.ToString());
                        IncomingLogArea.ScrollToEnd();
                    }
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("--- INCOMING OSC DASHBOARD ---");
                    sb.AppendLine($"Last Update: {DateTime.Now:HH:mm:ss.fff}");
                    sb.AppendLine("------------------------------");
                    
                    foreach (var kvp in _latestIncomingOsc.OrderBy(k => k.Key))
                    {
                        sb.AppendLine($"{kvp.Key} : {kvp.Value}");
                    }
                    
                    IncomingLogArea.Text = sb.ToString();
                }
            }
        }

        private void LogScroll(string message)
        {
            if (_isScrollMode) 
            {
                Dispatcher.InvokeAsync(() =>
                {
                    LogArea.AppendText(message + Environment.NewLine);
                    LogArea.ScrollToEnd();
                });
            }
        }

        private void ChkLogIncoming_Checked(object sender, RoutedEventArgs e) => _isIncomingLoggingEnabled = true;
        
        private void ChkLogIncoming_Unchecked(object sender, RoutedEventArgs e)
        {
            _isIncomingLoggingEnabled = false;
            _incomingLogQueue.Clear();
            _latestIncomingOsc.Clear();
        }

        private void IncomingOutputStyle_Changed(object sender, RoutedEventArgs e)
        {
            if (RadioIncomingScroll != null) _isIncomingScrollMode = RadioIncomingScroll.IsChecked == true;
            if (IncomingLogArea != null) IncomingLogArea.Clear();
        }

        private void BtnClearIncomingLog_Click(object sender, RoutedEventArgs e)
        {
            IncomingLogArea.Clear();
            _incomingLogQueue.Clear();
            _latestIncomingOsc.Clear();
        }

        private void OutputStyle_Changed(object sender, RoutedEventArgs e)
        {
            if (RadioScroll != null) _isScrollMode = RadioScroll.IsChecked == true;
            if (LogArea != null) LogArea.Clear();
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogArea.Clear();
        }

        // Dynamic FFB Test Button Click Handler
        private async void BtnTestFfb_Click(object sender, RoutedEventArgs e)
        {
            if (!_activeDevices.Any(d => d.Haptic != IntPtr.Zero))
            {
                MessageBox.Show("No devices with Haptic Force Feedback support are currently connected/active.");
                return;
            }

            BtnTestFfb.IsEnabled = false;
            BtnTestFfb.Content = "Rumbling...";

            float strength = (float)SliderTestStrength.Value;
            int durationMs = (int)SliderTestDuration.Value;
            int pulses = (int)SliderTestPulses.Value;
            int pauseMs = 150; 

            for (int i = 0; i < pulses; i++)
            {
                ApplyHapticForce(strength, SDL.SDL_HAPTIC_CONSTANT);
                await Task.Delay(durationMs);
                ApplyHapticForce(0f, SDL.SDL_HAPTIC_CONSTANT);
                
                if (i < pulses - 1)
                {
                    await Task.Delay(pauseMs);
                }
            }

            BtnTestFfb.Content = "Execute FFB Test";
            BtnTestFfb.IsEnabled = true;
        }

        private void BtnToggleStream_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning) StartStreaming();
            else StopStreaming();
        }

        private void StartStreaming()
        {
            string ip = TargetIpEntry.Text;
            if (!int.TryParse(TargetPortEntry.Text, out int port))
            {
                MessageBox.Show("Port must be an integer.");
                return;
            }

            _baseAddr = BaseAddrEntry.Text;
            _oscSender = new UdpClient(ip, port);
            
            if (ChkMotionEnable.IsChecked == true && int.TryParse(MotionPortEntry.Text, out int mPort))
            {
                _motionSender = new UdpClient(MotionIpEntry.Text, mPort);
            }

            _isRunning = true;
            _lastMotionReceiveTime = DateTime.UtcNow;
            _hasZeroedMotion = false;
            
            BtnToggleStream.Content = "Stop Streaming";
            BtnToggleStream.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC3545"));
            
            if (_iconOn != null)
            {
                this.Icon = _iconOn;
                StatusIcon.Source = _iconOn;
            }
            
            LogArea.Clear();
            LogScroll("--- STARTED STREAMING ---");

            _pollingCancellationToken = new CancellationTokenSource();
            Task.Run(() => HardwarePollingLoop(_pollingCancellationToken.Token));
            Task.Run(() => OscListenerLoop(_pollingCancellationToken.Token));
            Task.Run(() => MotionSafetyLoop(_pollingCancellationToken.Token));
        }

        private void StopStreaming()
        {
            _isRunning = false;
            _pollingCancellationToken?.Cancel();

            if (_oscSender != null)
            {
                _oscSender.Close();
                _oscSender.Dispose();
            }
            if (_oscListener != null)
            {
                _oscListener.Close();
                _oscListener.Dispose();
            }
            if (_motionSender != null)
            {
                _motionSender.Close();
                _motionSender.Dispose();
            }

            BtnToggleStream.Content = "Start Streaming";
            BtnToggleStream.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745"));
            
            if (_iconOff != null)
            {
                this.Icon = _iconOff;
                StatusIcon.Source = _iconOff;
            }
            
            LogScroll("--- STOPPED STREAMING ---");
        }

        // --- Hardware Generation UI ---

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshDevices();
        }

        private void RefreshDevices()
        {
            SDL.SDL_JoystickUpdate();
            int joystickCount = SDL.SDL_NumJoysticks();
            DeviceDropdown.Items.Clear();

            if (joystickCount == 0)
            {
                DeviceDropdown.Items.Add("No devices found");
                DeviceDropdown.SelectedIndex = 0;
            }
            else
            {
                for (int i = 0; i < joystickCount; i++)
                {
                    string name = SDL.SDL_JoystickNameForIndex(i);
                    DeviceDropdown.Items.Add($"[{i}] {name}");
                }
                DeviceDropdown.SelectedIndex = 0;
            }
        }

        private void BtnClearDevices_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                MessageBox.Show("Please stop streaming before clearing devices.");
                return;
            }

            foreach (var device in _activeDevices)
            {
                if (device.Haptic != IntPtr.Zero) SDL.SDL_HapticClose(device.Haptic);
                if (device.Joystick != IntPtr.Zero) SDL.SDL_JoystickClose(device.Joystick);
            }

            _activeDevices.Clear();
            DynamicSettingsPanel.Children.Clear();
            _axisEmaHistory.Clear();
            _prevAxes.Clear();
            _prevButtons.Clear();
            _prevHats.Clear();
            _axisPreviewBars.Clear();
            _buttonPreviewDots.Clear();
        }

        private void BtnAddDevice_Click(object sender, RoutedEventArgs e)
        {
            if (DeviceDropdown.SelectedIndex < 0 || DeviceDropdown.SelectedItem.ToString()!.Contains("No devices")) return;

            int selectedIndex = DeviceDropdown.SelectedIndex;
            if (_activeDevices.Any(d => d.Name == SDL.SDL_JoystickNameForIndex(selectedIndex))) return;

            IntPtr joy = SDL.SDL_JoystickOpen(selectedIndex);
            if (joy == IntPtr.Zero) return;

            IntPtr haptic = SDL.SDL_HapticOpenFromJoystick(joy);
            bool rumbleInit = false;

            if (haptic != IntPtr.Zero && SDL.SDL_HapticRumbleSupported(haptic) != 0)
            {
                if (SDL.SDL_HapticRumbleInit(haptic) == 0) rumbleInit = true;
            }

            int axOff = _activeDevices.Sum(d => d.NumAxes);
            int btnOff = _activeDevices.Sum(d => d.NumButtons);
            int hatOff = _activeDevices.Sum(d => d.NumHats);

            var newDevice = new DeviceInfo {
                Joystick = joy,
                Haptic = haptic,
                IsRumbleInitialized = rumbleInit,
                Name = SDL.SDL_JoystickName(joy),
                NumAxes = SDL.SDL_JoystickNumAxes(joy),
                NumButtons = SDL.SDL_JoystickNumButtons(joy),
                NumHats = SDL.SDL_JoystickNumHats(joy),
                AxisOffset = axOff,
                ButtonOffset = btnOff,
                HatOffset = hatOff
            };

            _activeDevices.Add(newDevice);
            BuildDeviceUI(newDevice);
        }

        private void BuildDeviceUI(DeviceInfo dev)
        {
            var deviceGroup = new Expander {
                Header = new TextBlock { Text = $"Device: {dev.Name}", Foreground = _labelColor, FontSize = 18, FontWeight = FontWeights.Bold },
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 10, 0, 15),
                Padding = new Thickness(15),
                IsExpanded = true
            };
            var mainStack = new StackPanel();
            deviceGroup.Content = mainStack;

            if (dev.Haptic != IntPtr.Zero)
            {
                string featureText = dev.IsRumbleInitialized ? "✅ Haptic FFB + Rumble Supported" : "✅ Haptic Force Feedback Supported";
                mainStack.Children.Add(new TextBlock { Text = featureText, Foreground = Brushes.LimeGreen, FontWeight = FontWeights.Bold, Margin = new Thickness(5, 0, 0, 10) });
            }

            if (dev.NumAxes > 0)
            {
                var axesGroup = new Expander { 
                    Header = new TextBlock { Text = "Axis Configuration", Foreground = _labelColor, FontSize = 16, FontWeight = FontWeights.Bold }, 
                    BorderThickness = new Thickness(0), 
                    Margin = new Thickness(0,10,0,10), 
                    Padding = new Thickness(5,10,0,0),
                    IsExpanded = true
                };
                var axesStack = new StackPanel { Margin = new Thickness(5) };
                for (int i = 0; i < dev.NumAxes; i++)
                {
                    int globalIdx = dev.AxisOffset + i;
                    if (!_axisConfigs.ContainsKey(globalIdx))
                        _axisConfigs[globalIdx] = new AxisConfig { OscId = globalIdx, CustomName = $"Axis {globalIdx}" };
                    axesStack.Children.Add(CreateAxisUI(globalIdx));
                }
                axesGroup.Content = axesStack;
                mainStack.Children.Add(axesGroup);
            }

            if (dev.NumButtons > 0)
            {
                var buttonsGroup = new Expander { 
                    Header = new TextBlock { Text = "Button Mapping", Foreground = _labelColor, FontSize = 16, FontWeight = FontWeights.Bold }, 
                    BorderThickness = new Thickness(0), 
                    Margin = new Thickness(0,0,0,10), 
                    Padding = new Thickness(5,10,0,0),
                    IsExpanded = true
                };
                var buttonsWrap = new WrapPanel { Margin = new Thickness(5) };
                for (int i = 0; i < dev.NumButtons; i++)
                {
                    int globalIdx = dev.ButtonOffset + i;
                    if (!_buttonConfigs.ContainsKey(globalIdx))
                        _buttonConfigs[globalIdx] = new ButtonConfig { CustomName = $"Btn {globalIdx}", OscId = globalIdx };
                    buttonsWrap.Children.Add(CreateButtonUI(globalIdx));
                }
                buttonsGroup.Content = buttonsWrap;
                mainStack.Children.Add(buttonsGroup);
            }

            if (dev.NumHats > 0)
            {
                var hatsGroup = new Expander { 
                    Header = new TextBlock { Text = "D-Pad / Hat Mapping", Foreground = _labelColor, FontSize = 16, FontWeight = FontWeights.Bold }, 
                    BorderThickness = new Thickness(0), 
                    Padding = new Thickness(5,10,0,0),
                    IsExpanded = true
                };
                var hatsWrap = new WrapPanel { Margin = new Thickness(5) };
                for (int i = 0; i < dev.NumHats; i++)
                {
                    int globalIdx = dev.HatOffset + i;
                    if (!_hatConfigs.ContainsKey(globalIdx))
                        _hatConfigs[globalIdx] = new HatConfig { OscId = globalIdx };
                    hatsWrap.Children.Add(CreateHatUI(globalIdx));
                }
                hatsGroup.Content = hatsWrap;
                mainStack.Children.Add(hatsGroup);
            }

            DynamicSettingsPanel.Children.Add(deviceGroup);
        }

        private void BuildKeyboardUI()
        {
            for (int i = 0; i < 12; i++)
            {
                if (!_keyConfigs.ContainsKey(i))
                    _keyConfigs[i] = new KeyConfig { OscId = i };
                
                var containerBorder = new Border {
                    Background = _bgDark,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10),
                    Margin = new Thickness(5)
                };
                
                var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                containerBorder.Child = panel;
                
                panel.Children.Add(new TextBlock { Text = $"Slot {i+1} Key:", Foreground = _labelColor, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,5,0) });
                var keyBox = new TextBox { Text = _keyConfigs[i].KeyName, Width = 60, Margin = new Thickness(0,0,15,0), Background = _inputBoxColor, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(2) };
                
                int localI = i; 
                keyBox.TextChanged += (s, e) => _keyConfigs[localI].KeyName = keyBox.Text;
                panel.Children.Add(keyBox);

                panel.Children.Add(new TextBlock { Text = "ID:", Foreground = _labelColor, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,5,0) });
                var idBox = new TextBox { Text = _keyConfigs[i].OscId.ToString(), Width = 30, Margin = new Thickness(0,0,15,0), Background = _inputBoxColor, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(2) };
                idBox.TextChanged += (s, e) => { if (int.TryParse(idBox.Text, out int parsed)) _keyConfigs[localI].OscId = parsed; };
                panel.Children.Add(idBox);

                panel.Children.Add(new TextBlock { Text = "Addr:", Foreground = _labelColor, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,5,0) });
                var addrBox = new TextBox { Text = _keyConfigs[i].CustomAddress, Width = 100, Background = _inputBoxColor, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(2) };
                addrBox.TextChanged += (s, e) => _keyConfigs[localI].CustomAddress = addrBox.Text;
                panel.Children.Add(addrBox);

                KeyboardSettingsPanel.Children.Add(containerBorder);
            }
        }

        private UIElement CreateAxisUI(int index)
        {
            var axisBorder = new Border {
                Background = _bgDark,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 0, 0, 15)
            };

            var axisPanel = new StackPanel();
            axisBorder.Child = axisPanel;
            
            var topRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,10) };
            
            var activeChk = new CheckBox { IsChecked = _axisConfigs[index].IsActive, Content = "Active", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,15,0) };
            activeChk.Checked += (s,e) => _axisConfigs[index].IsActive = true;
            activeChk.Unchecked += (s,e) => _axisConfigs[index].IsActive = false;
            topRow.Children.Add(activeChk);

            var nameBox = new TextBox { Text = _axisConfigs[index].CustomName, Width = 120, Margin = new Thickness(0,0,15,0), Background = _inputBoxColor, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(3) };
            nameBox.TextChanged += (s, e) => _axisConfigs[index].CustomName = nameBox.Text;
            topRow.Children.Add(nameBox);

            topRow.Children.Add(new TextBlock { Text = "ID:", Foreground = _labelColor, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,5,0), FontWeight = FontWeights.Bold });
            var idBox = new TextBox { Text = _axisConfigs[index].OscId.ToString(), Width = 40, Margin = new Thickness(0,0,15,0), Background = _inputBoxColor, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(3) };
            idBox.TextChanged += (s, e) => { if (int.TryParse(idBox.Text, out int parsed)) _axisConfigs[index].OscId = parsed; };
            topRow.Children.Add(idBox);

            topRow.Children.Add(new TextBlock { Text = "Addr:", Foreground = _labelColor, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,5,0), FontWeight = FontWeights.Bold });
            var addrBox = new TextBox { Text = _axisConfigs[index].CustomAddress, Width = 150, Margin = new Thickness(0,0,15,0), Background = _inputBoxColor, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(3) };
            addrBox.TextChanged += (s, e) => _axisConfigs[index].CustomAddress = addrBox.Text;
            topRow.Children.Add(addrBox);
            
            var invertChk = new CheckBox { IsChecked = _axisConfigs[index].IsInverted, Content = "Invert", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            invertChk.Checked += (s,e) => _axisConfigs[index].IsInverted = true;
            invertChk.Unchecked += (s,e) => _axisConfigs[index].IsInverted = false;
            topRow.Children.Add(invertChk);

            axisPanel.Children.Add(topRow);

            var previewBar = new ProgressBar { Minimum = -1, Maximum = 1, Value = 0, Height = 8, Margin = new Thickness(0, 0, 0, 15), BorderThickness = new Thickness(0) };
            _axisPreviewBars[index] = previewBar;
            axisPanel.Children.Add(previewBar);

            var controlsPanel = new WrapPanel { Orientation = Orientation.Horizontal };

            StackPanel BuildSlider(string label, double min, double max, double startValue, Action<float> onUpdate)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 20, 10) };
                var text = new TextBlock { Text = $"{label}: {startValue:0.00}", Width = 90, Foreground = _labelColor, VerticalAlignment = VerticalAlignment.Center };
                var slider = new Slider { Minimum = min, Maximum = max, Value = startValue, Width = 120, TickFrequency = 0.01, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
                slider.ValueChanged += (s, e) => { text.Text = $"{label}: {e.NewValue:0.00}"; onUpdate((float)e.NewValue); };
                panel.Children.Add(text);
                panel.Children.Add(slider);
                return panel;
            }

            controlsPanel.Children.Add(BuildSlider("Deadzone", 0.0, 0.5, _axisConfigs[index].Deadzone, val => _axisConfigs[index].Deadzone = val));
            controlsPanel.Children.Add(BuildSlider("Sens", 0.1, 5.0, _axisConfigs[index].Sensitivity, val => _axisConfigs[index].Sensitivity = val));
            controlsPanel.Children.Add(BuildSlider("Curve", 0.1, 5.0, _axisConfigs[index].Curve, val => _axisConfigs[index].Curve = val));
            controlsPanel.Children.Add(BuildSlider("Smooth", 0.0, 0.99, _axisConfigs[index].Smooth, val => _axisConfigs[index].Smooth = val));

            axisPanel.Children.Add(controlsPanel);
            return axisBorder;
        }

        private UIElement CreateButtonUI(int index)
        {
            var containerBorder = new Border {
                Background = _bgDark,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
                Margin = new Thickness(5)
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            containerBorder.Child = panel;
            
            var dot = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = Brushes.White, Margin = new Thickness(0,0,10,0), VerticalAlignment = VerticalAlignment.Center };
            _buttonPreviewDots[index] = dot;
            panel.Children.Add(dot);

            var nameBox = new TextBox { Text = _buttonConfigs[index].CustomName, Width = 80, Margin = new Thickness(0,0,15,0), Background = _inputBoxColor, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(2) };
            nameBox.TextChanged += (s, e) => _buttonConfigs[index].CustomName = nameBox.Text;
            panel.Children.Add(nameBox);

            panel.Children.Add(new TextBlock { Text = "ID:", Foreground = _labelColor, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,5,0) });
            var idBox = new TextBox { Text = _buttonConfigs[index].OscId.ToString(), Width = 30, Margin = new Thickness(0,0,15,0), Background = _inputBoxColor, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(2) };
            idBox.TextChanged += (s, e) => { if (int.TryParse(idBox.Text, out int parsed)) _buttonConfigs[index].OscId = parsed; };
            panel.Children.Add(idBox);

            panel.Children.Add(new TextBlock { Text = "Addr:", Foreground = _labelColor, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,5,0) });
            var addrBox = new TextBox { Text = _buttonConfigs[index].CustomAddress, Width = 100, Background = _inputBoxColor, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(2) };
            addrBox.TextChanged += (s, e) => _buttonConfigs[index].CustomAddress = addrBox.Text;
            panel.Children.Add(addrBox);

            return containerBorder;
        }

        private UIElement CreateHatUI(int index)
        {
            var containerBorder = new Border {
                Background = _bgDark,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
                Margin = new Thickness(5)
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            containerBorder.Child = panel;
            
            panel.Children.Add(new TextBlock { Text = $"Hat {index}  ->  ID:", Foreground = _labelColor, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,5,0) });
            
            var idBox = new TextBox { Text = _hatConfigs[index].OscId.ToString(), Width = 30, Margin = new Thickness(0,0,15,0), Background = _inputBoxColor, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(2) };
            idBox.TextChanged += (s, e) => { if (int.TryParse(idBox.Text, out int parsed)) _hatConfigs[index].OscId = parsed; };
            panel.Children.Add(idBox);

            panel.Children.Add(new TextBlock { Text = "Addr:", Foreground = _labelColor, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,5,0) });
            var addrBox = new TextBox { Text = _hatConfigs[index].CustomAddress, Width = 100, Background = _inputBoxColor, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(2) };
            addrBox.TextChanged += (s, e) => _hatConfigs[index].CustomAddress = addrBox.Text;
            panel.Children.Add(addrBox);

            return containerBorder;
        }
    }
}