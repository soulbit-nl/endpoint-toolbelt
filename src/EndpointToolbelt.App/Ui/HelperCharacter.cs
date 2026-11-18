using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using EndpointToolbelt.App.Core;

namespace EndpointToolbelt.App.Ui;

/// <summary>
/// The helper character, the window's one mascot: a small figure drawn in vectors in a 120 x 120 box (a
/// rounded body, two eyes and a square "bit"), with one pose per <see cref="HelperMood"/>. It is
/// decorative: never focusable or clickable, no name, and its automation element is in the raw view
/// only (neither a control nor a content element), so a screen reader never meets it while a test can
/// still find it by its automation ID; the window hides it in high contrast.
/// It lives calmly through <see cref="MascotBrain"/>: it breathes, blinks and now and then makes one
/// small action (WPF storyboards on its transforms, never a timer that redraws; a one-shot timer only
/// starts the next storyboard when it is due), and reacts to the app's state. It moves only while
/// Windows' animation effects are on, outside high contrast and while it is shown; otherwise every
/// mood is a still pose and nothing is scheduled.
/// </summary>
public sealed class HelperCharacter : Viewbox
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(HelperMood), typeof(HelperCharacter), new PropertyMetadata(HelperMood.Idle, (d, _) => ((HelperCharacter)d).OnMood()));

    /// <summary>The frame rates the storyboards ask for: low, so the window redraws little (breathing 12, the rest 30).</summary>
    internal const int BreathFrameRate = 12;
    internal const int ActionFrameRate = 30;

    private readonly Dictionary<HelperMood, UIElement[]> _poses = new();
    private readonly ScaleTransform _breath = new(1, 1, 60, 110);
    private readonly RotateTransform _tilt = new(0, 60, 110);
    // The squash jump scales the body from its feet, apart from the breathing (two storyboards never share a property).
    private readonly ScaleTransform _squash = new(1, 1, 60, 110);
    private readonly TranslateTransform _hop = new();
    private readonly List<ScaleTransform> _blinks = new();
    private readonly List<TranslateTransform> _looks = new();
    private readonly RotateTransform _wave = new(0, 84, 28);

    // The idle bit: a slow float while idle, and its jump (an idle action).
    private readonly TranslateTransform _bitFloat = new();
    private readonly TranslateTransform _bitHop = new();

    // The idle eyes, and the happy eyes that replace them for a moment (an idle action).
    private UIElement _idleEyes;
    private UIElement _smileEyes;
    private readonly List<Rectangle> _dots = new();
    private readonly ScaleTransform _pop = new(1, 1, 69, 17);
    private readonly TranslateTransform _fall = new();
    private readonly MascotBrain _brain;
    private readonly List<Storyboard> _running = new();
    private Storyboard _action;
    private Storyboard _moodBoard;
    private DispatcherTimer _timer;
    private bool _animationsEnabled;
    private bool _highContrast;
    private bool _paused;
    // The still pose a click shows while Windows animations are off, and how long it stays.
    private DispatcherTimer _stillTimer;
    private static readonly TimeSpan StillPoseLength = TimeSpan.FromSeconds(1.5);

    public HelperCharacter() : this(null) { }

    /// <summary>A test passes its own seeded random.</summary>
    internal HelperCharacter(Random random)
    {
        _brain = new MascotBrain(random);
        Stretch = Stretch.Uniform;
        Focusable = false;
        // A mouse click makes it hop (a small play); it is still decorative: no focus, no name,
        // nothing a keyboard or screen reader user misses.
        IsHitTestVisible = true;
        Cursor = System.Windows.Input.Cursors.Hand;
        MouseLeftButtonUp += (_, e) =>
        {
            Poke();
            e.Handled = true;
        };
        SnapsToDevicePixels = false;
        var box = new Canvas { Width = 120, Height = 120, RenderTransform = _hop };

        // The body breathes and tilts as a whole (eyes included); the bit moves on its own.
        var body = new Canvas { Width = 120, Height = 120, RenderTransform = new TransformGroup { Children = { _breath, _squash, _tilt } } };
        body.Children.Add(Shape(new Rectangle { Width = 88, Height = 74, RadiusX = 18, RadiusY = 18 }, 16, 36, "HelperBody"));
        var idleEyes = Eyes(38, 54, 66);
        // At rest the face smiles a little: nothing is wrong and nothing runs.
        var idleMouth = Stroke(new Path { Data = Geometry.Parse("M51,79 Q60,86 69,79") }, "HelperEye", 4);
        var smileEyes = Stroke(new Path { Data = Geometry.Parse("M37,62 Q44,52 51,62 M65,62 Q72,52 79,62") }, "HelperEye", 5);
        smileEyes.Visibility = Visibility.Collapsed;
        _idleEyes = idleEyes;
        _smileEyes = smileEyes;
        var workingEyes = Eyes(34, 44, 62);
        var doneEyes = Stroke(new Path { Data = Geometry.Parse("M36,58 Q42,49 48,58 M66,58 Q72,49 78,58") }, "HelperEye", 5);
        var attentionEyes = Eyes(38, 46, 66);
        var brow = Shape(new Rectangle { Width = 14, Height = 4, RadiusX = 2, RadiusY = 2, RenderTransform = new RotateTransform(-14, 7, 2) }, 37, 38, "HelperEye");
        var errorEyes = Eyes(38, 70, 66, blink: false);
        foreach (var part in new UIElement[] { idleEyes, idleMouth, smileEyes, workingEyes, doneEyes, attentionEyes, brow, errorEyes }) { body.Children.Add(part); }
        box.Children.Add(body);

        // Idle: the bit floats above the head; now and then it waves or jumps.
        var idleBit = Shape(new Rectangle { Width = 20, Height = 20, RadiusX = 4, RadiusY = 4 }, 74, 8, "HelperBit");
        idleBit.RenderTransform = new RotateTransform(8, 10, 10);
        var idleBitHolder = new Canvas { RenderTransform = new TransformGroup { Children = { _wave, _bitFloat, _bitHop } } };
        idleBitHolder.Children.Add(idleBit);

        // Working: three small squares that light up one after another.
        var dots = new Canvas();
        foreach (var (x, y, size, radius, brush) in new[] { (48.0, 20.0, 9.0, 2.0, "HelperSoft"), (62.0, 14.0, 10.0, 2.0, "HelperSoft"), (77.0, 6.0, 12.0, 2.5, "HelperBit") })
        {
            var dot = (Rectangle)Shape(new Rectangle { Width = size, Height = size, RadiusX = radius, RadiusY = radius }, x, y, brush);
            _dots.Add(dot);
            dots.Children.Add(dot);
        }

        // Done: the bit pops in with a check mark.
        var doneBit = new Canvas { RenderTransform = new TransformGroup { Children = { new RotateTransform(10, 69, 17), _pop } } };
        doneBit.Children.Add(Shape(new Rectangle { Width = 24, Height = 24, RadiusX = 5, RadiusY = 5 }, 57, 5, "HelperBit"));
        doneBit.Children.Add(Stroke(new Path { Data = Geometry.Parse("M62,17 L67,22 L76,12") }, "HelperMark", 4));

        // Attention: the bit with a question mark (the concerned look while a page shows a problem).
        var askBit = new Canvas { RenderTransform = new RotateTransform(8, 69, 17) };
        askBit.Children.Add(Shape(new Rectangle { Width = 24, Height = 24, RadiusX = 5, RadiusY = 5 }, 57, 5, "HelperBit"));
        askBit.Children.Add(Stroke(new Path { Data = Geometry.Parse("M64,13 Q64,9 69,9 Q74,9 74,13 Q74,16 69,18 L69,20") }, "HelperMark", 4));
        askBit.Children.Add(Shape(new Ellipse { Width = 4, Height = 4 }, 67, 23, "HelperMark"));

        // Error: the bit tumbled to the ground beside the body.
        var fallenBit = Shape(new Rectangle { Width = 13, Height = 13, RadiusX = 3, RadiusY = 3 }, 2, 97, "HelperBit");
        fallenBit.RenderTransform = new TransformGroup { Children = { new RotateTransform(25, 6.5, 6.5), _fall } };

        foreach (var part in new UIElement[] { idleBitHolder, dots, doneBit, askBit, fallenBit }) { box.Children.Add(part); }
        Child = box;

        _poses[HelperMood.Idle] = new UIElement[] { idleEyes, idleMouth, idleBitHolder };
        _poses[HelperMood.Working] = new UIElement[] { workingEyes, dots };
        _poses[HelperMood.Done] = new UIElement[] { doneEyes, doneBit };
        _poses[HelperMood.Attention] = new UIElement[] { attentionEyes, brow, askBit };
        _poses[HelperMood.Error] = new UIElement[] { errorEyes, fallenBit };
        ShowPose();
    }

    public HelperMood Mood
    {
        get => (HelperMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    /// <summary>Windows shows animations (<see cref="SystemParameters.ClientAreaAnimation"/>); false gives still poses.</summary>
    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set { _animationsEnabled = value; ApplyConditions(); }
    }

    /// <summary>Windows uses a high contrast theme: still (the window also hides it).</summary>
    public bool HighContrast
    {
        get => _highContrast;
        set { _highContrast = value; ApplyConditions(); }
    }

    /// <summary>The window is minimized or not visible: every animation pauses and nothing is scheduled.</summary>
    public bool Paused
    {
        get => _paused;
        set { _paused = value; ApplyConditions(); }
    }

    /// <summary>It moves now: it breathes, and blinks and acts when due.</summary>
    public bool IsAnimating => _brain.Moving;

    /// <summary>The state machine, for tests.</summary>
    internal MascotBrain Brain => _brain;

    /// <summary>The storyboards running now (breathing, the mood's own, a blink, the one action), for tests.</summary>
    internal IReadOnlyList<Storyboard> Running => _running;

    /// <summary>The storyboard of the one action running now, or null, for tests.</summary>
    internal Storyboard CurrentAction => _action;

    /// <summary>The one-shot timer that starts the next due storyboard; null or stopped while still.</summary>
    internal DispatcherTimer Timer => _timer;

    /// <summary>The parts of the pose shown now, for tests: every other part is collapsed.</summary>
    internal IReadOnlyList<UIElement> PoseParts => _poses[Mood];

    /// <summary>An element in the raw view only, with an automation ID and no name, children or focus.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new MascotAutomationPeer(this);

    /// <summary>What is due now; called by the one-shot timer, and by tests with their own clock.</summary>
    internal void Tick(DateTime nowUtc)
    {
        var (blink, start) = _brain.Tick(nowUtc);
        if (_action != null && _brain.Action == MascotAction.None) { StopAction(); }
        if (blink) { Begin(BlinkStoryboard()); }
        if (start != MascotAction.None) { StartAction(start); }
        Schedule(nowUtc);
    }

    /// <summary>
    /// A click: a hop (a puzzled tilt while something is wrong) when it may move and no other action runs.
    /// With Windows animations off it shows a still pose for a moment instead (no movement); while it works, nothing.
    /// </summary>
    internal void Poke()
    {
        var now = DateTime.UtcNow;
        var start = _brain.Poke(now);
        if (start == MascotAction.None)
        {
            // Windows animations off (a remote session turns them off by default): a still pose instead.
            if (!_animationsEnabled && !_highContrast && !_paused && Mood != HelperMood.Working) { ShowStillPose(); }
            return;
        }
        StartAction(start);
        Schedule(now);
    }

    /// <summary>The still pose a click shows for a moment, without movement: a smile at rest, a puzzled tilt otherwise.</summary>
    private void ShowStillPose()
    {
        if (Mood == HelperMood.Idle)
        {
            _idleEyes.Visibility = Visibility.Collapsed;
            _smileEyes.Visibility = Visibility.Visible;
        }
        else { _tilt.Angle = -8; }
        _stillTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = StillPoseLength };
        _stillTimer.Tick -= OnStillPoseEnd;
        _stillTimer.Tick += OnStillPoseEnd;
        _stillTimer.Stop();
        _stillTimer.Start();
    }

    private void OnStillPoseEnd(object sender, EventArgs e) => EndStillPose();

    /// <summary>Back to the mood's own pose, upright.</summary>
    internal void EndStillPose()
    {
        _stillTimer?.Stop();
        if (_tilt.HasAnimatedProperties) { return; }
        _tilt.Angle = 0;
        _smileEyes.Visibility = Visibility.Collapsed;
        ShowPose();
    }

    /// <summary>A still pose from a click shows now (Windows animations off), for tests.</summary>
    internal bool StillPoseShown => _stillTimer?.IsEnabled == true;

    /// <summary>The body's tilt now, for tests.</summary>
    internal double TiltAngle => _tilt.Angle;

    private void OnMood()
    {
        if (StillPoseShown) { EndStillPose(); }
        ShowPose();
        var now = DateTime.UtcNow;
        var start = _brain.SetMood(Mood, now);
        if (!_brain.Moving) { return; }
        // A reaction stops the idle action that runs (never two actions at a time).
        if (_brain.Action == MascotAction.None) { StopAction(); }
        StartMoodStoryboards();
        if (start != MascotAction.None) { StartAction(start); }
        Schedule(now);
    }

    private void ApplyConditions()
    {
        if (StillPoseShown) { EndStillPose(); }
        var now = DateTime.UtcNow;
        if (!_brain.SetConditions(_animationsEnabled, _highContrast, !_paused, now)) { return; }
        StopAll();
        if (!_brain.Moving) { return; }
        Begin(Breathing());
        StartMoodStoryboards();
        Schedule(now);
    }

    /// <summary>The pose of the mood: its parts shown, every other part collapsed.</summary>
    private void ShowPose()
    {
        foreach (var pose in _poses)
        {
            foreach (var part in pose.Value) { part.Visibility = Visibility.Collapsed; }
        }
        foreach (var part in _poses[Mood]) { part.Visibility = Visibility.Visible; }
    }

    /// <summary>The mood's own movement: the working dots pulse; the done bit pops in and the error bit falls, once.</summary>
    private void StartMoodStoryboards()
    {
        // The previous mood's movement ends first (the working dots would pulse on, unseen).
        if (_moodBoard != null)
        {
            _moodBoard.Stop(this);
            _moodBoard.Remove(this);
            _running.Remove(_moodBoard);
            _moodBoard = null;
        }
        var calm = new SineEase { EasingMode = EasingMode.EaseInOut };
        var board = NewStoryboard(ActionFrameRate);
        switch (Mood)
        {
            case HelperMood.Idle:
                // The bit floats 6 units up and down (about 2.4 px at the 48 px it is shown), about 2.8 seconds a round: alive, never busy.
                Add(board, _bitFloat, TranslateTransform.YProperty, new DoubleAnimation(0, -6, TimeSpan.FromSeconds(1.4))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = calm,
                });
                break;
            case HelperMood.Working:
                for (var i = 0; i < _dots.Count; i++)
                {
                    Add(board, _dots[i], OpacityProperty, new DoubleAnimation(0.45, 1, TimeSpan.FromSeconds(0.9))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        BeginTime = TimeSpan.FromSeconds(0.3 * i),
                        EasingFunction = calm,
                    });
                }
                break;
            case HelperMood.Done:
                Add(board, _pop, ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.6)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } });
                Add(board, _pop, ScaleTransform.ScaleYProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.6)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } });
                break;
            case HelperMood.Error:
                Add(board, _fall, TranslateTransform.YProperty, new DoubleAnimation(-88, 0, TimeSpan.FromSeconds(0.6)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
                Add(board, _fall, TranslateTransform.XProperty, new DoubleAnimation(70, 0, TimeSpan.FromSeconds(0.6)));
                break;
            default:
                return;
        }
        _moodBoard = board;
        Begin(board);
    }

    /// <summary>Breathing: a slow scale of 1.5 %, about 3.2 seconds per breath, for as long as it moves.</summary>
    private static Storyboard BreathingOf(ScaleTransform breath)
    {
        var board = NewStoryboard(BreathFrameRate);
        Add(board, breath, ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.015, TimeSpan.FromSeconds(1.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        Add(board, breath, ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.985, TimeSpan.FromSeconds(1.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        return board;
    }

    private Storyboard Breathing() => BreathingOf(_breath);

    /// <summary>One blink: the eyes close and open in 0.2 seconds.</summary>
    private Storyboard BlinkStoryboard()
    {
        var board = NewStoryboard(ActionFrameRate);
        foreach (var blink in _blinks)
        {
            var keys = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(0.2) };
            keys.KeyFrames.Add(new LinearDoubleKeyFrame(0.12, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.1))));
            keys.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.2))));
            Add(board, blink, ScaleTransform.ScaleYProperty, keys);
        }
        return board;
    }

    // The sizes of the movements, in the figure's 120 units (48 DIP on screen: times 0.4).
    internal const double LookIdle = 18;
    internal const double LookCalm = 12;
    internal const double TiltIdle = 15;
    internal const double TiltCalm = 10;
    internal const double HopIdle = -28;

    /// <summary>One action's storyboard: it ends where it started, so a stopped action leaves the still pose.</summary>
    internal Storyboard ActionStoryboard(MascotAction action)
    {
        var board = NewStoryboard(ActionFrameRate);
        var length = action == MascotAction.Bounce ? MascotBrain.BounceLength : MascotBrain.ActionLength;
        DoubleAnimationUsingKeyFrames Keys(params (double Seconds, double Value)[] frames)
        {
            var keys = new DoubleAnimationUsingKeyFrames { Duration = length };
            foreach (var (seconds, value) in frames)
            {
                keys.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)), new SineEase { EasingMode = EasingMode.EaseInOut }));
            }
            return keys;
        }
        ObjectAnimationUsingKeyFrames Show(params (double Seconds, Visibility Value)[] frames)
        {
            var keys = new ObjectAnimationUsingKeyFrames { Duration = length };
            foreach (var (seconds, value) in frames)
            {
                keys.KeyFrames.Add(new DiscreteObjectKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds))));
            }
            return keys;
        }
        // Big enough to see at 48 of its 120 units while the user reads elsewhere, and quick to
        // start (0.2 s in); while it asks for attention or shows an error its look and tilt are about
        // two thirds of that, so the calm set stays calm.
        var calm = Mood is HelperMood.Attention or HelperMood.Error;
        switch (action)
        {
            case MascotAction.LookLeft:
            case MascotAction.LookRight:
                var side = (action == MascotAction.LookLeft ? -1 : 1) * (calm ? LookCalm : LookIdle);
                foreach (var look in _looks) { Add(board, look, TranslateTransform.XProperty, Keys((0.2, side), (1.2, side), (1.6, 0))); }
                break;
            case MascotAction.Wave:
                Add(board, _wave, RotateTransform.AngleProperty, Keys((0.2, -35), (0.6, 30), (1.0, -22), (1.4, 0), (1.6, 0)));
                break;
            case MascotAction.Tilt:
                var angle = calm ? TiltCalm : TiltIdle;
                Add(board, _tilt, RotateTransform.AngleProperty, Keys((0.25, angle), (1.1, angle), (1.6, 0)));
                break;
            case MascotAction.Bounce:
                Add(board, _hop, TranslateTransform.YProperty, Keys((0.2, -20), (0.6, 0)));
                break;
            case MascotAction.Hop:
                Add(board, _hop, TranslateTransform.YProperty, Keys((0.2, HopIdle), (0.45, 0), (0.65, -12), (0.9, 0), (1.6, 0)));
                break;
            case MascotAction.BitHop:
                Add(board, _bitHop, TranslateTransform.YProperty, Keys((0.3, -30), (0.7, 0), (0.9, -8), (1.1, 0), (1.6, 0)));
                break;
            case MascotAction.SquashJump:
                // Squash, jump stretched, squash as it lands, back: the whole body, from its feet.
                Add(board, _squash, ScaleTransform.ScaleXProperty, Keys((0.15, 1.14), (0.35, 0.9), (0.6, 1.0), (0.72, 1.12), (0.95, 1.0), (1.6, 1.0)));
                Add(board, _squash, ScaleTransform.ScaleYProperty, Keys((0.15, 0.84), (0.35, 1.12), (0.6, 1.0), (0.72, 0.88), (0.95, 1.0), (1.6, 1.0)));
                Add(board, _hop, TranslateTransform.YProperty, Keys((0.15, 0), (0.4, HopIdle), (0.65, 0), (1.6, 0)));
                break;
            case MascotAction.Smile:
                // The eyes turn into happy arcs for a moment, then back.
                Add(board, _idleEyes, VisibilityProperty, Show((0, Visibility.Collapsed), (1.3, Visibility.Visible)));
                Add(board, _smileEyes, VisibilityProperty, Show((0, Visibility.Visible), (1.3, Visibility.Collapsed)));
                break;
            case MascotAction.Poke when Mood is HelperMood.Attention or HelperMood.Error:
                // A click while something is wrong: a puzzled tilt the other way, no hop and no happy eyes.
                Add(board, _tilt, RotateTransform.AngleProperty, Keys((0.3, -8), (1.0, -8), (1.4, 0)));
                break;
            case MascotAction.Poke:
                // A click: a hop, with happy eyes while it rests (done has them already).
                Add(board, _hop, TranslateTransform.YProperty, Keys((0.2, HopIdle), (0.45, 0), (0.65, -12), (0.9, 0), (1.6, 0)));
                if (Mood == HelperMood.Idle)
                {
                    Add(board, _idleEyes, VisibilityProperty, Show((0, Visibility.Collapsed), (1.3, Visibility.Visible)));
                    Add(board, _smileEyes, VisibilityProperty, Show((0, Visibility.Visible), (1.3, Visibility.Collapsed)));
                }
                break;
        }
        return board;
    }

    private void StartAction(MascotAction action)
    {
        StopAction();
        _action = ActionStoryboard(action);
        Begin(_action);
    }

    private void StopAction()
    {
        if (_action == null) { return; }
        _action.Stop(this);
        _action.Remove(this);
        _running.Remove(_action);
        _action = null;
    }

    /// <summary>The one-shot timer for the next due moment; stopped while still. Background priority: never ahead of input or layout.</summary>
    private void Schedule(DateTime nowUtc)
    {
        if (!_brain.Moving || _brain.NextDueUtc is not DateTime due)
        {
            _timer?.Stop();
            return;
        }
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                Tick(DateTime.UtcNow);
            };
        }
        _timer.Stop();
        var wait = due - nowUtc;
        _timer.Interval = wait > TimeSpan.FromMilliseconds(50) ? wait : TimeSpan.FromMilliseconds(50);
        _timer.Start();
    }

    /// <summary>Every storyboard stopped and removed (the transforms back to the still pose), and nothing scheduled.</summary>
    private void StopAll()
    {
        _timer?.Stop();
        foreach (var board in _running.ToArray())
        {
            board.Stop(this);
            board.Remove(this);
        }
        _running.Clear();
        _action = null;
        _moodBoard = null;
    }

    private void Begin(Storyboard board)
    {
        // Ended storyboards (a blink, a one-time movement) leave the list.
        board.Completed += (_, _) => _running.Remove(board);
        _running.Add(board);
        board.Begin(this, HandoffBehavior.SnapshotAndReplace, isControllable: true);
    }

    private static Storyboard NewStoryboard(int frameRate)
    {
        var board = new Storyboard { FillBehavior = FillBehavior.Stop };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(board, frameRate);
        return board;
    }

    private static void Add(Storyboard board, DependencyObject target, DependencyProperty property, AnimationTimeline animation)
    {
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        board.Children.Add(animation);
    }

    /// <summary>Two eyes (12 x 16, corner radius 3) at the same height; they blink unless <paramref name="blink"/> is false, and look aside.</summary>
    private Canvas Eyes(double left, double top, double right, bool blink = true)
    {
        var eyes = new Canvas();
        eyes.Children.Add(Shape(new Rectangle { Width = 12, Height = 16, RadiusX = 3, RadiusY = 3 }, left, top, "HelperEye"));
        eyes.Children.Add(Shape(new Rectangle { Width = 12, Height = 16, RadiusX = 3, RadiusY = 3 }, right, top, "HelperEye"));
        var look = new TranslateTransform();
        _looks.Add(look);
        var group = new TransformGroup();
        if (blink)
        {
            var scale = new ScaleTransform(1, 1, 0, top + 8);
            _blinks.Add(scale);
            group.Children.Add(scale);
        }
        group.Children.Add(look);
        eyes.RenderTransform = group;
        return eyes;
    }

    private static UIElement Shape(Shape shape, double left, double top, string brush)
    {
        shape.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
        return shape;
    }

    private static UIElement Stroke(Path path, string brush, double thickness)
    {
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brush);
        path.StrokeThickness = thickness;
        path.StrokeStartLineCap = PenLineCap.Round;
        path.StrokeEndLineCap = PenLineCap.Round;
        path.StrokeLineJoin = PenLineJoin.Round;
        return path;
    }

    /// <summary>
    /// The mascot's automation element: in the raw view only (not a control or content element), so
    /// screen readers and Axe.Windows, which read the control view, never meet it; no name, no children,
    /// never focusable. Its automation ID (the element's name, "Mascot") lets a test find its place.
    /// </summary>
    internal sealed class MascotAutomationPeer : FrameworkElementAutomationPeer
    {
        public MascotAutomationPeer(HelperCharacter owner) : base(owner) { }

        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override string GetNameCore() => "";

        protected override string GetClassNameCore() => nameof(HelperCharacter);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override List<AutomationPeer> GetChildrenCore() => null;

        protected override bool IsKeyboardFocusableCore() => false;
    }
}
