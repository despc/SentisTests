using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The PCU limit of SentisGameplayImprovements on the running server. Alice's ships and base, an NPC ship,
    /// and Bob, an enemy, near them; the dynamic limit set between a small ship and a big one.
    ///
    /// The limit set here is tiny, so the limiter's own passes over the world are held for the whole run - they
    /// would switch off and make static every real grid on the server - and its check is run here on this
    /// scenario's groups only, one check after another.
    /// <list type="bullet">
    /// <item>the big ship: after the first check its gyroscopes are off and its battery on; on the fifth check
    /// in a row it is static;</item>
    /// <item>the small ship, the NPC ship and a static base with a rotor head (a group with a static grid has
    /// the static limit) are left alone;</item>
    /// <item>a ship that went back within the limit (its gyroscopes taken off, then put back) counts again from
    /// nothing: on the sixth check it is still dynamic.</item>
    /// </list>
    /// </summary>
    internal sealed class PcuLimitScenario : TestScenario
    {
        public const string ScenarioName = "pcu_limit";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;

        private const string Prefix = "pcu-";
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);
        private readonly List<string> _failures = new List<string>();
        private Vector3D _up, _side, _forward;
        private object _limiter;
        private FieldInfo _lastPass, _strikes, _queue;
        private MethodInfo _checkGroup;
        private readonly List<MyCubeGrid> _groups = new List<MyCubeGrid>();

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var plugin = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("SentisGameplayImprovements.SentisGameplayImprovementsPlugin", false)).FirstOrDefault(t => t != null);
            Check(plugin != null, "SentisGameplayImprovements is not loaded");
            _limiter = plugin.GetField("_limiter", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            Check(_limiter != null, "no PCU limiter");
            _lastPass = _limiter.GetType().GetField("_lastPass", BindingFlags.Instance | BindingFlags.NonPublic);
            _strikes = _limiter.GetType().GetField("_strikes", BindingFlags.Instance | BindingFlags.NonPublic);
            _queue = _limiter.GetType().GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic);
            _checkGroup = _limiter.GetType().GetMethod("CheckGroup", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(_lastPass != null && _strikes != null && _queue != null && _checkGroup != null,
                "the limiter has no _lastPass, _strikes, _queue or CheckGroup");

            // hold the limiter's passes over the world before any limit is changed
            HoldPasses();

            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            _up = Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            _side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(_up));
            _forward = Vector3D.Cross(_side, _up);
            Vector3D centre = default;
            for (var d = 100000.0; d <= 1000000; d += 50000)
            {
                centre = anchor + _up * d - _side * 20000;
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(centre).Length() < 0.001f) break;
            }

            FakeClients.RemoveAll();
            FakeClients.Add(2, Network, p => (centre + _up * 60 + _side * (20 * p), 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the players arrive");
            while (arrive.MoveNext()) yield return arrive.Current;
            var alice = FakeClients.Character(0)?.GetPlayerIdentityId() ?? 0;
            var bob = FakeClients.Character(1)?.GetPlayerIdentityId() ?? 0;
            Check(alice != 0 && bob != 0 && alice != bob, "the fake players have no identities of their own");
            var npc = NpcIdentity();

            var big = Ship("big", centre, alice, 4, false);
            var small = Ship("small", centre + _side * 80, alice, 1, false);
            var npcShip = Ship("npc", centre + _side * 160, npc, 4, false);
            var resetter = Ship("resetter", centre + _side * 240, alice, 4, false);
            var baseGrid = Ship("base", centre + _side * 320, alice, 4, true, stator: true);
            _groups.AddRange(new[] { big, small, npcShip, resetter, baseGrid });
            var settle = WaitForSeconds(2, "the grids settle");
            while (settle.MoveNext()) yield return settle.Current;
            var stator = WorldApi.FindFunctional<MyMotorStator>(baseGrid);
            var recreate = typeof(MyMechanicalConnectionBlockBase).GetMethod("DoRecreateTop", BindingFlags.Instance | BindingFlags.NonPublic);
            recreate.Invoke(stator, new object[] { alice, Enum.Parse(recreate.GetParameters()[1].ParameterType, "Normal"), true });
            var topped = Wait(() => stator.TopGrid != null, "the base's rotor head is built", 10);
            while (topped.MoveNext()) yield return topped.Current;
            Track(stator.TopGrid);

            var smallPcu = Pcu(small);
            var bigPcu = Pcu(big);
            var limit = smallPcu + (bigPcu - smallPcu) / 2;
            Check(smallPcu < limit && limit < bigPcu && limit < Pcu(baseGrid), "the PCU do not straddle the limit: small " + smallPcu + ", big " + bigPcu);
            _gameplay.Set("MaxDinamycGridPCU", limit);
            _gameplay.Set("MaxStaticGridPCU", 200000);
            _gameplay.Set("IncludeConnectedGrids", false);
            _gameplay.Set("EnabledPcuLimiter", true);
            Note("dynamic limit " + limit + " PCU: small ship " + smallPcu + ", big ships " + bigPcu + ", base " + (Pcu(baseGrid) + Pcu(stator.TopGrid)) +
                 " with the static limit; Bob is Alice's enemy and near");

            // ------------------------------------------------------------- check 1
            var pass = Pass();
            while (pass.MoveNext()) yield return pass.Current;
            Expect("check 1: the big ship's gyroscopes are off", GyrosOn(big) == 0);
            Expect("check 1: the big ship's battery is on", WorldApi.FindFunctional<MyBatteryBlock>(big).Enabled);
            Expect("check 1: the big ship counts 1", Strikes(big) == 1);
            Expect("check 1: the small ship is left alone", GyrosOn(small) == 1 && Strikes(small) == 0);
            Expect("check 1: the NPC ship is left alone", GyrosOn(npcShip) == 4 && Strikes(npcShip) == 0);
            Expect("check 1: the base with a rotor head has the static limit", GyrosOn(baseGrid) == 4 && Strikes(baseGrid) == 0);
            Expect("check 1: the resetter counts 1", Strikes(resetter) == 1);

            // ------------------------------------------------------------- check 2, then the resetter goes within the limit
            pass = Pass();
            while (pass.MoveNext()) yield return pass.Current;
            Expect("check 2: the resetter counts 2", Strikes(resetter) == 2);
            var gyroCells = resetter.GetFatBlocks().OfType<MyGyro>().Select(g => g.Position).ToList();
            foreach (var cell in gyroCells) resetter.RazeBlock(cell);
            yield return null;
            Expect("the resetter is within the limit without its gyroscopes", Pcu(resetter) <= limit);

            pass = Pass();
            while (pass.MoveNext()) yield return pass.Current;
            Expect("check 3: the resetter counts from nothing", Strikes(resetter) == 0);
            foreach (var cell in gyroCells)
            {
                var gyro = WorldApi.MakeBlockOb("LargeBlockGyro");
                gyro.Min = new SerializableVector3I(cell.X, cell.Y, cell.Z);
                gyro.Owner = alice;
                gyro.BuiltBy = alice;
                ((IMyCubeGrid)resetter).AddBlock(gyro, false);
            }
            yield return null;
            Expect("the resetter is over the limit again", Pcu(resetter) > limit);

            // ------------------------------------------------------------- checks 4 and 5
            pass = Pass();
            while (pass.MoveNext()) yield return pass.Current;
            Expect("check 4: the big ship is still dynamic", !big.IsStatic && Strikes(big) == 4);
            pass = Pass();
            while (pass.MoveNext()) yield return pass.Current;
            var staticAt = 0;
            for (var f = 0; f < 60 && !big.IsStatic; f++, staticAt++) yield return null;
            Expect("check 5: the big ship is static", big.IsStatic);
            Expect("check 5: the big ship's count is gone", Strikes(big) == 0);

            // ------------------------------------------------------------- check 6
            pass = Pass();
            while (pass.MoveNext()) yield return pass.Current;
            Expect("check 6: the resetter is still dynamic, counting 3", !resetter.IsStatic && Strikes(resetter) == 3);
            Expect("check 6: the small ship, the NPC ship and the base are as they were",
                !small.IsStatic && !npcShip.IsStatic && GyrosOn(small) == 1 && GyrosOn(npcShip) == 4 && GyrosOn(baseGrid) == 4);

            Note("PCU LIMIT RESULT: " + (_failures.Count == 0 ? "all as expected" : string.Join("; ", _failures)));
            Check(_failures.Count == 0, string.Join("; ", _failures));
        }

        /// <summary>
        /// Stops the limiter's passes over the world: the pass in progress is dropped and the next one is due
        /// at the end of time. Its slices then find nothing to do.
        /// </summary>
        private void HoldPasses()
        {
            ((IList)_queue.GetValue(_limiter)).Clear();
            _lastPass.SetValue(_limiter, DateTime.MaxValue);
        }

        /// <summary>One check of this scenario's groups - and of nothing else.</summary>
        private IEnumerator Pass()
        {
            foreach (var grid in _groups)
            {
                var group = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid);
                _checkGroup.Invoke(_limiter, new object[] { group });
            }
            yield return null;
        }

        private int Strikes(MyCubeGrid grid)
        {
            var strikes = (Dictionary<long, int>)_strikes.GetValue(_limiter);
            return strikes.TryGetValue(grid.EntityId, out var n) ? n : 0;
        }

        private void Expect(string what, bool ok)
        {
            Note((ok ? "" : "FAIL ") + what);
            if (!ok) _failures.Add(what);
        }

        private static int GyrosOn(MyCubeGrid grid) => grid.GetFatBlocks().OfType<MyGyro>().Count(g => g.Enabled);

        /// <summary>PCU as the limiter counts it: the functional blocks.</summary>
        private static int Pcu(MyCubeGrid grid) =>
            grid.CubeBlocks.Where(b => b.ComponentStack.IsFunctional).Sum(b => b.BlockDefinition.PCU);

        private static long NpcIdentity()
        {
            var players = MySession.Static.Players;
            var identity = players.GetAllIdentities().FirstOrDefault(i => i.DisplayName == "SentisTests PCU NPC")
                           ?? players.CreateNewIdentity("SentisTests PCU NPC");
            players.MarkIdentityAsNPC(identity.IdentityId);
            return identity.IdentityId;
        }

        /// <summary>Armour 3 x 1 x 3, a battery and <paramref name="gyros"/> gyroscopes on top; a stator for the base.</summary>
        private MyCubeGrid Ship(string name, Vector3D at, long owner, int gyros, bool isStatic, bool stator = false)
        {
            MyObjectBuilder_CubeBlock Block(string subtype, int x, int y, int z)
            {
                var block = WorldApi.MakeBlockOb(subtype);
                block.Min = new SerializableVector3I(x, y, z);
                block.Owner = owner;
                block.BuiltBy = owner;
                return block;
            }

            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < 3; x++)
            for (var z = 0; z < 3; z++)
                blocks.Add(Block("LargeBlockArmorBlock", x, 0, z));
            var battery = (MyObjectBuilder_BatteryBlock)Block("LargeBlockBatteryBlock", 0, 1, 0);
            battery.CurrentStoredPower = 3f;
            blocks.Add(battery);
            var cells = new[] { (1, 1, 0), (2, 1, 0), (0, 1, 1), (1, 1, 1), (2, 1, 1) };
            for (var i = 0; i < gyros; i++) blocks.Add(Block("LargeBlockGyro", cells[i].Item1, cells[i].Item2, cells[i].Item3));
            if (stator) blocks.Add(Block("LargeStator", 1, 1, 2));
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = isStatic,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, _forward, _up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            });
            Track(grid);
            return grid;
        }

        public override void Cleanup()
        {
            try
            {
                // the settings back first, then the passes over the world again, the next one in 30 seconds
                _gameplay.Restore();
                if (_limiter != null && _lastPass != null) _lastPass.SetValue(_limiter, DateTime.UtcNow);
                FakeClients.RemoveAll();
            }
            catch (Exception e)
            {
                Log.Warn("cleaning up after the test failed: " + e.Message);
            }
            finally { base.Cleanup(); }
        }
    }
}
