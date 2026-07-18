using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

/// <summary>
/// Backs the "Friends" tab: pick a manager account, load its Roblox friends list,
/// see who is online / in-game, and join (follow) a friend with one click.
/// </summary>
public class FriendsViewModel : ObservableObject
{
    private readonly AccountStore _store;
    private readonly MainViewModel _main;

    // Full unfiltered list; Friends is the search-filtered view bound by the UI.
    private readonly List<Friend> _all = new();

    public FriendsViewModel(AccountStore store, MainViewModel main)
    {
        _store = store;
        _main = main;

        RefreshCommand     = new AsyncRelayCommand(_ => RefreshAsync());
        JoinCommand        = new AsyncRelayCommand(p => JoinAsync(p as Friend));
        OpenProfileCommand = new RelayCommand(p => OpenProfile(p as Friend));

        // Make the tab useful immediately by defaulting to the first account.
        _selectedAccount = _store.Accounts.FirstOrDefault();
    }

    /// <summary>Accounts available in the manager (drives the picker).</summary>
    public ObservableCollection<Account> Accounts => _store.Accounts;

    /// <summary>Search-filtered friends currently shown.</summary>
    public ObservableCollection<Friend> Friends { get; } = new();

    private Account? _selectedAccount;
    public Account? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetField(ref _selectedAccount, value)) return;
            // Switching accounts must drop the previous account's friends; otherwise
            // EnsureLoaded's "Friends.Count > 0" guard keeps showing the old account's list.
            _all.Clear();
            Friends.Clear();
            EmptyText = "Pick an account and hit Refresh to load its friends.";
        }
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { if (SetField(ref _searchText, value)) ApplyFilter(); }
    }

    private bool _busy;
    public bool Busy { get => _busy; private set => SetField(ref _busy, value); }

    private string _emptyText = "Pick an account and hit Refresh to load its friends.";
    public string EmptyText { get => _emptyText; private set => SetField(ref _emptyText, value); }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand JoinCommand { get; }
    public RelayCommand OpenProfileCommand { get; }

    /// <summary>Called when the Friends tab becomes visible — auto-loads the list once.</summary>
    public void EnsureLoaded()
    {
        if (Busy || Friends.Count > 0) return;
        if (SelectedAccount == null) return;
        _ = RefreshAsync();
    }

    // ------------------------------------------------------------------
    //  Load
    // ------------------------------------------------------------------
    private async Task RefreshAsync()
    {
        var acc = SelectedAccount;
        if (acc == null) { _main.SetStatus("Select an account first."); return; }
        if (string.IsNullOrEmpty(acc.Cookie) || acc.UserId <= 0)
        {
            _main.SetStatus($"{acc.DisplayNameOrUser} has no valid session — re-login it first.");
            return;
        }

        Busy = true;
        _main.SetStatus($"Loading friends of {acc.DisplayNameOrUser}…");
        try
        {
            var friends = await RobloxApi.GetFriendsAsync(acc.Cookie, acc.UserId);

            _all.Clear();
            _all.AddRange(friends.OrderBy(f => f.DisplayNameOrUser, StringComparer.OrdinalIgnoreCase));
            ApplyFilter();

            if (_all.Count == 0)
            {
                EmptyText = "No friends found for this account.";
                _main.SetStatus($"{acc.DisplayNameOrUser} has no friends listed.");
                return;
            }

            // Presence (join targets) + headshots — fill the loaded rows in place.
            var ids = _all.Select(f => f.UserId).ToList();
            var presence = await RobloxApi.GetPresenceDetailsAsync(acc.Cookie, ids);
            var heads    = await RobloxApi.GetHeadshotsAsync(ids);

            void Apply()
            {
                foreach (var f in _all)
                {
                    if (presence.TryGetValue(f.UserId, out var pd))
                    {
                        f.Presence     = pd.Status;
                        f.LastLocation = pd.LastLocation;
                        f.PlaceId      = pd.PlaceId;
                        f.RootPlaceId  = pd.RootPlaceId;
                        f.JobId        = pd.JobId;
                    }
                    if (heads.TryGetValue(f.UserId, out var url)) f.HeadshotUrl = url;
                }
                ReorderByPresence();   // in-game / online friends float to the top
            }

            var d = Application.Current?.Dispatcher;
            if (d != null && !d.CheckAccess()) d.Invoke(Apply); else Apply();

            int online = _all.Count(f => f.Presence != "Offline");
            _main.SetStatus($"{_all.Count} friends · {online} online.");
        }
        catch (Exception ex)
        {
            _main.SetStatus($"Couldn't load friends: {ex.Message}");
        }
        finally { Busy = false; }
    }

    private static int PresenceRank(string p) => p switch
    {
        "In Game"   => 0,
        "In Studio" => 1,
        "Online"    => 2,
        _           => 3
    };

    private void ReorderByPresence()
    {
        _all.Sort((a, b) =>
        {
            int r = PresenceRank(a.Presence).CompareTo(PresenceRank(b.Presence));
            return r != 0
                ? r
                : string.Compare(a.DisplayNameOrUser, b.DisplayNameOrUser, StringComparison.OrdinalIgnoreCase);
        });
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = (_searchText ?? "").Trim();
        Friends.Clear();
        foreach (var f in _all)
        {
            if (q.Length == 0
                || f.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
                || f.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
                Friends.Add(f);
        }
    }

    // ------------------------------------------------------------------
    //  Join (follow) + profile
    // ------------------------------------------------------------------
    private async Task JoinAsync(Friend? friend)
    {
        if (friend == null) return;
        var acc = SelectedAccount;
        if (acc == null) { _main.SetStatus("Select an account first."); return; }
        if (!friend.CanJoin)
        {
            _main.SetStatus($"{friend.DisplayNameOrUser} isn't in a joinable game right now.");
            return;
        }

        long place = friend.RootPlaceId > 0 ? friend.RootPlaceId : friend.PlaceId;
        _main.SetStatus($"Joining {friend.DisplayNameOrUser}…");
        var r = await LauncherService.LaunchAsync(acc, place, jobId: friend.JobId, followUserId: friend.UserId);
        _main.SetStatus(r.Success
            ? $"Launched {acc.DisplayNameOrUser} → following {friend.DisplayNameOrUser}."
            : r.Message);
    }

    private void OpenProfile(Friend? friend)
    {
        if (friend == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"https://www.roblox.com/users/{friend.UserId}/profile",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { _main.SetStatus($"Couldn't open profile: {ex.Message}"); }
    }
}
