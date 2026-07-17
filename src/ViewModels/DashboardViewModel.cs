using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

/// <summary>
/// Always-live view of every account's presence for the dashboard. The list binds
/// straight through to the shared account collection while the headline counters
/// (online / in-game / in-studio / offline) are kept in sync with the
/// <see cref="PresenceService"/> polling loop and any add/remove of accounts.
/// </summary>
public class DashboardViewModel : ObservableObject
{
    private readonly AccountStore _store;

    public DashboardViewModel(AccountStore store)
    {
        _store = store;
        RefreshCommand = new AsyncRelayCommand(() => PresenceService.PollNowAsync());
        PresenceService.PresenceUpdated += OnPresenceUpdated;
        _store.Accounts.CollectionChanged += OnAccountsChanged;
        Recompute();
    }

    /// <summary>The shared, observable account collection — rendered as the live list.</summary>
    public ObservableCollection<Account> Accounts => _store.Accounts;

    private int _total, _online, _inGame, _inStudio, _offline;
    public int Total    { get => _total;    private set => SetField(ref _total, value); }
    public int Online   { get => _online;   private set => SetField(ref _online, value); }
    public int InGame   { get => _inGame;   private set => SetField(ref _inGame, value); }
    public int InStudio { get => _inStudio; private set => SetField(ref _inStudio, value); }
    public int Offline  { get => _offline;  private set => SetField(ref _offline, value); }

    private long _totalRobux, _totalRap;
    private int _premiumCount;
    public long TotalRobux   { get => _totalRobux;   private set => SetField(ref _totalRobux, value); }
    public long TotalRap     { get => _totalRap;     private set => SetField(ref _totalRap, value); }
    public int  PremiumCount { get => _premiumCount; private set => SetField(ref _premiumCount, value); }

    private string _lastUpdated = "never";
    public string LastUpdated { get => _lastUpdated; private set => SetField(ref _lastUpdated, value); }

    public AsyncRelayCommand RefreshCommand { get; }

    private void OnAccountsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPresenceUpdated();

    private void OnPresenceUpdated()
    {
        // Poll callback arrives on a threadpool thread; marshal count updates to the UI.
        var d = Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) d.BeginInvoke(new Action(Recompute));
        else Recompute();
    }

    private void Recompute()
    {
        var list = _store.Accounts.ToList();
        Total    = list.Count;
        Online   = list.Count(a => a.Presence == "Online");
        InGame   = list.Count(a => a.Presence == "In Game");
        InStudio = list.Count(a => a.Presence == "In Studio");
        Offline  = list.Count - Online - InGame - InStudio;
        TotalRobux   = list.Where(a => a.Robux > 0).Sum(a => a.Robux);
        TotalRap     = list.Where(a => a.Rap > 0).Sum(a => a.Rap);
        PremiumCount = list.Count(a => a.IsPremium);
        LastUpdated = DateTime.Now.ToString("HH:mm:ss");
    }
}
