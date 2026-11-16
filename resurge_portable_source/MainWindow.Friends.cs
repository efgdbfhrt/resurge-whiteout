using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ResurgeLauncherCSharp.Services;

namespace ResurgeLauncherCSharp;

public partial class MainWindow
{
    private sealed class FriendItem(FriendEntry entry, Brush? avatar, Brush dot, Brush status)
    {
        public FriendEntry Entry { get; } = entry;
        public string Initial => Entry.Name[..1].ToUpperInvariant();
        public Brush? AvatarBrush { get; } = avatar;
        public Brush DotBrush { get; } = dot;
        public Brush StatusBrush { get; } = status;
        public Visibility UnreadVisibility => Entry.Unread > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Search suggestions: what the quick button does for this player.
        public string ActionText => Entry.State switch { FriendState.None => "Add", FriendState.Incoming => "Accept", _ => "" };
        public string ActionName => $"{ActionText} {Entry.Name}";
        public Visibility ActionVisibility => ActionText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string Note => Entry.State switch { FriendState.Friend => "Friend", FriendState.Outgoing => "Requested", _ => "" };
    }

    private sealed record ChatBubble(string Body, string Time, HorizontalAlignment Align, Brush Bubble, Brush Ink);

    private static string NotifySetting => Path.Combine(GameLauncher.DataPath, "notifications.txt");

    private FriendsService _friends = new();
    private RealtimeInbox? _realtime;
    private NotificationToast? _toast;
    private readonly ObservableCollection<FriendItem> _friendItems = [];
    private readonly ObservableCollection<ChatBubble> _chat = [];
    private readonly Dictionary<string, Brush?> _avatarCache = [];
    private readonly Dictionary<string, FriendState> _knownStates = [];
    private readonly DispatcherTimer _inboxTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private FriendEntry? _viewed;
    private string? _chatFriend;
    private long _lastMessageId;
    private long _lastInboxId;
    private bool _inboxReady;
    private bool _listLoaded;
    private DateTime _lastInboxPoll;
    private bool _friendsBusy, _refreshAgain;
    private bool _chatBusy, _chatAgain;
    private bool _inboxBusy;
    private bool _suppressSelection;
    private bool _friendsHooked;
    private bool _notifyEnabled = true, _notifySound = true;
    private int _searchVersion;
    private int _liveTicks;
    private string? _friendsError;
    private string? _confirmRemove;
    private DateTime _confirmRemoveAt;

    private Brush Res(string key) => (Brush)FindResource(key);

    // ------------------------------------------------------------------ connection

    private void HookFriends()
    {
        if (_friendsHooked) return;
        LoadNotifySettings(); // Before _friendsHooked, so setting the switches doesn't save half-loaded values.
        _friendsHooked = true;
        ChatItems.ItemsSource = _chat;
        FriendList.ItemsSource = _friendItems;
        _inboxTimer.Tick += (_, _) => InboxTick();
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _ = SearchAsync();
        };
        // Coming back to the window marks the open conversation read.
        Activated += (_, _) => { if (ChatVisible) _ = PollChatAsync(); };
        // The page sits in a scroll viewer, so give the friends panel the visible height; otherwise a long
        // chat stretches the page instead of scrolling inside the chat.
        if (SidebarPart<ScrollViewer>("PageScroll") is { } pageScroll)
            pageScroll.SizeChanged += (_, _) => FitFriendsPanel();
        FriendsPanel.IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(FitFriendsPanel, DispatcherPriority.Loaded);
        Closed += (_, _) =>
        {
            _realtime?.Dispose();
            _toast?.Close(); // A hidden toast would otherwise keep the app alive.
        };
    }

    private async Task ConnectFriendsAsync()
    {
        HookFriends();
        if (_profile is not { } profile) return;
        ShowMyProfileBadge();
        if (!FriendsService.Configured)
        {
            ShowFriendsUnavailable("Friends aren't switched on yet",
                "This copy of the launcher has no friends server. Everything else works as normal.", retry: false);
            return;
        }
        try
        {
            FriendsSummary.Text = "Connecting…";
            string token = await _friends.SignInAsync(profile.Name, profile.OnlineToken);
            if (_profile?.Name != profile.Name) return; // Signed out meanwhile.
            if (token != profile.OnlineToken)
                _profile = PlayerProfiles.SetOnlineToken(_profile, token);
            _friendsError = null;
            FriendsUnavailable.Visibility = Visibility.Collapsed;
            FriendsPanel.Visibility = Visibility.Visible;

            // Only messages that arrive from now on raise pop-ups; older unread ones show as badges.
            _lastInboxId = await _friends.GetLatestIncomingIdAsync();
            _inboxReady = true;
            StartRealtime();
            _inboxTimer.Start();

            await PushPresenceAsync();
            await UploadAvatarAsync();
            await RefreshFriendsAsync();
        }
        catch (FriendsException ex) { ShowFriendsUnavailable("Couldn't connect to Friends", ex.Message, retry: true); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            ShowFriendsUnavailable("You're offline", "Friends need an internet connection. The game still works offline.", retry: true);
        }
    }

    private void FitFriendsPanel()
    {
        if (!FriendsPanel.IsVisible || SidebarPart<ScrollViewer>("PageScroll") is not { } scroll
            || SidebarPart<FrameworkElement>("PART_SelectedContentHost") is not { } page) return;
        double top = FriendsPanel.TranslatePoint(new Point(0, 0), page).Y;
        const double bottomMargin = 30;
        FriendsPanel.Height = Math.Max(FriendsPanel.MinHeight, scroll.ViewportHeight - top - bottomMargin);
    }

    private void StartRealtime()
    {
        _realtime?.Dispose();
        _realtime = new RealtimeInbox(_friends);
        _realtime.MessageReceived += HandleIncoming;
        _realtime.FriendshipsChanged += () => _ = RefreshFriendsAsync();
        // Catch anything sent while the live connection was being (re)established.
        _realtime.ConnectionChanged += connected => { if (connected) _ = PollInboxAsync(); };
        _realtime.Start();
    }

    private void ShowFriendsUnavailable(string title, string text, bool retry)
    {
        _friendsError = text;
        FriendsUnavailableTitle.Text = title;
        FriendsUnavailableText.Text = text;
        FriendsRetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        FriendsUnavailable.Visibility = Visibility.Visible;
        FriendsPanel.Visibility = Visibility.Collapsed;
        FriendsSummary.Text = retry ? "Not connected" : "Not available";
        FriendsBadge.Visibility = Visibility.Collapsed;
    }

    private async void FriendsRetry_Click(object sender, RoutedEventArgs e)
    {
        FriendsRetryButton.IsEnabled = false;
        try { await ConnectFriendsAsync(); }
        finally { FriendsRetryButton.IsEnabled = true; }
    }

    private async Task DisconnectFriendsAsync()
    {
        _inboxTimer.Stop();
        _realtime?.Dispose();
        _realtime = null;
        _toast?.Hide();
        await _friends.GoOfflineAsync();
        _friends = new FriendsService();
        _avatarCache.Clear();
        _knownStates.Clear();
        _chat.Clear();
        _chatFriend = null;
        _viewed = null;
        _inboxReady = false;
        _listLoaded = false;
        _lastInboxId = 0;
        _suppressSelection = true;
        _friendItems.Clear();
        _suppressSelection = false;
        SuggestPopup.IsOpen = false;
        FriendDetail.Visibility = Visibility.Collapsed;
        FriendEmpty.Visibility = Visibility.Visible;
        FriendsBadge.Visibility = Visibility.Collapsed;
        FriendsSummary.Text = "Sign in to see friends";
    }

    private void ShowMyProfileBadge()
    {
        if (_profile is not { } profile) return;
        MyProfileInitial.Text = profile.Name[..1].ToUpperInvariant();
        MyProfileAvatar.Fill = AvatarBrush(profile);
    }

    private async Task UploadAvatarAsync()
    {
        if (!_friends.SignedIn || _profile is not { } profile) return;
        ShowMyProfileBadge();
        try
        {
            if (PlayerProfiles.LoadAvatar(profile) is not BitmapSource image) return;
            double scale = 96.0 / Math.Max(image.PixelWidth, image.PixelHeight);
            var small = new TransformedBitmap(image, new ScaleTransform(Math.Min(1, scale), Math.Min(1, scale)));
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(small));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            await _friends.UploadAvatarAsync(stream.ToArray());
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    // ------------------------------------------------------------------ live updates

    /// <summary>The open conversation is on screen right now (window focused, Friends page showing).</summary>
    private bool ChatVisible => _chatFriend is not null && IsActive && WindowState != WindowState.Minimized
                                && Navigation.SelectedItem == FriendsTab;

    /// <summary>Runs every 5 seconds: friend list every 15 s, own presence every 30 s.</summary>
    private async void LiveTick()
    {
        if (!_friends.SignedIn) return;
        int tick = _liveTicks++;
        try
        {
            if (tick % 6 == 0) await PushPresenceAsync();
            if (tick % 3 == 0) await RefreshFriendsAsync();
            // Without the live connection, keep the open chat fresh by polling.
            if (_chatFriend is not null && Navigation.SelectedItem == FriendsTab && _realtime?.Connected != true) await PollChatAsync();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    /// <summary>Every 2 s while the live connection is down (every 30 s as a safety net while it is up).</summary>
    private void InboxTick()
    {
        if (!_friends.SignedIn || !_inboxReady) return;
        var wait = _realtime?.Connected == true ? TimeSpan.FromSeconds(30) : TimeSpan.Zero;
        if (DateTime.UtcNow - _lastInboxPoll < wait) return;
        _ = PollInboxAsync();
    }

    private async Task PollInboxAsync()
    {
        if (_inboxBusy || !_friends.SignedIn || !_inboxReady) return;
        _inboxBusy = true;
        _lastInboxPoll = DateTime.UtcNow;
        try
        {
            foreach (var message in await _friends.GetIncomingAsync(_lastInboxId)) HandleIncoming(message);
        }
        catch (Exception ex) when (ex is FriendsException or System.Net.Http.HttpRequestException or TaskCanceledException) { }
        finally { _inboxBusy = false; }
    }

    /// <summary>A message to you arrived (live or polled): show it in the open chat, or notify.</summary>
    private void HandleIncoming(ChatMessage message)
    {
        if (!_inboxReady || message.Mine || message.Id <= _lastInboxId) return;
        _lastInboxId = message.Id;
        if (message.Sender == _chatFriend) _ = PollChatAsync();
        if (message.Sender == _chatFriend && ChatVisible) return;
        _ = NotifyMessageAsync(message);
        _ = RefreshFriendsAsync();
    }

    private async Task PushPresenceAsync()
    {
        if (!_friends.SignedIn || _profile is not { } profile) return;
        string activity = _sessionActive ? LiveStatus.Describe() : "In the launcher";
        try { await _friends.UpdatePresenceAsync(_sessionActive, activity, profile); }
        catch (Exception ex) when (ex is FriendsException or System.Net.Http.HttpRequestException or TaskCanceledException) { }
    }

    private Brush? FriendAvatarBrush(FriendEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Avatar)) return null;
        string key = entry.Id + ":" + entry.Avatar.Length;
        if (_avatarCache.TryGetValue(key, out var cached)) return cached;
        Brush? brush = null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(Convert.FromBase64String(entry.Avatar));
            image.EndInit();
            image.Freeze();
            brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException) { }
        _avatarCache[key] = brush;
        return brush;
    }

    private FriendItem MakeItem(FriendEntry entry)
    {
        bool visible = entry.State is FriendState.Friend or FriendState.None or FriendState.Self;
        Brush dot = !visible ? Res("Faint") : entry.InGame ? Res("Accent") : entry.Online ? Res("Good") : Res("Faint");
        Brush status = entry.State == FriendState.Incoming ? Res("Accent")
            : entry.InGame ? Res("Accent") : entry.Online ? Res("Good") : Res("Muted");
        return new FriendItem(entry, FriendAvatarBrush(entry), dot, status);
    }

    /// <summary>
    /// Updates the list in place (no rebuild), so the selection, scroll position and open chat stay put.
    /// </summary>
    private void SyncFriendList(IReadOnlyList<FriendItem> items)
    {
        _suppressSelection = true;
        try
        {
            var wanted = items.Select(item => item.Entry.Id).ToHashSet();
            for (int i = _friendItems.Count - 1; i >= 0; i--)
                if (!wanted.Contains(_friendItems[i].Entry.Id)) _friendItems.RemoveAt(i);
            for (int i = 0; i < items.Count; i++)
            {
                int existing = -1;
                for (int j = i; j < _friendItems.Count; j++)
                    if (_friendItems[j].Entry.Id == items[i].Entry.Id) { existing = j; break; }
                if (existing < 0) _friendItems.Insert(i, items[i]);
                else
                {
                    if (existing != i) _friendItems.Move(existing, i);
                    if (_friendItems[i].Entry != items[i].Entry) _friendItems[i] = items[i];
                }
            }
            SelectInList(_viewed?.Id);
        }
        finally { _suppressSelection = false; }
    }

    private void SelectInList(string? id)
    {
        bool was = _suppressSelection;
        _suppressSelection = true;
        FriendList.SelectedItem = _friendItems.FirstOrDefault(item => item.Entry.Id == id);
        _suppressSelection = was;
    }

    private async Task RefreshFriendsAsync()
    {
        if (!_friends.SignedIn) return;
        if (_friendsBusy) { _refreshAgain = true; return; }
        _friendsBusy = true;
        try
        {
            do
            {
                _refreshAgain = false;
                var entries = await _friends.GetFriendsAsync();
                if (!_friends.SignedIn) return;
                SyncFriendList(entries.Select(MakeItem).ToArray());
                NotifyFriendChanges(entries);

                if (_viewed is { State: not FriendState.Self } viewed)
                {
                    var current = entries.FirstOrDefault(entry => entry.Id == viewed.Id);
                    // Someone you only looked up keeps showing (as "not friends") even though they aren't in the list.
                    ShowPlayer(current ?? viewed with { State = FriendState.None, Unread = 0 }, reset: false);
                }

                int online = entries.Count(entry => entry.State == FriendState.Friend && entry.Online);
                int requests = entries.Count(entry => entry.State == FriendState.Incoming);
                int unread = entries.Sum(entry => entry.Id == _chatFriend && ChatVisible ? 0 : entry.Unread);
                FriendsBadgeText.Text = (requests + unread).ToString();
                FriendsBadge.Visibility = requests + unread > 0 ? Visibility.Visible : Visibility.Collapsed;
                int friends = entries.Count(entry => entry.State == FriendState.Friend);
                FriendsSummary.Text = unread > 0 ? $"{unread} unread message{(unread == 1 ? "" : "s")}"
                    : requests > 0 ? $"{requests} friend request{(requests == 1 ? "" : "s")}"
                    : friends == 0 ? "Add your first friend"
                    : online == 0 ? "No friends online" : $"{online} online now";
                FriendEmpty.Text = entries.Length == 0
                    ? "No friends yet. Search for a player on the left to visit their profile, add them or send a message."
                    : "Pick a friend, or search for any player, to see their profile and chat.";
            } while (_refreshAgain && _friends.SignedIn);
        }
        catch (Exception ex) when (ex is FriendsException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            FriendsStatus.Text = "Couldn't refresh friends. Retrying shortly.";
        }
        finally { _friendsBusy = false; }
    }

    // ------------------------------------------------------------------ notifications

    private void LoadNotifySettings()
    {
        try
        {
            if (File.Exists(NotifySetting))
            {
                string text = File.ReadAllText(NotifySetting);
                _notifyEnabled = !text.Contains("notify=0", StringComparison.Ordinal);
                _notifySound = !text.Contains("sound=0", StringComparison.Ordinal);
            }
        }
        catch (IOException) { }
        NotifyToggle.IsChecked = _notifyEnabled;
        NotifySoundToggle.IsChecked = _notifySound;
        NotifySoundToggle.IsEnabled = _notifyEnabled;
    }

    private void NotifySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_friendsHooked || NotifyToggle is null || NotifySoundToggle is null) return; // Fires while the page is being built.
        _notifyEnabled = NotifyToggle.IsChecked == true;
        _notifySound = NotifySoundToggle.IsChecked == true;
        NotifySoundToggle.IsEnabled = _notifyEnabled;
        if (!_notifyEnabled) _toast?.Hide();
        TryRun(() => SaveSetting(NotifySetting, $"notify={(_notifyEnabled ? 1 : 0)}\nsound={(_notifySound ? 1 : 0)}\n"));
    }

    private async Task NotifyMessageAsync(ChatMessage message)
    {
        var entry = _friendItems.FirstOrDefault(item => item.Entry.Id == message.Sender)?.Entry;
        if (entry is null)
        {
            try { entry = await _friends.GetPlayerAsync(message.Sender); }
            catch (Exception ex) when (ex is FriendsException or System.Net.Http.HttpRequestException or TaskCanceledException) { }
        }
        string name = entry?.Name ?? "Someone";
        ShowToast("New message", name, message.Body, entry, () => _ = OpenPlayerAsync(message.Sender, entry));
    }

    /// <summary>Pops up new friend requests and accepted requests (not on the first load after sign-in).</summary>
    private void NotifyFriendChanges(FriendEntry[] entries)
    {
        bool first = !_listLoaded;
        _listLoaded = true;
        foreach (var entry in entries)
        {
            _knownStates.TryGetValue(entry.Id, out var before);
            bool known = _knownStates.ContainsKey(entry.Id);
            _knownStates[entry.Id] = entry.State;
            if (first) continue;
            if (entry.State == FriendState.Incoming && (!known || before != FriendState.Incoming))
                ShowToast("Friend request", entry.Name, "Wants to be your friend.", entry, () => _ = OpenPlayerAsync(entry.Id, entry));
            else if (entry.State == FriendState.Friend && known && before == FriendState.Outgoing)
                ShowToast("Request accepted", entry.Name, "You're now friends.", entry, () => _ = OpenPlayerAsync(entry.Id, entry));
        }
    }

    private void ShowToast(string kind, string title, string body, FriendEntry? entry, Action open)
    {
        if (!_notifyEnabled) return;
        if (body.Length > 140) body = body[..140] + "…";
        _toast ??= new NotificationToast();
        _toast.Show(kind, title, body, title.Length > 0 ? title[..1].ToUpperInvariant() : "?",
            entry is null ? null : FriendAvatarBrush(entry), () =>
            {
                BringToFront();
                Navigation.SelectedItem = FriendsTab;
                open();
            });
        if (_notifySound) PlaySound("Notification.Default", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault);
        if (!IsActive) FlashTaskbar();
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndAlias = 0x00010000;
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string sound, IntPtr module, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags; public uint Count; public uint Timeout; }
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);

    private void FlashTaskbar()
    {
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = new WindowInteropHelper(this).Handle,
            Flags = 0x2 | 0xC, // Tray, until the window comes to the front.
            Count = 3
        };
        if (info.Window != IntPtr.Zero) FlashWindowEx(ref info);
    }

    // ------------------------------------------------------------------ search and add

    private void AddFriendName_TextChanged(object sender, TextChangedEventArgs e)
    {
        AddFriendHint.Visibility = AddFriendName.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchVersion++;
        _searchTimer.Stop();
        if (AddFriendName.Text.Trim().Length == 0)
        {
            SuggestPopup.IsOpen = false;
            return;
        }
        _searchTimer.Start();
    }

    private async Task SearchAsync()
    {
        string text = AddFriendName.Text.Trim();
        int version = _searchVersion;
        if (text.Length == 0 || !_friends.SignedIn) return;
        try
        {
            var found = await _friends.SearchPlayersAsync(text);
            if (version != _searchVersion) return; // Typed more meanwhile.
            // Use what we already know about each player (friend, request…).
            var items = found.Select(player => _friendItems.FirstOrDefault(item => item.Entry.Id == player.Id)?.Entry ?? player)
                .Select(MakeItem).ToArray();
            SuggestList.ItemsSource = items;
            _suggestFor = text;
            SuggestEmpty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SuggestPopup.IsOpen = AddFriendName.IsKeyboardFocusWithin || items.Length > 0;
        }
        catch (Exception ex) when (ex is FriendsException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            FriendsStatus.Text = "Search isn't available right now.";
        }
    }

    private string? _suggestFor;

    /// <summary>Suggestions for exactly what is typed now (not a shorter, older search).</summary>
    private FriendItem[] Suggestions => _suggestFor == AddFriendName.Text.Trim() ? SuggestList.ItemsSource as FriendItem[] ?? [] : [];

    private async void AddFriend_Click(object sender, RoutedEventArgs e)
    {
        string name = AddFriendName.Text.Trim();
        if (name.Length == 0)
        {
            FriendsStatus.Text = "Type a player's name first.";
            return;
        }
        if (!_friends.SignedIn)
        {
            FriendsStatus.Text = _friendsError ?? "Friends aren't connected.";
            return;
        }
        // Enter or Add picks the exact match, or the only/top suggestion, so the full name isn't needed.
        var pick = Suggestions.FirstOrDefault(item => item.Entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? Suggestions.FirstOrDefault();
        if (pick is not null)
        {
            await AddPlayerAsync(pick.Entry);
            return;
        }
        FriendsStatus.Text = "Looking…";
        try
        {
            FriendsStatus.Text = await _friends.SendRequestAsync(name);
            ClearSearch();
            await RefreshFriendsAsync();
        }
        catch (ArgumentException ex) { FriendsStatus.Text = ex.Message; }
        catch (FriendsException ex) { FriendsStatus.Text = ex.Message; }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            FriendsStatus.Text = "No connection. Try again in a moment.";
        }
    }

    /// <summary>Sends a friend request (or accepts theirs). Friends and pending requests just open the profile.</summary>
    private async Task AddPlayerAsync(FriendEntry player)
    {
        SuggestPopup.IsOpen = false;
        if (player.State is FriendState.Friend or FriendState.Outgoing or FriendState.Self)
        {
            FriendsStatus.Text = player.State == FriendState.Friend ? $"{player.Name} is already your friend."
                : player.State == FriendState.Outgoing ? $"You already sent {player.Name} a request." : "That's you.";
            ClearSearch();
            await OpenPlayerAsync(player.Id, player);
            return;
        }
        try
        {
            FriendsStatus.Text = player.State == FriendState.Incoming
                ? await AcceptAndSayAsync(player)
                : await _friends.SendRequestToAsync(player.Id, player.Name);
            ClearSearch();
            await RefreshFriendsAsync();
        }
        catch (Exception ex) when (ex is FriendsException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            FriendsStatus.Text = ex is FriendsException ? ex.Message : "No connection. Try again in a moment.";
        }
    }

    private async Task<string> AcceptAndSayAsync(FriendEntry player)
    {
        await _friends.AcceptAsync(player.Id);
        return $"You and {player.Name} are now friends.";
    }

    private void ClearSearch()
    {
        AddFriendName.Clear();
        SuggestPopup.IsOpen = false;
        SuggestList.ItemsSource = null;
    }

    private async void SuggestAdd_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // The row behind the button opens the profile; this button only adds.
        if (sender is FrameworkElement { Tag: FriendItem item }) await AddPlayerAsync(item.Entry);
    }

    private async void SuggestOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FriendItem item }) return;
        SuggestPopup.IsOpen = false;
        await OpenPlayerAsync(item.Entry.Id, item.Entry);
    }

    private void AddFriendName_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                AddFriend_Click(sender, e);
                break;
            case Key.Escape:
                SuggestPopup.IsOpen = false;
                break;
            case Key.Down when SuggestPopup.IsOpen && Suggestions.Length > 0:
                e.Handled = true;
                if (SuggestList.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement row)
                    (FindChild<Button>(row) as UIElement ?? row).Focus();
                break;
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private async void AddProfileFriend_Click(object sender, RoutedEventArgs e)
    {
        if (_viewed is { } viewed) await AddPlayerAsync(viewed);
    }

    private async void AcceptFriend_Click(object sender, RoutedEventArgs e)
    {
        if (_viewed is not { State: FriendState.Incoming } viewed) return;
        try
        {
            FriendsStatus.Text = await AcceptAndSayAsync(viewed);
            await RefreshFriendsAsync();
        }
        catch (Exception ex) when (ex is FriendsException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            FriendsStatus.Text = ex.Message;
        }
    }

    private async void RemoveFriend_Click(object sender, RoutedEventArgs e)
    {
        if (_viewed is not { } viewed || viewed.State is FriendState.None or FriendState.Self) return;
        // Removing a friend asks once in place: the first click turns the button into a confirm.
        if (_confirmRemove != viewed.Id || DateTime.UtcNow - _confirmRemoveAt > TimeSpan.FromSeconds(6))
        {
            _confirmRemove = viewed.Id;
            _confirmRemoveAt = DateTime.UtcNow;
            RemoveFriendButton.Content = "Click again to confirm";
            return;
        }
        _confirmRemove = null;
        try
        {
            await _friends.RemoveAsync(viewed.Id);
            FriendsStatus.Text = viewed.State switch
            {
                FriendState.Incoming => $"Declined {viewed.Name}'s request.",
                FriendState.Outgoing => $"Cancelled your request to {viewed.Name}.",
                _ => $"Removed {viewed.Name}."
            };
            ShowPlayer(viewed with { State = FriendState.None }, reset: false);
            await RefreshFriendsAsync();
        }
        catch (Exception ex) when (ex is FriendsException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            FriendsStatus.Text = ex.Message;
        }
    }

    // ------------------------------------------------------------------ profiles

    private void FriendList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (_suppressSelection || FriendList.SelectedItem is not FriendItem item) return;
        if (item.Entry.Id == _viewed?.Id && _viewed.State != FriendState.Self) return;
        ShowPlayer(item.Entry, reset: true);
    }

    /// <summary>Opens any player's profile and chat: from the list, a search, a notification, or yourself.</summary>
    private async Task OpenPlayerAsync(string id, FriendEntry? known)
    {
        var listed = _friendItems.FirstOrDefault(item => item.Entry.Id == id)?.Entry;
        var entry = listed ?? known;
        if (entry is null)
        {
            try { entry = await _friends.GetPlayerAsync(id); }
            catch (Exception ex) when (ex is FriendsException or System.Net.Http.HttpRequestException or TaskCanceledException) { }
        }
        if (entry is null)
        {
            FriendsStatus.Text = "That player couldn't be found.";
            return;
        }
        SelectInList(id);
        ShowPlayer(entry, reset: true);
    }

    private async void MyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!_friends.SignedIn || _friends.UserId is not { } me)
        {
            FriendsStatus.Text = _friendsError ?? "Friends aren't connected.";
            return;
        }
        try
        {
            if (await _friends.GetPlayerAsync(me) is not { } self) return;
            SelectInList(null);
            ShowPlayer(self with { State = FriendState.Self }, reset: true);
            AboutInput.Focus();
            AboutInput.CaretIndex = AboutInput.Text.Length;
        }
        catch (Exception ex) when (ex is FriendsException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            FriendsStatus.Text = "Couldn't load your profile. Check your connection.";
        }
    }

    private void ShowPlayer(FriendEntry entry, bool reset)
    {
        bool self = entry.State == FriendState.Self;
        _viewed = entry;
        if (reset)
        {
            _chatFriend = self ? null : entry.Id;
            _lastMessageId = 0;
            _chat.Clear();
            if (self)
            {
                AboutInput.Text = entry.About;
                AboutStatusReset();
            }
        }
        FriendEmpty.Visibility = Visibility.Collapsed;
        FriendDetail.Visibility = Visibility.Visible;
        FriendInitial.Text = entry.Name[..1].ToUpperInvariant();
        FriendAvatar.Fill = self && _profile is { } profile ? AvatarBrush(profile) ?? FriendAvatarBrush(entry) : FriendAvatarBrush(entry);
        FriendName.Text = self ? entry.Name + "  (you)" : entry.Name;
        FriendActivity.Text = entry.State switch
        {
            FriendState.Self => "This is how other players see your profile.",
            FriendState.Incoming => "Wants to be your friend",
            FriendState.Outgoing => "Friend request sent. Waiting for them to accept.",
            _ when entry.InGame => "Playing now  ·  " + entry.Activity,
            _ when entry.Online => "Online in the launcher",
            _ => entry.LastSeen == DateTimeOffset.MinValue ? "Offline" : "Last seen " + Ago(entry.LastSeen)
        };
        FriendActivity.Foreground = self ? Res("Muted") : entry.InGame ? Res("Accent") : entry.Online ? Res("Good") : Res("Muted");
        FriendPlayTime.Text = PlayTime(entry.PlaySeconds);
        FriendBestRound.Text = entry.BestRound > 0 ? entry.BestRound.ToString() : "—";
        FriendSessions.Text = entry.Sessions.ToString();

        // About me: editable on your own profile, read-only for everyone else.
        AboutEditor.Visibility = self ? Visibility.Visible : Visibility.Collapsed;
        FriendAbout.Visibility = self ? Visibility.Collapsed : Visibility.Visible;
        FriendAbout.Text = entry.About.Length > 0 ? entry.About : $"{entry.Name} hasn't written anything yet.";
        FriendAbout.Foreground = entry.About.Length > 0 ? Res("Ink") : Res("Faint");
        FriendAbout.FontStyle = entry.About.Length > 0 ? FontStyles.Normal : FontStyles.Italic;

        AddProfileFriendButton.Visibility = entry.State == FriendState.None ? Visibility.Visible : Visibility.Collapsed;
        AcceptFriendButton.Visibility = entry.State == FriendState.Incoming ? Visibility.Visible : Visibility.Collapsed;
        RemoveFriendButton.Visibility = entry.State is FriendState.None or FriendState.Self ? Visibility.Collapsed : Visibility.Visible;
        bool confirming = _confirmRemove == entry.Id && DateTime.UtcNow - _confirmRemoveAt <= TimeSpan.FromSeconds(6);
        RemoveFriendButton.Content = confirming ? "Click again to confirm" : entry.State switch
        {
            FriendState.Incoming => "Decline",
            FriendState.Outgoing => "Cancel request",
            _ => "Remove"
        };

        // Anyone can be messaged, friend or not; just not yourself.
        ChatBar.Visibility = self ? Visibility.Collapsed : Visibility.Visible;
        ChatScroll.Visibility = self ? Visibility.Collapsed : Visibility.Visible;
        UpdateChatNote();
        if (reset)
        {
            ChatScroll.ScrollToEnd();
            if (!self) _ = PollChatAsync();
        }
    }

    private void UpdateChatNote()
    {
        if (_viewed is not { } viewed) return;
        string note = viewed.State == FriendState.Self ? "Write something about yourself above. Everyone who visits your profile sees it."
            : _chat.Count > 0 ? ""
            : viewed.State == FriendState.Friend ? $"No messages yet. Say hi to {viewed.Name}!"
            : $"You can message {viewed.Name} even though you aren't friends.";
        ChatNote.Text = note;
        ChatNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AboutInput_TextChanged(object sender, TextChangedEventArgs e) => AboutStatusReset();

    private void AboutStatusReset()
    {
        AboutCount.Text = $"{AboutInput.Text.Length} / {FriendsService.AboutLimit}";
        AboutCount.Foreground = Res("Muted");
        SaveAboutButton.IsEnabled = AboutInput.Text.Trim() != (_viewed?.About ?? "");
    }

    private async void SaveAbout_Click(object sender, RoutedEventArgs e)
    {
        if (_viewed is not { State: FriendState.Self } self) return;
        SaveAboutButton.IsEnabled = false;
        try
        {
            string about = AboutInput.Text.Trim().ReplaceLineEndings(" ");
            await _friends.SetAboutAsync(about);
            _viewed = self with { About = about };
            AboutCount.Text = "Saved. Everyone can see it now.";
            AboutCount.Foreground = Res("Good");
        }
        catch (Exception ex) when (ex is FriendsException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            AboutCount.Text = ex is FriendsException ? ex.Message : "Not saved. Check your connection.";
            AboutCount.Foreground = Res("Bad");
            SaveAboutButton.IsEnabled = true;
        }
    }

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : span.TotalDays < 30 ? $"{(int)span.TotalDays} days ago"
            : when.LocalDateTime.ToString("d MMM yyyy");
    }

    // ------------------------------------------------------------------ chat

    private ChatBubble Bubble(ChatMessage message) => new(message.Body,
        message.SentAt.LocalDateTime.ToString(message.SentAt.LocalDateTime.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm"),
        message.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        message.Mine ? Res("Accent") : new SolidColorBrush(Color.FromRgb(0x22, 0x2B, 0x37)),
        message.Mine ? Res("AccentInk") : Res("Ink"));

    /// <summary>Appends messages newer than the last one shown. Safe to call often; overlapping calls are merged.</summary>
    private bool AppendMessages(IEnumerable<ChatMessage> messages)
    {
        bool atBottom = ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 24;
        bool added = false, mine = false;
        foreach (var message in messages.OrderBy(message => message.Id))
        {
            if (message.Id <= _lastMessageId) continue;
            _chat.Add(Bubble(message));
            _lastMessageId = message.Id;
            added = true;
            mine |= message.Mine;
        }
        if (added && (atBottom || mine)) ChatScroll.ScrollToEnd();
        if (added) UpdateChatNote();
        return added;
    }

    private async Task PollChatAsync()
    {
        if (_chatFriend is null || !_friends.SignedIn) return;
        if (_chatBusy) { _chatAgain = true; return; }
        _chatBusy = true;
        try
        {
            do
            {
                _chatAgain = false;
                string friend = _chatFriend!;
                var messages = await _friends.GetMessagesAsync(friend, _lastMessageId);
                if (friend != _chatFriend) { _chatAgain = _chatFriend is not null; continue; }
                AppendMessages(messages);
                // Only count a message as read once it has actually been on screen.
                if (ChatVisible && (_viewed?.Unread > 0 || messages.Any(message => !message.Mine)))
                {
                    await _friends.MarkReadAsync(friend);
                    if (_viewed?.Unread > 0) _ = RefreshFriendsAsync();
                }
            } while (_chatAgain && _chatFriend is not null);
        }
        catch (Exception ex) when (ex is FriendsException or System.Net.Http.HttpRequestException or TaskCanceledException) { }
        finally { _chatBusy = false; }
    }

    private async void SendChat_Click(object sender, RoutedEventArgs e)
    {
        if (_chatFriend is not { } friend) return;
        string text = ChatInput.Text.Trim();
        if (text.Length == 0) return;
        SendChatButton.IsEnabled = false;
        try
        {
            var sent = await _friends.SendMessageAsync(friend, text);
            ChatInput.Clear();
            if (sent is not null && friend == _chatFriend) AppendMessages([sent]);
            else await PollChatAsync();
            // A first message to a non-friend puts them in your list as a conversation.
            if (_viewed is { State: FriendState.None } && _friendItems.All(item => item.Entry.Id != friend)) _ = RefreshFriendsAsync();
        }
        catch (Exception ex) when (ex is FriendsException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            FriendsStatus.Text = "Message not sent. Check your connection.";
        }
        finally
        {
            SendChatButton.IsEnabled = true;
            ChatInput.Focus();
        }
    }

    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SendChat_Click(sender, e);
    }
}
