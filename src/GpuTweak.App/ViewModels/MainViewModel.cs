using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GpuTweak.Core;
using GpuTweak.Core.Backends;
using GpuTweak.Core.Models;
using GpuTweak.Core.Profiles;
using GpuTweak.Core.Safety;
using GpuTweak.Core.Settings;

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
    private AppSettings _settings;
    private TuningSettings _active;
    private string? _activeProfileName;

    public MainViewModel(IGpuBackend backend, string dataDirectory, CrashReport? crash)
    {
        _backend = backend;
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
        foreach (var s in CommunityProfileDb.Load(dataDirectory).ForGpu(Info)) Suggestions.Add(s);

        ApplyCommand = new RelayCommand(Apply, () => !IsConfirmPending);
        ResetCommand = new RelayCommand(ResetToStock, () => !IsConfirmPending);
        ConfirmCommand = new RelayCommand(() => _applyGuard.Confirm(), () => IsConfirmPending);
        RevertCommand = new RelayCommand(() => _applyGuard.Revert(), () => IsConfirmPending);
        QuickModeCommand = new RelayCommand(p => ApplyQuickMode(p as string), _ => !IsConfirmPending);
        SaveProfileCommand = new RelayCommand(SaveProfile, () => !string.IsNullOrWhiteSpace(NewProfileName));
        LoadProfileCommand = new RelayCommand(p => { if (p is UserProfile u) { LoadIntoEditor(u.Settings); _activeProfileName = u.Name; Status = $"\"{u.Name}\" yüklendi. Uygula'ya basınca karta yazılır."; } });
        DeleteProfileCommand = new RelayCommand(p => { if (p is UserProfile u) { _profileStore.Delete(u.Name); Profiles.Remove(u); } });
        ToggleStableCommand = new RelayCommand(p => { if (p is UserProfile u) UpdateProfile(u, u with { IsStable = !u.IsStable, AutoApplyOnStart = u.AutoApplyOnStart && !u.IsStable }); });
        ToggleAutoApplyCommand = new RelayCommand(p => ToggleAutoApply(p as UserProfile));
        UseSuggestionCommand = new RelayCommand(p => UseSuggestion(p as CommunityProfile));
        OpenSourceCommand = new RelayCommand(p => { if (p is CommunityProfile c) OpenUrl(c.SourceUrl); });

        _telemetry = new TelemetryService(backend, TimeSpan.FromMilliseconds(_settings.PollIntervalMs));
        _telemetry.Sample += s => _dispatcher.BeginInvoke(() => OnSample(s));

        if (crash is not null)
        {
            Status = "Önceki oturum özel ayarlar açıkken beklenmedik şekilde kapandı. Güvenlik için varsayılan ayarlarla başlatıldı.";
            StatusIsWarning = true;
        }
        else if (_profileStore.GetStartupProfile(Info.Name, crash) is { } startup)
        {
            ApplyDirect(startup.Settings, startup.Name);
            LoadIntoEditor(startup.Settings);
            Status = $"Stabil profil \"{startup.Name}\" otomatik uygulandı.";
        }
    }

    public GpuInfo Info { get; }
    public TuningLimits Limits { get; }
    public OverlayViewModel Overlay { get; }
    public string VendorName => Info.Vendor switch { GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", GpuVendor.Intel => "Intel", _ => "GPU" };
    public string Subtitle => $"Sürücü {Info.DriverVersion} · {Info.VramBytes / (1024 * 1024 * 1024.0):0} GB VRAM";
    public bool IsCustomActive => !_active.IsStock;
    public string ActiveStateText => _active.IsStock ? "Varsayılan ayarlar" : _activeProfileName is { } n ? $"Aktif: {n}" : "Özel ayarlar aktif";

    // ---- Live metrics ----
    private string _temperature = "–", _coreClock = "–", _memoryClock = "–", _power = "–", _vram = "–", _fan = "–", _load = "–";
    private double _temperatureLevel, _powerLevel, _vramLevel, _loadLevel;
    public string Temperature { get => _temperature; private set => Set(ref _temperature, value); }
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
    public int CoreOffset { get => _coreOffset; set => Set(ref _coreOffset, value); }
    public int MemoryOffset { get => _memoryOffset; set => Set(ref _memoryOffset, value); }
    public int PowerLimit { get => _powerLimit; set { if (Set(ref _powerLimit, value)) OnPropertyChanged(nameof(PowerPercentText)); } }
    public string PowerPercentText => Limits.PowerDefaultW > 0 ? $"%{100.0 * PowerLimit / Limits.PowerDefaultW:0}" : "";
    public bool UseClockCap { get => _useClockCap; set => Set(ref _useClockCap, value); }
    public int MaxClock { get => _maxClock; set => Set(ref _maxClock, value); }
    public bool ManualFan { get => _manualFan; set => Set(ref _manualFan, value); }
    public int FanPercent { get => _fanPercent; set => Set(ref _fanPercent, value); }
    public bool SupportsFanControl => Limits.SupportsFanControl;
    public bool SupportsOffsets => Limits.CoreOffsetMaxMHz > Limits.CoreOffsetMinMHz;

    // ---- Confirmation ----
    private bool _isConfirmPending;
    private int _countdown;
    public bool IsConfirmPending { get => _isConfirmPending; private set => Set(ref _isConfirmPending, value); }
    public int Countdown { get => _countdown; private set => Set(ref _countdown, value); }

    private string? _status;
    private bool _statusIsWarning;
    public string? Status { get => _status; set => Set(ref _status, value); }
    public bool StatusIsWarning { get => _statusIsWarning; set => Set(ref _statusIsWarning, value); }

    // ---- Profiles ----
    public ObservableCollection<UserProfile> Profiles { get; } = [];
    public ObservableCollection<CommunityProfile> Suggestions { get; } = [];
    public bool HasSuggestions => Suggestions.Count > 0;
    private string _newProfileName = "";
    public string NewProfileName { get => _newProfileName; set => Set(ref _newProfileName, value); }

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
                ? "Bazı değerler güvenli aralığa çekildi: " + string.Join("; ", result.Adjustments)
                : "Ayarlar uygulandı. Ekran düzgünse 15 saniye içinde \"Koru\"ya bas, yoksa otomatik geri alınır.";
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
                Status = "Ayarlar korundu. Bir süre oyunda test edip stabilse profili \"stabil\" işaretleyebilirsin.";
                StatusIsWarning = false;
                break;
            case ApplyOutcome.RevertedByTimeout:
                SetActive(settings, null);
                LoadIntoEditor(settings);
                Status = "Onay gelmedi, önceki ayarlara otomatik dönüldü.";
                StatusIsWarning = true;
                break;
            case ApplyOutcome.RevertedByUser:
                SetActive(settings, null);
                LoadIntoEditor(settings);
                Status = "Önceki ayarlara dönüldü.";
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
            Status = "Varsayılan ayarlara dönüldü.";
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
        _activeProfileName = mode switch { "quiet" => "Sessiz", "max" => "Performans", _ => null };
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
        Status = $"\"{name}\" kaydedildi. Test edip stabilse işaretle; sadece stabil profiller açılışta otomatik uygulanabilir.";
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
            Status = "Açılışta otomatik uygulama için önce profili stabil olarak işaretlemelisin.";
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
        Status = $"\"{suggestion.Title}\" yarı güçte yüklendi (offset'ler %50). Uygula, test et, sonra adım adım artır.";
        StatusIsWarning = false;
    }

    private void OnSample(GpuTelemetry s)
    {
        Temperature = s.TemperatureC is { } t ? $"{t:0}°" : "–";
        TemperatureLevel = s.TemperatureC is { } t2 ? Math.Clamp(t2 / 100.0, 0, 1) : 0;
        CoreClock = s.CoreClockMHz is { } c ? $"{c}" : "–";
        MemoryClock = s.MemoryClockMHz is { } m ? $"{m}" : "–";
        Power = s.PowerW is { } p ? $"{p:0}" : "–";
        PowerLevel = s.PowerW is { } p2 && s.PowerLimitW is > 0 ? Math.Clamp(p2 / s.PowerLimitW.Value, 0, 1) : 0;
        Vram = s.VramUsedBytes is { } u ? $"{u / (1024.0 * 1024 * 1024):0.0}" : "–";
        VramLevel = s.VramUsedBytes is { } u2 && s.VramTotalBytes is > 0 ? (double)u2 / s.VramTotalBytes.Value : 0;
        Fan = s.FanPercent is { } f ? $"{f}" : "–";
        Load = s.LoadPercent is { } l ? $"{l}" : "–";
        LoadLevel = (s.LoadPercent ?? 0) / 100.0;
        Overlay.Update(s);

        if (_thermalGuard.ShouldRevert(s, IsCustomActive || IsConfirmPending))
        {
            if (IsConfirmPending) _applyGuard.Revert();
            ResetToStock();
            Status = $"GPU {_thermalGuard.LimitC:0}°C üstüne çıktı. Güvenlik için varsayılan ayarlara dönüldü.";
            StatusIsWarning = true;
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
        _applyGuard.Dispose();
        // Leaving with an unconfirmed apply means nobody approved it.
        if (IsConfirmPending)
        {
            try { _backend.ResetToStock(); _crashGuard.MarkStock(); } catch (GpuBackendException) { }
        }
        _crashGuard.EndSession();
        _backend.Dispose();
    }
}
