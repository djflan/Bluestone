using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bluestone.Domain.Sequencing;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private MainViewModel? _viewModel;
    private bool _keyboardToggleHeld;
    private ClipId? _renamingClip;
    private bool _closeConfirmed;
    private bool _syncingScroll;
    private double _editorHeight = 330;

    public MainWindow() => InitializeComponent();

    public void Attach(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        _frameTimer.Tick += (_, _) => OnFrame();
        _frameTimer.Start();
        _autosaveTimer.Tick += async (_, _) => await viewModel.AutosaveAsync();
        _autosaveTimer.Start();

        viewModel.Connections.ShowRequested += (_, _) =>
        {
            viewModel.IsInspectorVisible = true;
            ConnectionsSection.IsExpanded = true;
            ConnectionsSection.BringIntoView();
        };
        Ruler.SeekRequested += (_, tick) => viewModel.SeekTo(tick);
        Ruler.LoopRequested += (_, loop) => viewModel.SetLoop(loop);
        Timeline.LaneClicked += (_, click) =>
        {
            viewModel.Arrangement.ClearSelection();
            OnLaneClicked(click.Lane, click.Modifiers);
        };
        Timeline.ClipPressed += (_, press) => OnClipPressed(press.Lane, press.Clip, press.Modifiers);
        Timeline.ClipDoubleClicked += (_, open) => OpenInPianoRoll(open.Lane, open.Clip);
        Timeline.ClipNameDoubleClicked += (_, clip) => BeginClipRename(clip);
        // Clicking elsewhere commits, and leaves focus where the user clicked.
        ClipNameBox.LostFocus += (_, _) => EndClipRename(commit: true, refocus: false);
        Timeline.PropertyChanged += (_, e) =>
        {
            if (e.Property == Controls.TimelineView.ProjectProperty || e.Property == Controls.TimelineView.PixelsPerQuarterProperty
                || e.Property == Controls.TimelineView.ExpandedTracksProperty || e.Property == Controls.TimelineView.VisibleLeftProperty
                || e.Property == Controls.TimelineView.VisibleWidthProperty)
            {
                PlaceClipNameBox();
            }
        };
        Timeline.EmptyDoubleClicked += (_, at) =>
        {
            if (at.Lane < viewModel.Tracks.Count && viewModel.Arrangement.CreateClip(viewModel.Tracks[at.Lane].Id, at.Tick) is { } clip)
            {
                OpenInPianoRoll(at.Lane, clip);
            }
        };
        Timeline.ClipsDragged += (_, drag) => viewModel.Arrangement.MoveSelection(drag.DeltaTicks, drag.LaneDelta, drag.Copy);
        Timeline.ClipResized += (_, resize) => viewModel.Arrangement.ResizeClip(resize.Clip, resize.Start, resize.End);
        Timeline.AutomationEdited += (_, edit) =>
        {
            if (edit.Lane < viewModel.Tracks.Count)
            {
                viewModel.SetAutomationPoints(viewModel.Tracks[edit.Lane].Id, edit.Automation, edit.Label, edit.Points);
            }
        };
        viewModel.Arrangement.Changed += (_, _) => Timeline.SelectedClips = viewModel.Arrangement.SelectedClips.ToHashSet();
        TimelineScroller.ScrollChanged += (_, _) => SyncTimelineViewport();
        TimelineScroller.SizeChanged += (_, _) => SyncTimelineViewport();
        TrackHeaderScroller.AddHandler(PointerWheelChangedEvent, OnTrackHeaderWheel, RoutingStrategies.Tunnel);
        viewModel.SelectedTracks.CollectionChanged += (_, _) => SyncSelectedLanes();
        viewModel.Tracks.CollectionChanged += (_, _) => SyncSelectedLanes();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyboardKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) =>
        {
            _keyboardToggleHeld = false;
            viewModel.Keyboard?.ReleaseAll();
        };
        viewModel.MonitorEntries.CollectionChanged += (_, _) =>
        {
            if (viewModel.MonitorEntries.Count > 0 && MonitorList.IsVisible)
            {
                MonitorList.ScrollIntoView(viewModel.MonitorEntries.Count - 1);
            }
        };
        viewModel.Messages.CollectionChanged += (_, _) =>
            LastMessage.Text = viewModel.Messages.Count > 0 ? viewModel.Messages[^1].Text : string.Empty;

        TimelineScroller.AddHandler(PointerWheelChangedEvent, OnTimelineWheel, RoutingStrategies.Tunnel);
        TimelineScroller.AddHandler(InputElement.PointerTouchPadGestureMagnifyEvent, OnTimelinePinch, RoutingStrategies.Tunnel);
        Ruler.AddHandler(PointerWheelChangedEvent, OnRulerWheel, RoutingStrategies.Tunnel);

        AttachPianoRoll(viewModel);
        AttachEventList(viewModel);
        AttachRecordingOptions(viewModel);
        EditorSplitter.DragCompleted += (_, _) => _editorHeight = MainArea.RowDefinitions[3].ActualHeight;
        ApplyEditorVisibility();

        TempoBox.LostFocus += (_, _) => viewModel.CommitTempo(TempoBox.Text ?? string.Empty);
        MeterBox.LostFocus += (_, _) => viewModel.CommitMeter(MeterBox.Text ?? string.Empty);

        var command = CommandModifier;
        var nativeMenuBar = OperatingSystem.IsMacOS();
        if (nativeMenuBar)
        {
            AppMenu.Install(this, viewModel, command);
            FileMenuButton.IsVisible = false;
        }

        AddKeyBindings(viewModel, command, skipMenuGestures: nativeMenuBar);
        Closing += OnClosing;

        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>True while the lower pane (an editor, or its toolbar) has keyboard focus.</summary>
    internal bool IsEditorFocused => PianoRoll.IsFocused || EditorPane.IsKeyboardFocusWithin || EditorHeader.IsKeyboardFocusWithin;

    /// <summary>True while a note editor is on screen, so Cut and Paste have somewhere to act.</summary>
    private bool IsEditorShowing => _viewModel is { IsEditorVisible: true, LowerPane: LowerPane.PianoRoll or LowerPane.EventList };

    private void AttachPianoRoll(MainViewModel vm)
    {
        PianoRoll.Editor = vm.Editor;
        PianoRoll.SeekRequested += (_, tick) => vm.SeekTo(tick);
        PianoRoll.ScrollStateChanged += (_, _) => SyncPianoRollScrollBars();
        PianoRollHorizontal.ValueChanged += (_, e) =>
        {
            if (!_syncingScroll)
            {
                PianoRoll.ScrollX = e.NewValue;
            }
        };
        PianoRollVertical.ValueChanged += (_, e) =>
        {
            if (!_syncingScroll)
            {
                PianoRoll.ScrollY = e.NewValue;
            }
        };
        PianoRoll.AddHandler(InputElement.PointerTouchPadGestureMagnifyEvent, (_, e) =>
        {
            PianoRoll.ZoomAround(e.GetPosition(PianoRoll).X - Controls.PianoRollView.KeyboardWidth, 1 + e.Delta.X);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private void AttachEventList(MainViewModel vm) =>
        EventListBox.SelectionChanged += (_, _) => vm.EventList.OnRowsSelected();

    private void AttachRecordingOptions(MainViewModel vm)
    {
        TakeModeBox.SelectedIndex = vm.IsReplaceRecording ? 1 : 0;
        TakeModeBox.SelectionChanged += (_, _) => vm.IsReplaceRecording = TakeModeBox.SelectedIndex == 1;
        CountInBox.SelectedIndex = vm.CountInBars - 1;
        CountInBox.SelectionChanged += (_, _) => vm.CountInBars = Math.Max(1, CountInBox.SelectedIndex + 1);
    }

    private void SyncPianoRollScrollBars()
    {
        _syncingScroll = true;
        try
        {
            PianoRollHorizontal.Maximum = Math.Max(0, PianoRoll.ExtentWidth - PianoRoll.GridWidth);
            PianoRollHorizontal.ViewportSize = PianoRoll.GridWidth;
            PianoRollHorizontal.Value = PianoRoll.ScrollX;
            PianoRollVertical.Maximum = Math.Max(0, PianoRoll.ExtentHeight - PianoRoll.GridHeight);
            PianoRollVertical.ViewportSize = PianoRoll.GridHeight;
            PianoRollVertical.Value = PianoRoll.ScrollY;
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsEditorVisible))
        {
            ApplyEditorVisibility();
        }
    }

    /// <summary>Collapses the lower pane to its tab strip, remembering the height it had.</summary>
    private void ApplyEditorVisibility()
    {
        if (_viewModel is null)
        {
            return;
        }

        var row = MainArea.RowDefinitions[3];
        if (_viewModel.IsEditorVisible)
        {
            row.Height = new GridLength(_editorHeight);
        }
        else
        {
            if (row.ActualHeight > 60)
            {
                _editorHeight = row.ActualHeight;
            }

            row.Height = new GridLength(0);
        }
    }

    /// <summary>The platform's command key: Cmd on macOS, Ctrl elsewhere.</summary>
    private static KeyModifiers CommandModifier =>
        Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    /// <summary>Selects a track from its lane, with the same modifiers as the track list.</summary>
    private void OnLaneClicked(int lane, KeyModifiers modifiers)
    {
        if (_viewModel is not { } vm || lane >= vm.Tracks.Count)
        {
            return;
        }

        var track = vm.Tracks[lane];
        if (modifiers.HasFlag(CommandModifier))
        {
            if (!vm.SelectedTracks.Remove(track))
            {
                vm.SelectedTracks.Add(track);
            }
        }
        else if (modifiers.HasFlag(KeyModifiers.Shift) && vm.SelectedTrack is { } anchor)
        {
            var (from, to) = (vm.Tracks.IndexOf(anchor), lane);
            foreach (var t in vm.Tracks.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1).Where(t => !vm.SelectedTracks.Contains(t)).ToList())
            {
                vm.SelectedTracks.Add(t);
            }
        }
        else
        {
            vm.Select(track);
        }
    }

    /// <summary>Selects a clip from the arrangement, with the same modifiers as the track list, and its track.</summary>
    private void OnClipPressed(int lane, ClipId clip, KeyModifiers modifiers)
    {
        if (_viewModel is not { } vm || lane >= vm.Tracks.Count)
        {
            return;
        }

        // The track first: changing the track selection drops selected clips on other tracks. Pressing
        // a clip that is already selected keeps the selection, so clips on several tracks move together.
        var track = vm.Tracks[lane];
        var arrangement = vm.Arrangement;
        var plain = modifiers is KeyModifiers.None or KeyModifiers.Alt;
        if (plain && !arrangement.SelectedClips.Contains(clip))
        {
            if (vm.SelectedTracks.Count != 1 || vm.SelectedTrack != track)
            {
                vm.Select(track);
            }
        }
        else if (!vm.SelectedTracks.Contains(track))
        {
            vm.SelectedTracks.Add(track);
        }

        if (modifiers.HasFlag(CommandModifier))
        {
            arrangement.SelectClip(clip, Presentation.SelectionMode.Toggle);
        }
        else if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            arrangement.SelectClip(clip, Presentation.SelectionMode.Add);
        }
        else if (!arrangement.SelectedClips.Contains(clip))
        {
            // Pressing a selected clip keeps the selection, so several clips can be dragged together.
            arrangement.SelectClip(clip);
        }

        if (arrangement.SelectedClips.Contains(clip))
        {
            vm.Editor.EditClip(clip);
        }
    }

    private void OpenInPianoRoll(int lane, ClipId? clip = null)
    {
        if (_viewModel is not { } vm || lane >= vm.Tracks.Count)
        {
            return;
        }

        vm.Select(vm.Tracks[lane]);
        if (clip is { } id)
        {
            vm.Editor.EditClip(id);
        }

        vm.LowerPane = LowerPane.PianoRoll;
        vm.IsEditorVisible = true;
        Dispatcher.UIThread.Post(() => PianoRoll.Focus(), DispatcherPriority.Background);
    }

    /// <summary>The first dropped file Bluestone can open, as a local path.</summary>
    private static string? DroppedFile(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .FirstOrDefault(path => path is not null && MainViewModel.CanOpenFile(path));

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var path = DroppedFile(e);
        e.DragEffects = path is null ? DragDropEffects.None : DragDropEffects.Copy;
        DropOverlay.IsVisible = path is not null;
        if (path is not null)
        {
            var isProject = path.EndsWith(".bluestone", StringComparison.OrdinalIgnoreCase);
            DropText.Text = isProject ? "Drop to open project" : "Drop to import MIDI file";
            DropDetail.Text = System.IO.Path.GetFileName(path);
        }

        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        e.Handled = true;
        if (_viewModel is not null && DroppedFile(e) is { } path)
        {
            Activate();
            await _viewModel.OpenFileAsync(path);
        }
    }

    /// <summary>⌘/Ctrl + scroll zooms the arrangement around the pointer.</summary>
    private void OnTimelineWheel(object? sender, PointerWheelEventArgs e)
    {
        var command = CommandModifier;
        if (!e.KeyModifiers.HasFlag(command) || e.Delta.Y == 0)
        {
            return;
        }

        ZoomAround(e.GetPosition(TimelineScroller).X, Math.Pow(1.12, e.Delta.Y));
        e.Handled = true;
    }

    /// <summary>Plain scrolling over the bar ruler zooms, as in most sequencers.</summary>
    private void OnRulerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y != 0)
        {
            ZoomAround(e.GetPosition(Ruler).X, Math.Pow(1.12, e.Delta.Y));
            e.Handled = true;
        }
    }

    private void OnTrackHeaderWheel(object? sender, PointerWheelEventArgs e)
    {
        TimelineScroller.Offset = new Vector(TimelineScroller.Offset.X, Math.Max(0, TimelineScroller.Offset.Y - (e.Delta.Y * 40)));
        e.Handled = true;
    }

    private void OnTimelinePinch(object? sender, PointerDeltaEventArgs e)
    {
        ZoomAround(e.GetPosition(TimelineScroller).X, 1 + e.Delta.X);
        e.Handled = true;
    }

    /// <summary>Zooms while keeping the musical position under <paramref name="viewportX"/> in place.</summary>
    private void ZoomAround(double viewportX, double factor)
    {
        if (_viewModel is null || factor <= 0)
        {
            return;
        }

        var tick = Timeline.XToTick(TimelineScroller.Offset.X + viewportX);
        _viewModel.ZoomBy(factor);
        TimelineScroller.UpdateLayout();
        var x = Timeline.TickToX(tick) - viewportX;
        TimelineScroller.Offset = new Vector(Math.Max(0, x), TimelineScroller.Offset.Y);
    }

    private void OnFrame()
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        vm.OnFrame();
        var tick = vm.PlayheadTick;
        Playhead.X = Timeline.TickToX(tick);
        Ruler.PlayheadTick = tick;
        PianoRoll.PlayheadTick = tick;
        PianoRoll.SoundingNotes = vm.SoundingNotes;
        var recordingHere = vm.RecordingLane >= 0 && vm.RecordingLane < vm.Tracks.Count && vm.Tracks[vm.RecordingLane].Id == vm.Editor.Track?.Id;
        PianoRoll.RecordingPreview = recordingHere ? vm.RecordingPreview : [];

        if (vm.IsPlaying)
        {
            if (vm.Editor.FollowPlayhead && vm.IsEditorVisible)
            {
                PianoRoll.Reveal(tick);
            }

            // Page the arrangement forward when the playhead leaves the view.
            var x = Timeline.TickToX(tick);
            var view = TimelineScroller.Viewport.Width;
            if (view > 0 && (x > TimelineScroller.Offset.X + (view * 0.95) || x < TimelineScroller.Offset.X))
            {
                TimelineScroller.Offset = new Vector(Math.Max(0, x - (view * 0.05)), TimelineScroller.Offset.Y);
            }
        }
    }

    private void SyncTimelineViewport()
    {
        Timeline.VisibleLeft = TimelineScroller.Offset.X;
        Timeline.VisibleWidth = TimelineScroller.Viewport.Width;
        Ruler.ScrollX = TimelineScroller.Offset.X;
        if (Math.Abs(TrackHeaderScroller.Offset.Y - TimelineScroller.Offset.Y) > 0.5)
        {
            TrackHeaderScroller.Offset = new Vector(0, TimelineScroller.Offset.Y);
        }
    }

    private void AddKeyBindings(MainViewModel vm, KeyModifiers command, bool skipMenuGestures)
    {
        void Bind(Key key, KeyModifiers modifiers, System.Windows.Input.ICommand target)
        {
            var isMenuGesture = modifiers.HasFlag(command) && AppMenu.MenuGestures.Contains((key, modifiers & ~command));
            if (!(skipMenuGestures && isMenuGesture))
            {
                KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key, modifiers), Command = target });
            }
        }

        Bind(Key.OemPeriod, command, vm.PanicCommand);
        Bind(Key.Z, command, vm.UndoCommand);
        Bind(Key.Z, command | KeyModifiers.Shift, vm.RedoCommand);
        Bind(Key.N, command, vm.NewProjectCommand);
        Bind(Key.O, command, vm.OpenCommand);
        Bind(Key.S, command, vm.SaveCommand);
        Bind(Key.S, command | KeyModifiers.Shift, vm.SaveAsCommand);
        Bind(Key.I, command, vm.ImportMidiCommand);
        Bind(Key.E, command, vm.ExportMidiCommand);
        Bind(Key.T, command, vm.AddTrackCommand);
        Bind(Key.G, command, vm.Groups.GroupSelectedCommand);
        Bind(Key.OemPlus, command, vm.ZoomInCommand);
        Bind(Key.OemMinus, command, vm.ZoomOutCommand);
    }

    private void SyncSelectedLanes()
    {
        if (_viewModel is { } vm)
        {
            Timeline.SelectedLanes = vm.SelectedTracks.Select(t => vm.Tracks.IndexOf(t)).Where(i => i >= 0).ToHashSet();
        }
    }

    /// <summary>The text field being edited, if any. Single-key shortcuts stand aside while one is focused.</summary>
    internal TextBox? FocusedTextBox() => FocusManager?.GetFocusedElement() as TextBox;

    /// <summary>Select All: the text in a focused field, the notes in a focused editor, otherwise every track.</summary>
    internal void SelectAll()
    {
        if (FocusedTextBox() is { } text)
        {
            text.SelectAll();
        }
        else if (IsEditorFocused)
        {
            _viewModel?.Editor.SelectAll();
        }
        else
        {
            _viewModel?.SelectAllTracksCommand.Execute(null);
        }
    }

    /// <summary>Copy, Cut, and Paste act on a focused text field, otherwise on the editor's notes.</summary>
    internal void Copy()
    {
        if (FocusedTextBox() is { } text)
        {
            text.Copy();
        }
        else
        {
            _viewModel?.Editor.Copy();
        }
    }

    internal void Cut()
    {
        if (FocusedTextBox() is { } text)
        {
            text.Cut();
        }
        else if (IsEditorShowing)
        {
            _viewModel?.Editor.Cut();
        }
    }

    internal void Paste()
    {
        if (FocusedTextBox() is { } text)
        {
            text.Paste();
        }
        else if (IsEditorShowing)
        {
            _viewModel?.Editor.Paste();
        }
    }

    /// <summary>Duplicate: the selected notes when an editor has focus, otherwise the selected tracks.</summary>
    internal void Duplicate()
    {
        if (IsEditorFocused && _viewModel?.Editor.HasSelection == true)
        {
            _viewModel.Editor.Duplicate();
        }
        else if (Timeline.IsFocused && _viewModel?.Arrangement.HasClipSelection == true)
        {
            _viewModel.Arrangement.DuplicateSelection();
        }
        else
        {
            _viewModel?.DuplicateTracksCommand.Execute(null);
        }
    }

    /// <summary>Delete: the selected notes when an editor has focus, otherwise the selected tracks.</summary>
    internal void Delete()
    {
        if (IsEditorFocused)
        {
            _viewModel?.Editor.DeleteSelection();
        }
        else if (Timeline.IsFocused && _viewModel?.Arrangement.HasClipSelection == true)
        {
            _viewModel.Arrangement.DeleteSelection();
        }
        else
        {
            _viewModel?.DeleteTracksCommand.Execute(null);
        }
    }

    /// <summary>Starts renaming: the selected clip when the arrangement has focus, otherwise the first selected track.</summary>
    internal void BeginRename()
    {
        if (Timeline.IsFocused && RenameSelectedClip())
        {
            return;
        }

        if (_viewModel?.SelectedTrack is not { } track
            || TrackList.ContainerFromItem(track) is not { } container
            || container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is not { } name)
        {
            return;
        }

        // Deferred so the key that started the rename cannot also type into the field.
        Dispatcher.UIThread.Post(() =>
        {
            name.Focus();
            name.SelectAll();
        }, DispatcherPriority.Background);
    }

    /// <summary>Renames the one selected clip (Edit ▸ Rename Clip). Returns false when not exactly one clip is selected.</summary>
    internal bool RenameSelectedClip()
    {
        if (_viewModel?.Arrangement.SelectedClips is not { Count: 1 } clips)
        {
            return false;
        }

        BeginClipRename(clips.First());
        return true;
    }

    /// <summary>Opens a text field over a clip's name strip, holding the name it shows.</summary>
    internal void BeginClipRename(ClipId clip)
    {
        if (_viewModel?.Arrangement.DisplayName(clip) is not { } name || Timeline.ClipHeaderBounds(clip) is null)
        {
            return;
        }

        _renamingClip = clip;
        ClipNameBox.Text = name;
        PlaceClipNameBox();

        // Deferred so the key or click that started the rename cannot also reach the field.
        Dispatcher.UIThread.Post(() =>
        {
            ClipNameBox.Focus();
            ClipNameBox.SelectAll();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Keeps the rename field over the visible part of its clip's name strip. When the clip scrolls out of
    /// view the rename is committed; when the clip is gone (undo), committing does nothing.
    /// </summary>
    private void PlaceClipNameBox()
    {
        if (_renamingClip is not { } clip)
        {
            return;
        }

        if (Timeline.ClipHeaderBounds(clip) is not { } bounds)
        {
            EndClipRename(commit: true, refocus: false);
            return;
        }

        Canvas.SetLeft(ClipNameBox, bounds.X);
        Canvas.SetTop(ClipNameBox, bounds.Y - 1);
        ClipNameBox.Width = Math.Clamp(bounds.Width, 120, 320);
        ClipNameBox.Height = bounds.Height + 2;
        ClipNameBox.IsVisible = true;
    }

    private void EndClipRename(bool commit, bool refocus)
    {
        if (_renamingClip is not { } clip)
        {
            return;
        }

        _renamingClip = null;
        ClipNameBox.IsVisible = false;
        if (commit)
        {
            _viewModel?.Arrangement.RenameClip(clip, ClipNameBox.Text ?? string.Empty);
        }

        if (refocus)
        {
            Timeline.Focus();
        }
    }

    internal void ShowPane(LowerPane pane)
    {
        _viewModel?.ShowPaneCommand.Execute(pane);
        if (_viewModel is { IsEditorVisible: true } && pane == LowerPane.PianoRoll)
        {
            Dispatcher.UIThread.Post(() => PianoRoll.Focus(), DispatcherPriority.Background);
        }
    }

    /// <summary>Leaves a text field, so the next keys act on tracks or notes again.</summary>
    private void EndEditing(TextBox editing)
    {
        var row = editing.DataContext is TrackViewModel track ? TrackList.ContainerFromItem(track) : null;
        if (row?.Focus() == true)
        {
            return;
        }

        if (editing.FindAncestorOfType<ListBox>() == EventListBox && EventListBox.Focus())
        {
            return;
        }

        if (PianoRoll.IsEffectivelyVisible && editing.DataContext is EditorViewModel)
        {
            PianoRoll.Focus();
            return;
        }

        Timeline.Focus();
    }

    internal void ShowShortcuts() => _ = Shortcuts.CreateWindow().ShowDialog(this);

    /// <summary>
    /// Single-key and navigation shortcuts. Runs before focused controls (tunnel) so that, for
    /// example, Space plays even when the track list has focus, but never while text is being edited.
    /// Keys that edit notes go to the piano roll while it has focus.
    /// </summary>
    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        var command = CommandModifier;
        var modifiers = e.KeyModifiers;
        if (FocusedTextBox() is { } editing)
        {
            HandleTextKey(editing, e, vm);
            return;
        }

        if (vm.Keyboard is { } keyboard && HandleKeyboardKeyDown(keyboard, e))
        {
            e.Handled = true;
            return;
        }

        if (PianoRoll.IsFocused && PianoRoll.HandleKey(e.Key, modifiers))
        {
            e.Handled = true;
            return;
        }

        if (IsEditorFocused && modifiers == command && e.Key is Key.C or Key.X or Key.V or Key.D && !OperatingSystem.IsMacOS())
        {
            switch (e.Key)
            {
                case Key.C:
                    Copy();
                    break;
                case Key.X:
                    Cut();
                    break;
                case Key.V:
                    Paste();
                    break;
                default:
                    Duplicate();
                    break;
            }

            e.Handled = true;
            return;
        }

        System.Windows.Input.ICommand? target = (e.Key, modifiers) switch
        {
            (Key.Space, KeyModifiers.None) => vm.PlayPauseCommand,
            (Key.R, KeyModifiers.None) or (Key.Multiply, KeyModifiers.None) => vm.RecordCommand,
            (Key.K, KeyModifiers.None) => vm.ToggleMetronomeCommand,
            (Key.Home, KeyModifiers.None) => vm.ReturnToStartCommand,
            (Key.End, KeyModifiers.None) => vm.GoToEndCommand,
            (Key.OemComma, KeyModifiers.None) => vm.PreviousBarCommand,
            (Key.OemPeriod, KeyModifiers.None) => vm.NextBarCommand,
            (Key.L, KeyModifiers.None) => vm.ToggleLoopCommand,
            (Key.M, KeyModifiers.None) => vm.ToggleMuteCommand,
            (Key.S, KeyModifiers.None) => vm.ToggleSoloCommand,
            (Key.I, KeyModifiers.None) => vm.ToggleInspectorCommand,
            _ => null,
        };

        if (target is not null)
        {
            target.Execute(null);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.P when modifiers == KeyModifiers.None:
                ShowPane(LowerPane.PianoRoll);
                e.Handled = true;
                break;
            case Key.B when modifiers == KeyModifiers.None:
                vm.Arrangement.SplitAtPlayhead();
                e.Handled = true;
                break;
            case Key.D when modifiers == KeyModifiers.None:
                ShowPane(LowerPane.EventList);
                e.Handled = true;
                break;
            case Key.D1 or Key.D2 or Key.D3 when modifiers == KeyModifiers.None:
                vm.Editor.Tool = e.Key switch { Key.D1 => EditTool.Arrow, Key.D2 => EditTool.Pencil, _ => EditTool.Eraser };
                e.Handled = true;
                break;
            case Key.Delete or Key.Back when modifiers == KeyModifiers.None:
                Delete();
                e.Handled = true;
                break;
            case Key.Return or Key.Enter when modifiers == KeyModifiers.None && !IsEditorFocused:
                BeginRename();
                e.Handled = true;
                break;
            case Key.Up or Key.Down when (modifiers is KeyModifiers.None or KeyModifiers.Shift) && !TrackList.IsKeyboardFocusWithin && !EventListBox.IsKeyboardFocusWithin:
                vm.SelectAdjacent(e.Key == Key.Up ? -1 : 1, extend: modifiers == KeyModifiers.Shift);
                e.Handled = true;
                break;
            case Key.A when modifiers == command:
                SelectAll();
                e.Handled = true;
                break;
            case Key.D when modifiers == command && !OperatingSystem.IsMacOS():
                Duplicate();
                e.Handled = true;
                break;
            case Key.OemQuestion when modifiers.HasFlag(command):
                ShowShortcuts();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Computer keyboard notes, by physical position so the layout is the same on AZERTY and others:
    /// the home row plays white keys from C, the row above plays black keys.
    /// </summary>
    private static int? KeyboardSemitone(PhysicalKey key) => key switch
    {
        PhysicalKey.A => 0,
        PhysicalKey.W => 1,
        PhysicalKey.S => 2,
        PhysicalKey.E => 3,
        PhysicalKey.D => 4,
        PhysicalKey.F => 5,
        PhysicalKey.T => 6,
        PhysicalKey.G => 7,
        PhysicalKey.Y => 8,
        PhysicalKey.H => 9,
        PhysicalKey.U => 10,
        PhysicalKey.J => 11,
        PhysicalKey.K => 12,
        PhysicalKey.O => 13,
        PhysicalKey.L => 14,
        PhysicalKey.P => 15,
        PhysicalKey.Semicolon => 16,
        PhysicalKey.Quote => 17,
        _ => null,
    };

    /// <summary>
    /// ` toggles the computer keyboard. While it is on, note keys and Z/X (octave) and C/V (velocity)
    /// belong to it; everything else, including Space and command shortcuts, still works.
    /// </summary>
    private bool HandleKeyboardKeyDown(ComputerKeyboardViewModel keyboard, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return false;
        }

        if (e.PhysicalKey == PhysicalKey.Backquote)
        {
            // Auto-repeat sends more key-downs while held; toggle once per press.
            if (!_keyboardToggleHeld)
            {
                _keyboardToggleHeld = true;
                keyboard.IsEnabled = !keyboard.IsEnabled;
            }

            return true;
        }

        if (!keyboard.IsEnabled)
        {
            return false;
        }

        if (KeyboardSemitone(e.PhysicalKey) is { } semitone)
        {
            keyboard.Press(semitone);
            return true;
        }

        switch (e.PhysicalKey)
        {
            case PhysicalKey.Z:
                keyboard.OctaveDownCommand.Execute(null);
                return true;
            case PhysicalKey.X:
                keyboard.OctaveUpCommand.Execute(null);
                return true;
            case PhysicalKey.C:
                keyboard.VelocityDownCommand.Execute(null);
                return true;
            case PhysicalKey.V:
                keyboard.VelocityUpCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    private void OnKeyboardKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.PhysicalKey == PhysicalKey.Backquote)
        {
            _keyboardToggleHeld = false;
        }

        if (_viewModel?.Keyboard is { IsEnabled: true } keyboard && KeyboardSemitone(e.PhysicalKey) is { } semitone)
        {
            keyboard.Release(semitone);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Return commits a field and Escape cancels it; everything else belongs to the field. Track
    /// names, LCD fields, event cells, and the note inspector commit when focus leaves them.
    /// </summary>
    private void HandleTextKey(TextBox editing, KeyEventArgs e, MainViewModel vm)
    {
        if (e.Key is not (Key.Return or Key.Enter or Key.Escape))
        {
            return;
        }

        if (editing == ClipNameBox)
        {
            EndClipRename(commit: e.Key != Key.Escape, refocus: true);
            e.Handled = true;
            return;
        }

        if (editing.Classes.Contains("inline") && editing.DataContext is TrackViewModel track)
        {
            if (e.Key == Key.Escape)
            {
                editing.Text = track.Name;
            }
            else if (!string.IsNullOrWhiteSpace(editing.Text))
            {
                track.Name = editing.Text.Trim();
            }
        }
        else if (editing == TempoBox || editing == MeterBox)
        {
            if (e.Key == Key.Escape)
            {
                editing.Text = editing == TempoBox ? vm.TempoText : vm.MeterText;
            }
        }
        else if (editing.Classes.Contains("commit") && e.Key == Key.Escape)
        {
            // Restore the bound value so leaving the field commits nothing.
            BindingOperations.GetBindingExpressionBase(editing, TextBox.TextProperty)?.UpdateTarget();
        }
        else if (!editing.Classes.Contains("commit"))
        {
            return;
        }

        EndEditing(editing);
        e.Handled = true;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || _viewModel is null)
        {
            return;
        }

        e.Cancel = true;
        if (await _viewModel.ConfirmCloseAsync())
        {
            _closeConfirmed = true;
            _frameTimer.Stop();
            _autosaveTimer.Stop();
            Close();
        }
    }
}
