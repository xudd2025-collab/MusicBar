using System;
using System.Collections.Generic;
using MusicBar;
using Windows.Media.Control;

internal static class TimelineTimingChecks
{
    private static int checks;
    private static readonly DateTime Origin = new DateTime(2026, 10, 4, 6, 0, 0, DateTimeKind.Utc);
    private static void Main()
    {
        try
        {
            VerifyNativeReadCompletionAnchor();
            VerifyPauseDuringNativeRead();
            VerifyRateChangeDuringNativeRead();
            VerifyUnstableStateIsRejected();
            VerifyPausedClockAndRepeatedSample();
            VerifyAdvancingNativePositionWithOldTimestamp();
            VerifyResumeWithOldPauseTimestamp();
            VerifyRepeatedPauseResumeAndSeek();
            Console.WriteLine("Timeline timing checks passed: " + checks);
        }
        catch (Exception ex) { Console.WriteLine(ex.ToString()); Environment.ExitCode = 1; }
    }

    private static void VerifyNativeReadCompletionAnchor()
    {
        double elapsed = 2;
        MusicSessionReader.TimelineObservation observation;
        bool coherent = MusicSessionReader.TryReadTimelineObservation(
            () => Playing(1),
            () => { elapsed += .8; return Timeline(10, Origin); },
            () => Origin.AddSeconds(elapsed), out observation);
        Check(coherent, "Stable native reads must produce a timeline observation.");
        Check(observation.CapturedUtc == Origin.AddSeconds(2.8), "The timestamp must follow the native timeline read.");
        using (var reader = new MusicSessionReader())
        {
            var snapshot = Snapshot(observation, "A");
            Apply(reader, snapshot, observation);
            Near(snapshot.PositionSeconds, 12.8, "API sample age must include the native read duration exactly once.");
        }
    }

    private static void VerifyPauseDuringNativeRead()
    {
        var states = new Queue<MusicSessionReader.PlaybackClock>(new[] { Playing(1), Paused(), Paused(), Paused() });
        int reads = 0;
        MusicSessionReader.TimelineObservation observation;
        bool coherent = MusicSessionReader.TryReadTimelineObservation(
            () => states.Dequeue(),
            () => ++reads == 1 ? Timeline(10, Origin) : Timeline(11, Origin.AddSeconds(1)),
            () => Origin.AddSeconds(1.5), out observation);
        Check(coherent && reads == 2, "A mid-read pause must cause one fresh native sample.");
        Check(!observation.Playback.IsPlaying, "The coherent snapshot must preserve the paused state.");
        using (var reader = new MusicSessionReader())
        {
            var snapshot = Snapshot(observation, "A");
            Apply(reader, snapshot, observation);
            Near(snapshot.PositionSeconds, 11, "A paused sample must not advance through native read time.");
        }
    }

    private static void VerifyRateChangeDuringNativeRead()
    {
        var states = new Queue<MusicSessionReader.PlaybackClock>(new[] { Playing(1), Playing(2), Playing(2), Playing(2) });
        int reads = 0;
        MusicSessionReader.TimelineObservation observation;
        bool coherent = MusicSessionReader.TryReadTimelineObservation(
            () => states.Dequeue(),
            () => { reads++; return Timeline(10, Origin.AddSeconds(1)); },
            () => Origin.AddSeconds(1.4), out observation);
        Check(coherent && reads == 2 && observation.Playback.Rate == 2, "A mid-read rate change must use a fresh matching sample.");
        using (var reader = new MusicSessionReader())
        {
            var snapshot = Snapshot(observation, "A");
            Apply(reader, snapshot, observation);
            Near(snapshot.PositionSeconds, 10.8, "The coherent native sample must use the actual playback rate.");
        }
    }

    private static void VerifyUnstableStateIsRejected()
    {
        var states = new Queue<MusicSessionReader.PlaybackClock>(new[] { Playing(1), Paused(), Playing(1), Paused() });
        MusicSessionReader.TimelineObservation observation;
        bool coherent = MusicSessionReader.TryReadTimelineObservation(
            () => states.Dequeue(), () => Timeline(10, Origin), () => Origin.AddSeconds(2), out observation);
        Check(!coherent && states.Count == 0, "Continually changing playback must be rejected after one bounded retry.");
    }

    private static void VerifyPausedClockAndRepeatedSample()
    {
        using (var reader = new MusicSessionReader())
        {
            var paused = new MusicSessionReader.TimelineObservation { Timeline = Timeline(54.561, Origin), Playback = Paused(), CapturedUtc = Origin.AddMinutes(7) };
            var first = Snapshot(paused, "Paused"); Apply(reader, first, paused);
            var secondObservation = paused; secondObservation.CapturedUtc = Origin.AddMinutes(8);
            var second = Snapshot(secondObservation, "Paused"); Apply(reader, second, secondObservation);
            Near(first.PositionSeconds, 54.561, "An old paused SDK timestamp must retain the true paused position.");
            Near(second.PositionSeconds, 54.561, "Repeated paused reads must not drift.");
            var playing = new MusicSessionReader.TimelineObservation { Timeline = Timeline(10, Origin), Playback = Playing(1), CapturedUtc = Origin.AddSeconds(2) };
            var start = Snapshot(playing, "Playing"); Apply(reader, start, playing);
            playing.CapturedUtc = Origin.AddSeconds(2.35);
            var later = Snapshot(playing, "Playing"); Apply(reader, later, playing);
            Near(later.PositionSeconds, 12.35, "Repeated unchanged SDK samples must retain the extrapolated playback clock.");
        }
    }

    private static void VerifyAdvancingNativePositionWithOldTimestamp()
    {
        using (var reader = new MusicSessionReader())
        {
            var initial = new MusicSessionReader.TimelineObservation { Timeline = Timeline(10, Origin), Playback = Playing(1), CapturedUtc = Origin.AddSeconds(60) };
            var first = Snapshot(initial, "Live position"); Apply(reader, first, initial);
            Near(first.PositionSeconds, 10, "A cold read must not add a minute-old publisher timestamp to a current native position.");
            for (int seconds = 1; seconds <= 40; seconds++)
            {
                var observation = initial;
                observation.Timeline = Timeline(10 + seconds, Origin);
                observation.CapturedUtc = Origin.AddSeconds(60 + seconds);
                var live = Snapshot(observation, "Live position"); Apply(reader, live, observation);
                Near(live.PositionSeconds, 10 + seconds, "An advancing native position with an unchanged timestamp must stay at one-second-per-second speed.");
            }
            initial.Timeline = Timeline(50, Origin);
            initial.Playback = Paused();
            initial.CapturedUtc = Origin.AddSeconds(100.4);
            var paused = Snapshot(initial, "Live position"); Apply(reader, paused, initial);
            Near(paused.PositionSeconds, 50, "Pausing a live-position publisher must freeze its actual native position.");
        }
        using (var reader = new MusicSessionReader())
        {
            var observation = new MusicSessionReader.TimelineObservation { Timeline = Timeline(10, Origin), Playback = Playing(1), CapturedUtc = Origin.AddSeconds(2) };
            Apply(reader, Snapshot(observation, "Anchor publisher"), observation);
            observation.Timeline = Timeline(11, Origin.AddSeconds(1));
            observation.CapturedUtc = Origin.AddSeconds(3);
            var delayed = Snapshot(observation, "Anchor publisher"); Apply(reader, delayed, observation);
            Near(delayed.PositionSeconds, 13, "A genuinely updated short-delay anchor must still compensate its measured age exactly once.");
        }
    }

    private static void VerifyResumeWithOldPauseTimestamp()
    {
        using (var reader = new MusicSessionReader())
        {
            var observation = new MusicSessionReader.TimelineObservation { Timeline = Timeline(91.947, Origin), Playback = Paused(), CapturedUtc = Origin.AddHours(3) };
            Apply(reader, Snapshot(observation, "Resume"), observation);
            observation.Playback = Playing(1);
            observation.Timeline = Timeline(92.247, Origin);
            observation.CapturedUtc = Origin.AddHours(3).AddSeconds(.3);
            var resumed = Snapshot(observation, "Resume"); Apply(reader, resumed, observation);
            Near(resumed.PositionSeconds, 92.247, "A resumed native update must not count the three-hour pause or jump to the track ending.");
            observation.Timeline = Timeline(92.547, Origin.AddSeconds(.6));
            observation.CapturedUtc = Origin.AddHours(3).AddSeconds(.6);
            var laggingStamp = Snapshot(observation, "Resume"); Apply(reader, laggingStamp, observation);
            Near(laggingStamp.PositionSeconds, 92.547, "A later but still obsolete publisher timestamp must not reintroduce paused time.");
            observation.Timeline = Timeline(93.047, Origin.AddHours(3).AddSeconds(1.1));
            observation.CapturedUtc = Origin.AddHours(3).AddSeconds(1.1);
            var fresh = Snapshot(observation, "Resume"); Apply(reader, fresh, observation);
            Near(fresh.PositionSeconds, 93.047, "Fresh publisher timestamps must retain the actual resumed position.");
        }
    }

    private static void VerifyRepeatedPauseResumeAndSeek()
    {
        using (var reader = new MusicSessionReader())
        {
            var observation = new MusicSessionReader.TimelineObservation { Timeline = Timeline(10, Origin), Playback = Playing(1), CapturedUtc = Origin.AddSeconds(2) };
            Apply(reader, Snapshot(observation, "Repeated"), observation);
            observation.Playback = Paused(); observation.CapturedUtc = Origin.AddSeconds(4);
            var paused = Snapshot(observation, "Repeated"); Apply(reader, paused, observation);
            Near(paused.PositionSeconds, 14, "A publisher retaining an anchor on pause must freeze the extrapolated active time.");
            for (int cycle = 0; cycle < 10; cycle++)
            {
                observation.Playback = Playing(1); observation.CapturedUtc = Origin.AddSeconds(104 + cycle * 101);
                var resumed = Snapshot(observation, "Repeated"); Apply(reader, resumed, observation);
                Near(resumed.PositionSeconds, 14 + cycle, "Each unchanged-anchor resume must exclude every completed pause.");
                observation.Playback = Paused(); observation.CapturedUtc = observation.CapturedUtc.AddSeconds(1);
                var nextPause = Snapshot(observation, "Repeated"); Apply(reader, nextPause, observation);
                Near(nextPause.PositionSeconds, 15 + cycle, "Repeated pause cycles must accumulate only active playback seconds.");
            }
            observation.Timeline = Timeline(80, observation.CapturedUtc); observation.CapturedUtc = observation.CapturedUtc.AddSeconds(.2);
            var seek = Snapshot(observation, "Repeated"); Apply(reader, seek, observation);
            Near(seek.PositionSeconds, 80, "Seeking while paused must use the new native position immediately.");
            observation.Playback = Playing(2); observation.CapturedUtc = observation.CapturedUtc.AddSeconds(30);
            var rateResume = Snapshot(observation, "Repeated"); Apply(reader, rateResume, observation);
            Near(rateResume.PositionSeconds, 80, "Resuming at a new playback rate must exclude the preceding pause.");
            observation.CapturedUtc = observation.CapturedUtc.AddSeconds(.5);
            var rateLater = Snapshot(observation, "Repeated"); Apply(reader, rateLater, observation);
            Near(rateLater.PositionSeconds, 81, "After resume, the clock must use the player's reported rate without extra ageing.");
        }
    }

    private static MusicSessionReader.PlaybackClock Playing(double rate) { return new MusicSessionReader.PlaybackClock { Status = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, Rate = rate }; }
    private static MusicSessionReader.PlaybackClock Paused() { return new MusicSessionReader.PlaybackClock { Status = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, Rate = 1 }; }
    private static MusicSessionReader.RawTimeline Timeline(double position, DateTime updated) { return new MusicSessionReader.RawTimeline { Position = position, Duration = 240, UpdatedUtc = updated }; }
    private static MusicSnapshot Snapshot(MusicSessionReader.TimelineObservation observation, string title) { return new MusicSnapshot { Player = MusicPlayer.QQMusic, Title = title, TimestampUtc = observation.CapturedUtc, IsPlaying = observation.Playback.IsPlaying, PlaybackRate = observation.Playback.Rate }; }
    private static void Apply(MusicSessionReader reader, MusicSnapshot snapshot, MusicSessionReader.TimelineObservation observation) { reader.ApplyTimeline(snapshot, "QQMusic.exe", observation.Timeline.Position, observation.Timeline.Duration, observation.Timeline.UpdatedUtc, observation.Playback.Rate); }
    private static void Near(double actual, double expected, string message) { Check(Math.Abs(actual - expected) < .000001, message + " Actual=" + actual + " Expected=" + expected); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
}
