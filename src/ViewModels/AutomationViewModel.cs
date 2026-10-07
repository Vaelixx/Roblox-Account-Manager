using System.Collections.ObjectModel;
using System.Globalization;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

/// <summary>An account's membership in a preset (a tick box in the editor).</summary>
public class PresetMember : ObservableObject
{
    private readonly Action _changed;
    public Account Account { get; }

    public PresetMember(Account account, bool included, Action changed)
    {
        Account = account;
        _isIncluded = included;
        _changed = changed;
    }

    private bool _isIncluded;
    public bool IsIncluded { get => _isIncluded; set { if (SetField(ref _isIncluded, value)) _changed(); } }
}

/// <summary>Editable wrapper around a <see cref="LaunchPreset"/>; every change is saved straight away.</summary>
public class PresetItem : ObservableObject
{
    private readonly AutomationViewModel _owner;
    public LaunchPreset Model { get; }

    public PresetItem(LaunchPreset model, AutomationViewModel owner)
    {
        Model = model;
        _owner = owner;
    }

    public string Name
    {
        get => Model.Name;
        set
        {
            string v = string.IsNullOrWhiteSpace(value) ? L.T("Automation.Preset.Untitled") : value.Trim();
            if (v == Model.Name) return;
            string old = Model.Name;
            Model.Name = v;
            _owner.OnPresetRenamed(old, v);
            OnPropertyChanged();
            _owner.Persist();
        }
    }

    public string PlaceIdText
    {
        get => Model.PlaceId > 0 ? Model.PlaceId.ToString() : "";
        set
        {
            string text = (value ?? "").Trim();
            if (JoinLinks.LooksLikeLink(text)) ApplyLink(text);
            else Model.PlaceId = JoinLinks.ParsePlaceId(text);
            RaiseDestination();
            _owner.Persist();
        }
    }

    /// <summary>
    /// A pasted link decides the destination the same way the launch bar reads it: a private-server
    /// or share link makes this a private-server preset, a link with a server id a specific-server one.
    /// </summary>
    private void ApplyLink(string link)
    {
        var parsed = JoinLinks.Parse(link);
        if (parsed.PlaceId > 0) Model.PlaceId = parsed.PlaceId;
        if (Model.Destination == JoinKind.FollowUser) return;

        if (parsed.LinkCode != null || parsed.ShareCode != null)
        {
            Model.Destination = JoinKind.PrivateServer;
            Model.PrivateServerLink = link;
        }
        else if (parsed.JobId != null)
        {
            Model.Destination = JoinKind.Server;
            Model.JobId = parsed.JobId;
        }
        else if (Model.Destination == JoinKind.PrivateServer)
        {
            Model.PrivateServerLink = link;   // kept so the hint can say why it isn't a private-server link
        }
    }

    /// <summary>Id of the saved place this preset uses instead of its own destination; "" = its own.</summary>
    public string SavedPlaceId
    {
        get => Model.SavedPlaceId;
        set
        {
            Model.SavedPlaceId = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(UsesSaved));
            OnPropertyChanged(nameof(Summary));
            _owner.Persist();
        }
    }

    public bool UsesSaved => Model.SavedPlaceId.Length > 0;

    private SavedPlace? Saved => UsesSaved ? SettingsService.Current.SavedPlaces.FirstOrDefault(p => p.Id == Model.SavedPlaceId) : null;

    /// <summary>"Place", "Server", "PrivateServer" or "FollowUser" — bound to the segmented picker.</summary>
    public string Destination
    {
        get => Model.Destination.ToString();
        set
        {
            if (!Enum.TryParse<JoinKind>(value, out var kind) || kind == Model.Destination) return;
            Model.Destination = kind;
            RaiseDestination();
            _owner.Persist();
        }
    }

    public bool ShowPlace => Model.Destination != JoinKind.FollowUser;
    public bool ShowTarget => Model.Destination != JoinKind.Place;

    public string TargetLabel => Model.Destination switch
    {
        JoinKind.PrivateServer => L.T("Automation.PrivateLink"),
        JoinKind.FollowUser => L.T("Automation.JoinUser"),
        _ => L.T("Automation.Server"),
    };

    /// <summary>The Job ID, the private-server link or the player, depending on <see cref="Destination"/>.</summary>
    public string TargetText
    {
        get => Model.Destination switch
        {
            JoinKind.Server => Model.JobId,
            JoinKind.PrivateServer => Model.PrivateServerLink,
            JoinKind.FollowUser => Model.FollowUsername,
            _ => "",
        };
        set
        {
            string text = (value ?? "").Trim();
            switch (Model.Destination)
            {
                case JoinKind.Server when JoinLinks.LooksLikeLink(text):
                case JoinKind.PrivateServer when text.Length > 0:
                    ApplyLink(text);
                    break;
                case JoinKind.PrivateServer:
                    Model.PrivateServerLink = "";
                    break;
                case JoinKind.Server:
                    Model.JobId = text;
                    break;
                case JoinKind.FollowUser:
                    string user = text.TrimStart('@');
                    if (user == Model.FollowUsername) return;
                    Model.FollowUsername = user;
                    Model.FollowUserId = 0;
                    _ = ResolveFollowAsync(user);
                    break;
                default:
                    return;
            }
            RaiseDestination();
            _owner.Persist();
        }
    }

    private string? _followHint;

    /// <summary>Looks the player up once while editing, so the preset launches by id and a typo shows now, not at 3 am.</summary>
    private async Task ResolveFollowAsync(string user)
    {
        if (user.Length == 0) { _followHint = null; RaiseDestination(); return; }
        _followHint = L.T("Follow.LookingUp", user);
        OnPropertyChanged(nameof(TargetHint));
        long id = await JoinTargetResolver.ResolveUserIdAsync(user);
        if (Model.FollowUsername != user) return;   // edited again meanwhile
        Model.FollowUserId = id;
        _followHint = id > 0 ? L.T("Automation.Preset.FollowFound", user, id) : L.T("Follow.NotFound", user);
        RaiseDestination();
        _owner.Persist();
    }

    /// <summary>Feedback under the destination box: what was understood, or why it won't work.</summary>
    public string TargetHint
    {
        get
        {
            switch (Model.Destination)
            {
                case JoinKind.Server:
                    return Model.JobId.Length > 0 && !JoinLinks.LooksLikeJobId(Model.JobId) ? L.T("Launch.BadJobId") : "";
                case JoinKind.PrivateServer:
                    if (Model.PrivateServerLink.Length == 0) return L.T("Automation.Preset.PasteLink");
                    var parsed = JoinLinks.Parse(Model.PrivateServerLink);
                    if (parsed.LinkCode == null && parsed.ShareCode == null) return L.T("Automation.Preset.NeedLink");
                    return parsed.LinkCode != null && Model.PlaceId <= 0 ? L.T("Launch.NeedPlace") : "";
                case JoinKind.FollowUser:
                    return _followHint ?? (Model.FollowUserId > 0 ? L.T("Automation.Preset.FollowFound", Model.FollowUsername, Model.FollowUserId) : "");
                default:
                    return "";
            }
        }
    }

    private void RaiseDestination()
    {
        OnPropertyChanged(nameof(PlaceIdText));
        OnPropertyChanged(nameof(Destination));
        OnPropertyChanged(nameof(ShowPlace));
        OnPropertyChanged(nameof(ShowTarget));
        OnPropertyChanged(nameof(TargetLabel));
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(TargetHint));
        OnPropertyChanged(nameof(Summary));
    }

    public int JoinDelaySeconds
    {
        get => Model.JoinDelaySeconds;
        set { Model.JoinDelaySeconds = Math.Clamp(value, 0, 600); OnPropertyChanged(); _owner.Persist(); }
    }

    public int RandomDelaySeconds
    {
        get => Model.RandomDelaySeconds;
        set { Model.RandomDelaySeconds = Math.Clamp(value, 0, 600); OnPropertyChanged(); _owner.Persist(); }
    }

    public IReadOnlyList<Choice> Profiles => new[]
    {
        new Choice(PerformanceProfiles.Normal, L.T("Profile.Normal")),
        new Choice(PerformanceProfiles.UltraLowAfk, L.T("Profile.UltraLowAfk")),
    };

    public string Profile
    {
        get => Model.PerformanceProfile;
        set { Model.PerformanceProfile = PerformanceProfiles.IsKnown(value) ? value ?? "" : ""; OnPropertyChanged(); _owner.Persist(); }
    }

    public ObservableCollection<PresetMember> Members { get; } = new();

    /// <summary>Rebuilds the tick list from the current accounts, keeping this preset's picks.</summary>
    public void SyncMembers(IEnumerable<Account> accounts)
    {
        Members.Clear();
        foreach (var a in accounts.OrderBy(a => a.DisplayNameOrUser, StringComparer.CurrentCultureIgnoreCase))
        {
            bool included = Model.Aliases.Any(k => Matches(a, k));
            Members.Add(new PresetMember(a, included, OnMembersChanged));
        }
        OnPropertyChanged(nameof(Summary));
    }

    private void OnMembersChanged()
    {
        // Usernames are what get stored: an alias can be renamed at any time, a username cannot.
        Model.Aliases = Members.Where(m => m.IsIncluded).Select(m => m.Account.Username).Where(u => u.Length > 0).ToList();
        OnPropertyChanged(nameof(Summary));
        _owner.Persist();
    }

    public static bool Matches(Account a, string key)
        => string.Equals(a.Username, key, StringComparison.OrdinalIgnoreCase)
        || (a.Alias.Length > 0 && string.Equals(a.Alias, key, StringComparison.OrdinalIgnoreCase));

    public string Summary => UsesSaved
        ? L.N("Automation.Preset.SummarySaved", Model.Aliases.Count, Saved?.Name ?? L.T("Automation.Saved.Gone"))
        : Model.Destination == JoinKind.FollowUser
        ? L.N("Automation.Preset.SummaryFollow", Model.Aliases.Count, Model.FollowUsername)
        : Model.PlaceId > 0
            ? L.N("Automation.Preset.Summary", Model.Aliases.Count, Model.PlaceId)
            : L.N("Automation.Preset.SummaryNoPlace", Model.Aliases.Count);

    public void RaiseAll() => OnPropertyChanged(string.Empty);
}

/// <summary>One weekday toggle in the schedule editor.</summary>
public class DayToggle : ObservableObject
{
    private readonly Action _changed;
    public DayOfWeek Day { get; }
    public string Label { get; }

    public DayToggle(DayOfWeek day, string label, bool on, Action changed)
    {
        Day = day;
        Label = label;
        _isOn = on;
        _changed = changed;
    }

    private bool _isOn;
    public bool IsOn { get => _isOn; set { if (SetField(ref _isOn, value)) _changed(); } }
}

/// <summary>Editable wrapper around a <see cref="ScheduledTask"/>.</summary>
public class ScheduleItem : ObservableObject
{
    private readonly AutomationViewModel _owner;
    public ScheduledTask Model { get; }

    public ScheduleItem(ScheduledTask model, AutomationViewModel owner)
    {
        Model = model;
        _owner = owner;
        BuildDays();
    }

    public string Name
    {
        get => Model.Name;
        set { Model.Name = string.IsNullOrWhiteSpace(value) ? L.T("Automation.Schedule.Untitled") : value.Trim(); OnPropertyChanged(); _owner.Persist(); }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set { Model.Enabled = value; RaiseSchedule(); _owner.Persist(); }
    }

    /// <summary>"Launch" or "Close".</summary>
    public string Action
    {
        get => Model.Action.ToString();
        set
        {
            Model.Action = value == "Close" ? ScheduleAction.Close : ScheduleAction.Launch;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLaunch));
            OnPropertyChanged(nameof(Summary));
            _owner.Persist();
        }
    }

    public bool IsLaunch => Model.Action == ScheduleAction.Launch;

    /// <summary>"Preset" or "Account".</summary>
    public string TargetKind
    {
        get => string.IsNullOrEmpty(Model.PresetName) && !_preferPreset ? "Account" : "Preset";
        set
        {
            _preferPreset = value == "Preset";
            if (_preferPreset) Model.Alias = "";
            else Model.PresetName = "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAccountTarget));
            OnPropertyChanged(nameof(PresetName));
            OnPropertyChanged(nameof(Account));
            OnPropertyChanged(nameof(Summary));
            _owner.Persist();
        }
    }

    private bool _preferPreset;
    public bool IsAccountTarget => TargetKind == "Account";

    public string? PresetName
    {
        get => string.IsNullOrEmpty(Model.PresetName) ? null : Model.PresetName;
        set { Model.PresetName = value ?? ""; if (Model.PresetName.Length > 0) Model.Alias = ""; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); _owner.Persist(); }
    }

    public Account? Account
    {
        get => _owner.Accounts.FirstOrDefault(a => Model.Alias.Length > 0 && PresetItem.Matches(a, Model.Alias));
        set { Model.Alias = value?.Username ?? ""; if (Model.Alias.Length > 0) Model.PresetName = ""; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); _owner.Persist(); }
    }

    public string PlaceIdText
    {
        get => Model.PlaceId > 0 ? Model.PlaceId.ToString() : "";
        set
        {
            Model.PlaceId = JoinLinks.ParsePlaceId(value);
            OnPropertyChanged();
            _owner.Persist();
        }
    }

    public string Time
    {
        get => Model.TimeOfDay;
        set
        {
            string normalized = SchedulerService.NormalizeTime(value);
            TimeValid = normalized.Length > 0;
            if (TimeValid) Model.TimeOfDay = normalized;
            OnPropertyChanged();
            RaiseSchedule();
            _owner.Persist();
        }
    }

    private bool _timeValid = true;
    public bool TimeValid { get => _timeValid; private set => SetField(ref _timeValid, value); }

    public int AutoCloseAfterMinutes
    {
        get => Model.AutoCloseAfterMinutes;
        set { Model.AutoCloseAfterMinutes = Math.Clamp(value, 0, 24 * 60); OnPropertyChanged(); _owner.Persist(); }
    }

    public ObservableCollection<DayToggle> Days { get; } = new();

    private void BuildDays()
    {
        Days.Clear();
        CultureInfo culture;
        try { culture = new CultureInfo(LocalizationService.Current); }
        catch { culture = CultureInfo.CurrentUICulture; }

        // Start the week where the user's locale starts it.
        var first = culture.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)first + i) % 7);
            Days.Add(new DayToggle(day, culture.DateTimeFormat.GetShortestDayName(day), Model.Days.Contains(day), OnDaysChanged));
        }
    }

    private void OnDaysChanged()
    {
        Model.Days = Days.Where(d => d.IsOn).Select(d => d.Day).ToList();
        RaiseSchedule();
        _owner.Persist();
    }

    public string NextRunText
    {
        get
        {
            if (!Model.Enabled) return L.T("Automation.Schedule.Paused");
            var next = SchedulerService.NextRun(Model);
            return next == null ? L.T("Automation.Schedule.Never") : L.T("Automation.Schedule.Next", next.Value.ToString("ddd t", CultureInfo.CurrentCulture));
        }
    }

    public string Summary
    {
        get
        {
            string target = !string.IsNullOrEmpty(Model.PresetName) ? Model.PresetName
                          : Account?.DisplayNameOrUser ?? L.T("Automation.Schedule.NoTarget");
            string days = Model.Days.Count is 0 or 7 ? L.T("Automation.Schedule.Daily")
                        : string.Join(", ", Days.Where(d => d.IsOn).Select(d => d.Label));
            return L.T(IsLaunch ? "Automation.Schedule.SummaryLaunch" : "Automation.Schedule.SummaryClose", target, Model.TimeOfDay, days);
        }
    }

    public void RaiseSchedule()
    {
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(NextRunText));
        OnPropertyChanged(nameof(Summary));
    }

    public void RaiseAll()
    {
        BuildDays();
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Launch presets (named sets of accounts that start together) and the scheduler that runs them.</summary>
public class AutomationViewModel : ObservableObject
{
    private readonly AccountStore _store;
    private readonly MainViewModel _main;
    private readonly System.Windows.Threading.DispatcherTimer _saveDebounce;

    public AutomationViewModel(AccountStore store, MainViewModel main)
    {
        _store = store;
        _main = main;

        _saveDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SettingsService.Save(); };

        foreach (var p in SettingsService.Current.LaunchPresets) Presets.Add(new PresetItem(p, this));
        foreach (var t in SettingsService.Current.ScheduledTasks) Schedules.Add(new ScheduleItem(t, this));

        AddPresetCommand = new RelayCommand(_ => AddPreset());
        DeletePresetCommand = new RelayCommand(p => DeletePreset(p as PresetItem ?? SelectedPreset));
        RunPresetCommand = new AsyncRelayCommand(p => RunPresetAsync(p as PresetItem ?? SelectedPreset));
        StopPresetCommand = new RelayCommand(_ => { try { _presetCts?.Cancel(); } catch (ObjectDisposedException) { } });
        AddScheduleCommand = new RelayCommand(_ => AddSchedule());
        DeleteScheduleCommand = new RelayCommand(p => DeleteSchedule(p as ScheduleItem ?? SelectedSchedule));
        RunScheduleNowCommand = new AsyncRelayCommand(p => RunScheduleAsync(p as ScheduleItem ?? SelectedSchedule));

        _store.Accounts.CollectionChanged += (_, _) => _selectedPreset?.SyncMembers(_store.Accounts);

        // "Next run" text moves with the clock.
        var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        tick.Tick += (_, _) => { foreach (var s in Schedules) s.RaiseSchedule(); };
        tick.Start();
    }

    public ObservableCollection<Account> Accounts => _store.Accounts;
    public ObservableCollection<PresetItem> Presets { get; } = new();
    public ObservableCollection<ScheduleItem> Schedules { get; } = new();
    public IEnumerable<string> PresetNames => Presets.Select(p => p.Name).ToList();

    /// <summary>The launch bar's saved places, for a preset's "Saved place" picker. First entry = none.</summary>
    public IReadOnlyList<Choice> SavedPlaceChoices =>
        new[] { new Choice("", L.T("Automation.Saved.None")) }
            .Concat(SettingsService.Current.SavedPlaces.Select(p => new Choice(p.Id, p.Name)))
            .ToList();

    private string _section = "Presets";
    public string Section { get => _section; set => SetField(ref _section, value ?? "Presets"); }

    /// <summary>Opens the first preset and schedule so the editor isn't empty on the first visit.</summary>
    public void OnShown()
    {
        OnPropertyChanged(nameof(SavedPlaceChoices));   // saved from the launch bar meanwhile
        foreach (var p in Presets) p.RaiseAll();
        SelectedPreset ??= Presets.FirstOrDefault();
        SelectedSchedule ??= Schedules.FirstOrDefault();
    }

    private PresetItem? _selectedPreset;
    public PresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetField(ref _selectedPreset, value)) return;
            _selectedPreset?.SyncMembers(_store.Accounts);
            OnPropertyChanged(nameof(HasPreset));
        }
    }
    public bool HasPreset => _selectedPreset != null;

    private ScheduleItem? _selectedSchedule;
    public ScheduleItem? SelectedSchedule
    {
        get => _selectedSchedule;
        set { if (SetField(ref _selectedSchedule, value)) OnPropertyChanged(nameof(HasSchedule)); }
    }
    public bool HasSchedule => _selectedSchedule != null;

    public RelayCommand AddPresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }
    public AsyncRelayCommand RunPresetCommand { get; }
    public RelayCommand StopPresetCommand { get; }
    public RelayCommand AddScheduleCommand { get; }
    public RelayCommand DeleteScheduleCommand { get; }
    public AsyncRelayCommand RunScheduleNowCommand { get; }

    public void Persist()
    {
        SettingsService.Current.LaunchPresets = Presets.Select(p => p.Model).ToList();
        SettingsService.Current.ScheduledTasks = Schedules.Select(s => s.Model).ToList();
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    public void OnPresetRenamed(string oldName, string newName)
    {
        foreach (var s in Schedules.Where(s => string.Equals(s.Model.PresetName, oldName, StringComparison.OrdinalIgnoreCase)))
        {
            s.Model.PresetName = newName;
            s.RaiseAll();
        }
        OnPropertyChanged(nameof(PresetNames));
    }

    public void RefreshLocalized()
    {
        foreach (var p in Presets) p.RaiseAll();
        foreach (var s in Schedules) s.RaiseAll();
        OnPropertyChanged(string.Empty);
    }

    private void AddPreset()
    {
        var model = new LaunchPreset
        {
            Name = UniqueName(L.T("Automation.Preset.NewName"), Presets.Select(p => p.Name)),
            PlaceId = SettingsService.Current.DefaultPlaceId,
            JoinDelaySeconds = SettingsService.Current.AccountJoinDelay,
            Aliases = _store.Accounts.Where(a => a.IsChecked).Select(a => a.Username).ToList(),
        };
        var item = new PresetItem(model, this);
        Presets.Add(item);
        SelectedPreset = item;
        Persist();
        OnPropertyChanged(nameof(PresetNames));
    }

    private void DeletePreset(PresetItem? item)
    {
        if (item == null) return;
        if (!DialogService.Confirm(L.T("Automation.Preset.Delete.Title"), L.T("Automation.Preset.Delete.Body", item.Name), L.T("Common.Delete"), danger: true))
            return;
        Presets.Remove(item);
        if (SelectedPreset == item) SelectedPreset = Presets.FirstOrDefault();
        Persist();
        OnPropertyChanged(nameof(PresetNames));
    }

    private CancellationTokenSource? _presetCts;
    private bool _presetRunning;
    public bool IsPresetRunning { get => _presetRunning; private set => SetField(ref _presetRunning, value); }

    private async Task RunPresetAsync(PresetItem? item)
    {
        if (item == null) return;
        if (_presetRunning) { _main.SetStatus(L.T("Automation.Preset.AlreadyRunning")); return; }
        SettingsService.Save();
        if (item.Model.Aliases.Count == 0) { _main.SetStatus(L.T("Automation.Preset.NoAccounts")); return; }
        if (!RequirementsService.IsRobloxInstalled())
        {
            DialogService.OfferDownload(L.T("Requirements.NoRoblox.Title"), L.T("Requirements.NoRoblox.Body"), "https://www.roblox.com/download");
            return;
        }

        IsPresetRunning = true;
        _presetCts = new CancellationTokenSource();
        _main.SetStatus(L.T("Automation.Preset.Running", item.Name));
        PresetService.RunResult r;
        try
        {
            r = await PresetService.LaunchAsync(item.Model,
                onLaunching: (a, i) => _main.SetStatus(L.T("Launch.LaunchingOf", a.DisplayNameOrUser, i + 1, item.Model.Aliases.Count)),
                onWaiting: left => _main.SetStatus(left > 0 ? L.T("Launch.NextIn", left) : L.T("Automation.Preset.Running", item.Name)),
                ct: _presetCts.Token);
        }
        finally
        {
            _presetCts.Dispose();
            _presetCts = null;
            IsPresetRunning = false;
        }

        if (r.NotStarted > 0)
        {
            _main.SetStatus(L.T("Launch.Stopped", r.Launched, r.NotStarted));
            return;
        }
        if (r.Error != null)
        {
            _main.SetStatus(r.Error);
            ToastService.Warning(item.Name, r.Error);
            return;
        }
        string done = L.T("Automation.Preset.Done", item.Name, r.Launched, r.Failed);
        _main.SetStatus(done);
        if (r.Failed > 0) ToastService.Warning(done, string.Join("\n", r.Errors.Take(3)));
    }

    private void AddSchedule()
    {
        var model = new ScheduledTask
        {
            Name = UniqueName(L.T("Automation.Schedule.NewName"), Schedules.Select(s => s.Name)),
            TimeOfDay = DateTime.Now.AddHours(1).ToString("HH:00"),
            PresetName = SelectedPreset?.Name ?? Presets.FirstOrDefault()?.Name ?? "",
            PlaceId = SettingsService.Current.DefaultPlaceId,
            Enabled = true,
        };
        var item = new ScheduleItem(model, this);
        Schedules.Add(item);
        SelectedSchedule = item;
        Section = "Schedules";
        Persist();
    }

    private void DeleteSchedule(ScheduleItem? item)
    {
        if (item == null) return;
        if (!DialogService.Confirm(L.T("Automation.Schedule.Delete.Title"), L.T("Automation.Schedule.Delete.Body", item.Name), L.T("Common.Delete"), danger: true))
            return;
        Schedules.Remove(item);
        if (SelectedSchedule == item) SelectedSchedule = Schedules.FirstOrDefault();
        Persist();
    }

    private async Task RunScheduleAsync(ScheduleItem? item)
    {
        if (item == null) return;
        SettingsService.Save();
        var preset = string.IsNullOrEmpty(item.Model.PresetName) ? null : PresetService.Find(item.Model.PresetName);
        if (item.Model.Action == ScheduleAction.Launch)
        {
            if (preset != null) await RunPresetAsync(Presets.FirstOrDefault(p => p.Model == preset));
            else if (item.Account is { } acc && item.Model.PlaceId > 0)
            {
                var r = await LauncherService.LaunchAsync(acc, item.Model.PlaceId);
                _main.SetStatus(r.Success ? L.T("Status.Launched", acc.DisplayNameOrUser) : r.Message);
            }
            else _main.SetStatus(L.T("Automation.Schedule.Incomplete"));
        }
        else
        {
            var ids = preset != null
                ? _store.Accounts.Where(a => preset.Aliases.Any(k => PresetItem.Matches(a, k))).Select(a => a.UserId).ToList()
                : item.Account is { } a ? new List<long> { a.UserId } : new List<long>();
            int closed = await Task.Run(() => ids.Sum(InstanceControlService.CloseFor));
            _main.SetStatus(closed > 0 ? L.N("Status.ClosedClients", closed) : L.T("Status.NoClients"));
        }
    }

    private static string UniqueName(string baseName, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        for (int i = 2; ; i++)
            if (!taken.Contains($"{baseName} {i}")) return $"{baseName} {i}";
    }
}
