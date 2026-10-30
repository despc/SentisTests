using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The wild animals' search for a target round players, and what SentisOptimisations' WolfTargetPath saves of it.
    ///
    /// <c>wolves</c> of the game's wolves (<c>MyAIComponent.SpawnNewBot</c>, the wolf's bot definition) on a planet's
    /// ground 40-120 m round <c>players</c> fake players standing on it 10 m apart, invulnerable (not untargetable: the
    /// wolves are to go for them). Phases (<c>modes=off,on,off,on</c>, <c>phase</c> s each): WolfTargetPath's transpiler
    /// taken off the game's <c>MyWolfActions.GetTargetWithPriority</c> (off) or put back (on); measured in each: the time
    /// in that method a second and its calls, the paths it asked for and skipped, the server's frames, how many wolves
    /// stood by a player (within 5 m) on average - the wolves are to behave the same either way. A measure: it fails
    /// only when the wolves did not come.
    /// </summary>
    internal sealed class WolfTargetsScenario : TestScenario
    {
        public const string ScenarioName = "wolf_targets";
        private const string PatchId = "SentisOptimisations.WolfTargetPath";

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _optimisations = new ConfigOverride(ConfigOverride.Optimisations);
        private readonly List<ulong> _protected = new List<ulong>();
        private readonly List<MyCharacter> _wolves = new List<MyCharacter>();
        private HarmonyLib.Harmony _harmony;
        private MethodInfo _target;
        private Type _patch;
        private bool _patchOff;

        private static long _calls, _ticks;
        private static readonly Stopwatch Timer = new Stopwatch();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;

        private static int Arg(string name, int otherwise)
        {
            var m = Regex.Match(TestRunner.Focus ?? "", @"\b" + name + @"=(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : otherwise;
        }

        private static string Text(string name, string otherwise)
        {
            var m = Regex.Match(TestRunner.Focus ?? "", @"\b" + name + @"=([\w,]+)");
            return m.Success ? m.Groups[1].Value : otherwise;
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var wolfCount = Arg("wolves", 12);
            var playerCount = Arg("players", 4);
            var phaseSeconds = Arg("phase", 40);
            var modes = Text("modes", "off,on,off,on").Split(',');
            _patch = Integrations.TypeOf("SentisOptimisations", "SentisOptimisationsPlugin.WolfTargetPath");
            Check(_patch != null, "SentisOptimisations has no WolfTargetPath");
            _target = typeof(SpaceEngineers.Game.AI.MyWolfActions).GetMethod("GetTargetWithPriority", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Check(_target != null, "no MyWolfActions.GetTargetWithPriority");
            _optimisations.Set("FreezerEnabled", false);

            // the planet's ground 2 km off the regression's anchor
            var anchor = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + "regression-anchor").Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var other = Vector3D.Cross(up, side);
            var centre = anchor + side * 2000;
            centre = planet.GetClosestSurfacePointGlobal(ref centre);
            Vector3D Ground(Vector3D at) => planet.GetClosestSurfacePointGlobal(ref at) + up * 1.0;

            var spots = Enumerable.Range(0, playerCount).Select(i => Ground(centre + side * ((i - (playerCount - 1) / 2.0) * 10))).ToList();
            FakeClients.RemoveAll();
            FakeClients.Add(playerCount, Network, i => (spots[i], 0, 0), withCharacters: true);
            for (var i = 0; i < FakeClients.Count; i++)
            {
                var steamId = FakeClients.PlayerOf(i).Id.SteamId;
                _protected.Add(steamId);
                MySession.Static.RemoteAdminSettings[steamId] = AdminSettingsEnum.Invulnerable;
            }
            var arrive = WaitForSeconds(5, "the players arrive");
            while (arrive.MoveNext()) yield return arrive.Current;

            var definition = Sandbox.Definitions.MyDefinitionManager.Static.GetBotDefinition(MyDefinitionId.Parse("MyObjectBuilder_AnimalBot/Wolf")) as Sandbox.Definitions.MyAgentDefinition;
            Check(definition != null, "no wolf bot definition");
            bool IsWolf(MyCharacter c) => !c.IsDead && !c.MarkedForClose && c.Definition?.Id.SubtypeName?.Contains("Wolf") == true && c.Physics != null;
            var before = new HashSet<long>(MyEntities.GetEntities().OfType<MyCharacter>().Select(c => c.EntityId));
            for (var k = 0; k < wolfCount; k++)
            {
                var angle = MathHelper.TwoPi / wolfCount * k;
                Sandbox.Game.AI.MyAIComponent.Static.SpawnNewBot(definition, Ground(centre + (side * Math.Cos(angle) + other * Math.Sin(angle)) * (40 + 80.0 * k / Math.Max(1, wolfCount - 1))));
                yield return null;
            }
            List<MyCharacter> Born() => MyEntities.GetEntities().OfType<MyCharacter>()
                .Where(c => !before.Contains(c.EntityId) && IsWolf(c) && Vector3D.Distance(c.PositionComp.GetPosition(), centre) < 300).ToList();
            var born = Wait(() => Born().Count >= wolfCount, wolfCount + " wolves come into the world", 30);
            while (born.MoveNext()) yield return born.Current;
            _wolves.AddRange(Born());

            var harmony = _harmony = new HarmonyLib.Harmony("SentisTests.WolfTargets");
            harmony.Patch(_target, prefix: new HarmonyLib.HarmonyMethod(typeof(WolfTargetsScenario).GetMethod(nameof(Before), BindingFlags.Static | BindingFlags.NonPublic)),
                postfix: new HarmonyLib.HarmonyMethod(typeof(WolfTargetsScenario).GetMethod(nameof(After), BindingFlags.Static | BindingFlags.NonPublic)));
            Note("WOLF TARGETS | " + _wolves.Count + " wolves 40-120 m round " + playerCount + " players on " + planet.Generator?.Id.SubtypeName + "; phases " + string.Join(", ", modes) + ", " + phaseSeconds + " s each");

            var results = new List<string>();
            foreach (var mode in modes)
            {
                SetPatch(mode == "on");
                var settle = WaitForSeconds(5, "switched " + mode);
                while (settle.MoveNext()) yield return settle.Current;
                _calls = 0;
                _ticks = 0;
                var paths0 = Counter("Paths");
                var skipped0 = Counter("Skipped");
                var noWay0 = Counter("NoWayKept");
                TickMetrics.Take();
                var watch = Stopwatch.StartNew();
                var nearSum = 0.0;
                var samples = 0;
                while (watch.Elapsed.TotalSeconds < phaseSeconds)
                {
                    if (samples < watch.Elapsed.TotalSeconds * 2)
                    {
                        samples++;
                        var players = Enumerable.Range(0, FakeClients.Count).Select(FakeClients.Character).Where(c => c != null && !c.IsDead).Select(c => c.PositionComp.GetPosition()).ToList();
                        nearSum += _wolves.Count(w => IsWolf(w) && players.Any(p => Vector3D.Distance(p, w.PositionComp.GetPosition()) < 5));
                    }
                    yield return null;
                }
                var seconds = watch.Elapsed.TotalSeconds;
                var metrics = TickMetrics.Take();
                var line = mode + ": search " + (_ticks * 1000.0 / Stopwatch.Frequency / seconds).ToString("F1") + " ms/s in " + (_calls / seconds).ToString("F0") + " calls/s, paths " +
                           ((Counter("Paths") - paths0) / seconds).ToString("F1") + "/s, skipped " + ((Counter("Skipped") - skipped0) / seconds).ToString("F1") + "/s, no way kept " + ((Counter("NoWayKept") - noWay0) / seconds).ToString("F1") + "/s, wolves by a player " +
                           (nearSum / Math.Max(1, samples)).ToString("F1") + " of " + _wolves.Count(IsWolf) + ", frame avg " + metrics.AvgFrameMs.ToString("F2") + " ms, p99 " + metrics.P99FrameMs.ToString("F1");
                Note(line);
                results.Add(line);
            }
            Note("WOLF TARGETS RESULT | " + string.Join(" || ", results));
        }

        private long Counter(string name) => (long?)_patch.GetField(name, BindingFlags.Static | BindingFlags.Public)?.GetValue(null) ?? 0;

        private void SetPatch(bool on)
        {
            if (on && _patchOff)
            {
                _patch.GetMethod("PatchImpl", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, new object[] { null });
                _patchOff = false;
            }
            else if (!on && !_patchOff)
            {
                new HarmonyLib.Harmony(PatchId).Unpatch(_target, HarmonyLib.HarmonyPatchType.Transpiler, PatchId);
                _patchOff = true;
            }
        }

        private static void Before(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void After(long __state)
        {
            _ticks += Stopwatch.GetTimestamp() - __state;
            _calls++;
        }

        public override void Cleanup()
        {
            try
            {
                SetPatch(true);
                _harmony?.Unpatch(_target, HarmonyLib.HarmonyPatchType.All, "SentisTests.WolfTargets");
                foreach (var wolf in _wolves)
                    if (!wolf.IsDead && !wolf.MarkedForClose) wolf.DoDamage(10000, MyDamageType.Suicide, true, 0);
                _wolves.Clear();
                FakeClients.RemoveAll();
                foreach (var steamId in _protected) MySession.Static?.RemoteAdminSettings.Remove(steamId);
                _protected.Clear();
                _optimisations.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
