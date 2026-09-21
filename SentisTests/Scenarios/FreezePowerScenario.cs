using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using SpaceEngineers.Game.Entities.Blocks;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What a frozen grid does to its power: stored charge, uranium and engine fuel.
    ///
    /// Power itself is instantaneous - a frozen grid produces none and consumes none - so the only
    /// thing a freeze can take or give is what is kept: a battery's charge, the uranium in a
    /// reactor, the hydrogen in an engine. Each site therefore carries three grids that only do one
    /// thing each: solar panels charging a battery, a reactor charging one, and a hydrogen engine
    /// charging one. A player stands at the near site; the far one freezes and is thawed at the end,
    /// and the two are compared over the whole run.
    ///
    /// The sites sit in space on the sunlit side of the planet, because a solar panel in the dark
    /// would measure the night rather than the freezer.
    /// </summary>
    public sealed class FreezePowerScenario : TestScenario
    {
        public const string ScenarioName = "freeze_power";
        private const string Prefix = "frzpow-";
        private const double AboveSurfaceM = 30000;
        private const double ApartM = 1000;
        private const double SiteSpacingM = 150;
        private const int FreezeDistanceM = 300;
        private const double SettleSeconds = 20;
        private const double StartupSeconds = 25;
        private const double FreezeWaitSeconds = 180;
        private const double RunSeconds = 180;
        private const double ThawSeconds = 40;
        private const double LogEverySeconds = 30;
        private const int SolarPanels = 6;
        private const int UraniumKg = 100;
        private const int HydrogenTanks = 4;
        private const int StarterX = 3 + HydrogenTanks * 4;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private static readonly MyDefinitionId Uranium =
            new MyDefinitionId(typeof(MyObjectBuilder_Ingot), "Uranium");

        private readonly ConfigOverride _config = new ConfigOverride();
        private Site _near;
        private Site _far;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => (int)(RunSeconds + FreezeWaitSeconds + 500);

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            // Nothing freezes while the sites are being built and started: out here there is no
            // stepped physics world, and the freezer takes such a group on the spot, delay or not.
            _config.Set("FreezerEnabled", false);
            _config.Set("DelayBeforeFreezeSec", 5);
            _config.Set("FreezeDistanceStatic", FreezeDistanceM);
            _config.Set("FreezeDistanceDynamic", FreezeDistanceM);

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");

            // Straight out from the planet towards the sun: full light, nothing in the way.
            var sun = Vector3D.Normalize(MySector.DirectionToSunNormalized);
            var centre = planet.PositionComp.GetPosition();
            var at = centre + sun * (planet.MaximumRadius + AboveSurfaceM);
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(sun));

            _near = BuildSite("near", at, sun, side);
            _far = BuildSite("far", at + side * ApartM, sun, side);
            var settle = WaitForSeconds(SettleSeconds, "the sites settle");
            while (settle.MoveNext()) yield return settle.Current;

            foreach (var site in new[] { _near, _far })
            {
                Check(site.SolarBattery != null, site.Label + ": no battery on the solar grid");
                Check(site.ReactorBattery != null, site.Label + ": no battery on the reactor grid");
                Check(site.EngineBattery != null, site.Label + ": no battery on the engine grid");
                Check(site.Reactor != null, site.Label + ": no reactor");
                Check(site.Engine != null, site.Label + ": no hydrogen engine");
                site.Prepare();
            }

            FakeClients.Add(1, Network, p => (at + sun * 30, 0, 0), withCharacters: true);
            var starting = WaitUntil(null, StartupSeconds, "the engines pick up");
            while (starting.MoveNext()) yield return starting.Current;

            // The engine can only fill itself from a tank that has power; once it is fuelled it
            // carries the grid on its own, so the battery that started it off is switched out.
            foreach (var site in new[] { _near, _far }) site.StopStarter();
            var running = WaitUntil(null, StartupSeconds, "the starter batteries are out");
            while (running.MoveNext()) yield return running.Current;

            Note("near " + _near.Describe() + " | far " + _far.Describe());
            var start = new[] { _near.Read(), _far.Read() };

            _config.Set("FreezerEnabled", true);
            var freezing = WaitUntil(() => _far.Frozen, FreezeWaitSeconds, "the far site freezes");
            while (freezing.MoveNext()) yield return freezing.Current;

            Check(!_near.Frozen, "the near site froze although a player is standing at it");
            Check(_far.Frozen, "the far site did not freeze, so there is nothing to compare against");

            // A battery books its charge every 100 frames, and the booking due at the moment of the
            // freeze can still land a frame or two after it - about a second and a half of the
            // reactor's output, which came out as a site "still running" whenever the freeze
            // happened to fall that way. The frozen window starts once that one is booked.
            var booked = WaitUntil(null, 3, "the last booking before the freeze lands");
            while (booked.MoveNext()) yield return booked.Current;

            var before = new[] { _near.Read(), _far.Read() };
            Note("far site frozen: " + _far.FreezeState());
            var measuring = WaitUntil(null, RunSeconds, "measuring");
            while (measuring.MoveNext()) yield return measuring.Current;
            var frozenWindow = new[] { _near.Read().Minus(before[0]), _far.Read().Minus(before[1]) };
            var stillFrozen = _far.Frozen;

            // ------------------------------------------------------------- let it thaw
            _config.Set("FreezerEnabled", false);
            var thawing = WaitUntil(null, ThawSeconds, "the far site thaws");
            while (thawing.MoveNext()) yield return thawing.Current;

            var nearTotal = _near.Read().Minus(start[0]);
            var farTotal = _far.Read().Minus(start[1]);
            Note("near " + _near.Describe() + " | far " + _far.Describe());
            Note("FREEZE POWER RESULT | over the frozen window (" + RunSeconds.ToString("F0") + " s) running: " +
                 frozenWindow[0] + " | frozen: " + frozenWindow[1] +
                 " | over everything, running: " + nearTotal + " | frozen then thawed: " + farTotal +
                 " | far site frozen the whole window: " + stillFrozen);

            Check(stillFrozen, "the far site thawed early, so the frozen window measured nothing");
            Check(nearTotal.ReactorCharge > 0.001,
                "the running site charged nothing from its reactor, so the bench says nothing: " + nearTotal);

            // Nothing happens while frozen - power is made and spent in the same instant, so a
            // frozen grid neither produces nor consumes.
            Check(frozenWindow[1].ReactorCharge <= 0.001 && frozenWindow[1].Uranium <= 0.01,
                "the frozen site was still running: " + frozenWindow[1]);

            // The charge comes back on the thaw (vanilla works a battery out from a timestamp) and
            // the fuel for it has to come back with it, or a base left out in the cold charges free.
            Check(farTotal.ReactorCharge >= nearTotal.ReactorCharge * 0.8,
                "the frozen site ended up with less charge than the running one: " +
                farTotal.ReactorCharge.ToString("F3") + " against " + nearTotal.ReactorCharge.ToString("F3") + " MWh");
            Check(farTotal.Uranium >= nearTotal.Uranium * 0.5,
                "the frozen site charged its batteries on far less uranium than the running one: " +
                farTotal.Uranium.ToString("F2") + " kg against " + nearTotal.Uranium.ToString("F2") + " kg");
            Check(farTotal.Uranium <= nearTotal.Uranium * 1.5,
                "the frozen site burned more uranium than the running one: " +
                farTotal.Uranium.ToString("F2") + " kg against " + nearTotal.Uranium.ToString("F2") + " kg");

            // The same for the engine, when its tanks lasted long enough to say anything.
            if (nearTotal.Hydrogen > 0.01)
            {
                Check(farTotal.Hydrogen >= nearTotal.Hydrogen * 0.5,
                    "the frozen site charged its batteries on far less hydrogen than the running one: " +
                    (farTotal.Hydrogen * 100).ToString("F2") + "% against " + (nearTotal.Hydrogen * 100).ToString("F2") + "%");
                Check(farTotal.Hydrogen <= nearTotal.Hydrogen * 1.5,
                    "the frozen site burned more hydrogen than the running one: " +
                    (farTotal.Hydrogen * 100).ToString("F2") + "% against " + (nearTotal.Hydrogen * 100).ToString("F2") + "%");
            }
            else
            {
                Note("the engine burned no measurable hydrogen at either site, so that half says nothing");
            }
        }

        // ------------------------------------------------------------------ readings

        private sealed class Reading
        {
            public double SolarCharge;    // MWh stored
            public double ReactorCharge;
            public double EngineCharge;
            public double Uranium;        // kg burned
            public double Hydrogen;       // tank fill, 0..1

            public Reading Minus(Reading start) => new Reading
            {
                SolarCharge = SolarCharge - start.SolarCharge,
                ReactorCharge = ReactorCharge - start.ReactorCharge,
                EngineCharge = EngineCharge - start.EngineCharge,
                Uranium = start.Uranium - Uranium,
                Hydrogen = start.Hydrogen - Hydrogen,
            };

            public override string ToString() =>
                "battery on solar " + SolarCharge.ToString("F3") + " MWh, on reactor " + ReactorCharge.ToString("F3") +
                " MWh, on engine " + EngineCharge.ToString("F3") + " MWh, uranium burned " + Uranium.ToString("F2") +
                " kg, hydrogen burned " + (Hydrogen * 100).ToString("F2") + "%";
        }

        private sealed class Site
        {
            public string Label;
            public MyCubeGrid SolarGrid;
            public MyCubeGrid ReactorGrid;
            public MyCubeGrid EngineGrid;
            public MyBatteryBlock SolarBattery;
            public MyBatteryBlock ReactorBattery;
            public MyBatteryBlock EngineBattery;
            public MyBatteryBlock Starter;
            public MyReactor Reactor;
            public MyHydrogenEngine Engine;
            public MyGasTank HydrogenTank;
            public List<MyGasTank> HydrogenTanks = new List<MyGasTank>();
            public List<MySolarPanel> Panels = new List<MySolarPanel>();

            public string FreezeState() =>
                string.Join(", ", new[] { SolarGrid, ReactorGrid, EngineGrid }.Select(g =>
                    g.DisplayName.Substring(g.DisplayName.LastIndexOf('-') + 1) +
                    (g.IsStatic ? " static" : " dynamic") +
                    (RuntimePluginControls.IsGridFrozen(g.EntityId) ? " frozen" : " running") +
                    (RuntimePluginControls.IsGridPhysicsFrozen(g.EntityId) ? " physics-frozen" : "") +
                    (g.Physics?.RigidBody != null && g.Physics.RigidBody.IsFixed ? " fixed" : " not fixed") +
                    ", updates " + g.NeedsUpdate)) +
                " | batteries " + string.Join(", ", new[] { SolarBattery, ReactorBattery, EngineBattery }.Select(b =>
                    b.CurrentStoredPower.ToString("F4") + " MWh " + (b.IsWorking ? "working" : "off") + " " + b.NeedsUpdate));

            public bool Frozen =>
                RuntimePluginControls.IsGridFrozen(SolarGrid.EntityId) &&
                RuntimePluginControls.IsGridFrozen(ReactorGrid.EntityId) &&
                RuntimePluginControls.IsGridFrozen(EngineGrid.EntityId);

            /// <summary>Empty batteries set to charge: everything the grid makes goes into them.</summary>
            public void Prepare()
            {
                foreach (var battery in new[] { SolarBattery, ReactorBattery, EngineBattery })
                {
                    battery.Enabled = true;
                    battery.CurrentStoredPower = 0;
                    ((Sandbox.ModAPI.Ingame.IMyBatteryBlock)battery).ChargeMode =
                        Sandbox.ModAPI.Ingame.ChargeMode.Recharge;
                }

                // An engine only makes power once it holds fuel, and it can only draw fuel from a
                // tank that has power - which the grid does not have until the engine runs. Its own
                // fuel buffer is filled here to break that circle.
                if (Engine != null)
                {
                    var definition = Engine.BlockDefinition as Sandbox.Definitions.MyGasFueledPowerProducerDefinition;
                    if (definition != null) Engine.Capacity = definition.FuelCapacity;
                }

                if (Starter != null)
                {
                    Starter.Enabled = true;
                    Starter.CurrentStoredPower = Starter.MaxStoredPower;
                    ((Sandbox.ModAPI.Ingame.IMyBatteryBlock)Starter).ChargeMode =
                        Sandbox.ModAPI.Ingame.ChargeMode.Discharge;
                }
            }

            public string Describe() =>
                Label + ": panels working " + Panels.Count(p => p.IsWorking) + "/" + Panels.Count +
                " making " + Panels.Sum(p => Output(p)).ToString("F3") + " MW, reactor " +
                (Reactor.IsWorking ? "working" : "idle") + " making " + Output(Reactor).ToString("F3") +
                " MW, engine " + (Engine.IsWorking ? "working" : "idle") + " making " + Output(Engine).ToString("F3") +
                " MW, its tank " + (HydrogenTank == null ? "missing" :
                    (HydrogenTank.IsWorking ? "working" : "idle") + " at " + (HydrogenTank.FilledRatio * 100).ToString("F0") + "%") +
                ", frozen: " + (Frozen ? "yes" : "no");

            /// <summary>Takes the starter battery out: from here the engine stands on its own.</summary>
            public void StopStarter()
            {
                if (Starter == null) return;
                Starter.Enabled = false;
            }

            public Reading Read() => new Reading
            {
                SolarCharge = SolarBattery.CurrentStoredPower,
                ReactorCharge = ReactorBattery.CurrentStoredPower,
                EngineCharge = EngineBattery.CurrentStoredPower,
                Uranium = (double)(Reactor.GetInventory(0) as Sandbox.Game.MyInventory).GetItemAmount(Uranium),
                Hydrogen = HydrogenTanks.Count == 0 ? 0 : HydrogenTanks.Sum(t => t.FilledRatio) / HydrogenTanks.Count,
            };
        }

        /// <summary>The battery whose block starts at that x on the grid.</summary>
        private static MyBatteryBlock Battery(MyCubeGrid grid, int x) =>
            WorldApi.FindFunctionals<MyBatteryBlock>(grid).FirstOrDefault(b => b.Min.X == x);

        private static float Output(MyFunctionalBlock block) =>
            block is Sandbox.ModAPI.Ingame.IMyPowerProducer producer ? producer.CurrentOutput : 0;

        // ------------------------------------------------------------------ waiting

        private IEnumerator WaitUntil(Func<bool> until, double seconds, string what)
        {
            var started = DateTime.UtcNow;
            double lastFull = -LogEverySeconds;
            double lastShort = -5;
            while ((DateTime.UtcNow - started).TotalSeconds < seconds)
            {
                if (until != null && until()) yield break;
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                if (elapsed - lastFull >= LogEverySeconds)
                {
                    lastFull = elapsed;
                    Note(what + " " + elapsed.ToString("F0") + "/" + seconds.ToString("F0") + "s | near " +
                         _near.Read() + " | far " + _far.Read());
                }
                else if (elapsed - lastShort >= 5)
                {
                    lastShort = elapsed;
                    Note(what + " (" + elapsed.ToString("F0") + "/" + seconds.ToString("F0") + "s)");
                }

                for (var i = 0; i < 30; i++) yield return null;
            }
        }

        // ------------------------------------------------------------------ building

        private Site BuildSite(string label, Vector3D at, Vector3D up, Vector3D side)
        {
            // A panel collects along its own -Z, so the grid is turned to put that at the sun; the
            // panels are four blocks wide and sit side by side, with the battery under the first.
            var solar = BuildGrid(label + "-solar", at, side, -up, add =>
            {
                for (var i = 0; i < SolarPanels; i++)
                    add(WorldApi.MakeBlockOb("LargeBlockSolarPanel"), new Vector3I(i * 4, 0, 0));
                add(WorldApi.MakeBlockOb("LargeBlockBatteryBlock"), new Vector3I(0, -1, 0));
            });

            var reactor = BuildGrid(label + "-reactor", at + side * SiteSpacingM, up, side, add =>
            {
                add(WorldApi.MakeBlockOb("LargeBlockSmallGenerator"), new Vector3I(0, 0, 0));
                add(WorldApi.MakeBlockOb("LargeBlockBatteryBlock"), new Vector3I(1, 0, 0));
            });

            // The tank needs power before it can push hydrogen at the engine, and the engine needs
            // hydrogen before it can make any: a charged battery at x = 7 gets that started, and the
            // empty one at x = 8 is what the engine's output is measured in.
            var engine = BuildGrid(label + "-engine", at + side * (SiteSpacingM * 2), up, side, add =>
            {
                add(WorldApi.MakeBlockOb("LargeHydrogenEngine"), new Vector3I(0, 0, 0));
                // Four tanks: at five megawatts one of them is gone before the measuring even
                // starts, and an engine that ran dry measures nothing.
                for (var i = 0; i < HydrogenTanks; i++)
                {
                    add(WorldApi.MakeBlockOb("LargeBlockConveyor"), new Vector3I(3 + i * 4, 0, 0));
                    add(WorldApi.MakeBlockOb("LargeHydrogenTank"), new Vector3I(4 + i * 4, 0, 0));
                }

                add(WorldApi.MakeBlockOb("LargeBlockBatteryBlock"), new Vector3I(StarterX, 0, 0));
                add(WorldApi.MakeBlockOb("LargeBlockBatteryBlock"), new Vector3I(StarterX + 1, 0, 0));
            });

            var site = new Site
            {
                Label = label,
                SolarGrid = solar,
                ReactorGrid = reactor,
                EngineGrid = engine,
                SolarBattery = WorldApi.FindFunctional<MyBatteryBlock>(solar),
                ReactorBattery = WorldApi.FindFunctional<MyBatteryBlock>(reactor),
                EngineBattery = Battery(engine, StarterX + 1),
                Starter = Battery(engine, StarterX),
                Reactor = WorldApi.FindFunctional<MyReactor>(reactor),
                Engine = WorldApi.FindFunctional<MyHydrogenEngine>(engine),
                HydrogenTank = WorldApi.FindFunctional<MyGasTank>(engine),
                Panels = WorldApi.FindFunctionals<MySolarPanel>(solar),
            };

            if (site.Reactor != null)
            {
                var inventory = site.Reactor.GetInventory(0) as Sandbox.Game.MyInventory;
                inventory?.AddItems((MyFixedPoint)UraniumKg, new MyObjectBuilder_Ingot { SubtypeName = "Uranium" });
            }

            foreach (var tank in WorldApi.FindFunctionals<MyGasTank>(engine))
                ((Sandbox.ModAPI.IMyGasTank)tank).ChangeFilledRatio(1, true);
            site.HydrogenTanks = WorldApi.FindFunctionals<MyGasTank>(engine);

            return site;
        }

        private MyCubeGrid BuildGrid(string name, Vector3D at, Vector3D up, Vector3D side,
            Action<Action<MyObjectBuilder_CubeBlock, Vector3I>> layout)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            var owner = WorldApi.PlayerIdentityId();
            layout((block, min) =>
            {
                block.Min = new SerializableVector3I(min.X, min.Y, min.Z);
                block.Owner = owner;
                block.BuiltBy = owner;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
                blocks.Add(block);
            });

            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, side, up)),
                PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            };
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
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
