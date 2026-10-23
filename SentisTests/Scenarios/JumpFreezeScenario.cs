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
    /// A jump drive charging while frozen (SentisOptimisations' compensation, FrozenProduction): three stations far out in
    /// space, an empty large jump drive on each, charging - one from a reactor with uranium, one from batteries, and a
    /// third the same as the first with a player kept by it, the measure of what vanilla charges in that time. The players
    /// go from the first two, the freezer freezes them for <see cref="FrozenSeconds"/> s, the players come back and they
    /// thaw. The charge each drive gained over the whole run is compared with the third's (the reactor's) and with what
    /// its power over the time would give (the batteries'); the uranium the reactor burned with the third's.
    /// </summary>
    public sealed class JumpFreezeScenario : TestScenario
    {
        public const string ScenarioName = "jump_freeze";
        private const string Prefix = "jump-freeze-";
        private const double FrozenSeconds = 60;
        private const int FreezeDistance = 500;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _config = new ConfigOverride();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        private sealed class Station
        {
            public string Name;
            public Vector3D At;
            public MyCubeGrid Grid;
            public MyJumpDrive Drive;
            public Sandbox.ModAPI.Ingame.IMyJumpDrive Jump => Drive;
            public MyReactor Reactor;
            public double Start, Uranium0;
        }

        public override IEnumerator Run()
        {
            var planet = MyEntities.GetEntities().OfType<MyPlanet>().First();
            var far = planet.PositionComp.GetPosition() + Vector3D.Normalize(new Vector3D(0.3, 0.9, -0.2)) * (planet.MaximumRadius + 60000);
            var stations = new List<Station>
            {
                Make("reactor, frozen", far, reactor: true),
                Make("batteries, frozen", far + Vector3D.Right * 2000, reactor: false),
                Make("reactor, player by it", far + Vector3D.Right * 4000, reactor: true),
            };
            yield return null;
            Watchers(stations);
            _config.Set("FreezeDistanceDynamic", FreezeDistance);
            _config.Set("FreezeDistanceStatic", FreezeDistance);
            _config.Set("FreezerEnabled", true);
            _config.Set("EnableCompensationLogs", true);
            _config.Set("EnableDebugLogs", true);
            var warm = WaitForSeconds(10, "the drives charging with players by them");
            while (warm.MoveNext()) yield return warm.Current;
            foreach (var s in stations)
            {
                s.Start = s.Jump.CurrentStoredPower;
                s.Uranium0 = s.Reactor == null ? 0 : (double)s.Reactor.GetInventory().GetItemAmount(Uranium);
                Note(s.Name + ": drawing " + s.Drive.ResourceSink.CurrentInputByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId).ToString("F2") +
                     " MW, stored " + s.Start.ToString("F4") + " of " + s.Jump.MaxStoredPower + " MWh");
            }
            var started = DateTime.UtcNow;

            // the players go from the first two: frozen, held, and the players back
            Watchers(stations.Skip(2).ToList());
            var frozen = Wait(() => stations.Take(2).All(s => FreezerState.IsFrozen(s.Grid)), "the first two frozen", 60);
            while (frozen.MoveNext()) yield return frozen.Current;
            var stored = stations.Take(2).Select(s => s.Jump.CurrentStoredPower).ToList();
            var hold = WaitForSeconds(FrozenSeconds, "frozen");
            while (hold.MoveNext()) yield return hold.Current;
            var stalled = stations.Take(2).Select((s, i) => Math.Abs(s.Jump.CurrentStoredPower - stored[i]) < 1e-6).ToList();
            Watchers(stations);
            var thawed = Wait(() => stations.All(s => !FreezerState.IsFrozen(s.Grid)), "all thawed", 60);
            while (thawed.MoveNext()) yield return thawed.Current;
            var settle = WaitForSeconds(3, "after the thaw");
            while (settle.MoveNext()) yield return settle.Current;
            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            FakeClients.RemoveAll();

            var gained = stations.Select(s => s.Jump.CurrentStoredPower - s.Start).ToList();
            var burned = stations.Select(s => s.Reactor == null ? 0 : s.Uranium0 - (double)s.Reactor.GetInventory().GetItemAmount(Uranium)).ToList();
            for (var i = 0; i < stations.Count; i++)
                Note(stations[i].Name + ": gained " + gained[i].ToString("F4") + " MWh in " + seconds.ToString("F0") + " s" + (i < 2 ? " (no charge while frozen: " + stalled[i] + ")" : "") +
                     (stations[i].Reactor != null ? ", uranium burned " + burned[i].ToString("F5") : ""));
            var vanilla = gained[2];
            Note("JUMP FREEZE RESULT | reactor frozen " + (gained[0] / vanilla).ToString("P0") + " of vanilla's charge, uranium " + (burned[0] / Math.Max(1e-9, burned[2])).ToString("P0") +
                 "; batteries frozen " + gained[1].ToString("F4") + " MWh");
            Check(gained[0] > vanilla * 0.85 && gained[0] < vanilla * 1.15, "the frozen reactor's drive gained " + gained[0].ToString("F4") + " MWh, vanilla " + vanilla.ToString("F4"));
            Check(burned[0] > burned[2] * 0.7 && burned[0] < burned[2] * 1.3, "the frozen reactor burned " + burned[0].ToString("F5") + " uranium, vanilla " + burned[2].ToString("F5"));
            Check(gained[1] > vanilla * 0.85, "the frozen batteries' drive gained " + gained[1].ToString("F4") + " MWh, a reactor's in vanilla " + vanilla.ToString("F4"));
        }

        private static readonly MyDefinitionId Uranium = MyDefinitionId.Parse("MyObjectBuilder_Ingot/Uranium");

        private Station Make(string name, Vector3D at, bool reactor)
        {
            var owner = WorldApi.PlayerIdentityId();
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            void Add(string subtype, int x, int y, int z, Action<MyObjectBuilder_CubeBlock> setup = null)
            {
                var block = WorldApi.MakeBlockOb(subtype);
                block.Min = new SerializableVector3I(x, y, z);
                block.Owner = owner;
                block.BuiltBy = owner;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
                setup?.Invoke(block);
                blocks.Add(block);
            }
            Add("LargeJumpDrive", 0, 0, 0, b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_JumpDrive)b).StoredPower = 0);
            if (reactor) Add("LargeBlockSmallGenerator", 4, 0, 0);
            else
                for (var i = 0; i < 3; i++) Add("LargeBlockBatteryBlock", 4, i, 0);
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name.Replace(' ', '-').Replace(",", ""),
                DisplayName = WorldApi.EntityPrefix + Prefix + name.Replace(' ', '-').Replace(",", ""),
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(at, Vector3.Forward, Vector3.Up),
                PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            });
            Track(grid);
            var station = new Station { Name = name, At = at, Grid = grid, Drive = grid.GetFatBlocks().OfType<MyJumpDrive>().First() };
            ((Sandbox.ModAPI.IMyJumpDrive)station.Drive).CurrentStoredPower = 0;
            ((Sandbox.ModAPI.Ingame.IMyJumpDrive)station.Drive).Recharge = true;
            if (reactor)
            {
                station.Reactor = grid.GetFatBlocks().OfType<MyReactor>().First();
                station.Reactor.GetInventory().AddItems((MyFixedPoint)10, VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(Uranium));
            }
            else WorldApi.ChargeBatteries(grid);
            return station;
        }

        private void Watchers(List<Station> at)
        {
            FakeClients.RemoveAll();
            if (at.Count == 0) return;
            FakeClients.Add(at.Count, Network, i => (at[i].At + Vector3D.Up * 15, 0, 0), withCharacters: true);
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
