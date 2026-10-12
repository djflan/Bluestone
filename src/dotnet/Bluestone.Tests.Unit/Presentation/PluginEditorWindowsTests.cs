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
/// Floating plugin editors: following the selected track, pinning, coalesced requests, keys sent to Bluestone, and
/// releases so no forwarded key is left held.
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
        private Task _pending = Task.CompletedTask;

        public Dictionary<DeviceId, bool> Visible { get; } = [];

        public Dictionary<DeviceId, bool> KeysToHost { get; } = [];

        public bool Follow { get; set; } = true;

        public bool CanRoute { get; set; } = true;

        public int VisibilityRequests { get; private set; }

        public TaskCompletionSource? Hold { get; set; }

        public bool EditorsFollowSelection => Follow;

        public event EventHandler<(DeviceId Device, PluginEditorKey Key)>? EditorKeyPressed;

        public event EventHandler<DeviceId>? StatusChanged;

        public event EventHandler? HostingChanged;

        public bool IsEditorOpen(DeviceId device) => _open.Contains(device);

        public Task<string?> OpenEditorAsync(DeviceId device, CancellationToken cancellationToken = default)
        {
            if (_open.Add(device))
            {
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
