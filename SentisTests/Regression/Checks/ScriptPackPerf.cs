using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SentisTests.Core;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.ModAPI;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// What Sentis_v5_ScriptPack_Fix's game logic costs the server's frame, in this world: every logic component of the
    /// mod on the world's grids and blocks, by class - how many, which updates they ask for (every frame, 10th, 100th),
    /// and, for the classes whose updates change nothing the game would not change itself, their updates run 20 times
    /// over and timed: the cost per frame. The game's own dispatch of each update (its lists, the composite logic) comes
    /// on top: told as the updates asked for per frame. Run only when asked (it runs the mod's updates by hand).
    /// </summary>
    public sealed class ScriptPackPerfCheck : RegressionCheck
    {
        public override string Name => Prefix + "scriptpack_perf";
        public override bool OnlyWhenAsked => true;
        public override int TimeoutSeconds => 120;

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const int Rounds = 20;

        /// <summary>Classes run by hand: their updates do what the game does with them anyway, or nothing here.</summary>
        private static readonly HashSet<string> Safe = new HashSet<string>
        {
            "SpeedRestrictionGrid", "NYSnow", "CockpitActions", "CockpitInhibitorBehavior", "SafeZoneInhibitorBehavior",
            "BlinkDrive", "HudLcd", "AdminProtectorBlock", "NanoBotSuppressor", "NanoBotInhibitorBehavior", "CustomInhibitorBlock",
            "LootCollector", "ParachuteFix",
        };

        protected override IEnumerator Check()
        {
            var mod = MyScriptManager.Static?.Scripts?.Values.FirstOrDefault(a => a.GetType("AdminProtector.AdminProtectorBlock") != null);
            SkipUnless(mod != null, "no Sentis_v5_ScriptPack_Fix mod in this world");
            yield return Seconds(1);
            var (ms, calls, components, classes) = Measure(mod, Note);
            Result("the mod's logic: " + (ms * 1000).ToString("F0") + " µs a frame in the classes run, " + calls.ToString("F0") +
                   " updates asked a frame in all (" + components + " components of " + classes + " classes)");
        }

        /// <summary>
        /// The census of the mod's logic components on the world's grids and blocks, the safe classes' updates timed,
        /// and the session's per-frame speed limits pass (when the mod has one): the cost a frame (ms), the updates the game
        /// is asked for a frame, the components and their classes.
        /// </summary>
        public static (double Ms, double Calls, int Components, int Classes) Measure(Assembly mod, Action<string> note)
        {
            var composite = typeof(MyCubeGrid).Assembly.GetType("Sandbox.Game.Entities.MyCompositeGameLogicComponent");
            var inner = composite?.GetField("m_logicComponents", Any);

            var found = new Dictionary<string, List<MyGameLogicComponent>>();
            var entities = 0;
            foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose).ToList())
            {
                foreach (var entity in new MyEntity[] { grid }.Concat(grid.GetFatBlocks()))
                {
                    entities++;
                    var logic = entity.GameLogic as MyGameLogicComponent;
                    if (logic == null) continue;
                    IEnumerable<MyGameLogicComponent> parts = logic.GetType() == composite && inner != null
                        ? (IEnumerable<MyGameLogicComponent>)inner.GetValue(logic) : new[] { logic };
                    foreach (var part in parts)
                    {
                        if (part == null || part.GetType().Assembly != mod) continue;
                        if (!found.TryGetValue(part.GetType().Name, out var list)) found[part.GetType().Name] = list = new List<MyGameLogicComponent>();
                        list.Add(part);
                    }
                }
            }
            note("the mod's logic on " + entities + " grids and blocks of " + MyEntities.GetEntities().OfType<MyCubeGrid>().Count() + " grids");

            double totalMs = 0, totalCalls = 0;
            var lines = new List<(double Ms, string Line)>();
            foreach (var pair in found.OrderByDescending(p => p.Value.Count))
            {
                var name = pair.Key;
                var list = pair.Value;
                int every = 0, tenth = 0, hundredth = 0;
                foreach (var c in list)
                {
                    var u = c.NeedsUpdate;
                    if ((u & MyEntityUpdateEnum.EACH_FRAME) != 0) every++;
                    if ((u & MyEntityUpdateEnum.EACH_10TH_FRAME) != 0) tenth++;
                    if ((u & MyEntityUpdateEnum.EACH_100TH_FRAME) != 0) hundredth++;
                }
                // the game calls both the before and the after update of each frequency asked for
                var calls = 2 * (every + tenth / 10.0 + hundredth / 100.0);
                totalCalls += calls;
                string cost = "not run (its updates act: counted only)";
                double perFrame = 0;
                if (Safe.Contains(name))
                {
                    double t1 = 0, t10 = 0, t100 = 0;
                    var watch = new Stopwatch();
                    foreach (var c in list)
                    {
                        var u = c.NeedsUpdate;
                        // a blink in progress is not run twice
                        if (name == "BlinkDrive" && IsBlinking(c)) continue;
                        try
                        {
                            if ((u & MyEntityUpdateEnum.EACH_FRAME) != 0)
                            {
                                watch.Restart();
                                for (var i = 0; i < Rounds; i++) { c.UpdateBeforeSimulation(); c.UpdateAfterSimulation(); }
                                t1 += watch.Elapsed.TotalMilliseconds / Rounds;
                            }
                            if ((u & MyEntityUpdateEnum.EACH_10TH_FRAME) != 0)
                            {
                                watch.Restart();
                                for (var i = 0; i < Rounds; i++) { c.UpdateBeforeSimulation10(); c.UpdateAfterSimulation10(); }
                                t10 += watch.Elapsed.TotalMilliseconds / Rounds;
                            }
                            if ((u & MyEntityUpdateEnum.EACH_100TH_FRAME) != 0)
                            {
                                watch.Restart();
                                for (var i = 0; i < Rounds; i++) { c.UpdateBeforeSimulation100(); c.UpdateAfterSimulation100(); }
                                t100 += watch.Elapsed.TotalMilliseconds / Rounds;
                            }
                        }
                        catch (Exception e)
                        {
                            note(name + ": an update threw " + e.GetType().Name + ": " + e.Message);
                            break;
                        }
                    }
                    perFrame = t1 + t10 / 10 + t100 / 100;
                    totalMs += perFrame;
                    cost = (perFrame * 1000).ToString("F1") + " µs a frame (each frame " + (t1 * 1000).ToString("F1") + ", 10th " + (t10 * 1000).ToString("F1") +
                           ", 100th " + (t100 * 1000).ToString("F1") + " µs a run)";
                }
                lines.Add((perFrame, name + ": " + list.Count + " (every frame " + every + ", 10th " + tenth + ", 100th " + hundredth + "), " +
                                     calls.ToString("F0") + " updates a frame, " + cost));
            }
            foreach (var line in lines.OrderByDescending(l => l.Ms)) note(line.Line);
            // the speed limits' pass of the session, once a frame (the per-grid component's work, when it has none)
            var pass = mod.GetType("Sentis_v5.SpeedRestrictionsSession")?.GetMethod("ApplyLimits", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (pass != null)
            {
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < Rounds; i++) pass.Invoke(null, null);
                var passMs = watch.Elapsed.TotalMilliseconds / Rounds;
                totalMs += passMs;
                totalCalls += 1;
                note("SpeedRestrictionsSession.ApplyLimits: " + (passMs * 1000).ToString("F1") + " µs a frame");
            }
            return (totalMs, totalCalls, found.Sum(f => f.Value.Count), found.Count);
        }

        private static bool IsBlinking(MyGameLogicComponent c)
        {
            var sync = c.GetType().GetField("IsBlinking", Any)?.GetValue(c);
            var value = sync?.GetType().GetProperty("Value")?.GetValue(sync);
            return value is bool b && b;
        }
    }
}
