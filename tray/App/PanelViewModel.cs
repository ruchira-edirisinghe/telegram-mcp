using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using TelegramMCPTray.Core;

namespace TelegramMCPTray;

/// <summary>
/// Everything the panel shows, polled from the daemon every 2 s (which also tells the daemon to send
/// approvals here rather than to the Claude Code dialog). Lives on the UI thread.
/// </summary>
public sealed class PanelViewModel : Observable
{
    readonly DaemonApi _api;
    readonly Settings _settings;
    readonly DispatcherTimer _timer;
    readonly DispatcherTimer _flashTimer;
    readonly Dictionary<string, ApprovalVM> _approvals = [];
    readonly Dictionary<string, ContactVM> _contacts = [];
    string _sessionsKey = "", _recentKey = "";
    /// <summary>Recently sent chats the user clicked open, by chat id: kept when the rows are rebuilt.</summary>
    readonly HashSet<long> _openRecent = [];
    /// <summary>The poll in flight, which a second caller joins instead of starting another.</summary>
    Task? _poll;

    UiState? _state;
    bool _reachable;
    string? _problem;
    string? _flash;
    bool _flashOk = true;
    IReadOnlyList<Stat> _stats = [];
    HomeSection _section = HomeSection.Waiting;
    IReadOnlyCollection<string> _lastWaiting = [];
    bool _contactsPage;
    bool _awayPage, _awaySaving, _showTemplates, _awayQuietLoaded;
    bool _footerPage, _footerLoaded, _footerSaving;
    /// <summary>The footer's On / Off being saved, until a poll after it shows what was saved.</summary>
    bool? _footerPending;
    string _footerBody = "";
    FooterSpacing _footerSpacing = FooterSpacing.BlankLine;
    /// <summary>The value the away switch is saving, until a poll after it shows what was saved.</summary>
    bool? _awayPending;
    string _awayQuiet = "";
    /// <summary>The schedule rows by id, kept across polls so typing and open rows survive.</summary>
    readonly Dictionary<string, ScheduleVM> _schedules = [];
    /// <summary>Schedules just saved: the next poll overwrites their fields with what the daemon kept.</summary>
    readonly HashSet<string> _forceSchedule = [];
    string _search = "";
    string? _openNickname;
    ListenFilter _filter = ListenFilter.All;
    bool _showAllListen;
    /// <summary>"Chats Claude may listen to" shows this many until Show all.</summary>
    const int ShortList = 6;

    public PanelViewModel(DaemonApi api, Settings settings)
    {
        _api = api;
        _settings = settings;
        AvatarPhotos.Use(chatId => _api.AvatarAsync(chatId));
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await PollAsync();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            Flash = null;
        };
        ShowContacts = new Command(_ => ContactsPage = true);
        ShowMain = new Command(_ => ContactsPage = false);
        ReviewWaiting = new Command(_ => ShowWaiting());
        ShowAway = new Command(_ =>
        {
            ContactsPage = false;
            AwayPage = true;
        });
        ShowFooter = new Command(_ =>
        {
            ContactsPage = false;
            LoadFooter();
            FooterPage = true;
        });
        SetFooterOn = new Command(p => _ = SetFooterOnAsync(p is "on"), _ => FooterIdle && FooterShown);
        ToggleFooter = new Command(_ => _ = SetFooterOnAsync(!FooterOn), _ => FooterIdle && FooterShown);
        SaveFooter = new Command(_ => _ = SaveFooterAsync(), _ => FooterIdle && FooterShown && FooterError is null && FooterDirty);
        DiscardFooter = new Command(_ => LoadFooter(), _ => FooterDirty);
        ResetFooter = new Command(_ =>
        {
            var (spacing, body) = Footer.Split(Footer.Default);
            _footerSpacing = spacing;
            FooterBody = body;
        });
        UseFooterIdea = new Command(p => { if (p is string idea) FooterBody = idea; });
        SetFooterSpacingCommand = new Command(p => { if (p is ChoiceVM c) SetFooterSpacing((FooterSpacing)c.Value); });
        SetFooterStyle = new Command(p => { if (p is ChoiceVM c) FooterBody = Footer.Restyle(_footerBody, (FooterStyle)c.Value); });
        foreach (var (label, sp, tip) in new[]
                 {
                     ("Blank line", FooterSpacing.BlankLine, "An empty line between the message and the footer"),
                     ("New line", FooterSpacing.NewLine, "The footer on the line under the message"),
                     ("Same line", FooterSpacing.SameLine, "The footer right after the last word"),
                 })
            SpacingChoices.Add(new ChoiceVM(label, (int)sp, tip));
        foreach (var st in Enum.GetValues<FooterStyle>())
            StyleChoices.Add(new ChoiceVM(Footer.StyleLabel(st), (int)st, "Turn the letters into this style (emoji and symbols stay)"));
        ToggleAwayQuick = new Command(_ => _ = QuickAwayAsync(!AwayOn));
        ShowAdd = new Command(_ => ShowTemplates = !ShowTemplates, _ => CanAddSchedule);
        SaveQuiet = new Command(_ => _ = SaveQuietAsync(), _ => !_awaySaving && QuietError is null && QuietDirty);
        foreach (var t in Away.Templates(DateOnly.FromDateTime(DateTime.Today)))
            Templates.Add(new TemplateVM(t, new Command(_ => AddSchedule(t))));
        ClearSearch = new Command(_ => Search = "");
        OpenRules = new Command(_ => OpenFile("config.json"));
        OpenLog = new Command(_ => OpenFile("daemon.log"));
        Quit = new Command(_ => QuitRequested?.Invoke());
        ToggleMayListen = new Command(_ =>
        {
            _showAllListen = !_showAllListen;
            SyncContacts();
        });
        SetFilter = new Command(p => Filter = p switch { "on" => ListenFilter.On, "off" => ListenFilter.Off, _ => ListenFilter.All });
    }

    /// <summary>After every poll: the tray icon, tooltip and notifications follow.</summary>
    public event Action? Polled;
    public event Action? QuitRequested;

    public void Start()
    {
        _timer.Start();
        _ = PollAsync();
    }

    public UiState? State { get => _state; private set => Set(ref _state, value); }
    public bool Reachable { get => _reachable; private set => Set(ref _reachable, value); }
    public string? Problem { get => _problem; private set => Set(ref _problem, value); }

    public Status Header => Presenter.Header(State, Reachable);
    public bool ShowContent => Reachable && State is not null;
    public string? ShownProblem => ShowContent ? null : Problem;
    public string? ConfigError => ShowContent ? State?.ConfigError : null;

    /// <summary>
    /// Home's tiles: waiting, listening, sent. The same three are updated in place (numbers, chips, which
    /// one is selected), so the one with keyboard focus keeps it.
    /// </summary>
    public ObservableCollection<StatVM> Stats { get; } = [];

    /// <summary>Which section Home shows under its tiles.</summary>
    public HomeSection Section
    {
        get => _section;
        set
        {
            if (!Set(ref _section, value)) return;
            Raise(nameof(WaitingShown));
            Raise(nameof(ListeningShown));
            Raise(nameof(SentShown));
            Raise(nameof(AutoReplyShown));
            SyncTiles();
        }
    }

    public bool WaitingShown => _section == HomeSection.Waiting;
    public bool ListeningShown => _section == HomeSection.Listening;
    public bool SentShown => _section == HomeSection.Sent;
    public bool AutoReplyShown => _section == HomeSection.AutoReply;

    /// <summary>Home on Waiting: from the Contacts page's "Review" and from a draft's notification.</summary>
    public void ShowWaiting()
    {
        ContactsPage = false;
        Section = HomeSection.Waiting;
    }

    void SyncTiles()
    {
        if (Stats.Count != _stats.Count)
        {
            Stats.Clear();
            for (var i = 0; i < _stats.Count; i++)
            {
                var section = (HomeSection)i;
                Stats.Add(new StatVM(section, _stats[i], new Command(_ => Section = section)));
            }
        }
        for (var i = 0; i < Stats.Count; i++)
        {
            Stats[i].Stat = _stats[i];
            Stats[i].Selected = Stats[i].Section == _section;
        }
    }

    public ObservableCollection<ApprovalVM> Approvals { get; } = [];
    public ObservableCollection<SessionVM> Listening { get; } = [];
    public ObservableCollection<RecentChatVM> Recent { get; } = [];
    public ObservableCollection<ContactVM> ContactRows { get; } = [];

    public string? ApprovalCount => Approvals.Count > 0 ? Approvals.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    public bool NoApprovals => Approvals.Count == 0;
    public bool NoListening => Listening.Count == 0;
    public bool NoRecent => Recent.Count == 0;

    public string? Flash { get => _flash; private set => Set(ref _flash, value); }

    /// <summary>Whether the flash reports something that worked (a check) or didn't (a warning, in red).</summary>
    public bool FlashOk { get => _flashOk; private set => Set(ref _flashOk, value); }

    // ---- the Contacts page --------------------------------------------------------------

    /// <summary>Contacts when true, Home when false. Either way the Away page closes, so the menu and Ctrl+1 / Ctrl+2 always land where they say.</summary>
    public bool ContactsPage
    {
        get => _contactsPage;
        set
        {
            AwayPage = false;
            FooterPage = false;
            if (!Set(ref _contactsPage, value)) return;
            Raise(nameof(MainPage));
            if (!value) Search = "";
        }
    }

    public bool MainPage => !_contactsPage && !_awayPage && !_footerPage;

    // ---- the Away page --------------------------------------------------------------------

    public bool AwayPage
    {
        get => _awayPage;
        private set
        {
            if (!Set(ref _awayPage, value)) return;
            Raise(nameof(MainPage));
        }
    }

    /// <summary>The daemon sent away schedules, so the page can show them.</summary>
    public bool AwayShown => ShowContent && State?.Away?.Schedules is not null;

    /// <summary>Instead of the page: a daemon too old to know about away, or about schedules.</summary>
    public string? AwayPlaceholder =>
        !ShowContent ? null : State!.Away is null ? Away.OlderDaemon : State.Away.Schedules is null ? Away.NoSchedules : null;

    public string? AwayStatus => State?.Away is { } a ? Away.Status(a) : null;

    /// <summary>Away is on and a schedule is replying right now.</summary>
    public bool AwayOnNow => State?.Away is { Enabled: true, Active: true };

    /// <summary>The master switch (Home → Auto-reply and the Away tab), showing the value being saved until a poll brings it back.</summary>
    public bool AwayOn => _awayPending ?? State?.Away?.Enabled ?? false;
    /// <summary>Off while a change saves and until a poll that started after it shows what was saved.</summary>
    public bool AwayQuickIdle => !_awaySaving && _awayPending is null && State?.Away is not null;
    public string AwayQuickTip => AwayOn ? "Turn every away reply off" : "Turn away replies on, for the schedules on the Away tab";

    /// <summary>"Daytime until 21:00" on the main page while replies can go out.</summary>
    public string? AwayBadge => ShowContent ? Away.Badge(State?.Away) : null;

    /// <summary>The Away tab's headline.</summary>
    public string AwayHeadline => AwayOn ? "Away replies are on" : "Away replies are off";

    /// <summary>The sun by day, the moon at night (or while off).</summary>
    public string AwayIcon => State?.Away is { } a && Away.Current(a) is { } c ? (Away.IsNight(c.From, c.Until) ? "moon" : "sun") : "moon";

    /// <summary>"1 chat is waiting for you…" while a reply waits out a schedule's delay.</summary>
    public string? AwayWaiting => State?.Away is { Waiting: > 0 } a
        ? $"{a.Waiting} chat{(a.Waiting == 1 ? " is" : "s are")} waiting for you. Read {(a.Waiting == 1 ? "it" : "them")} or reply and no away reply goes out."
        : null;

    /// <summary>Today's timeline: a lane per saved schedule.</summary>
    public IReadOnlyList<TimelineLane> TimelineLanes { get; private set; } = [];
    public double TimelineNow => DateTime.Now.TimeOfDay.TotalMinutes / 1440;

    public ObservableCollection<ScheduleVM> Schedules { get; } = [];
    public ObservableCollection<TemplateVM> Templates { get; } = [];

    /// <summary>The template chips under "Add a schedule".</summary>
    public bool ShowTemplates
    {
        get => _showTemplates;
        set => Set(ref _showTemplates, value);
    }

    public bool CanAddSchedule => Schedules.Count < Away.SchedulesMax;
    public string? NoSchedulesYet => AwayShown && Schedules.Count == 0 ? "No schedules yet. Add one to start replying while you're away." : null;

    /// <summary>Quiet minutes as typed (a whole number, 0 to 1440).</summary>
    public string AwayQuiet
    {
        get => _awayQuiet;
        set
        {
            if (!Set(ref _awayQuiet, value ?? "")) return;
            Raise(nameof(QuietError));
            Raise(nameof(QuietDirty));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string? QuietError => Away.Quiet(_awayQuiet) is null ? $"A whole number of minutes, 0 to {Away.QuietMax}." : null;
    public bool QuietDirty => State?.Away is { } a && Away.Quiet(_awayQuiet) is { } q && q != a.QuietMinutes;

    /// <summary>Home's and the Away tab's switch: away on or off at once. The schedules stay as saved.</summary>
    async Task QuickAwayAsync(bool on)
    {
        // Still saving, or waiting for the poll after a save: refuse, and put back the switch that was clicked.
        if (_awaySaving || _awayPending is not null || State?.Away is null)
        {
            RaiseAwayQuick();
            return;
        }
        _awayPending = on;
        _awaySaving = true;
        RaiseAwayQuick();
        CommandManager.InvalidateRequerySuggested();
        try
        {
            ActionResult r;
            try
            {
                r = await _api.SaveAwayAsync(new AwayChange(Enabled: on));
            }
            finally
            {
                _awaySaving = false;
                CommandManager.InvalidateRequerySuggested();
            }
            ShowFlash(r.Text, r.Ok);
            await PollFreshAsync();
        }
        finally
        {
            _awayPending = null;
            RaiseAwayQuick();
        }
    }

    void RaiseAwayQuick()
    {
        Raise(nameof(AwayOn));
        Raise(nameof(AwayQuickIdle));
        Raise(nameof(AwayQuickTip));
        Raise(nameof(AwayHeadline));
    }

    async Task SaveQuietAsync()
    {
        if (Away.Quiet(_awayQuiet) is not { } q) return;
        await SaveAwayChangeAsync(new AwayChange(QuietMinutes: q), null);
        _awayQuietLoaded = false;
    }

    /// <summary>The schedules as saved, each passed through `map` (null drops it), then `added` at the end.</summary>
    List<ScheduleData> SavedSchedules(Func<ScheduleData, ScheduleData?> map, ScheduleData? added = null)
    {
        var list = (State?.Away?.Schedules ?? []).Select(s => map(s.ToData())).OfType<ScheduleData>().ToList();
        if (added is not null) list.Add(added);
        return list;
    }

    /// <summary>Save one schedule's edits (or add a new one); the others go back as saved.</summary>
    public async Task SaveScheduleAsync(ScheduleVM vm)
    {
        if (vm.Error is not null) return;
        var data = vm.Data();
        var list = vm.IsNew ? SavedSchedules(s => s, data) : SavedSchedules(s => s.Id == vm.Id ? data : s);
        if (await SaveAwayChangeAsync(new AwayChange(Schedules: list), vm, vm.IsNew ? $"Added {data.Name}." : $"Saved {data.Name}."))
        {
            vm.IsOpen = false;
            _forceSchedule.Add(vm.Id);
        }
        await PollFreshAsync();
    }

    public async Task SetScheduleEnabledAsync(ScheduleVM vm, bool on)
    {
        if (vm.IsNew) return;
        await SaveAwayChangeAsync(new AwayChange(Schedules: SavedSchedules(s => s.Id == vm.Id ? s with { Enabled = on } : s)), vm,
            $"{vm.Title} is {(on ? "on" : "off")}.");
        await PollFreshAsync();
    }

    public async Task DeleteScheduleAsync(ScheduleVM vm)
    {
        if (await SaveAwayChangeAsync(new AwayChange(Schedules: SavedSchedules(s => s.Id == vm.Id ? null : s)), vm, $"Deleted {vm.Title}."))
        {
            _schedules.Remove(vm.Id);
            Schedules.Remove(vm);
            RaiseSchedules();
        }
        await PollFreshAsync();
    }

    /// <summary>A new schedule's Cancel: it was never saved, so it just goes.</summary>
    public void DropNewSchedule(ScheduleVM vm)
    {
        _schedules.Remove(vm.Id);
        Schedules.Remove(vm);
        RaiseSchedules();
    }

    void AddSchedule(ScheduleTemplate t)
    {
        ShowTemplates = false;
        if (!CanAddSchedule) return;
        var id = Away.NewId(_schedules.Keys);
        var vm = new ScheduleVM(this, null, t, id) { Index = Schedules.Count, IsOpen = true };
        foreach (var other in Schedules) other.IsOpen = false;
        _schedules[id] = vm;
        Schedules.Add(vm);
        RaiseSchedules();
    }

    /// <summary>POST /ui/away. The row (when there is one) is busy meanwhile. True when the daemon saved it.</summary>
    async Task<bool> SaveAwayChangeAsync(AwayChange change, ScheduleVM? vm, string? okText = null)
    {
        if (vm is not null) vm.Busy = true;
        _awaySaving = true;
        CommandManager.InvalidateRequerySuggested();
        ActionResult r;
        try
        {
            r = await _api.SaveAwayAsync(change);
        }
        finally
        {
            _awaySaving = false;
            if (vm is not null) vm.Busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
        ShowFlash(r.Ok && okText is not null ? okText : r.Text, r.Ok);
        return r.Ok;
    }

    /// <summary>The schedule rows follow the daemon's list; new, unsaved ones stay at the end.</summary>
    void SyncSchedules(DateTime now)
    {
        var saved = State?.Away?.Schedules ?? [];
        foreach (var gone in _schedules.Where(kv => !kv.Value.IsNew && saved.All(s => s.Id != kv.Key)).Select(kv => kv.Key).ToList())
            _schedules.Remove(gone);
        var rows = new List<ScheduleVM>();
        foreach (var s in saved)
        {
            if (_schedules.TryGetValue(s.Id, out var vm)) vm.Update(s, force: _forceSchedule.Remove(s.Id));
            else _schedules[s.Id] = vm = new ScheduleVM(this, s, null, s.Id);
            rows.Add(vm);
        }
        rows.AddRange(_schedules.Values.Where(v => v.IsNew && saved.All(s => s.Id != v.Id)));
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Index = i;
            rows[i].RefreshPreview();
        }
        ListSync.Apply(Schedules, rows);

        var on = State?.Away?.Enabled == true;
        TimelineLanes = rows.Where(r => r.Saved is not null)
            .Select(r => new TimelineLane(r.Color, Away.TodaySpans(r.Saved!, now), on && r.Saved!.Enabled, r.Title)).ToList();
        Raise(nameof(TimelineLanes));
        Raise(nameof(TimelineNow));
        if (!_awayQuietLoaded && State?.Away is { } a)
        {
            _awayQuietLoaded = true;
            AwayQuiet = a.QuietMinutes.ToString(CultureInfo.InvariantCulture);
        }
        RaiseSchedules();
    }

    void RaiseSchedules()
    {
        Raise(nameof(CanAddSchedule));
        Raise(nameof(NoSchedulesYet));
        Raise(nameof(QuietDirty));
    }

    // ---- the Footer page ----------------------------------------------------------------

    public bool FooterPage
    {
        get => _footerPage;
        private set
        {
            if (!Set(ref _footerPage, value)) return;
            Raise(nameof(MainPage));
        }
    }

    /// <summary>The daemon sent the footer's text, so the page can edit it.</summary>
    public bool FooterShown => ShowContent && State?.FooterText is not null;
    public string? FooterPlaceholder => ShowContent && State!.FooterText is null ? Footer.OlderDaemon : null;

    /// <summary>On or off, showing the value being saved until a poll brings it back.</summary>
    public bool FooterOn => _footerPending ?? State?.FooterOn ?? false;
    public bool FooterOff => !FooterOn;
    public bool FooterIdle => !_footerSaving && _footerPending is null;

    public string FooterBody
    {
        get => _footerBody;
        set
        {
            if (!Set(ref _footerBody, value ?? "")) return;
            RaiseFooter();
        }
    }

    public ObservableCollection<ChoiceVM> SpacingChoices { get; } = [];
    public ObservableCollection<ChoiceVM> StyleChoices { get; } = [];
    public IReadOnlyList<string> FooterIdeas => Footer.Ideas;

    public string FooterComposed => Footer.Compose(_footerSpacing, _footerBody);
    public string? FooterError => Footer.Check(_footerBody);
    public bool FooterDirty => State?.FooterText is { } t && FooterComposed != t;
    public string FooterCounter => $"{_footerBody.Trim().Length} / {Footer.Max - 2}";
    public string FooterSample => Footer.Sample;

    /// <summary>A message as people get it: the sample with the footer as typed, or without one while it's off.</summary>
    public string FooterPreview => FooterOn ? Footer.Preview(Footer.Sample, FooterComposed) : Footer.Sample;

    public string FooterStatus => FooterOn
        ? "On: added to every message sent for you"
        : "Off: messages go out exactly as written";

    void RaiseFooter()
    {
        var style = Footer.Detect(_footerBody);
        foreach (var c in StyleChoices) c.On = (FooterStyle)c.Value == style;
        foreach (var c in SpacingChoices) c.On = (FooterSpacing)c.Value == _footerSpacing;
        foreach (var n in new[]
                 {
                     nameof(FooterComposed), nameof(FooterError), nameof(FooterDirty), nameof(FooterCounter), nameof(FooterPreview),
                     nameof(FooterOn), nameof(FooterOff), nameof(FooterIdle), nameof(FooterStatus),
                 })
        {
            Raise(n);
        }
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Fills the editor from what the daemon has. Only when the page opens and after a save: polling leaves typing alone.</summary>
    void LoadFooter()
    {
        if (State?.FooterText is not { } text) return;
        _footerLoaded = true;
        var (spacing, body) = Footer.Split(text);
        _footerSpacing = spacing;
        _footerBody = body;
        Raise(nameof(FooterBody));
        RaiseFooter();
    }

    void SetFooterSpacing(FooterSpacing spacing)
    {
        _footerSpacing = spacing;
        RaiseFooter();
    }

    /// <summary>On / Off saves at once; the words keep waiting for Save.</summary>
    async Task SetFooterOnAsync(bool on)
    {
        if (!FooterIdle || on == FooterOn || State?.FooterText is null) return;
        _footerPending = on;
        RaiseFooter();
        try
        {
            ActionResult r;
            _footerSaving = true;
            try
            {
                r = await _api.SaveFooterAsync(null, on);
            }
            finally
            {
                _footerSaving = false;
            }
            ShowFlash(r.Ok ? (on ? "The footer is on." : "The footer is off. Its text is kept for when you switch it back on.") : r.Text, r.Ok);
            await PollFreshAsync();
        }
        finally
        {
            _footerPending = null;
            RaiseFooter();
        }
    }

    async Task SaveFooterAsync()
    {
        if (FooterError is not null) return;
        var sent = FooterComposed;
        _footerSaving = true;
        RaiseFooter();
        ActionResult r;
        try
        {
            // Saving the words keeps it on or off as it is.
            r = await _api.SaveFooterAsync(sent, FooterOn);
        }
        finally
        {
            _footerSaving = false;
            RaiseFooter();
        }
        ShowFlash(r.Ok ? (FooterOn ? "Saved. New messages carry this footer." : "Saved. It's kept for when you switch the footer on.") : r.Text, r.Ok);
        await PollFreshAsync();
        RaiseFooter();
    }

    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value ?? "")) return;
            Raise(nameof(SearchEmpty));
            SyncContacts();
        }
    }

    public bool SearchEmpty => _search.Length == 0;

    /// <summary>One contact row is open at a time.</summary>
    public string? OpenNickname
    {
        get => _openNickname;
        set
        {
            if (!Set(ref _openNickname, value)) return;
            foreach (var c in _contacts.Values) c.OpenChanged();
        }
    }

    public string ContactsSummary => Contacts.Summary(State?.Contacts?.Count ?? 0, State?.Contacts?.Count(c => c.AutoReply) ?? 0);

    /// <summary>Instead of rows: no contacts yet, none matching, or a daemon without the Contacts page.</summary>
    public string? ContactsPlaceholder =>
        !ShowContent ? null
        : State!.Contacts is null ? Contacts.OlderDaemon
        : State.Contacts.Count == 0 ? Contacts.Empty
        : ContactRows.Count == 0 ? Contacts.NoneShown(_search, _filter)
        : null;

    /// <summary>The Contacts tab's filter: all, those Claude may listen to, or the rest.</summary>
    public ListenFilter Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            Raise(nameof(FilterAll));
            Raise(nameof(FilterOn));
            Raise(nameof(FilterOff));
            SyncContacts();
        }
    }

    public bool FilterAll => _filter == ListenFilter.All;
    public bool FilterOn => _filter == ListenFilter.On;
    public bool FilterOff => _filter == ListenFilter.Off;

    /// <summary>Home → Listening: every saved chat with its listen switch, alphabetical so a row doesn't jump when switched.</summary>
    public ObservableCollection<ContactVM> MayListen { get; } = [];

    /// <summary>The Away tab: every saved person with a switch for whether they get away replies (away.skip).</summary>
    public ObservableCollection<ContactVM> AwayPeople { get; } = [];

    /// <summary>Instead of that list: a daemon without the skip list, or no saved people yet.</summary>
    public string? AwayPeoplePlaceholder =>
        !AwayShown ? null : State?.Away?.Skip is null ? Away.OlderDaemon : AwayPeople.Count == 0 ? "No saved people yet. Save a chat in Contacts to choose here." : null;

    internal bool AwaySkips(long chatId) => Away.Skips(State?.Away, chatId);

    /// <summary>A person's away switch: off puts them on away.skip, on takes them off. Saved at once.</summary>
    public async Task SetAwayReplyAsync(ContactVM vm, bool gets)
    {
        if (State?.Away is not { Skip: not null } a || _awaySaving)
        {
            if (State?.Away is { Skip: null }) ShowFlash(Away.OlderDaemon, false);
            vm.AwayResync();
            return;
        }
        vm.AwayPending = gets;
        try
        {
            await SaveAwayChangeAsync(new AwayChange(Skip: Away.SkipWith(a, vm.ChatId, vm.Nickname, skip: !gets)), null,
                gets ? $"{vm.Nickname} gets away replies again." : $"{vm.Nickname} won't get away replies.");
            await PollFreshAsync();
        }
        finally
        {
            vm.AwayPending = null;
        }
    }

    /// <summary>"Show all 9" / "Show fewer" under that list, or null when it's short.</summary>
    public string? MayListenMore =>
        (State?.Contacts?.Count ?? 0) <= ShortList ? null : _showAllListen ? "Show fewer" : $"Show all {State!.Contacts!.Count}";

    /// <summary>Instead of that list: no saved chats yet, or a daemon without the Contacts page.</summary>
    public string? MayListenPlaceholder =>
        !ShowContent ? null : State!.Contacts is null ? Contacts.OlderDaemon : State.Contacts.Count == 0 ? Contacts.Empty : null;

    public string? WaitingBanner => Reachable && State is { Approvals.Count: > 0 } s ? Format.Waiting(s.Approvals.Count) : null;

    public ICommand ShowContacts { get; }
    public ICommand ShowAway { get; }
    /// <summary>Home → Auto-reply and the Away tab: the away switch.</summary>
    public ICommand ToggleAwayQuick { get; }
    /// <summary>"Add a schedule": shows the templates to start from.</summary>
    public ICommand ShowAdd { get; }
    public ICommand SaveQuiet { get; }
    public ICommand ShowFooter { get; }
    /// <summary>The footer's On / Off: "on" or "off", saved at once.</summary>
    public ICommand SetFooterOn { get; }
    public ICommand ToggleFooter { get; }
    public ICommand SaveFooter { get; }
    public ICommand DiscardFooter { get; }
    public ICommand ResetFooter { get; }
    public ICommand UseFooterIdea { get; }
    public ICommand SetFooterSpacingCommand { get; }
    public ICommand SetFooterStyle { get; }
    public ICommand ShowMain { get; }
    /// <summary>The Contacts page's "N drafts waiting · Review": Home, on Waiting.</summary>
    public ICommand ReviewWaiting { get; }
    public ICommand ClearSearch { get; }
    public ICommand OpenRules { get; }
    public ICommand OpenLog { get; }
    public ICommand Quit { get; }
    public ICommand ToggleMayListen { get; }
    public ICommand SetFilter { get; }

    // ---- talking to the daemon ------------------------------------------------------------

    /// <summary>Polls the daemon, or joins the poll already in flight.</summary>
    public Task PollAsync() => _poll is { IsCompleted: false } running ? running : _poll = RunPollAsync();

    /// <summary>
    /// A poll that starts after now. One already in flight may have read the state before a change that
    /// just saved, so wait it out and poll again.
    /// </summary>
    async Task PollFreshAsync()
    {
        if (_poll is { IsCompleted: false } running) await running;
        await PollAsync();
    }

    async Task RunPollAsync()
    {
        var r = await _api.PollAsync();
        Reachable = r.Reachable;
        Problem = r.Problem;
        if (r.State is { } s) State = s;
        Refresh();
        Polled?.Invoke();
    }

    public async Task DecideAsync(ApprovalVM approval, Decision decision)
    {
        approval.Busy = true;
        var r = await _api.DecideAsync(approval.Id, decision);
        approval.Busy = false;
        ShowFlash(r.Text, r.Ok);
        await PollAsync();
    }

    /// <summary>Ack or snooze a page from the alarm window or its notification.</summary>
    public async Task PageAsync(string id, bool ack, int minutes)
    {
        var r = await _api.PageAsync(id, ack, minutes);
        ShowFlash(r.Text, r.Ok);
        await PollAsync();
    }

    /// <summary>From a notification button, which may outlive its row.</summary>
    public async Task DecideAsync(string id, Decision decision)
    {
        if (_approvals.TryGetValue(id, out var vm)) await DecideAsync(vm, decision);
        else
        {
            var r = await _api.DecideAsync(id, decision);
            ShowFlash(r.Text, r.Ok);
            await PollAsync();
        }
    }

    public async Task EditContactAsync(ContactVM contact, ContactChange change)
    {
        contact.Busy = true;
        var r = await _api.EditContactAsync(change);
        contact.Busy = false;
        // After a rename the row has a new name: keep it open.
        if (r.Ok && r.Nickname is { } now && OpenNickname == contact.Nickname) OpenNickname = now;
        ShowFlash(r.Text, r.Ok);
        await PollAsync();
    }

    /// <summary>The Page button: "/page &lt;reason&gt;" to that person. The reason box clears once it went out.</summary>
    public async Task PageContactAsync(ContactVM contact)
    {
        contact.Paging = true;
        var r = await _api.PageContactAsync(contact.Nickname, contact.PageReason);
        contact.Paging = false;
        if (r.Ok) contact.PageReason = "";
        ShowFlash(r.Text, r.Ok);
        await PollAsync();
    }

    /// <summary>
    /// The listen switches: turn auto-reply on or off for these saved chats. The daemon passes a chat's
    /// messages to sessions only while it's on, so off pauses listening there (the watches stay) and on
    /// resumes it. One flash for the lot; it stops at the first one the daemon refuses, says which it had
    /// already switched (those stay switched), and the switches then show what is saved.
    /// These chats' switches stay disabled, showing the value clicked, until a poll that started after the
    /// change has brought what the daemon saved.
    /// </summary>
    public async Task SetListeningAsync(IReadOnlyList<string> nicknames, bool on, string label)
    {
        var rows = nicknames.Select(n => _contacts.GetValueOrDefault(n)).OfType<ContactVM>().ToList();
        // One of them is still saving a change (its switches are disabled, so this is a quick second
        // click): refuse, and put back the switch that was clicked.
        if (nicknames.Count == 0 || rows.Any(r => r.Busy))
        {
            foreach (var r in rows) r.Resync();
            return;
        }
        foreach (var r in rows)
        {
            r.Busy = true;
            r.Pending = on;
        }
        try
        {
            List<string> done = [];
            ActionResult? failed = null;
            foreach (var n in nicknames)
            {
                var r = await _api.EditContactAsync(new ContactChange(n, AutoReply: on));
                if (!r.Ok)
                {
                    failed = r;
                    break;
                }
                done.Add(n);
            }
            if (failed is { } f) ShowFlash(Contacts.ListenStopped(on, done, f.Text), ok: false);
            else ShowFlash(Contacts.ListenFlash(on, label));
            await PollFreshAsync();
        }
        finally
        {
            foreach (var r in rows)
            {
                r.Pending = null;
                r.Busy = false;
            }
        }
    }

    /// <summary>A chat's listen state or busy state changed: the sessions' "all" switches follow.</summary>
    internal void ListenChanged()
    {
        foreach (var s in Listening) s.Sync();
    }

    void ToggleRecent(RecentChatVM row)
    {
        row.IsOpen = !row.IsOpen;
        if (row.IsOpen) _openRecent.Add(row.ChatId);
        else _openRecent.Remove(row.ChatId);
    }

    public void ShowFlash(string text, bool ok = true)
    {
        FlashOk = ok;
        Flash = text;
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    // ---- keeping the lists in step ------------------------------------------------------

    void Refresh()
    {
        var now = DateTimeOffset.Now;
        var s = ShowContent ? State : null;

        IReadOnlyList<UiApproval> approvals = s?.Approvals ?? [];
        foreach (var gone in _approvals.Keys.Except(approvals.Select(a => a.Id)).ToList()) _approvals.Remove(gone);
        var rows = approvals.Select(a => _approvals.TryGetValue(a.Id, out var vm) ? vm : _approvals[a.Id] = new ApprovalVM(this, a)).ToList();
        foreach (var vm in rows) vm.Ago = Format.Ago(vm.Item.CreatedAt, now);
        ListSync.Apply(Approvals, rows);

        // Contacts first: the sessions' switches are the contact rows' own.
        IReadOnlyList<UiContact> contacts = s?.Contacts ?? [];
        var replaced = false;
        foreach (var gone in _contacts.Keys.Except(contacts.Select(c => c.Nickname)).ToList())
        {
            _contacts.Remove(gone);
            replaced = true;
        }
        foreach (var c in contacts)
        {
            if (_contacts.TryGetValue(c.Nickname, out var vm)) vm.Update(c, s!.DefaultPolicy);
            else
            {
                _contacts[c.Nickname] = new ContactVM(this, c, s!.DefaultPolicy);
                replaced = true;
            }
        }
        SyncContacts();

        // Sessions and recent sends: rebuilt when they change (or a minute passes, for "ago"). A session's
        // switches bind to the contact rows, so a chat switched on or off doesn't rebuild it (and the
        // switch keeps keyboard focus); which row each watch uses is part of the key.
        IReadOnlyList<UiSession> sessions = s is null ? [] : Presenter.Listening(s);
        var rowFor = sessions.SelectMany(x => x.Watches).Select(w => Presenter.ListeningSwitch(s!, w.ChatId)?.Nickname ?? "-");
        if (Changed(ref _sessionsKey, JsonSerializer.Serialize(sessions) + string.Join(",", rowFor) + (s?.Contacts is null ? "?" : "")) || replaced)
        {
            ListSync.Apply(Listening, sessions.Select(x => SessionVM.From(this, x, s!.Contacts is not null,
                id => Presenter.ListeningSwitch(s!, id) is { } c ? _contacts.GetValueOrDefault(c.Nickname) : null)).ToList());
        }
        ListenChanged();
        IReadOnlyList<RecentChat> recent = s is null ? [] : Presenter.RecentChats(s.Recent);
        if (Changed(ref _recentKey, JsonSerializer.Serialize(recent) + now.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture)))
        {
            _openRecent.IntersectWith(recent.Select(c => c.ChatId));
            ListSync.Apply(Recent, recent.Select(c => RecentChatVM.From(c, s!.Footer, now, _openRecent.Contains(c.ChatId), ToggleRecent)).ToList());
        }

        SyncSchedules(now.LocalDateTime);
        // The page was opened before the daemon answered: fill the editor once it does.
        if (_footerPage && !_footerLoaded) LoadFooter();

        _stats = s is null ? [] : Presenter.Stats(s);
        if (s is not null)
        {
            var waiting = approvals.Select(a => a.Id).ToList();
            Section = Presenter.SectionAfterPoll(_section, _lastWaiting, waiting);
            _lastWaiting = waiting;
        }
        SyncTiles();

        foreach (var name in new[]
                 {
                     nameof(Header), nameof(ShowContent), nameof(ShownProblem), nameof(ConfigError), nameof(ApprovalCount),
                     nameof(NoApprovals), nameof(NoListening), nameof(NoRecent), nameof(ContactsSummary), nameof(WaitingBanner),
                     nameof(AwayShown), nameof(AwayPlaceholder), nameof(AwayStatus), nameof(AwayBadge), nameof(AwayOnNow),
                     nameof(AwayOn), nameof(AwayQuickIdle), nameof(AwayQuickTip), nameof(AwayHeadline), nameof(AwayIcon), nameof(AwayWaiting),
                     nameof(FooterShown), nameof(FooterPlaceholder), nameof(FooterOn), nameof(FooterOff), nameof(FooterDirty), nameof(FooterPreview),
                     nameof(FooterStatus), nameof(FooterIdle),
                 })
        {
            Raise(name);
        }
    }

    void SyncContacts()
    {
        IReadOnlyList<UiContact> all = State?.Contacts ?? [];
        var shown = Contacts.Filter(Contacts.FilterListening(all, _filter), _search);
        ListSync.Apply(ContactRows, shown.Where(c => _contacts.ContainsKey(c.Nickname)).Select(c => _contacts[c.Nickname]).ToList());
        Raise(nameof(ContactsPlaceholder));

        var sorted = all.Where(c => _contacts.ContainsKey(c.Nickname))
            .OrderBy(c => c.Nickname, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => _contacts[c.Nickname]);
        ListSync.Apply(MayListen, (_showAllListen ? sorted : sorted.Take(ShortList)).ToList());
        Raise(nameof(MayListenMore));
        Raise(nameof(MayListenPlaceholder));

        ListSync.Apply(AwayPeople, sorted.Where(c => c.IsPerson).ToList());
        foreach (var vm in AwayPeople) vm.AwayResync();
        Raise(nameof(AwayPeoplePlaceholder));
    }

    static bool Changed(ref string key, string now)
    {
        if (key == now) return false;
        key = now;
        return true;
    }

    void OpenFile(string name)
    {
        var path = Path.Combine(_settings.Home, name);
        if (!File.Exists(path))
        {
            ShowFlash($"There is no {name} in {_settings.Home} yet.", ok: false);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No app is set to open .json or .log files.
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path } });
        }
    }
}
