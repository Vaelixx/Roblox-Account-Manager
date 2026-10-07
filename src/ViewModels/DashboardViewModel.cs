using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

/// <summary>A running Roblox client as shown on the Overview.</summary>
/// <summary>An account whose next auto-rejoin is waiting out a backoff delay after repeated crashes.</summary>
public sealed class WaitingRejoinRow
{
    public long UserId { get; init; }
    public string Text { get; init; } = "";
}

public sealed class ClientRow
{
    public int Pid { get; init; }
    public ProcessRegistry.ProcessToken Token { get; init; }
    public Account? Account { get; init; }
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Uptime { get; init; } = "";
    public string Memory { get; init; } = "";
    public bool IsExternal { get; init; }
    public bool HasWindow { get; init; }
}

/// <summary>A recorded session in the activity list.</summary>
public sealed class SessionRow
{
    public Account? Account { get; init; }
    public string Name { get; init; } = "";
    public string Duration { get; init; } = "";
    public string When { get; init; } = "";
}

/// <summary>
/// Backs the Overview page: headline numbers, the running clients with their controls, accounts
/// that need attention (the session health panel), who is online and recent sessions.
/// </summary>
public class DashboardViewModel : ObservableObject
{
    private readonly AccountStore _store;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _clientTimer;
    private readonly DispatcherTimer _recompute;

    public DashboardViewModel(AccountStore store, MainViewModel main)
    {
        _store = store;
        _main = main;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ValidateAllCommand = new AsyncRelayCommand(ValidateAllAsync);
        ArrangeCommand = new RelayCommand(_ => Report(InstanceControlService.ArrangeGrid(), "Clients.Arranged"));
        MinimizeAllCommand = new RelayCommand(_ => Report(InstanceControlService.MinimizeAll(), "Clients.Minimized"));
        RestoreAllCommand = new RelayCommand(_ => Report(InstanceControlService.RestoreAll(), "Clients.Restored"));
        CloseAllCommand = new RelayCommand(_ => CloseAll());
        FocusClientCommand = new RelayCommand(p => { if (p is ClientRow row && !InstanceControlService.Focus(row.Token)) _main.SetStatus(L.T("Clients.NoWindow")); });
        CloseClientCommand = new RelayCommand(p => { if (p is ClientRow row) _ = Task.Run(() => { InstanceControlService.Close(row.Token); }); });
        MinimizeClientCommand = new RelayCommand(p => { if (p is ClientRow row && !InstanceControlService.Minimize(row.Token)) _main.SetStatus(L.T("Clients.NoWindow")); });
        CancelRejoinCommand = new RelayCommand(p => { if (p is long id) { WatchdogService.CancelRejoin(id); RefreshClients(); } });
        OpenAccountCommand = new RelayCommand(p => { if (p is Account a) _main.ShowAccount(a); });
        FixAccountCommand = new AsyncRelayCommand(p => FixAsync(p as Account));

        _clientTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _clientTimer.Tick += (_, _) => RefreshClients();

        // Recompute the headline numbers at most a few times a second — a presence poll changes
        // every account at once.
        _recompute = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _recompute.Tick += (_, _) => { _recompute.Stop(); Recompute(); };

        PresenceService.PresenceUpdated += QueueRecompute;
        _store.Accounts.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (Account a in e.NewItems) a.PropertyChanged += OnAccountChanged;
            if (e.OldItems != null) foreach (Account a in e.OldItems) a.PropertyChanged -= OnAccountChanged;
            QueueRecompute();
        };
        _store.Saved += QueueRecompute;
        Recompute();
    }

    private void OnAccountChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Account.Presence) or nameof(Account.Health) or nameof(Account.Robux)
            or nameof(Account.LastLocation) or nameof(Account.ThumbnailUrl))
            QueueRecompute();
    }

    private void QueueRecompute()
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        if (!d.CheckAccess()) { d.BeginInvoke(new Action(QueueRecompute)); return; }
        _recompute.Stop();
        _recompute.Start();
    }

    public void OnShown()
    {
        RefreshClients();
        foreach (var a in _store.Accounts) a.RaiseHealth();   // "checked 3 days ago" moves with the clock
        Recompute();
        _clientTimer.Start();
    }

    public void RefreshLocalized()
    {
        OnPropertyChanged(string.Empty);
        Recompute();
        RefreshClients();
    }

    // ================================================================ headline numbers

    private int _total, _online, _inGame, _attentionCount;
    private long _totalRobux;
    public int Total { get => _total; private set => SetField(ref _total, value); }
    public int Online { get => _online; private set => SetField(ref _online, value); }
    public int InGame { get => _inGame; private set => SetField(ref _inGame, value); }
    public int AttentionCount { get => _attentionCount; private set { if (SetField(ref _attentionCount, value)) OnPropertyChanged(nameof(AllHealthy)); } }
    public long TotalRobux { get => _totalRobux; private set { if (SetField(ref _totalRobux, value)) OnPropertyChanged(nameof(TotalRobuxText)); } }
    public string TotalRobuxText => _totalRobux.ToString("N0");

    public string PlaytimeWeekText => PlaytimeService.Format(PlaytimeService.Last7DaysTotal);
    public string PlaytimeTotalText => PlaytimeService.AllTimeTotal > TimeSpan.Zero
        ? L.T("Overview.PlaytimeTotal", PlaytimeService.Format(PlaytimeService.AllTimeTotal))
        : L.T("Playtime.Nothing");

    public string OnlineNote => L.N("Overview.OnlineNote", _total);

    public bool HasAccounts => _total > 0;
    public bool AllHealthy => _attentionCount == 0;

    public string Subtitle => _total == 0 ? L.T("Overview.Subtitle.Empty") : L.N("Overview.Subtitle", _total, _online);

    public bool MaskUsernames => SettingsService.Current.HideUsernames;
    public void RefreshMask() { OnPropertyChanged(nameof(MaskUsernames)); Recompute(); RefreshClients(); }

    public ObservableCollection<Account> AttentionAccounts { get; } = new();
    public ObservableCollection<Account> OnlineAccounts { get; } = new();
    public ObservableCollection<SessionRow> RecentSessions { get; } = new();

    private void Recompute()
    {
        var list = _store.Accounts.ToList();
        Total = list.Count;
        Online = list.Count(a => a.IsOnline);
        InGame = list.Count(a => a.Presence == PresenceStatus.InGame);
        TotalRobux = list.Where(a => a.Robux > 0).Sum(a => a.Robux);

        var attention = list.Where(a => a.NeedsAttention)
            .OrderBy(a => a.Health == "Invalid" ? 0 : 1)
            .ThenBy(a => a.DisplayNameOrUser, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        AttentionCount = attention.Count;
        Sync(AttentionAccounts, attention);

        Sync(OnlineAccounts, list.Where(a => a.IsOnline)
            .OrderBy(a => PresenceStatus.Rank(a.Presence))
            .ThenBy(a => a.DisplayNameOrUser, StringComparer.CurrentCultureIgnoreCase)
            .ToList());

        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(OnlineNote));
        RefreshSessions();
    }

    /// <summary>Brings a collection in line with <paramref name="wanted"/> without clearing it (no flicker).</summary>
    private static void Sync<T>(ObservableCollection<T> target, IList<T> wanted)
    {
        for (int i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            int at = target.IndexOf(wanted[i]);
            if (at < 0) target.Insert(i, wanted[i]);
            else if (at != i) target.Move(at, i);
        }
    }

    public void RefreshPlaytime()
    {
        OnPropertyChanged(nameof(PlaytimeWeekText));
        OnPropertyChanged(nameof(PlaytimeTotalText));
        RefreshSessions();
    }

    private void RefreshSessions()
    {
        var byId = _store.Accounts.Where(a => a.UserId > 0).GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.First());
        RecentSessions.Clear();
        foreach (var s in PlaytimeService.Recent(6))
        {
            byId.TryGetValue(s.UserId, out var acc);
            RecentSessions.Add(new SessionRow
            {
                Account = acc,
                Name = MaskUsernames ? "••••••" : acc?.DisplayNameOrUser ?? s.UserId.ToString(),
                Duration = PlaytimeService.Format(s.Duration),
                When = Account.RelativeTime(DateTimeOffset.FromUnixTimeSeconds(s.EndUnix).UtcDateTime),
            });
        }
        OnPropertyChanged(nameof(HasSessions));
    }

    public bool HasSessions => RecentSessions.Count > 0;

    // ================================================================ running clients

    public ObservableCollection<ClientRow> Clients { get; } = new();

    /// <summary>Rejoins waiting out a backoff delay, with a Stop action — a toast alone is easy to miss.</summary>
    public ObservableCollection<WaitingRejoinRow> WaitingRejoins { get; } = new();
    public bool HasWaitingRejoins => WaitingRejoins.Count > 0;
    public bool HasClients => Clients.Count > 0;

    public void RefreshClients()
    {
        var snapshot = InstanceControlService.Snapshot();
        var byId = _store.Accounts.Where(a => a.UserId > 0).GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.First());

        Clients.Clear();
        foreach (var c in snapshot)
        {
            byId.TryGetValue(c.UserId, out var acc);
            string name = c.IsExternal ? L.T("Clients.External")
                        : MaskUsernames ? "••••••"
                        : acc?.DisplayNameOrUser ?? c.Alias;
            string detail = !c.HasWindow ? L.T("Clients.Starting")
                : acc != null && acc.Presence == PresenceStatus.InGame && acc.LastLocation.Length > 0 ? acc.LastLocation
                : Destination(c.Target);

            // Extras only when they carry information: how often the watchdog brought it back, and
            // when Anti-AFK next presses a key (roughly: it runs on a 15 s tick).
            int rejoins = c.UserId > 0 ? WatchdogService.RejoinsFor(c.UserId) : 0;
            if (rejoins > 0) detail += "  ·  " + L.N("Clients.Rejoins", rejoins);
            var (_, nextAfk) = AntiAfkService.StatusFor(c.Pid);
            if (nextAfk is { } due)
                detail += "  ·  " + L.T("Clients.AfkNext", Math.Max(1, (int)Math.Ceiling((due - DateTime.UtcNow).TotalMinutes)));
            Clients.Add(new ClientRow
            {
                Pid = c.Pid,
                Token = c.Token,
                Account = acc,
                Name = name,
                Detail = detail,
                Uptime = c.UptimeText,
                Memory = c.MemoryText,
                IsExternal = c.IsExternal,
                HasWindow = c.HasWindow,
            });
        }
        OnPropertyChanged(nameof(HasClients));
        OnPropertyChanged(nameof(ClientsTitle));

        WaitingRejoins.Clear();
        foreach (var (id, due) in WatchdogService.PendingRejoins())
        {
            byId.TryGetValue(id, out var acc);
            string who = MaskUsernames ? "••••••" : acc?.DisplayNameOrUser ?? id.ToString();
            int mins = Math.Max(1, (int)Math.Ceiling((due - DateTime.UtcNow).TotalMinutes));
            WaitingRejoins.Add(new WaitingRejoinRow { UserId = id, Text = L.T("Watchdog.Waiting", who, mins) });
        }
        OnPropertyChanged(nameof(HasWaitingRejoins));
    }

    public string ClientsTitle => L.N("Clients.Title", Clients.Count);

    /// <summary>Where a client was sent. Never shows private-server codes.</summary>
    private static string Destination(JoinTarget t) => t.Kind switch
    {
        JoinKind.FollowUser => L.T("Clients.Dest.Follow", t.FollowUserId),
        JoinKind.PrivateServer => L.T("Clients.Dest.Private", t.PlaceId),
        _ => t.PlaceId > 0 ? L.T("Place.Fallback", t.PlaceId) : L.T("Clients.NoPlace"),
    };

    // ================================================================ commands

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ValidateAllCommand { get; }
    public RelayCommand ArrangeCommand { get; }
    public RelayCommand MinimizeAllCommand { get; }
    public RelayCommand RestoreAllCommand { get; }
    public RelayCommand CloseAllCommand { get; }
    public RelayCommand FocusClientCommand { get; }
    public RelayCommand CloseClientCommand { get; }
    public RelayCommand MinimizeClientCommand { get; }
    public RelayCommand CancelRejoinCommand { get; }
    public RelayCommand OpenAccountCommand { get; }
    public AsyncRelayCommand FixAccountCommand { get; }

    private async Task RefreshAsync()
    {
        _main.SetStatus(L.T("Status.Refreshing"));
        await _store.RefreshLiveDataAsync();
        RefreshClients();
        Recompute();
        _main.SetStatus(L.T("Status.Refreshed"));
    }

    private async Task ValidateAllAsync()
    {
        var accounts = _store.Accounts.ToList();
        if (accounts.Count == 0) { _main.SetStatus(L.T("Status.NothingToRefresh")); return; }
        _main.SetStatus(L.T("Health.Checking"));
        int invalid = await CookieHealthService.ValidateAllAsync(accounts, new Progress<string>(_main.SetStatus));
        _store.Save();
        Recompute();
        string message = invalid == 0 ? L.N("Health.AllValid", accounts.Count) : L.N("Health.SomeInvalid", invalid, accounts.Count);
        _main.SetStatus(message);
        if (invalid == 0) ToastService.Success(L.T("Health.DoneTitle"), message);
        else ToastService.Warning(L.T("Health.DoneTitle"), message);
    }

    /// <summary>The one-click fix for an attention row: sign in again when expired, otherwise check it now.</summary>
    private async Task FixAsync(Account? account)
    {
        if (account == null) return;
        if (account.Health == "Invalid")
        {
            _main.ShowAccount(account);
            _main.Accounts.InspectorTab = "Security";
            return;
        }
        await CookieHealthService.ValidateAllAsync(new[] { account });
        _store.Save();
        Recompute();
    }

    private void Report(int affected, string key)
    {
        _main.SetStatus(affected > 0 ? L.N(key, affected) : L.T("Clients.NoneOpen"));
        RefreshClients();
    }

    private void CloseAll()
    {
        if (Clients.Count == 0) { _main.SetStatus(L.T("Status.NoClients")); return; }
        if (!DialogService.Confirm(L.T("Clients.CloseAll.Title"), L.N("Clients.CloseAll.Body", Clients.Count), L.T("Clients.CloseAll.Action"), danger: true))
            return;
        _main.CloseAllClients();
    }
}
