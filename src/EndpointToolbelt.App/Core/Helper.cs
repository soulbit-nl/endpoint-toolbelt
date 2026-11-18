using System;

namespace EndpointToolbelt.App.Core;

/// <summary>The helper character's pose. Each is a distinct pose; none carries information alone.</summary>
public enum HelperMood
{
    /// <summary>Resting: eyes ahead, the bit above its head.</summary>
    Idle,

    /// <summary>A check or action runs: eyes up and left, three small squares pulse one after another.</summary>
    Working,

    /// <summary>It finished: happy eyes, the bit pops in with a check mark (with a short happy bounce).</summary>
    Done,

    /// <summary>Something needs attention: one raised eyebrow, the bit with a question mark.</summary>
    Attention,

    /// <summary>The service cannot be reached, or a run failed: eyes low, the bit on the ground.</summary>
    Error,
}

/// <summary>
/// One small movement of the mascot, at most one at a time: an idle action (a look to the left
/// or right, a small wave of its bit, a tilt) or the happy bounce when something ended well.
/// </summary>
public enum MascotAction
{
    None,
    LookLeft,
    LookRight,
    Wave,
    Tilt,
    Bounce,

    /// <summary>A small double hop in place (idle).</summary>
    Hop,

    /// <summary>The bit above the head jumps up and lands again (idle).</summary>
    BitHop,

    /// <summary>The eyes turn into happy arcs for a moment (idle).</summary>
    Smile,

    /// <summary>The whole body squashes, jumps stretched and squashes again as it lands (idle).</summary>
    SquashJump,

    /// <summary>The answer to a click: a hop with happy eyes.</summary>
    Poke,
}

/// <summary>
/// When and how the helper character shows. It is decorative: the text and the progress
/// indicator always carry the information, so every rule here may hide it without losing any.
/// </summary>
public static class HelperRules
{
    /// <summary>
    /// Shown unless Windows uses a high contrast theme (its brand colours are not system colours). The
    /// helper is part of the brand, so neither the user nor the organization turns it off.
    /// </summary>
    public static bool Visible(bool highContrast) => !highContrast;

    /// <summary>Animated only when Windows shows animations; otherwise every mood is a still pose.</summary>
    public static bool Animate(bool windowsAnimations) => windowsAnimations;

    /// <summary>
    /// The one mascot's mood on every page, from the app's state; a reaction (anything but idle)
    /// stops the idle actions. Working while something runs (a tool, a check, the health checks);
    /// for a few seconds after something ended, the tone it ended with (done with a happy bounce,
    /// attention, error); an error while the service cannot be reached; the concerned look ("?")
    /// while the page shows a problem; idle otherwise.
    /// </summary>
    public static HelperMood ForPage(bool running, bool justEnded, Tone endedTone, bool serviceMissing, bool pageShowsProblem)
    {
        if (running) { return HelperMood.Working; }
        if (justEnded && endedTone is Tone.Success or Tone.Warning or Tone.Error) { return ForRun(false, endedTone); }
        if (serviceMissing) { return HelperMood.Error; }
        return pageShowsProblem ? HelperMood.Attention : HelperMood.Idle;
    }

    /// <summary>A run's mood: working while it runs, then the tone of how it ended.</summary>
    public static HelperMood ForRun(bool running, Tone tone)
    {
        if (running) { return HelperMood.Working; }
        return tone switch
        {
            Tone.Success => HelperMood.Done,
            Tone.Warning => HelperMood.Attention,
            Tone.Error => HelperMood.Error,
            _ => HelperMood.Idle,
        };
    }

    /// <summary>How long the mood after something ended lasts before the page's own mood returns.</summary>
    public static readonly TimeSpan EndedFor = TimeSpan.FromSeconds(4);

    /// <summary>The mascot's one size on every page (device-independent pixels), as the theme's MascotSize.</summary>
    public const double Size = 48;
}

/// <summary>
/// The mascot's calm life as a state machine, without any drawing, so tests can check it with a
/// clock and a seeded random of their own. It moves only while it may: Windows' animation effects on,
/// no high contrast, and shown (not minimized or hidden); otherwise it is static with nothing
/// scheduled. While it moves it breathes (a slow scale of 1 to 2 %), blinks every 3 to 6 seconds, and,
/// while idle, makes one small action every 3 to 6 seconds (its bit floats gently in between); never more than one action at a time. A
/// reaction (any mood but idle) takes priority: it stops an idle action; while it asks for attention
/// or shows an error it makes only calm actions (a look aside, a tilt) at the same pace, while it works
/// none; done starts the happy bounce.
/// </summary>
public sealed class MascotBrain
{
    public static readonly TimeSpan BlinkMin = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan BlinkMax = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan ActionMin = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ActionMax = TimeSpan.FromSeconds(6);

    /// <summary>How long an idle action takes (look, wave, tilt), and the bounce.</summary>
    public static readonly TimeSpan ActionLength = TimeSpan.FromSeconds(1.6);
    public static readonly TimeSpan BounceLength = TimeSpan.FromSeconds(0.6);

    private static readonly MascotAction[] IdleActions = { MascotAction.LookLeft, MascotAction.LookRight, MascotAction.Wave, MascotAction.Tilt, MascotAction.Hop, MascotAction.BitHop, MascotAction.Smile, MascotAction.SquashJump };

    /// <summary>The calm actions while it asks for attention or shows an error: it looks around or tilts its head, never hops, waves or smiles.</summary>
    private static readonly MascotAction[] CalmActions = { MascotAction.LookLeft, MascotAction.LookRight, MascotAction.Tilt };

    /// <summary>The moods with actions of their own: idle (all eight) and attention and error (the calm ones).</summary>
    private static bool Acts(HelperMood mood) => mood is HelperMood.Idle or HelperMood.Attention or HelperMood.Error;
    private readonly Random _random;

    public MascotBrain(Random random = null) => _random = random ?? new Random();

    public HelperMood Mood { get; private set; } = HelperMood.Idle;

    /// <summary>It may move now: animation effects on, no high contrast, shown. False: a still pose, nothing scheduled.</summary>
    public bool Moving { get; private set; }

    /// <summary>The one action running now, or none.</summary>
    public MascotAction Action { get; private set; }

    public DateTime ActionEndsUtc { get; private set; }

    public DateTime? NextBlinkUtc { get; private set; }

    /// <summary>When the next action starts; null while it does not move, works or shows done.</summary>
    public DateTime? NextActionUtc { get; private set; }

    /// <summary>The earliest moment something is due (a blink, an action's start or end); null when nothing is.</summary>
    public DateTime? NextDueUtc
    {
        get
        {
            DateTime? due = null;
            foreach (var at in new[] { NextBlinkUtc, NextActionUtc, Action == MascotAction.None ? (DateTime?)null : ActionEndsUtc })
            {
                if (at.HasValue && (!due.HasValue || at.Value < due.Value)) { due = at; }
            }
            return due;
        }
    }

    /// <summary>
    /// The conditions to move: Windows' animation effects, high contrast and whether the mascot is shown
    /// (the window not minimized or hidden). True when this changed whether it moves.
    /// </summary>
    public bool SetConditions(bool windowsAnimations, bool highContrast, bool shown, DateTime nowUtc)
    {
        var moving = HelperRules.Animate(windowsAnimations) && HelperRules.Visible(highContrast) && shown;
        if (moving == Moving) { return false; }
        Moving = moving;
        Action = MascotAction.None;
        if (!moving)
        {
            NextBlinkUtc = null;
            NextActionUtc = null;
            return true;
        }
        NextBlinkUtc = nowUtc + Between(BlinkMin, BlinkMax);
        NextActionUtc = Acts(Mood) ? nowUtc + Between(ActionMin, ActionMax) : null;
        return true;
    }

    /// <summary>
    /// A new mood from the app's state. A reaction stops an idle action; done starts the bounce (only
    /// while it moves). Returns the action to start now, or none.
    /// </summary>
    public MascotAction SetMood(HelperMood mood, DateTime nowUtc)
    {
        if (mood == Mood) { return MascotAction.None; }
        Mood = mood;
        if (!Moving) { return MascotAction.None; }
        if (mood == HelperMood.Idle)
        {
            // Back at rest: the next idle action comes after a calm pause, never at once.
            if (Action != MascotAction.Bounce) { Action = MascotAction.None; }
            NextActionUtc = nowUtc + Between(ActionMin, ActionMax);
            return MascotAction.None;
        }
        if (mood == HelperMood.Done)
        {
            NextActionUtc = null;
            Action = MascotAction.Bounce;
            ActionEndsUtc = nowUtc + BounceLength;
            return MascotAction.Bounce;
        }
        // Working: no action. Attention or error: the running action stops; a calm one comes after a pause.
        Action = MascotAction.None;
        NextActionUtc = Acts(mood) ? nowUtc + Between(ActionMin, ActionMax) : null;
        return MascotAction.None;
    }

    /// <summary>
    /// What is due at <paramref name="nowUtc"/>: a blink, and an idle action to start (none while another
    /// action runs or a reaction shows). An action that has run its time ends first.
    /// </summary>
    public (bool Blink, MascotAction Start) Tick(DateTime nowUtc)
    {
        if (!Moving) { return (false, MascotAction.None); }
        if (Action != MascotAction.None && nowUtc >= ActionEndsUtc)
        {
            Action = MascotAction.None;
            if (Acts(Mood) && NextActionUtc == null) { NextActionUtc = nowUtc + Between(ActionMin, ActionMax); }
        }
        var blink = false;
        if (NextBlinkUtc.HasValue && nowUtc >= NextBlinkUtc.Value)
        {
            blink = true;
            NextBlinkUtc = nowUtc + Between(BlinkMin, BlinkMax);
        }
        var start = MascotAction.None;
        if (Acts(Mood) && Action == MascotAction.None && NextActionUtc.HasValue && nowUtc >= NextActionUtc.Value)
        {
            var choice = Mood == HelperMood.Idle ? IdleActions : CalmActions;
            start = choice[_random.Next(choice.Length)];
            Action = start;
            ActionEndsUtc = nowUtc + ActionLength;
            NextActionUtc = ActionEndsUtc + Between(ActionMin, ActionMax);
        }
        return (blink, start);
    }

    /// <summary>
    /// A click on the mascot: a hop with happy eyes while it rests or shows done, a puzzled tilt while
    /// it asks for attention or shows an error (it still answers, without looking happy), nothing while
    /// it works. Only while it moves, and never on top of another action (clicks during one are
    /// ignored). The next idle action comes a calm pause later. Returns the action to start, or none.
    /// </summary>
    public MascotAction Poke(DateTime nowUtc)
    {
        if (!Moving || Action != MascotAction.None || Mood == HelperMood.Working) { return MascotAction.None; }
        Action = MascotAction.Poke;
        ActionEndsUtc = nowUtc + ActionLength;
        if (Acts(Mood)) { NextActionUtc = ActionEndsUtc + Between(ActionMin, ActionMax); }
        return MascotAction.Poke;
    }

    private TimeSpan Between(TimeSpan min, TimeSpan max) => min + TimeSpan.FromMilliseconds(_random.NextDouble() * (max - min).TotalMilliseconds);
}

/// <summary>
/// The window's sizes. One layout at every size: the tabs at the top and one centered
/// column of at most <see cref="ContentMaxWidth"/> (the theme's ContentMaxWidth); a larger or
/// maximized window only adds margin.
/// </summary>
public static class WindowLayout
{
    /// <summary>The widest the content column gets, its side margins included.</summary>
    public const double ContentMaxWidth = 900;

    /// <summary>
    /// The room Home and the For IT landing must fit in without a vertical scroll bar, in their
    /// normal states: the client area of the default window (540 wide) on a small screen. The
    /// window has one stable size, <see cref="DefaultWindowWidth"/> x <see cref="DefaultWindowHeight"/>
    /// by default (a client area of about 520 x 600, which fits a 1280 x 720 work area, 1920 x 1080 at
    /// 150 %, with room for the taskbar); it changes only when the user resizes or maximizes it, never
    /// with the page, and a page taller than the window scrolls inside it. A test lays out the pages
    /// at the client size.
    /// </summary>
    public const double DefaultClientWidth = 520;
    public const double CompactClientHeight = 600;
    public const double DefaultWindowWidth = 540;
    public const double DefaultWindowHeight = 640;
}
