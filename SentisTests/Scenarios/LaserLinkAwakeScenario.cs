using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A grid at the far end of a laser link does not freeze while the near end is awake.
    ///
    /// The two grids come from the world - <see cref="NearName"/> and <see cref="FarName"/>, placed so
    /// their dishes reach each other. A player stands beside the near one, which is therefore thawed;
    /// the far one is far away with nobody near it and nobody controlling it, and it
    /// has to stay thawed too, because the link needs both ends running.
    ///
    /// The player then leaves, and both are expected to freeze - otherwise the test would pass on a
    /// freezer that simply never freezes anything.
    /// </summary>
    public sealed class LaserLinkAwakeScenario : TestScenario
    {
        public const string ScenarioName = "laser_link_awake";
        private const string NearName = "Laser_1";
        private const string FarName = "Laser_2";
        private const double SettleSeconds = 10;
        private const double ConnectWaitSeconds = 180;
        private const double FreezeWaitSeconds = 90;
        private const double LogEverySeconds = 20;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly ConfigOverride _config = new ConfigOverride();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            _config.Set("FreezerEnabled", true);
            _config.Set("DelayBeforeFreezeSec", 5);
            _config.Set("FreezeDistanceStatic", 3000);
            _config.Set("FreezeDistanceDynamic", 10000);

            var near = Find(NearName);
            var far = Find(FarName);
            Check(near != null, "there is no grid called " + NearName + " in the world");
            Check(far != null, "there is no grid called " + FarName + " in the world");

            var apart = Vector3D.Distance(near.PositionComp.GetPosition(), far.PositionComp.GetPosition());
            Note(NearName + " and " + FarName + " are " + apart.ToString("F0") + " m apart");

            // Both ends need power and a switched-on dish; the grids come from the world as they are.
            WorldApi.ChargeBatteries(near);
            WorldApi.ChargeBatteries(far);
            var here = WorldApi.FindFunctional<MyLaserAntenna>(near);
            var there = WorldApi.FindFunctional<MyLaserAntenna>(far);
            Check(here != null, NearName + " has no laser antenna");
            Check(there != null, FarName + " has no laser antenna");
            here.Enabled = true;
            there.Enabled = true;

            // A player beside the near grid: that is what keeps this end awake.
            FakeClients.Add(1, Network, p => (near.PositionComp.GetPosition() + new Vector3D(0, 30, 0), 0, 0), withCharacters: true);
            var settle = WaitForSeconds(SettleSeconds, "the player arrives");
            while (settle.MoveNext()) yield return settle.Current;
            Check(FakeClients.Character(0) != null, "the fake player has no character");

            Pair(here, there);
            Pair(there, here);

            var connecting = DateTime.UtcNow;
            var lastLog = connecting;
            while ((DateTime.UtcNow - connecting).TotalSeconds < ConnectWaitSeconds)
            {
                if (Connected(here) && Connected(there)) break;
                if ((DateTime.UtcNow - lastLog).TotalSeconds >= LogEverySeconds)
                {
                    lastLog = DateTime.UtcNow;
                    Note("connecting: " + Describe(here, near) + " | " + Describe(there, far));
                }

                yield return WaitForTicks(30);
            }

            Note("after " + (DateTime.UtcNow - connecting).TotalSeconds.ToString("F0") + " s: " +
                 Describe(here, near) + " | " + Describe(there, far));
            Check(Connected(here) && Connected(there),
                "the laser antennas never connected: " + Describe(here, near) + " | " + Describe(there, far));

            // Long enough that the freezer would have taken the far end if nothing held it.
            var holding = WaitForSeconds(FreezeWaitSeconds, "the freezer settles with the link up");
            while (holding.MoveNext()) yield return holding.Current;

            var nearFrozen = RuntimePluginControls.IsGridFrozen(near.EntityId);
            var farFrozen = RuntimePluginControls.IsGridFrozen(far.EntityId);
            Note("with the player beside " + NearName + ": " + Describe(here, near) + " | " + Describe(there, far));

            Check(!nearFrozen, NearName + " froze although a player is standing beside it");
            Check(!farFrozen,
                FarName + " froze although " + NearName + " holds a laser link to it: the far end of a link must stay awake");

            // ---------------------------------------------------------------- and the other way round
            FakeClients.RemoveAll();
            var leaving = WaitForSeconds(FreezeWaitSeconds, "the player leaves and the freezer catches up");
            while (leaving.MoveNext()) yield return leaving.Current;

            var nearFrozenAfter = RuntimePluginControls.IsGridFrozen(near.EntityId);
            var farFrozenAfter = RuntimePluginControls.IsGridFrozen(far.EntityId);
            Note("LASER LINK RESULT | with a player beside " + NearName + ": " + NearName +
                 (nearFrozen ? " frozen" : " thawed") + ", " + FarName + (farFrozen ? " frozen" : " thawed") +
                 " | after the player left: " + NearName + (nearFrozenAfter ? " frozen" : " thawed") + ", " +
                 FarName + (farFrozenAfter ? " frozen" : " thawed"));

            Check(nearFrozenAfter && farFrozenAfter,
                "nothing froze after the player left, so holding the far end awake proves nothing: " +
                NearName + (nearFrozenAfter ? " frozen" : " thawed") + ", " + FarName + (farFrozenAfter ? " frozen" : " thawed"));
        }

        // ------------------------------------------------------------------ helpers

        private static MyCubeGrid Find(string displayName) =>
            MyEntities.GetEntities().OfType<MyCubeGrid>()
                .FirstOrDefault(g => !g.MarkedForClose &&
                                     string.Equals(g.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));

        private void Pair(MyLaserAntenna from, MyLaserAntenna to)
        {
            try
            {
                var connect = typeof(MyLaserAntenna).GetMethod("ConnectTo",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(long) }, null);
                Check(connect != null, "MyLaserAntenna.ConnectTo(long) is gone");
                connect.Invoke(from, new object[] { to.EntityId });
                ((Sandbox.ModAPI.Ingame.IMyLaserAntenna)from).IsPermanent = true;
            }
            catch (Exception e)
            {
                Note("pairing the laser antennas failed: " + e.Message);
            }
        }

        private static bool Connected(MyLaserAntenna antenna) => antenna.State == MyLaserAntenna.StateEnum.connected;

        private static string Describe(MyLaserAntenna antenna, MyCubeGrid grid) =>
            grid.DisplayName + " " + antenna.State + " (" + Error(antenna) + ", working " + antenna.IsWorking + ", " +
            (RuntimePluginControls.IsGridFrozen(grid.EntityId) ? "frozen" : "thawed") + ")";

        private static string Error(MyLaserAntenna antenna)
        {
            var field = typeof(MyLaserAntenna).GetField("m_connectionError", BindingFlags.Instance | BindingFlags.NonPublic);
            var sync = field?.GetValue(antenna);
            return sync?.GetType().GetProperty("Value")?.GetValue(sync)?.ToString() ?? "unknown";
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
