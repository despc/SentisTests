using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace SentisTests.Core
{
    /// <summary>
    /// Main-thread load sampler. The plugin Update() runs at the end of the server's main (simulation)
    /// thread frame, so:
    ///  - the gap between consecutive Update() calls == full server frame time (simulation cost),
    ///  - the time spent inside Update() == our own plug-in cost.
    /// A snapshot is taken before and after every scenario so reports show load baseline vs load during the test.
    /// </summary>
    public static class TickMetrics
    {
        private const int WindowFrames = 20000;

        private static readonly Stopwatch _gapWatch = new Stopwatch();
        private static readonly Stopwatch _workWatch = new Stopwatch();
        private static readonly Queue<double> _frameMs = new Queue<double>();
        private static int _frames;
        private static double _maxFrameMs;
        private static double _workSumMs;
        private static double _workMaxMs;
        private static bool _inFrame;

        /// <summary>Call at the very beginning of plugin Update().</summary>
        public static void FrameBegin()
        {
            if (_inFrame)
                return;
            _inFrame = true;

            if (_gapWatch.IsRunning)
            {
                var frame = _gapWatch.Elapsed.TotalMilliseconds;
                // absurd outliers (machine sleep / debugger) would skew averages
                if (frame > 0.01 && frame < 60000)
                {
                    _frameMs.Enqueue(frame);
                    _frames++;
                    if (frame > _maxFrameMs)
                        _maxFrameMs = frame;
                    while (_frameMs.Count > WindowFrames)
                        _frameMs.Dequeue();
                }
            }
            _gapWatch.Restart();
            _workWatch.Restart();
        }

        /// <summary>Call at the very end of plugin Update().</summary>
        public static void FrameEnd()
        {
            if (!_inFrame)
                return;
            _inFrame = false;

            var work = _workWatch.Elapsed.TotalMilliseconds;
            _workSumMs += work;
            if (work > _workMaxMs)
                _workMaxMs = work;
        }

        public class Snapshot
        {
            public int Frames;
            public double AvgFrameMs;
            public double MaxFrameMs;
            public double P95FrameMs;
            public double P99FrameMs;
            public double P999FrameMs;
            public int Over50Ms;
            public int Over100Ms;
            public int Over250Ms;
            public double AvgWorkMs;
            public double MaxWorkMs;

            public string Format()
            {
                if (Frames == 0)
                    return "no frames sampled";
                return string.Format(
                    "frames={0} fps={1:F1} frame avg={2:F2}ms p95={3:F2} p99={4:F2} p99.9={5:F2} max={6:F2}ms " +
                    "over50/100/250={7}/{8}/{9} | plugin work avg={10:F3}ms max={11:F3}ms",
                    Frames, 1000.0 / Math.Max(0.001, AvgFrameMs), AvgFrameMs, P95FrameMs,
                    P99FrameMs, P999FrameMs, MaxFrameMs, Over50Ms, Over100Ms, Over250Ms,
                    AvgWorkMs, MaxWorkMs);
            }
        }

        /// <summary>Returns statistics for the frames observed so far and clears the window.</summary>
        public static Snapshot Take()
        {
            var snapshot = new Snapshot
            {
                Frames = _frames,
                MaxFrameMs = _maxFrameMs,
            };
            double sum = 0;
            var sorted = new List<double>(_frameMs);
            sorted.Sort();
            foreach (var f in _frameMs)
            {
                sum += f;
                if (f > 50.0) snapshot.Over50Ms++;
                if (f > 100.0) snapshot.Over100Ms++;
                if (f > 250.0) snapshot.Over250Ms++;
            }
            snapshot.AvgFrameMs = _frameMs.Count > 0 ? sum / _frameMs.Count : 0;
            snapshot.P95FrameMs = Percentile(sorted, 0.95);
            snapshot.P99FrameMs = Percentile(sorted, 0.99);
            snapshot.P999FrameMs = Percentile(sorted, 0.999);
            snapshot.AvgWorkMs = _frames > 0 ? _workSumMs / _frames : 0;
            snapshot.MaxWorkMs = _workMaxMs;

            _frameMs.Clear();
            _frames = 0;
            _maxFrameMs = 0;
            _workSumMs = 0;
            _workMaxMs = 0;
            return snapshot;
        }

        private static double Percentile(List<double> sorted, double percentile)
        {
            if (sorted.Count == 0) return 0;
            var rank = percentile * (sorted.Count - 1);
            var lower = (int)Math.Floor(rank);
            var upper = (int)Math.Ceiling(rank);
            if (lower == upper) return sorted[lower];
            return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
        }
    }
}
