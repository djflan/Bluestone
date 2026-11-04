using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Sequencing;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Presentation;
using Bluestone.Profiles;

namespace Bluestone.Tests.Unit.Presentation;

/// <summary>
/// Floating plugin editors: following the selected track, pinning, coalesced requests, keys sent to Bluestone, releases
/// so no forwarded key is left held, and the header on each window (Bypass, Pin, Keys) kept in step with the panel.
/// </summary>
public sealed class PluginEditorWindowsTests : IAsyncLifetime
{
    private readonly VirtualClock _clock = new(TimeSpan.FromSeconds(5));
    private readonly LoopbackMidiProvider _provider;
    private readonly EndpointDirectory _endpoints;
    private readonly ProjectSession _session = new();
    private readonly MainViewModel _vm;
    private readonly FakeEditorHost _host = new();
    private readonly PluginEditorWindows _windows;
    private readonly List<PluginEditorKey> _forwarded = [];

    public PluginEditorWindowsTests()
    {
        _provider = new LoopbackMidiProvider(_clock);
        _endpoints = new EndpointDirectory([_provider]);
        var playback = new PlaybackController(_session, _endpoints, ProfileCatalog.Empty, _clock, startThread: false);
        _vm = new MainViewModel(_session, playback, _endpoints, new NoUi(), new ImmediateDispatcher());
        _windows = new PluginEditorWindows(_vm, _host, new ImmediateDispatcher());
        _windows.KeyForwarded += (_, key) => _forwarded.Add(key);
    }

    public async ValueTask InitializeAsync()
    {
        await _vm.InitializeAsync();
        foreach (var name in new[] { "A", "B" })
        {
            var track = Track.Create(name, TrackRole.Hybrid);
            _session.Execute(ProjectCommands.AddTrack(track));
            _session.Execute(DeviceCommands.InsertDevice(track.Id, TestDevices.Instance(TestDevices.Synth), TestDevices.Lookup));
        }

        await Task.Yield();
    }

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        _endpoints.Dispose();
        _provider.Dispose();
    }

    private DeviceId DeviceOn(int track) => _session.Project.ChainOf(_session.Project.Sequence.Tracks[track].Id)!.Devices[0].Id;

    // MainViewModel notifies its own coordinator (created with a real plugin host); this one is told directly.
    private void Select(int track)
    {
        _vm.Select(_vm.Tracks[track]);
        _windows.SelectionChanged();
    }

    [Fact]
    public async Task AnUnpinnedEditor_HidesWhenItsTrackIsDeselected_AndReturnsWithIt_WhileAPinnedOneStays()
    {
        var a = DeviceOn(0);
        var b = DeviceOn(1);
        Select(0);
        await _windows.OpenAsync(a);
        await _windows.OpenAsync(b);
        _windows.SetPinned(b, true);

        Select(1);
        Assert.False(_host.Visible[a]);
        Assert.True(_windows.IsHiddenByFollow(a));

        Select(0);
        Assert.True(_host.Visible[a]);
        Assert.True(_host.Visible[b]);
        Assert.True(_host.IsEditorOpen(a), "Hiding never closes the editor.");
    }

    [Fact]
    public async Task AnEditorThatCannotHide_StaysOpen_IsNotReportedHidden_AndLosesItsPin()
    {
        var a = DeviceOn(0);
        Select(0);
        await _windows.OpenAsync(a);
        _host.CanHide = false;

        Select(1);

        Assert.True(_host.Visible[a]);
        Assert.False(_windows.IsHiddenByFollow(a));
        Assert.False(_windows.CanHide(a));
        var requests = _host.VisibilityRequests;
        Select(0);
        Select(1);
        Assert.Equal(requests, _host.VisibilityRequests);
    }

    [Fact]
    public async Task WithFollowingOff_EditorsStayOpen_AndHiddenOnesComeBack()
    {
        var a = DeviceOn(0);
        Select(0);
        await _windows.OpenAsync(a);
        Select(1);
        Assert.False(_host.Visible[a]);

        _host.Follow = false;
        _host.RaiseHostingChanged();

        Assert.True(_host.Visible[a]);
        Select(0);
        Select(1);
        Assert.True(_host.Visible[a]);
    }

    [Fact]
    public async Task RapidSelectionChanges_SendOneRequestAtATime_AndEndAtTheLatest()
    {
        var a = DeviceOn(0);
        Select(0);
        await _windows.OpenAsync(a);
        var hold = new TaskCompletionSource();
        _host.Hold = hold;

        Select(1);
        Select(0);
        Select(1);
        Assert.Equal(1, _host.VisibilityRequests);

        hold.SetResult();
        await _host.Settled();
        Assert.False(_host.Visible[a]);
        Assert.True(_host.VisibilityRequests <= 2);
    }

    [Fact]
    public async Task KeysGoToBluestone_OnlyFromThatEditor_AndHeldOnesAreReleased_WhenSwitchedBackHiddenOrClosed()
    {
        var a = DeviceOn(0);
        var b = DeviceOn(1);
        Select(0);
        await _windows.OpenAsync(a);
        await _windows.OpenAsync(b);
        Assert.True(await _windows.SetKeysToBluestoneAsync(a, true));

        _host.Key(a, new PluginEditorKey(29, 0, Down: true, Repeat: false));
        _host.Key(b, new PluginEditorKey(27, 0, Down: true, Repeat: false));
        _host.Key(a, new PluginEditorKey(29, 0, Down: true, Repeat: true));
        Assert.Equal([29, 29], _forwarded.Select(k => k.Usage));

        await _windows.SetKeysToBluestoneAsync(a, false);
        Assert.Equal(new PluginEditorKey(29, 0, Down: false, Repeat: false), _forwarded[^1]);

        await _windows.SetKeysToBluestoneAsync(a, true);
        _host.Key(a, new PluginEditorKey(4, 0, Down: true, Repeat: false));
        Select(1);
        Assert.Equal(new PluginEditorKey(4, 0, Down: false, Repeat: false), _forwarded[^1]);

        Select(0);
        _host.Key(a, new PluginEditorKey(5, 0, Down: true, Repeat: false));
        _host.Close(a);
        Assert.Equal(new PluginEditorKey(5, 0, Down: false, Repeat: false), _forwarded[^1]);
        Assert.DoesNotContain(_forwarded, k => k.Usage == 27);
    }

    [Fact]
    public async Task KeysArrivingWhileAHideIsInFlight_AreNotHeld()
    {
        var a = DeviceOn(0);
        Select(0);
        await _windows.OpenAsync(a);
        await _windows.SetKeysToBluestoneAsync(a, true);
        var hold = new TaskCompletionSource();
        _host.Hold = hold;

        Select(1);
        _host.Key(a, new PluginEditorKey(4, 0, Down: true, Repeat: false));
        _host.Key(a, new PluginEditorKey(4, 0, Down: true, Repeat: true));
        hold.SetResult();
        await _host.Settled();

        Assert.False(_host.Visible[a]);
        Assert.Empty(_forwarded);
    }

    [Fact]
    public async Task TheKeyboardChoice_LastsThroughClosingAndReopening_AndIsDroppedWhereItCannotBeHonoured()
    {
        var a = DeviceOn(0);
        Select(0);
        await _windows.OpenAsync(a);
        await _windows.SetKeysToBluestoneAsync(a, true);
        _host.Close(a);

        await _windows.OpenAsync(a);
        Assert.True(_host.KeysToHost[a]);

        _host.CanRoute = false;
        _host.Close(a);
        await _windows.OpenAsync(a);
        Assert.False(_windows.SendsKeysToBluestone(a));
        Assert.False(_windows.CanRouteKeys(a));
        Assert.False(await _windows.SetKeysToBluestoneAsync(a, true));
    }

    private static PluginEditorButtonState Keys(bool on) =>
        !PluginEditorWindows.KeyRoutingSupported ? PluginEditorButtonState.Unavailable
        : on ? PluginEditorButtonState.On
        : PluginEditorButtonState.Off;

    private bool IsBypassed(DeviceId device) => _session.Project.FindDevice(device)!.Value.Device.IsBypassed;

    [Fact]
    public async Task AnOpenedEditor_IsToldItsHeader_AndTheHeaderFollowsThePanelAndTheProject()
    {
        var a = DeviceOn(0);
        Select(0);
        await _windows.OpenAsync(a);
        Assert.Equal(new PluginEditorHeader(PluginEditorButtonState.Off, PluginEditorButtonState.Off, Keys(false)), _host.Headers[a]);

        _windows.SetPinned(a, true);
        Assert.Equal(PluginEditorButtonState.On, _host.Headers[a].Pin);

        await _windows.SetKeysToBluestoneAsync(a, true);
        Assert.Equal(Keys(true), _host.Headers[a].Keys);

        // Bypassed from the device panel (or undone): MainViewModel tells its own coordinator; this one is told directly.
        _session.Execute(DeviceCommands.SetBypassed(a, true, TestDevices.Lookup));
        _windows.SyncHeaders();
        Assert.Equal(PluginEditorButtonState.On, _host.Headers[a].Bypass);

        // Not following the selection, a pin means nothing, so it is not shown.
        _host.Follow = false;
        _host.RaiseHostingChanged();
        Assert.Equal(PluginEditorButtonState.Hidden, _host.Headers[a].Pin);

        var requests = _host.HeaderRequests;
        _windows.SyncHeaders();
        Assert.Equal(requests, _host.HeaderRequests);
    }

    [Fact]
    public async Task HeaderClicks_ChangeWhatThePanelChanges_AndABypassIsUndoable()
    {
        var a = DeviceOn(0);
        Select(0);
        await _windows.OpenAsync(a);

        _host.Click(a, PluginEditorButton.Bypass, true);
        Assert.True(IsBypassed(a));
        Assert.Equal(PluginEditorButtonState.On, _host.Headers[a].Bypass);
        _session.History.Undo();
        Assert.False(IsBypassed(a));

        _host.Click(a, PluginEditorButton.Pin, true);
        Assert.True(_windows.IsPinned(a));
        Select(1);
        Assert.True(_host.Visible[a], "Pinned from its own window, it stays.");

        _host.Click(a, PluginEditorButton.Keys, true);
        await _host.Settled();
        Assert.Equal(PluginEditorWindows.KeyRoutingSupported, _windows.SendsKeysToBluestone(a));
        Assert.Equal(Keys(true), _host.Headers[a].Keys);
    }

    [Fact]
    public async Task AClickHandledAfterItsWindowClosed_IsStillApplied()
    {
        var a = DeviceOn(0);
        Select(0);
        await _windows.OpenAsync(a);

        // The click and then the close arrive from the worker; the close is seen first.
        _host.Close(a);
        _host.Click(a, PluginEditorButton.Bypass, true);
        _host.Click(a, PluginEditorButton.Pin, true);

        Assert.True(IsBypassed(a));
        Assert.True(_windows.IsPinned(a));
    }

    [Fact]
    public async Task ARefusedClick_IsUndoneInTheWindow()
    {
        var a = DeviceOn(0);
        Select(0);
        _host.Follow = false;
        await _windows.OpenAsync(a);
        var requests = _host.HeaderRequests;

        // The window flipped a pin it should not have offered; Bluestone sends back what it shows.
        _host.Click(a, PluginEditorButton.Pin, true);

        Assert.False(_windows.IsPinned(a));
        Assert.Equal(requests + 1, _host.HeaderRequests);
        Assert.Equal(PluginEditorButtonState.Hidden, _host.Headers[a].Pin);
    }

    [Fact]
    public async Task AClickWhileAHeaderIsOnItsWay_IsStillUndoneInTheWindow()
    {
        var a = DeviceOn(0);
        Select(0);
        _host.Follow = false;
        await _windows.OpenAsync(a);
        var hold = new TaskCompletionSource();
        _host.HeaderHold = hold;
        _session.Execute(DeviceCommands.SetBypassed(a, true, TestDevices.Lookup));
        _windows.SyncHeaders();
        var requests = _host.HeaderRequests;

        // Refused (no pin while editors do not follow), while the bypass's header is still on its way.
        _host.Click(a, PluginEditorButton.Pin, true);
        hold.SetResult();
        await _host.Settled();

        Assert.Equal(requests + 1, _host.HeaderRequests);
        Assert.Equal(new PluginEditorHeader(PluginEditorButtonState.On, PluginEditorButtonState.Hidden, Keys(false)), _host.Headers[a]);
    }

    [Fact]
    public async Task AHeaderThatFailsForAClosedWindow_DoesNotKeepTheReopenedOneWithoutItsHeader()
    {
        var a = DeviceOn(0);
        Select(0);
        await _windows.OpenAsync(a);
        var hold = new TaskCompletionSource();
        _host.HeaderHold = hold;
        _windows.SetPinned(a, true);

        // The window goes (its worker with it) and a new one opens while the old window's header is still on its way.
        _host.Close(a);
        await _windows.OpenAsync(a);
        hold.SetResult();
        await _host.Settled();

        Assert.Equal(PluginEditorButtonState.On, _host.Headers[a].Pin);
    }

    [Fact]
    public async Task AWindowWithoutAHeader_IsNotAskedAgain_UntilTheEditorIsReopened()
    {
        var a = DeviceOn(0);
        Select(0);
        _host.HasHeader = false;
        await _windows.OpenAsync(a);
        Assert.Equal(1, _host.HeaderRequests);

        _windows.SetPinned(a, true);
        _session.Execute(DeviceCommands.SetBypassed(a, true, TestDevices.Lookup));
        _windows.SyncHeaders();
        Assert.Equal(1, _host.HeaderRequests);

        _host.HasHeader = true;
        _host.Close(a);
        await _windows.OpenAsync(a);
        Assert.Equal(new PluginEditorHeader(PluginEditorButtonState.On, PluginEditorButtonState.On, Keys(false)), _host.Headers[a]);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();
    }

    private sealed class NoUi : IUserInteraction
    {
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive) => Task.FromResult(true);

        public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName) => Task.FromResult(UnsavedChangesChoice.Discard);
    }

    private sealed class FakeEditorHost : IPluginEditorHost
    {
        private readonly HashSet<DeviceId> _open = [];
        private readonly Dictionary<DeviceId, int> _windows = [];
        private Task _pending = Task.CompletedTask;

        public Dictionary<DeviceId, bool> Visible { get; } = [];

        public Dictionary<DeviceId, bool> KeysToHost { get; } = [];

        public bool Follow { get; set; } = true;

        public bool CanRoute { get; set; } = true;

        public int VisibilityRequests { get; private set; }

        public TaskCompletionSource? Hold { get; set; }

        public bool EditorsFollowSelection => Follow;

        public event EventHandler<(DeviceId Device, PluginEditorKey Key)>? EditorKeyPressed;

        public event EventHandler<(DeviceId Device, PluginEditorButton Button, bool On)>? EditorButtonClicked;

        public event EventHandler<DeviceId>? StatusChanged;

        public Dictionary<DeviceId, PluginEditorHeader> Headers { get; } = [];

        public int HeaderRequests { get; private set; }

        /// <summary>False for a window the plugin owns, which has no header.</summary>
        public bool HasHeader { get; set; } = true;

        public TaskCompletionSource? HeaderHold { get; set; }

        public async Task<bool> SetEditorHeaderAsync(DeviceId device, PluginEditorHeader header, CancellationToken cancellationToken = default)
        {
            HeaderRequests++;
            var window = _open.Contains(device) ? _windows[device] : 0;
            var hold = HeaderHold;
            HeaderHold = null;
            if (hold is not null)
            {
                _pending = hold.Task;
                await hold.Task;
            }

            // The window the request was for: one that closed meanwhile cannot show it, even if another has opened.
            if (window == 0 || !_open.Contains(device) || _windows[device] != window || !HasHeader)
            {
                return false;
            }

            Headers[device] = header;
            return true;
        }

        public void Click(DeviceId device, PluginEditorButton button, bool on) => EditorButtonClicked?.Invoke(this, (device, button, on));

        public event EventHandler? HostingChanged;

        public bool IsEditorOpen(DeviceId device) => _open.Contains(device);

        public Task<string?> OpenEditorAsync(DeviceId device, CancellationToken cancellationToken = default)
        {
            if (_open.Add(device))
            {
                _windows[device] = _windows.GetValueOrDefault(device) + 1;
                Visible[device] = true;
                KeysToHost[device] = false;
            }

            return Task.FromResult<string?>(null);
        }

        public bool CanHide { get; set; } = true;

        public async Task<bool> SetEditorVisibleAsync(DeviceId device, bool visible, CancellationToken cancellationToken = default)
        {
            VisibilityRequests++;
            if (!visible && !CanHide)
            {
                return false;
            }

            var hold = Hold;
            Hold = null;
            var work = hold is null ? Task.CompletedTask : hold.Task;
            _pending = work;
            await work;
            Visible[device] = visible;
            return true;
        }

        public Task<bool> SetEditorKeyboardAsync(DeviceId device, bool toHost, CancellationToken cancellationToken = default)
        {
            if (!_open.Contains(device) || !CanRoute)
            {
                return Task.FromResult(false);
            }

            KeysToHost[device] = toHost;
            return Task.FromResult(true);
        }

        public async Task Settled()
        {
            await _pending;
            await Task.Yield();
        }

        public void Key(DeviceId device, PluginEditorKey key) => EditorKeyPressed?.Invoke(this, (device, key));

        public void Close(DeviceId device)
        {
            _open.Remove(device);
            StatusChanged?.Invoke(this, device);
        }

        public void RaiseHostingChanged() => HostingChanged?.Invoke(this, EventArgs.Empty);
    }
}
