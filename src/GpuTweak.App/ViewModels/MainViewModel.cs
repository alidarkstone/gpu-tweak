using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GpuTweak.App.Native;
using GpuTweak.App.Stress;
using GpuTweak.Core;
using GpuTweak.Core.AutoTune;
using GpuTweak.Core.Backends;
using GpuTweak.Core.Localization;
using GpuTweak.Core.Models;
using GpuTweak.Core.Profiles;
using GpuTweak.Core.Safety;
using GpuTweak.Core.Settings;
using GpuTweak.Core.Stability;

namespace GpuTweak.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IGpuBackend _backend;
    private readonly ApplyGuard _applyGuard;
    private readonly CrashGuard _crashGuard;
    private readonly ThermalGuard _thermalGuard;
    private readonly ProfileStore _profileStore;
    private readonly SettingsStore _settingsStore;
    private readonly TelemetryService _telemetry;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly string _dataDirectory;
    private readonly StabilityTracker _stability;
    private readonly GameSessionDetector _sessions = new();
    private readonly DispatcherTimer _stabilityTimer;
    private readonly FrameRateTracker _frameRates = new();
    private readonly PresentEventSource _presents;
    private CancellationTokenSource? _tuneCts;
    private AppSettings _settings;
    private TuningSettings _active;
    private string? _activeProfileName;

    public MainViewModel(IGpuBackend backend, string dataDirectory, CrashReport? crash)
    {
        _backend = backend;
        _dataDirectory = dataDirectory;
        _crashGuard = new CrashGuard(dataDirectory);
        _profileStore = new ProfileStore(dataDirectory);
        _settingsStore = new SettingsStore(dataDirectory);
        _settings = _settingsStore.Load();
        _thermalGuard = new ThermalGuard(_settings.ThermalLimitC);
        _applyGuard = new ApplyGuard(backend);
        _applyGuard.CountdownTick += s => _dispatcher.BeginInvoke(() => Countdown = s);
        _applyGuard.Completed += (outcome, settings) => _dispatcher.BeginInvoke(() => OnApplyCompleted(outcome, settings));

        Info = backend.Info;
        Limits = backend.Limits;
        Overlay = new OverlayViewModel(Info, _settings.Overlay);
        Overlay.SettingsChanged += s => { _settings = _settings with { Overlay = s }; _settingsStore.Save(_settings); };

        if (crash is not null)
        {
            // The app (or the driver) died with custom settings on; if the card still has them, drop them.
            try { backend.ResetToStock(); } catch (GpuBackendException) { }
        }
        _active = backend.ReadCurrentSettings();
        LoadIntoEditor(_active);

        foreach (var p in _profileStore.Profiles.Where(p => p.GpuName == Info.Name)) Profiles.Add(p);
        Profiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasProfiles));
        foreach (var s in CommunityProfileDb.Load(dataDirectory).ForGpu(Info)) Suggestions.Add(s);

        ApplyCommand = new RelayCommand(Apply, () => !IsConfirmPending && !IsTuning);
        ResetCommand = new RelayCommand(ResetToStock, () => !IsConfirmPending && !IsTuning);
        ConfirmCommand = new RelayCommand(() => _applyGuard.Confirm(), () => IsConfirmPending);
        RevertCommand = new RelayCommand(() => _applyGuard.Revert(), () => IsConfirmPending);
        QuickModeCommand = new RelayCommand(p => ApplyQuickMode(p as string), _ => !IsConfirmPending && !IsTuning);
        StartTuneCommand = new RelayCommand(() => _ = StartTuneAsync(resume: false), () => !IsTuning && !IsConfirmPending);
        ResumeTuneCommand = new RelayCommand(() => _ = StartTuneAsync(resume: true), () => !IsTuning && PendingJournal is not null);
        StopTuneCommand = new RelayCommand(() => _tuneCts?.Cancel(), () => IsTuning);
        UndoStabilityCommand = new RelayCommand(UndoStabilityChange, () => !IsTuning && !IsConfirmPending);
        StartStressCommand = new RelayCommand(() => _ = StartStressAsync(), () => !IsTuning && !IsConfirmPending);
        SaveProfileCommand = new RelayCommand(SaveProfile, () => !string.IsNullOrWhiteSpace(NewProfileName));
        LoadProfileCommand = new RelayCommand(p =>
        {
            if (p is not UserProfile u) return;
            LoadIntoEditor(u.Settings);
            _activeProfileName = u.Name;
            Apply();
        }, _ => !IsConfirmPending && !IsTuning);
        DeleteProfileCommand = new RelayCommand(p => { if (p is UserProfile u) { _profileStore.Delete(u.Name); Profiles.Remove(u); } });
        ToggleStableCommand = new RelayCommand(p => { if (p is UserProfile u) UpdateProfile(u, u with { IsStable = !u.IsStable, AutoApplyOnStart = u.AutoApplyOnStart && !u.IsStable }); });
        ToggleAutoApplyCommand = new RelayCommand(p => ToggleAutoApply(p as UserProfile));
        UseSuggestionCommand = new RelayCommand(p => UseSuggestion(p as CommunityProfile));
        OpenSourceCommand = new RelayCommand(p => { if (p is CommunityProfile c) OpenUrl(c.SourceUrl); });
        OpenDataFolderCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dataDirectory}\"") { UseShellExecute = true }));

        PendingJournal = TuneJournal.Load(dataDirectory);
        if (PendingJournal is not null && crash is not null && PendingJournal.TestingOffsetMHz is { } crashedAt)
            TuneMessage = L.F("Otomatik ayar testi +{0} MHz'de bilgisayarı kilitledi. Bu değer kararsız sayıldı; kaldığı yerden devam edebilirsin.", crashedAt);
        else if (PendingJournal is not null)
            TuneMessage = L.T("Önceki otomatik ayar testi yarıda kaldı. Kaldığı yerden devam edebilirsin.");

        // Look at what happened since the last run before anything is applied: a crash or driver reset with the
        // previous settings steps them (and the profile that holds them) down before it is auto-applied again.
        _stability = new StabilityTracker(dataDirectory, new WindowsIncidentSource()) { AutoAdjust = _settings.AutoFixStability };
        if (PendingJournal is not null) _stability.SkipToNow(); // crashes during the auto-tune test are expected
        else HandleStabilityUpdates(_stability.Check());
        _stabilityTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => CheckStability(), _dispatcher);
        _stabilityTimer.Start();
        ReportArtifactCommand = new RelayCommand(ReportArtifact, () => !IsTuning);
        MarkStableCommand = new RelayCommand(MarkActiveStable, () => CanMarkStable);

        // Keep the startup task pointing at this exe, in case the user moved it or downloaded a newer version.
        if (_startWithWindows) StartupTask.Enable();

        _presents = new PresentEventSource(_frameRates);
        _telemetry = new TelemetryService(backend, TimeSpan.FromMilliseconds(_settings.PollIntervalMs));
        _telemetry.Sample += s => _dispatcher.BeginInvoke(() => OnSample(s));

        if (crash is not null)
        {
            Status = L.T("Önceki oturum özel ayarlar açıkken beklenmedik şekilde kapandı. Güvenlik için varsayılan ayarlarla başlatıldı.") + (Status is null ? "" : " " + Status);
            StatusIsWarning = true;
        }
        else if (_profileStore.GetStartupProfile(Info.Name, crash) is { } startup)
        {
            ApplyDirect(startup.Settings, startup.Name);
            LoadIntoEditor(startup.Settings);
            Status ??= L.F("Stabil profil \"{0}\" otomatik uygulandı.", startup.Name);
        }
        Stability.Update(_stability.State, null);
        RefreshSilicon();
    }

    public GpuInfo Info { get; }
    public TuningLimits Limits { get; }
    public OverlayViewModel Overlay { get; }
    public string VendorName => Info.Vendor switch { GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", GpuVendor.Intel => "Intel", _ => "GPU" };
    public string Subtitle => L.F("Sürücü {0} · {1:0} GB VRAM · Tweakstone v{2}", Info.DriverVersion, Info.VramBytes / (1024 * 1024 * 1024.0), AppVersion);
    private static string AppVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";
    public bool IsCustomActive => !_active.IsStock;
    public string ActiveStateText => _active.IsStock ? L.T("Varsayılan ayarlar") : _activeProfileName is { } n ? L.F("Aktif: {0}", n) : L.T("Özel ayarlar aktif");

    // ---- Live metrics ----
    private string _temperature = "–", _coreClock = "–", _memoryClock = "–", _power = "–", _vram = "–", _fan = "–", _load = "–";
    private double _temperatureLevel, _powerLevel, _vramLevel, _loadLevel;
    public string Temperature { get => _temperature; private set => Set(ref _temperature, value); }

    // GPU temperature since the app started (or since the user reset it).
    private readonly RunningStats _temperatureStats = new();
    private string _temperatureMin = "–", _temperatureAvg = "–", _temperatureMax = "–";
    public string TemperatureMin { get => _temperatureMin; private set => Set(ref _temperatureMin, value); }
    public string TemperatureAvg { get => _temperatureAvg; private set => Set(ref _temperatureAvg, value); }
    public string TemperatureMax { get => _temperatureMax; private set => Set(ref _temperatureMax, value); }
    private string? _memoryTemperature;
    /// <summary>VRAM temperature, or null when the card does not report it (the line is then hidden).</summary>
    public string? MemoryTemperature { get => _memoryTemperature; private set => Set(ref _memoryTemperature, value); }
    public ICommand ResetTemperatureStatsCommand => new RelayCommand(() => { _temperatureStats.Reset(); ShowTemperatureStats(); });

    private void ShowTemperatureStats()
    {
        TemperatureMin = _temperatureStats.Min is { } min ? $"{min:0}°" : "–";
        TemperatureAvg = _temperatureStats.Average is { } avg ? $"{avg:0}°" : "–";
        TemperatureMax = _temperatureStats.Max is { } max ? $"{max:0}°" : "–";
    }
    public string CoreClock { get => _coreClock; private set => Set(ref _coreClock, value); }
    public string MemoryClock { get => _memoryClock; private set => Set(ref _memoryClock, value); }
    public string Power { get => _power; private set => Set(ref _power, value); }
    public string Vram { get => _vram; private set => Set(ref _vram, value); }
    public string Fan { get => _fan; private set => Set(ref _fan, value); }
    public string Load { get => _load; private set => Set(ref _load, value); }
    public double TemperatureLevel { get => _temperatureLevel; private set => Set(ref _temperatureLevel, value); }
    public double PowerLevel { get => _powerLevel; private set => Set(ref _powerLevel, value); }
    public double VramLevel { get => _vramLevel; private set => Set(ref _vramLevel, value); }
    public double LoadLevel { get => _loadLevel; private set => Set(ref _loadLevel, value); }

    // ---- Editor ----
    private int _coreOffset, _memoryOffset, _powerLimit, _maxClock, _fanPercent;
    private bool _useClockCap, _manualFan;
    // Values can be typed in, so they are kept inside the card's limits here too (the backend clamps again on apply).
    public int CoreOffset { get => _coreOffset; set => SetClamped(ref _coreOffset, value, Limits.CoreOffsetMinMHz, Limits.CoreOffsetMaxMHz); }
    public int MemoryOffset { get => _memoryOffset; set => SetClamped(ref _memoryOffset, value, Limits.MemoryOffsetMinMHz, Limits.MemoryOffsetMaxMHz); }
    public int PowerLimit { get => _powerLimit; set { if (SetClamped(ref _powerLimit, value, Limits.PowerMinW, Limits.PowerMaxW)) OnPropertyChanged(nameof(PowerPercentText)); } }
    public string PowerPercentText => Limits.PowerDefaultW > 0 ? $"%{100.0 * PowerLimit / Limits.PowerDefaultW:0}" : "";
    public bool UseClockCap { get => _useClockCap; set => Set(ref _useClockCap, value); }
    public int MaxClock { get => _maxClock; set => SetClamped(ref _maxClock, value, Limits.CoreClockMinMHz, Limits.CoreClockMaxMHz); }
    public bool ManualFan { get => _manualFan; set => Set(ref _manualFan, value); }
    public int FanPercent { get => _fanPercent; set => SetClamped(ref _fanPercent, value, Limits.FanMinPercent, Limits.FanMaxPercent); }

    /// <summary>Like Set, but an out-of-range value is pulled back to the limit and the box shows the corrected number.</summary>
    private bool SetClamped(ref int field, int value, int min, int max, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var clamped = max >= min ? Math.Clamp(value, min, max) : value;
        if (Set(ref field, clamped, name)) return true;
        if (clamped != value) OnPropertyChanged(name);
        return false;
    }
    public bool SupportsFanControl => Limits.SupportsFanControl;
    public bool SupportsOffsets => Limits.CoreOffsetMaxMHz > Limits.CoreOffsetMinMHz;

    // ---- Confirmation ----
    private bool _isConfirmPending;
    private int _countdown;
    public bool IsConfirmPending { get => _isConfirmPending; private set => Set(ref _isConfirmPending, value); }
    public int Countdown { get => _countdown; private set => Set(ref _countdown, value); }

    private string? _status;
    private bool _statusIsWarning;
    private DispatcherTimer? _statusTimer;
    /// <summary>The toast at the bottom; it hides itself after a while so it never covers the live panel for long.</summary>
    public string? Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            _statusTimer?.Stop();
            if (value is null) return;
            _statusTimer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
            _statusTimer.Interval = TimeSpan.FromSeconds(StatusIsWarning ? 25 : 12);
            _statusTimer.Tick -= HideStatus;
            _statusTimer.Tick += HideStatus;
            _statusTimer.Start();
        }
    }

    private void HideStatus(object? sender, EventArgs e)
    {
        _statusTimer?.Stop();
        Status = null;
    }
    public bool StatusIsWarning { get => _statusIsWarning; set => Set(ref _statusIsWarning, value); }

    // ---- Profiles ----
    public ObservableCollection<UserProfile> Profiles { get; } = [];
    public bool HasProfiles => Profiles.Count > 0;
    public ObservableCollection<CommunityProfile> Suggestions { get; } = [];
    public bool HasSuggestions => Suggestions.Count > 0;
    private string _newProfileName = "";
    public string NewProfileName { get => _newProfileName; set => Set(ref _newProfileName, value); }

    // ---- Auto tune ----
    private bool _isTuning;
    private double _tuneProgress;
    private string? _tuneMessage;
    private TuneResult? _tuneResult;
    private TuneJournal? _pendingJournal;
    public bool IsTuning
    {
        get => _isTuning;
        private set
        {
            if (!Set(ref _isTuning, value)) return;
            OnPropertyChanged(nameof(MonitorTitle));
            OnPropertyChanged(nameof(CanChooseGoal));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public double TuneProgress { get => _tuneProgress; private set => Set(ref _tuneProgress, value); }
    public string? TuneMessage { get => _tuneMessage; private set => Set(ref _tuneMessage, value); }
    public TuneJournal? PendingJournal
    {
        get => _pendingJournal;
        private set
        {
            if (!Set(ref _pendingJournal, value)) return;
            OnPropertyChanged(nameof(HasPendingJournal));
            OnPropertyChanged(nameof(CanChooseGoal));
        }
    }
    public bool HasPendingJournal => PendingJournal is not null;
    public TuneResult? TuneResult
    {
        get => _tuneResult;
        private set
        {
            if (!Set(ref _tuneResult, value)) return;
            OnPropertyChanged(nameof(HasTuneResult));
            OnPropertyChanged(nameof(MonitorTitle));
        }
    }
    public bool HasTuneResult => TuneResult is { Success: true };
    public string TuneSummary => TuneResult is { Success: true } r
        ? L.F("Güç {0:0} W → {1:0} W   ·   Sıcaklık {2:0}° → {3:0}°   ·   Performans %{4:0}", r.BaselinePowerW, r.TunedPowerW, r.BaselineTemperatureC, r.TunedTemperatureC, 100 * r.TunedScore / r.BaselineScore)
        : "";

    public TuneMonitorViewModel Monitor { get; } = new();

    // ---- Auto-tune goal ----
    public TuneGoal Goal => _settings.TuneGoal;
    public bool IsGoalCool => Goal == TuneGoal.Cool;
    public bool IsGoalPerformance => Goal == TuneGoal.Performance;
    public bool IsGoalEfficient => Goal == TuneGoal.Efficient;
    public bool IsGoalMemory => Goal == TuneGoal.Memory;
    public bool CanChooseGoal => !IsTuning && PendingJournal is null;
    public string GoalDescription => Goal switch
    {
        TuneGoal.Performance => L.T("Overclock: saat kilidi yok, güç limiti kartın izin verdiği en yüksek değerde. Çekirdek hızı hatasız kaldığı yere kadar artırılır. FPS biraz artar, kart biraz daha ısınır."),
        TuneGoal.Efficient => L.T("Hız stok boost'un %6 altında kilitlenir ve voltaj düşürülür. FPS biraz düşer, güç ve ısı belirgin azalır."),
        TuneGoal.Memory => L.T("Şu anki çekirdek ayarı korunur, bellek hızı adım adım artırılır. Her değer kontrol edilir; hata çıkınca ya da bant genişliği artmayı bırakınca durur ve bir adım geri gelir. Yaklaşık 13 dakika sürer."),
        _ => L.T("Undervolt: hız stokta kalır, voltaj düşürülür. FPS aynı kalır; kart daha serin ve sessiz çalışır."),
    };
    public ICommand SetGoalCommand => new RelayCommand(p =>
    {
        if (!CanChooseGoal || !Enum.TryParse<TuneGoal>(p as string, out var goal) || goal == Goal) return;
        _settings = _settings with { TuneGoal = goal };
        _settingsStore.Save(_settings);
        foreach (var name in new[] { nameof(Goal), nameof(IsGoalCool), nameof(IsGoalPerformance), nameof(IsGoalEfficient), nameof(IsGoalMemory), nameof(GoalDescription) })
            OnPropertyChanged(name);
    });

    private static string ProfileNameFor(TuneGoal goal) => goal switch
    {
        TuneGoal.Performance => L.T("Otomatik OC"),
        TuneGoal.Efficient => L.T("Otomatik Verimli"),
        _ => L.T("Otomatik UV"),
    };

    // ---- Game stability ----
    public StabilityViewModel Stability { get; } = new();
    public ICommand ReportArtifactCommand { get; }
    public ICommand MarkStableCommand { get; }

    /// <summary>Step settings down by themselves after a driver reset / crash while gaming.</summary>
    public bool AutoFixStability
    {
        get => _settings.AutoFixStability;
        set
        {
            if (value == _settings.AutoFixStability) return;
            _settings = _settings with { AutoFixStability = value };
            _settingsStore.Save(_settings);
            _stability.AutoAdjust = value;
            OnPropertyChanged();
        }
    }

    private bool _canMarkStable;
    /// <summary>The settings on the card have enough clean gaming time and belong to a profile not yet marked stable.</summary>
    public bool CanMarkStable { get => _canMarkStable; private set => Set(ref _canMarkStable, value); }

    // ---- Silicon ----
    private string _siliconHeadroom = "–", _siliconText = L.T("Otomatik ayarı bir kez çalıştırınca çipinin ne kadar kaldırdığı burada görünür."), _siliconDaily = "–", _siliconGame = "–";
    private bool _hasSilicon;
    public bool HasSilicon { get => _hasSilicon; private set => Set(ref _hasSilicon, value); }
    public string SiliconHeadroom { get => _siliconHeadroom; private set => Set(ref _siliconHeadroom, value); }
    public string SiliconDaily { get => _siliconDaily; private set => Set(ref _siliconDaily, value); }
    public string SiliconGame { get => _siliconGame; private set => Set(ref _siliconGame, value); }
    public string SiliconText { get => _siliconText; private set => Set(ref _siliconText, value); }

    public string DataFolder => _dataDirectory;

    public bool IsTurkish => L.Language == "tr";
    public bool IsEnglish => L.Language == "en";
    public ICommand SetLanguageCommand => new RelayCommand(p => SetLanguage(p as string));

    private void SetLanguage(string? language)
    {
        if (language is null || language == L.Language) return;
        _settings = _settings with { Language = language };
        _settingsStore.Save(_settings);
        L.SetLanguage(language);
        // Texts built in code are re-created in the new language; messages already shown are dropped.
        Status = null;
        if (!IsTuning) TuneMessage = null;
        Stability.LastChange = "";
        Stability.Update(_stability.State, _sessions.CurrentGame);
        // Profile rows take their button texts when created; rebuild them.
        Profiles.Clear();
        foreach (var p in _profileStore.Profiles.Where(p => p.GpuName == Info.Name)) Profiles.Add(p);
        RefreshSilicon();
        OnPropertyChanged(string.Empty);
    }

    private bool _startWithWindows = StartupTask.IsEnabled();
    /// <summary>Starts the app at sign-in, so the profile marked stable + automatic is applied again after every reboot.</summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (value == _startWithWindows) return;
            var ok = value ? StartupTask.Enable() : StartupTask.Disable();
            if (ok) _startWithWindows = value;
            OnPropertyChanged();
            if (!ok)
            {
                Status = L.T("Windows başlangıç görevi ayarlanamadı.");
                StatusIsWarning = true;
            }
            else if (value && !_profileStore.Profiles.Any(p => p.IsStable && p.AutoApplyOnStart))
            {
                Status = L.T("Program artık Windows ile açılacak. Ayarının da uygulanması için bir profili \"Stabil\" ve \"Otomatik: açık\" yap.");
                StatusIsWarning = false;
            }
        }
    }

    /// <summary>Live panel at the bottom of the dashboard; on by default, remembered between runs.</summary>
    public bool IsMonitorVisible
    {
        get => _settings.ShowLivePanel;
        set
        {
            if (value == _settings.ShowLivePanel) return;
            _settings = _settings with { ShowLivePanel = value };
            _settingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }
    private bool _isStressTest;
    public string MonitorTitle => IsTuning && _isMemoryTune ? L.T("Bellek ayarı çalışıyor") : IsTuning && _isStressTest ? L.T("Doğrulama testi çalışıyor") : IsTuning ? L.T("Otomatik ayar çalışıyor") : TuneResult is { Success: true } ? L.T("Otomatik ayar bitti") : L.T("Otomatik ayar");

    public ICommand StartTuneCommand { get; }
    public ICommand ResumeTuneCommand { get; }
    public ICommand StopTuneCommand { get; }
    public ICommand UndoStabilityCommand { get; }
    public ICommand StartStressCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand QuickModeCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand LoadProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand ToggleStableCommand { get; }
    public ICommand ToggleAutoApplyCommand { get; }
    public ICommand UseSuggestionCommand { get; }
    public ICommand OpenSourceCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    private TuningSettings EditorSettings() => new(
        CoreOffset,
        MemoryOffset,
        PowerLimit == Limits.PowerDefaultW ? null : PowerLimit,
        UseClockCap ? MaxClock : null,
        ManualFan && SupportsFanControl ? FanPercent : null);

    private void LoadIntoEditor(TuningSettings s)
    {
        CoreOffset = s.CoreOffsetMHz;
        MemoryOffset = s.MemoryOffsetMHz;
        PowerLimit = s.PowerLimitW ?? Limits.PowerDefaultW;
        UseClockCap = s.MaxCoreClockMHz is not null;
        MaxClock = s.MaxCoreClockMHz ?? Limits.CoreClockMaxMHz;
        ManualFan = s.FanPercent is not null;
        FanPercent = s.FanPercent ?? Math.Max(Limits.FanMinPercent, 50);
    }

    private void Apply()
    {
        try
        {
            var result = _applyGuard.Apply(EditorSettings());
            IsConfirmPending = true;
            LoadIntoEditor(result.Settings);
            Status = result.WasAdjusted
                ? L.T("Bazı değerler güvenli aralığa çekildi: ") + string.Join("; ", result.Adjustments)
                : L.T("Ayarlar uygulandı. Ekran düzgünse 15 saniye içinde \"Koru\"ya bas, yoksa otomatik geri alınır.");
            StatusIsWarning = result.WasAdjusted;
            _crashGuard.MarkCustomApplied(_activeProfileName);
        }
        catch (GpuBackendException e)
        {
            Status = e.Message;
            StatusIsWarning = true;
        }
    }

    /// <summary>Used for startup auto-apply of a profile that the user already marked stable.</summary>
    private void ApplyDirect(TuningSettings settings, string? profileName)
    {
        try
        {
            var clamped = SafetyClamp.Clamp(settings, Limits).Settings;
            _backend.Apply(clamped);
            SetActive(clamped, profileName);
        }
        catch (GpuBackendException e)
        {
            Status = e.Message;
            StatusIsWarning = true;
        }
    }

    private void OnApplyCompleted(ApplyOutcome outcome, TuningSettings settings)
    {
        IsConfirmPending = false;
        CommandManager.InvalidateRequerySuggested();
        switch (outcome)
        {
            case ApplyOutcome.Confirmed:
                SetActive(settings, _activeProfileName);
                Status = L.T("Ayarlar korundu. Bir süre oyunda test edip stabilse profili \"stabil\" işaretleyebilirsin.");
                StatusIsWarning = false;
                break;
            case ApplyOutcome.RevertedByTimeout:
                SetActive(settings, null);
                LoadIntoEditor(settings);
                Status = L.T("Onay gelmedi, önceki ayarlara otomatik dönüldü.");
                StatusIsWarning = true;
                break;
            case ApplyOutcome.RevertedByUser:
                SetActive(settings, null);
                LoadIntoEditor(settings);
                Status = L.T("Önceki ayarlara dönüldü.");
                StatusIsWarning = false;
                break;
            case ApplyOutcome.Failed:
                SetActive(settings, null);
                break;
        }
    }

    private void SetActive(TuningSettings settings, string? profileName)
    {
        _active = settings;
        _activeProfileName = settings.IsStock ? null : profileName;
        if (settings.IsStock) _crashGuard.MarkStock();
        else _crashGuard.MarkCustomApplied(_activeProfileName);
        _stability.SettingsChanged(settings);
        Stability.Update(_stability.State, _sessions.CurrentGame);
        UpdateCanMarkStable();
        RefreshSilicon();
        OnPropertyChanged(nameof(IsCustomActive));
        OnPropertyChanged(nameof(ActiveStateText));
    }

    private void ResetToStock()
    {
        try
        {
            _backend.ResetToStock();
            SetActive(TuningSettings.Stock, null);
            LoadIntoEditor(TuningSettings.Stock);
            Status = L.T("Varsayılan ayarlara dönüldü.");
            StatusIsWarning = false;
        }
        catch (GpuBackendException e)
        {
            Status = e.Message;
            StatusIsWarning = true;
        }
    }

    /// <summary>
    /// Quick modes only touch the power limit, which is the one change that is safe on every card:
    /// lowering it makes the card cooler and quieter, raising it stays within the vBIOS maximum.
    /// </summary>
    private void ApplyQuickMode(string? mode)
    {
        _activeProfileName = mode switch { "quiet" => L.T("Sessiz"), "max" => L.T("Performans"), _ => null };
        LoadIntoEditor(TuningSettings.Stock);
        PowerLimit = mode switch
        {
            "quiet" => Math.Max(Limits.PowerMinW, (int)(Limits.PowerDefaultW * 0.8)),
            "max" => Limits.PowerMaxW,
            _ => Limits.PowerDefaultW,
        };
        if (mode == "balanced") ResetToStock();
        else Apply();
    }

    private void SaveProfile()
    {
        var name = NewProfileName.Trim();
        var profile = new UserProfile(name, Info.Name, SafetyClamp.Clamp(EditorSettings(), Limits).Settings, CreatedAt: DateTimeOffset.Now);
        _profileStore.Save(profile);
        var existing = Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) Profiles[Profiles.IndexOf(existing)] = profile;
        else Profiles.Add(profile);
        NewProfileName = "";
        Status = L.F("\"{0}\" kaydedildi. Test edip stabilse işaretle; sadece stabil profiller açılışta otomatik uygulanabilir.", name);
        StatusIsWarning = false;
    }

    private void UpdateProfile(UserProfile old, UserProfile updated)
    {
        _profileStore.Save(updated);
        Profiles.Clear();
        foreach (var p in _profileStore.Profiles.Where(p => p.GpuName == Info.Name)) Profiles.Add(p);
    }

    private void ToggleAutoApply(UserProfile? profile)
    {
        if (profile is null) return;
        if (!profile.IsStable && !profile.AutoApplyOnStart)
        {
            Status = L.T("Açılışta otomatik uygulama için önce profili stabil olarak işaretlemelisin.");
            StatusIsWarning = true;
            return;
        }
        UpdateProfile(profile, profile with { AutoApplyOnStart = !profile.AutoApplyOnStart });
    }

    private void UseSuggestion(CommunityProfile? suggestion)
    {
        if (suggestion is null) return;
        LoadIntoEditor(suggestion.ConservativeStart());
        _activeProfileName = suggestion.Title;
        Status = L.F("\"{0}\" yarı güçte yüklendi (offset'ler %50). Uygula, test et, sonra adım adım artır.", suggestion.Title);
        StatusIsWarning = false;
    }

    private void OnSample(GpuTelemetry s)
    {
        Temperature = s.TemperatureC is { } t ? $"{t:0}°" : "–";
        TemperatureLevel = s.TemperatureC is { } t2 ? Math.Clamp(t2 / 100.0, 0, 1) : 0;
        _temperatureStats.Add(s.TemperatureC);
        // Keep the line once the card has shown it can report memory temperature.
        MemoryTemperature = s.MemoryTemperatureC is { } mt ? $"{mt:0}°" : MemoryTemperature is null ? null : "–";
        ShowTemperatureStats();
        CoreClock = s.CoreClockMHz is { } c ? $"{c}" : "–";
        MemoryClock = s.MemoryClockMHz is { } m ? $"{m}" : "–";
        Power = s.PowerW is { } p ? $"{p:0}" : "–";
        TrayText = TrayLine(s);
        PowerLevel = s.PowerW is { } p2 && s.PowerLimitW is > 0 ? Math.Clamp(p2 / s.PowerLimitW.Value, 0, 1) : 0;
        Vram = s.VramUsedBytes is { } u ? $"{u / (1024.0 * 1024 * 1024):0.0}" : "–";
        VramLevel = s.VramUsedBytes is { } u2 && s.VramTotalBytes is > 0 ? (double)u2 / s.VramTotalBytes.Value : 0;
        Fan = s.FanPercent is { } f ? $"{f}" : "–";
        Load = s.LoadPercent is { } l ? $"{l}" : "–";
        LoadLevel = (s.LoadPercent ?? 0) / 100.0;
        Overlay.Update(s, _frameRates.Sample(ForegroundApp.ProcessId(), PresentEventSource.Seconds(DateTime.Now)));
        Monitor.OnSample(s);
        if (!IsTuning) FeedSession(s);
        _stability.Heartbeat(busy: IsTuning || (s.LoadPercent ?? 0) >= 50 || _sessions.CurrentGame is not null);

        // While tuning, the tuner watches temperature itself (with a lower limit) and resets between steps.
        if (!IsTuning && _thermalGuard.ShouldRevert(s, IsCustomActive || IsConfirmPending))
        {
            if (IsConfirmPending) _applyGuard.Revert();
            ResetToStock();
            Status = L.F("GPU {0:0}°C üstüne çıktı. Güvenlik için varsayılan ayarlara dönüldü.", _thermalGuard.LimitC);
            StatusIsWarning = true;
        }
    }

    private async Task StartTuneAsync(bool resume)
    {
        if (!resume && Goal == TuneGoal.Memory)
        {
            await StartMemoryTuneAsync();
            return;
        }
        var journal = resume ? PendingJournal : null;
        var goal = journal?.Goal ?? Goal;
        var profileName = ProfileNameFor(goal);
        if (!resume) TuneJournal.Delete(_dataDirectory);
        PendingJournal = null;
        TuneResult = null;
        IsTuning = true;
        _tuneCts = new CancellationTokenSource();
        // If the PC locks up mid-test, the next start must come up at stock and offer to resume.
        _crashGuard.MarkCustomApplied("Otomatik ayar testi");

        var progress = new Progress<TuneProgress>(p =>
        {
            TuneProgress = p.Fraction;
            TuneMessage = p.Message;
        });

        var steps = new Progress<TuneStep>(Monitor.AddStep);

        TuneResult result;
        try
        {
            using var stress = new D3D11StressTest();
            Monitor.Start(stress);
            IsMonitorVisible = true;
            var tuner = new AutoTuner(_backend, stress, _dataDirectory, progress: progress, steps: steps);
            result = await Task.Run(() => tuner.RunAsync(journal, _tuneCts.Token, goal));
        }
        catch (Exception e)
        {
            try { _backend.ResetToStock(); } catch (GpuBackendException) { }
            result = Core.AutoTune.TuneResult.Failed(e.Message);
        }
        finally
        {
            Monitor.Stop();
            IsTuning = false;
            _tuneCts.Dispose();
            _tuneCts = null;
        }

        TuneResult = result;
        OnPropertyChanged(nameof(TuneSummary));
        // The test may have reset the driver on purpose; that is not a gaming problem.
        _stability.SkipToNow();
        // The silicon score is the undervolt headroom at the stock clock, so only the "cool" goal measures it.
        if (result is { Success: true, Settings: { } tuned } && goal == TuneGoal.Cool)
        {
            _settings = _settings with { Silicon = new SiliconResult(Info.Name, result.HighestPassedOffsetMHz, result.ReachedSearchLimit, tuned.CoreOffsetMHz, DateTimeOffset.Now) };
            _settingsStore.Save(_settings);
        }
        if (result is { Success: true, Settings: { } settings })
        {
            ApplyDirect(settings, profileName);
            LoadIntoEditor(settings);
            var profile = new UserProfile(profileName, Info.Name, settings, CreatedAt: DateTimeOffset.Now);
            _profileStore.Save(profile);
            var existing = Profiles.FirstOrDefault(p => p.Name == profile.Name);
            if (existing is not null) Profiles[Profiles.IndexOf(existing)] = profile;
            else Profiles.Add(profile);
            TuneMessage = result.Message + L.F(" Uygulandı ve \"{0}\" profili olarak kaydedildi.", profileName);
            Status = L.T("Birkaç gün oyunlarda sorunsuz giderse profili \"Stabil\" işaretle; o zaman açılışta otomatik uygulanabilir.");
            StatusIsWarning = false;
        }
        else
        {
            SetActive(TuningSettings.Stock, null);
            LoadIntoEditor(TuningSettings.Stock);
            TuneMessage = result.Message;
        }
    }

    private bool _isMemoryTune;

    private async Task StartMemoryTuneAsync()
    {
        var original = _active;
        _isMemoryTune = true;
        TuneResult = null;
        OnPropertyChanged(nameof(TuneSummary));
        TuneMessage = null;
        IsTuning = true;
        _tuneCts = new CancellationTokenSource();
        _crashGuard.MarkCustomApplied("Bellek ayar testi");
        var progress = new Progress<TuneProgress>(p => { TuneProgress = p.Fraction; TuneMessage = p.Message; });

        MemoryTuneResult result;
        try
        {
            using var stress = new D3D11StressTest();
            Monitor.Start(stress);
            IsMonitorVisible = true;
            var tuner = new MemoryTuner(_backend, stress, progress: progress, steps: new Progress<TuneStep>(Monitor.AddStep));
            result = await Task.Run(() => tuner.RunAsync(original, _tuneCts.Token));
        }
        catch (Exception e)
        {
            try { _backend.Apply(original); } catch (GpuBackendException) { }
            result = MemoryTuneResult.Failed(e.Message);
        }
        finally
        {
            Monitor.Stop();
            IsTuning = false;
            _isMemoryTune = false;
            OnPropertyChanged(nameof(MonitorTitle));
            _tuneCts.Dispose();
            _tuneCts = null;
        }
        // Driver resets during the search are expected, not a gaming problem.
        _stability.SkipToNow();
        if (original.IsStock) _crashGuard.MarkStock();
        else _crashGuard.MarkCustomApplied(_activeProfileName);

        TuneMessage = result.Message;
        if (result is { Success: true, Settings: { } tuned } && tuned != original)
        {
            // Same as a step-down: the active profile (and any other holding the old settings) gets the memory offset too.
            if (_activeProfileName is not null && !original.IsStock) StepDown(new Adjustment(original, tuned, L.F("bellek +{0} MHz", tuned.MemoryOffsetMHz)));
            else ApplyDirect(tuned, null);
            LoadIntoEditor(tuned);
            Status = L.F("Bellek ayarı uygulandı: +{0} MHz. Oyunlarda doku bozulması görürsen \"Artifact gördüm\"e bas.", tuned.MemoryOffsetMHz);
            StatusIsWarning = false;
        }
    }

    private bool _checkingStability;

    private async void CheckStability()
    {
        if (IsTuning || _checkingStability) return;
        _checkingStability = true;
        try
        {
            var game = _sessions.CurrentGame;
            var updates = await Task.Run(() => _stability.Check(game));
            if (!IsTuning) HandleStabilityUpdates(updates);
            else _stability.SkipToNow();
        }
        catch (Exception) { /* the event log can be unavailable; try again next time */ }
        finally { _checkingStability = false; }
    }

    private void FeedSession(GpuTelemetry s)
    {
        var finished = _sessions.Feed(s, ForegroundApp.ProcessName(), _active);
        if (finished is not null && finished.Duration >= TimeSpan.FromMinutes(2))
        {
            _stability.AddSession(finished);
            if (StabilityAdvisor.ShouldSuggestStable(_stability.State) && ActiveProfile() is { IsStable: false } p)
            {
                Status = L.F("Bu ayarlar {0} oyunda sorunsuz çalıştı. \"{1}\" profilini \"Stabil\" işaretleyebilirsin.", Stability.CleanTime, p.Name);
                StatusIsWarning = false;
            }
        }
        if (finished is not null || Stability.CurrentGame != (_sessions.CurrentGame ?? L.T("Oyun yok")))
        {
            Stability.Update(_stability.State, _sessions.CurrentGame);
            UpdateCanMarkStable();
        }
    }

    private void ReportArtifact()
    {
        var game = _sessions.CurrentGame;
        _sessions.MarkIncident();
        HandleStabilityUpdates([_stability.ReportArtifact(game)]);
    }

    /// <summary>
    /// Applies the step-down for each new incident: the card gets the safer settings right away (if custom
    /// settings are on), and every saved profile that held the old settings is updated, so the next auto-apply
    /// uses the safer values too.
    /// </summary>
    private void HandleStabilityUpdates(IReadOnlyList<TrackerUpdate> updates)
    {
        foreach (var (incident, adjustment) in updates)
        {
            _sessions.MarkIncident();
            var where = incident.Game is { } g ? L.F("{0} oyununda", g) : L.T("Oyun sırasında");
            var what = incident.Kind switch
            {
                IncidentKind.DriverReset => L.T("ekran sürücüsü çökmesi"),
                IncidentKind.GameCrash => L.T("oyun çökmesi"),
                IncidentKind.SystemCrash => L.T("sistem çökmesi"),
                _ => L.T("görüntü bozulması (artifact)"),
            };
            if (incident.Kind == IncidentKind.SystemCrash) where = L.T("Bilgisayarda");

            if (incident.Ignored)
            {
                Status = L.T("Bilgisayar beklenmedik şekilde yeniden başladı, ama o sırada ekran kartı boştaydı (oyun ya da yük yoktu). Ayarlarla ilgisi olmadığı için hiçbir şey değiştirilmedi.");
                StatusIsWarning = false;
            }
            else if (adjustment is null)
            {
                Status = L.F("{0} {1} kaydedildi. Kart varsayılan ayarlardaydı, ayarlarla ilgisi yok.", where, what);
                StatusIsWarning = false;
            }
            else if (!_stability.AutoAdjust)
            {
                Status = L.F("{0} {1} algılandı. Öneri: {2}. (Otomatik düzeltme kapalı.)", where, what, adjustment.Description);
                StatusIsWarning = true;
            }
            else
            {
                StepDown(adjustment);
                Status = L.F("{0} {1} algılandı. Daha güvenli ayara geçildi: {2}.", where, what, adjustment.Description);
                StatusIsWarning = true;
                Stability.LastChange = $"{DateTime.Now:dd.MM HH:mm} · {adjustment.Description}";
            }
        }
        if (updates.Count > 0)
        {
            Stability.Update(_stability.State, _sessions.CurrentGame);
            UpdateCanMarkStable();
            RefreshSilicon();
        }
    }

    private void UndoStabilityChange()
    {
        if (_stability.UndoLastAdjustment() is not { } undo) return;
        Stability.LastChange = "";
        if (undo.From != undo.Settings)
        {
            StepDown(undo);
            Status = L.F("Önceki ayara dönüldü: çekirdek +{0} MHz, bellek +{1} MHz. Bu sorun ayarlara sayılmayacak.", undo.Settings.CoreOffsetMHz, undo.Settings.MemoryOffsetMHz);
        }
        else
        {
            // The user already changed the settings by hand; only the incident is dropped.
            Status = L.T("Bu sorun artık ayarlara sayılmayacak. Ayarların değiştirilmedi.");
        }
        StatusIsWarning = false;
        Stability.Update(_stability.State, _sessions.CurrentGame);
        UpdateCanMarkStable();
        RefreshSilicon();
    }

    private void StepDown(Adjustment adjustment)
    {
        // Profiles that hold the old settings get the new values, so the next auto-apply is the safer one.
        foreach (var profile in _profileStore.Profiles.Where(p => p.GpuName == Info.Name && p.Settings == adjustment.From).ToList())
            _profileStore.Save(profile with { Settings = adjustment.Settings });
        Profiles.Clear();
        foreach (var p in _profileStore.Profiles.Where(p => p.GpuName == Info.Name)) Profiles.Add(p);

        if (!_active.IsStock && !IsConfirmPending)
        {
            ApplyDirect(adjustment.Settings, _activeProfileName);
            LoadIntoEditor(adjustment.Settings);
        }
    }

    private string _trayText = "Tweakstone";
    /// <summary>Hover text of the notification-area icon, e.g. "Tweakstone · 57°C · 133 W · Profil: Otomatik UV".</summary>
    public string TrayText { get => _trayText; private set => Set(ref _trayText, value); }

    private string TrayLine(GpuTelemetry s)
    {
        var parts = new List<string> { "Tweakstone" };
        if (s.TemperatureC is { } t) parts.Add($"{t:0}°C");
        if (s.PowerW is { } w) parts.Add($"{w:0} W");
        if (_activeProfileName is { } name) parts.Add(L.F("Profil: {0}", name));
        var text = string.Join(" · ", parts);
        return text.Length <= 127 ? text : text[..126] + "…";  // NotifyIcon.Text limit
    }

    private UserProfile? ActiveProfile() =>
        _activeProfileName is { } name ? _profileStore.Profiles.FirstOrDefault(p => p.GpuName == Info.Name && p.Name == name) : null;

    private void UpdateCanMarkStable() =>
        CanMarkStable = StabilityAdvisor.ShouldSuggestStable(_stability.State) && ActiveProfile() is { IsStable: false };

    private void MarkActiveStable()
    {
        if (ActiveProfile() is not { } profile) return;
        UpdateProfile(profile, profile with { IsStable = true });
        UpdateCanMarkStable();
        Status = L.F("\"{0}\" stabil işaretlendi. Açılışta otomatik uygulamak için \"Otomatik\" düğmesini aç.", profile.Name);
        StatusIsWarning = false;
    }

    /// <summary>Shows what the auto-tune measured on this chip, and what survived real games since.</summary>
    private void RefreshSilicon()
    {
        if (_settings.Silicon is not { } si || si.GpuName != Info.Name)
        {
            HasSilicon = false;
            SiliconText = L.T("Otomatik ayarı bir kez çalıştırınca çipinin ne kadar kaldırdığı burada görünür.");
            return;
        }
        HasSilicon = true;
        SiliconHeadroom = $"+{si.HighestPassedOffsetMHz} MHz";
        SiliconDaily = $"+{si.AppliedOffsetMHz} MHz";
        var tracked = _stability.State.TrackedSettings;
        SiliconGame = !tracked.IsStock && tracked.MaxCoreClockMHz is not null && _stability.State.Sessions.Count > 0 ? $"+{tracked.CoreOffsetMHz} MHz" : "–";
        SiliconText = si.ReachedSearchLimit
            ? L.F("Bu çip testin üst sınırına kadar hatasız çalıştı; gerçek payı daha da yüksek olabilir. Ölçüm: {0:dd.MM.yyyy}.", si.TestedAt.LocalDateTime)
            : L.F("Bu çip, stok hızında +{0} MHz'e kadar hatasız çalıştı; üstünde hata verdi. Günlük kullanımda {1} MHz güvenlik payı bırakıldı. Ölçüm: {2:dd.MM.yyyy}.", si.HighestPassedOffsetMHz, si.HighestPassedOffsetMHz - si.AppliedOffsetMHz, si.TestedAt.LocalDateTime);
    }

    /// <summary>
    /// 15-minute burn-in with the settings that are on now. An error with custom settings puts the card back
    /// to stock right away, because those settings just proved unstable.
    /// </summary>
    private async Task StartStressAsync()
    {
        var settings = _active;
        _isStressTest = true;
        TuneResult = null;
        TuneMessage = null;
        IsTuning = true;
        _tuneCts = new CancellationTokenSource();
        if (!settings.IsStock) _crashGuard.MarkCustomApplied(_activeProfileName);
        var progress = new Progress<TuneProgress>(p => { TuneProgress = p.Fraction; TuneMessage = p.Message; });

        StressTestResult result;
        try
        {
            using var stress = new D3D11StressTest();
            Monitor.Start(stress);
            IsMonitorVisible = true;
            var runner = new StressTestRunner(_backend, stress, progress: progress, steps: new Progress<TuneStep>(Monitor.AddStep));
            result = await Task.Run(() => runner.RunAsync(settings, _tuneCts.Token));
        }
        catch (Exception e)
        {
            result = new StressTestResult(false, false, e.Message, TimeSpan.Zero, 0, 0, 0, 1);
        }
        finally
        {
            Monitor.Stop();
            IsTuning = false;
            _isStressTest = false;
            OnPropertyChanged(nameof(MonitorTitle));
            _tuneCts.Dispose();
            _tuneCts = null;
        }
        // A driver reset during the test is already reported here; it is not a gaming problem.
        _stability.SkipToNow();

        var summary = L.F("{0:0} dk · maks {1:0}° · ort. {2:0} W · ort. {3} MHz · en düşük performans %{4:0}",
            result.Elapsed.TotalMinutes, result.MaxTemperatureC, result.AvgPowerW, result.AvgCoreClockMHz, 100 * result.LowestRelativeScore);
        if (result.Passed && result.StepDowns > 0 && result.FinalSettings is { } final)
        {
            // Some point of the curve needed more voltage: keep the lowered offset, in the profile too.
            var description = L.F("çekirdek +{0} → +{1} MHz", settings.CoreOffsetMHz, final.CoreOffsetMHz);
            StepDown(new Adjustment(settings, final, description));
            TuneMessage = L.F("Doğrulama testi geçti, ama önce hata buldu; ayar bir miktar düşürüldü: {0}.", description) + " " + summary;
            Status = L.F("Ayar daha güvenli hale getirildi: {0}.", description);
            StatusIsWarning = false;
        }
        else if (result.Passed)
        {
            TuneMessage = L.T("Doğrulama testi geçti: hiç hata yok.") + " " + summary;
            Status = null;
        }
        else if (result.Cancelled)
        {
            TuneMessage = L.T("Doğrulama testi durduruldu.") + " " + summary;
        }
        else
        {
            TuneMessage = L.F("Doğrulama testi geçemedi: {0}.", result.Failure) + " " + summary;
            if (!settings.IsStock)
            {
                ResetToStock();
                Status = L.T("Bu ayarlar doğrulama testinde düşürülmelerine rağmen hata verdi, kart varsayılan ayarlara döndürüldü. Otomatik ayarı yeniden çalıştır.");
                StatusIsWarning = true;
            }
            else
            {
                Status = L.T("Kart varsayılan ayarlarda bile hata verdi. Sürücüyü, soğutmayı ve güç kablolarını kontrol et.");
                StatusIsWarning = true;
            }
        }
    }

    private static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void Dispose()
    {
        _telemetry.Dispose();
        _presents.Dispose();
        _stabilityTimer.Stop();
        if (_sessions.Flush() is { } session && session.Duration >= TimeSpan.FromMinutes(2)) _stability.AddSession(session);
        _applyGuard.Dispose();
        if (IsTuning)
        {
            _tuneCts?.Cancel();
            try { _backend.ResetToStock(); _crashGuard.MarkStock(); } catch (GpuBackendException) { }
        }
        // Leaving with an unconfirmed apply means nobody approved it.
        if (IsConfirmPending)
        {
            try { _backend.ResetToStock(); _crashGuard.MarkStock(); } catch (GpuBackendException) { }
        }
        _crashGuard.EndSession();
        _backend.Dispose();
    }
}
