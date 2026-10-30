using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SentisTests.Core;
using VRage.Game.Components;
using VRage.Game.Entity;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Whose game logic makes a frame long. The game updates the logic components that are not their entity's own update
    /// (mods' scripts, composite logic of blocks) in <c>MyGameLogic.UpdateBeforeSimulation</c>/<c>UpdateAfterSimulation</c>,
    /// one lambda per update kind (each frame, each 10th, each 100th); SentisWatcher times them all as one part,
    /// "entities.game_logic". Here, for <c>secs=</c> seconds (60 by default), Harmony hooks those six lambdas and times every
    /// call: the components by their type with the total and the worst call, the worst calls with their entity, and the
    /// frames whose game logic was the longest with what was in them. The lambdas are patched by no one else (SentisWatcher
    /// patches the methods round them, with Torch); the hooks are taken off at the end.
    /// </summary>
    public sealed class GameLogicProbeScenario : TestScenario
    {
        public const string ScenarioName = "game_logic_probe";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;
        // it only watches: the grids the game or the players make meanwhile stay
        public override bool KeepsNewGrids => true;

        private const string HarmonyId = "SentisTests.GameLogicProbe";
        private static HarmonyLib.Harmony _harmony;
        private static long _start;
        private static int _depth;
        private static readonly Dictionary<string, (long Ticks, long Calls, long Worst)> ByType = new Dictionary<string, (long, long, long)>();
        private static readonly List<(double Ms, string Who, long Frame)> Worst = new List<(double, string, long)>();
        private static readonly Dictionary<long, (long Ticks, List<(double, string)> Calls)> Frames = new Dictionary<long, (long, List<(double, string)>)>();
        private static long _frame;
        private static double _worstKept;

        public override IEnumerator Run()
        {
            var secs = Regex.Match(TestRunner.Focus ?? "", @"secs=(\d+)") is var m && m.Success ? int.Parse(m.Groups[1].Value) : 60;
            var gameLogic = typeof(MyEntity).Assembly.GetType("VRage.Game.Entity.MyGameLogic")
                            ?? throw new ScenarioFailedException("VRage.Game.Entity.MyGameLogic is not in this game");
            var closure = gameLogic.GetNestedTypes(BindingFlags.NonPublic).FirstOrDefault(t => t.Name == "<>c")
                          ?? throw new ScenarioFailedException("MyGameLogic's lambdas are not where they were");
            var lambdas = closure.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(x => (x.Name.StartsWith("<UpdateBeforeSimulation>") || x.Name.StartsWith("<UpdateAfterSimulation>"))
                            && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(MyGameLogicComponent)).ToList();
            Check(lambdas.Count == 6, $"MyGameLogic: {lambdas.Count} update lambdas found, 6 expected");

            ByType.Clear(); Worst.Clear(); Frames.Clear(); _frame = 0; _worstKept = 0; _depth = 0;
            _harmony = new HarmonyLib.Harmony(HarmonyId);
            try
            {
                var pre = new HarmonyLib.HarmonyMethod(typeof(GameLogicProbeScenario).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic));
                var post = new HarmonyLib.HarmonyMethod(typeof(GameLogicProbeScenario).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic));
                foreach (var lambda in lambdas) _harmony.Patch(lambda, prefix: pre, postfix: post);
                Note($"timing {lambdas.Count} MyGameLogic update lambdas for {secs} s");
                var until = Stopwatch.StartNew();
                while (until.Elapsed.TotalSeconds < secs)
                {
                    _frame++;
                    yield return null;
                }
            }
            finally
            {
                _harmony.UnpatchAll(HarmonyId);
                _harmony = null;
            }

            var frames = Math.Max(_frame, 1);
            Note($"{ByType.Count} component types; per type: total ms a frame, calls a frame, worst call ms");
            foreach (var kv in ByType.OrderByDescending(kv => kv.Value.Ticks).Take(25))
                Note($"  {Ms(kv.Value.Ticks) / frames,8:F4} ms/frame  {kv.Value.Calls / (double)frames,8:F1} calls/frame  worst {Ms(kv.Value.Worst),7:F2} ms  {kv.Key}");
            Note("worst calls:");
            foreach (var w in Worst.OrderByDescending(w => w.Ms).Take(20))
                Note($"  {w.Ms,7:F2} ms  frame {w.Frame}  {w.Who}");
            Note("frames with the most game logic:");
            foreach (var f in Frames.OrderByDescending(f => f.Value.Ticks).Take(10))
                Note($"  frame {f.Key}: {Ms(f.Value.Ticks):F2} ms - " + string.Join("; ", f.Value.Calls.OrderByDescending(c => c.Item1).Take(5).Select(c => $"{c.Item2} {c.Item1:F2}")));
            var top = Worst.OrderByDescending(w => w.Ms).FirstOrDefault();
            Note($"GAME LOGIC PROBE RESULT | {frames} frames; the worst frame's game logic {(Frames.Count == 0 ? 0 : Frames.Max(f => Ms(f.Value.Ticks))):F2} ms; the worst call {top.Ms:F2} ms {top.Who}");
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static void Pre()
        {
            if (_depth++ == 0) _start = Stopwatch.GetTimestamp();
        }

        private static void Post(MyGameLogicComponent __0)
        {
            if (--_depth != 0) return;
            var took = Stopwatch.GetTimestamp() - _start;
            try
            {
                var type = __0?.GetType().FullName ?? "?";
                ByType.TryGetValue(type, out var t);
                ByType[type] = (t.Ticks + took, t.Calls + 1, Math.Max(t.Worst, took));
                var ms = Ms(took);
                if (!Frames.TryGetValue(_frame, out var f)) Frames[_frame] = f = (0, new List<(double, string)>());
                if (ms >= 0.2) f.Calls.Add((ms, Who(__0)));
                Frames[_frame] = (f.Ticks + took, f.Calls);
                // only the frames that matter kept: drop the lightest when there are many
                if (Frames.Count > 4000)
                {
                    var keep = Frames.OrderByDescending(x => x.Value.Ticks).Take(200).Select(x => x.Key).ToHashSet();
                    keep.Add(_frame);
                    foreach (var k in Frames.Keys.Where(k => !keep.Contains(k)).ToList()) Frames.Remove(k);
                }
                if (ms >= 1 && (Worst.Count < 50 || ms > _worstKept))
                {
                    Worst.Add((ms, Who(__0), _frame));
                    if (Worst.Count > 50)
                    {
                        Worst.Sort((a, b) => b.Ms.CompareTo(a.Ms));
                        Worst.RemoveAt(Worst.Count - 1);
                    }
                    _worstKept = Worst.Min(w => w.Ms);
                }
            }
            catch { }
        }

        private static string Who(MyGameLogicComponent c)
        {
            var e = c?.Entity as MyEntity;
            if (e == null) return c?.GetType().Name ?? "?";
            var grid = (e as VRage.Game.ModAPI.IMyCubeBlock)?.CubeGrid;
            return $"{c.GetType().Name} on {e.GetType().Name} '{e.DisplayNameText ?? e.DisplayName}' {e.EntityId}" + (grid != null ? $" of grid '{grid.DisplayName}'" : "");
        }
    }
}
