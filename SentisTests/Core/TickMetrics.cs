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
            public double AvgWorkMs;
            public double MaxWorkMs;

            public string Format()
            {
                if (Frames == 0)
                    return "no frames sampled";
                return string.Format(
                    "frames={0} fps={1:F1} frame avg={2:F2}ms max={3:F2}ms | plugin work avg={4:F3}ms max={5:F3}ms",
                    Frames, 1000.0 / Math.Max(0.001, AvgFrameMs), AvgFrameMs, MaxFrameMs,
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
            foreach (var f in _frameMs)
                sum += f;
            snapshot.AvgFrameMs = _frameMs.Count > 0 ? sum / _frameMs.Count : 0;
            snapshot.AvgWorkMs = _frames > 0 ? _workSumMs / _frames : 0;
            snapshot.MaxWorkMs = _workMaxMs;

            _frameMs.Clear();
            _frames = 0;
            _maxFrameMs = 0;
            _workSumMs = 0;
            _workMaxMs = 0;
            return snapshot;
        }
    }
}
