using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// <see cref="ThawFallScenario"/> across a restart. What fell through the planet on the stand had lain there through
    /// many restarts (a respawn rover with its four wheels, landed on 06.10.2026, fell on 10.10.2026 13 s after
    /// freezer_stress switched the freezer off), frozen again after every load. thaw_seed leaves junk lying on the
    /// ground and saves the world; the server is restarted; thaw_release finds it frozen, switches FreezePhysics on and
    /// the freezer off the way freezer_stress does, and watches: none may end up more than 5 m under where it lay.
    /// </summary>
    public sealed class ThawRestartScenario : TestScenario
    {
        public const string SeedName = "thaw_seed";
        public const string ReleaseName = "thaw_release";
        internal const string Prefix = WorldApi.EntityPrefix + "thawkeep-";
        private const int Junk = 24;
        private const double SpreadM = 120;
        private const double SinkM = 5;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly List<ulong> _protected = new List<ulong>();
        private readonly bool _seed;

        public ThawRestartScenario(bool seed) { _seed = seed; }

        public override string Name => _seed ? SeedName : ReleaseName;
        public override int TimeoutSeconds => 600;
        public override bool KeepsNewGrids => _seed;

        public override IEnumerator Run() => _seed ? Seed() : Release();

        private IEnumerator Seed()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            foreach (var old in Kept()) old.Close();
            yield return WaitForTicks(30);
            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var up = Vector3D.Normalize(anchor - centre);
            var east = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var north = Vector3D.Cross(up, east);
            // its own patch, away from thaw_fall's
            var site = planet.GetClosestSurfacePointGlobal(anchor - east * 2500 + north * 2500);
            Player(site + up * 5);
            var random = new Random(4712);
            for (var i = 0; i < Junk; i++)
            {
                var at = site + east * ((random.NextDouble() * 2 - 1) * SpreadM) + north * ((random.NextDouble() * 2 - 1) * SpreadM);
                var ground = planet.GetClosestSurfacePointGlobal(ref at);
                var upAt = Vector3D.Normalize(ground - centre);
                var large = i % 3 != 0;
                var plate = i % 4 == 0 ? 3 : 1;
                var blocks = new List<MyObjectBuilder_CubeBlock>();
                for (var x = 0; x < plate; x++)
                for (var z = 0; z < plate; z++)
                {
                    var block = WorldApi.MakeBlockOb(large ? "LargeBlockArmorBlock" : "SmallBlockArmorBlock");
                    block.Min = new SerializableVector3I(x, 0, z);
                    blocks.Add(block);
                }
                WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
                {
                    Name = Prefix + i,
                    DisplayName = Prefix + i,
                    GridSizeEnum = large ? MyCubeSize.Large : MyCubeSize.Small,
                    IsStatic = false,
                    PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(ground + upAt * (large ? 4 : 2),
                        Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(upAt)), upAt)),
                    PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                    CubeBlocks = blocks,
                });
                yield return null;
            }
            var settle = WaitForSeconds(15, "the grids settle on the ground");
            while (settle.MoveNext()) yield return settle.Current;
            // nobody by them: the stand's freezer (FreezePhysics off) freezes them, and they are saved that way
            FakeClients.RemoveAll();
            var kept = Kept();
            var frozenAt = DateTime.UtcNow;
            while ((DateTime.UtcNow - frozenAt).TotalSeconds < 90 && kept.Any(g => !RuntimePluginControls.IsGridFrozen(g.EntityId)))
                yield return null;
            Note(kept.Count(g => RuntimePluginControls.IsGridFrozen(g.EntityId)) + " of " + kept.Count + " frozen; lying " +
                 string.Join("/", kept.Select(g => Over(planet, g).ToString("F1"))) + " m over the ground");
            var task = SentisTestsPlugin.TorchInstance.Save();
            Check(task != null, "Torch refused to start a save");
            while (!task.IsCompleted) yield return null;
            Note("THAW SEED RESULT | " + kept.Count + " grids left on the ground at " + site + ", saved: " + task.Result);
        }

        private IEnumerator Release()
        {
            WorldApi.EnsureUnpaused(Name);
            var kept = Kept();
            Check(kept.Count > 0, "no " + Prefix + "* grids: run thaw_seed and restart first");
            foreach (var g in kept) Track(g);
            foreach (var anchor in MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.DisplayName != null && g.DisplayName.StartsWith(Prefix + "anchor")).ToList())
                Track(anchor);
            var planet = MyGamePruningStructure.GetClosestPlanet(kept[0].PositionComp.GetPosition());
            Check(planet != null, "no planet");
            var site = kept.Aggregate(Vector3D.Zero, (sum, g) => sum + g.PositionComp.GetPosition()) / kept.Count;
            var up = Vector3D.Normalize(site - planet.PositionComp.GetPosition());
            var east = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var lying = kept.Select(g => Over(planet, g)).ToList();
            Note("after the restart: " + kept.Count(g => RuntimePluginControls.IsGridFrozen(g.EntityId)) + " of " + kept.Count + " frozen, lying " +
                 string.Join("/", lying.Select(h => h.ToString("F1"))) + " m over the ground");
            // a player in their cluster of the physics but beyond the freeze distance, as freezer_stress has players about
            Player(site + up * 5 + east * 3500);
            _config.Set("FreezePhysics", true);
            var hold = WaitForSeconds(20, "FreezePhysics on");
            while (hold.MoveNext()) yield return hold.Current;
            var fixedBodies = kept.Count(FreezerState.IsPhysicsFrozen);
            Note("FreezePhysics on: " + fixedBodies + " of them with fixed bodies");
            _config.Set("FreezerEnabled", false);
            var deepest = lying.Select(_ => double.MaxValue).ToArray();
            var fastest = new double[kept.Count];
            var watch = DateTime.UtcNow;
            while ((DateTime.UtcNow - watch).TotalSeconds < 40)
            {
                for (var i = 0; i < kept.Count; i++)
                {
                    if (kept[i].Closed) continue;
                    deepest[i] = Math.Min(deepest[i], Over(planet, kept[i]));
                    fastest[i] = Math.Max(fastest[i], kept[i].Physics?.LinearVelocity.Length() ?? 0);
                }
                yield return null;
            }
            var fell = Enumerable.Range(0, kept.Count).Count(i => deepest[i] < lying[i] - SinkM);
            Note("THAW RELEASE RESULT | " + kept.Count + " grids, " + fixedBodies + " fixed, thawed by the freezer off: fell " + fell + " | " +
                 string.Join("; ", Enumerable.Range(0, kept.Count).Select(i => "lay " + lying[i].ToString("F1") + " deepest " +
                     (deepest[i] == double.MaxValue ? "?" : deepest[i].ToString("F1")) + " fastest " + fastest[i].ToString("F1"))));
            Check(fell == 0, fell + " grids fell through the ground after the thaw");
        }

        private static List<MyCubeGrid> Kept() =>
            MyEntities.GetEntities().OfType<MyCubeGrid>()
                .Where(g => !g.Closed && g.DisplayName != null && g.DisplayName.StartsWith(Prefix) && !g.DisplayName.Contains("anchor"))
                .OrderBy(g => g.DisplayName).ToList();

        private static double Over(MyPlanet planet, MyCubeGrid g)
        {
            var centre = planet.PositionComp.GetPosition();
            var p = g.PositionComp.WorldAABB.Center;
            return (p - centre).Length() - (planet.GetClosestSurfacePointGlobal(ref p) - centre).Length();
        }

        private void Player(Vector3D at)
        {
            FakeClients.Add(1, Network, p => (at, 0, 0), withCharacters: true);
            var steamId = FakeClients.PlayerOf(0).Id.SteamId;
            _protected.Add(steamId);
            Sandbox.Game.World.MySession.Static.RemoteAdminSettings[steamId] =
                Sandbox.Game.World.AdminSettingsEnum.Invulnerable | Sandbox.Game.World.AdminSettingsEnum.Untargetable;
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _config.Restore();
                foreach (var steamId in _protected) Sandbox.Game.World.MySession.Static?.RemoteAdminSettings.Remove(steamId);
                _protected.Clear();
            }
            finally { base.Cleanup(); }
        }
    }
}
