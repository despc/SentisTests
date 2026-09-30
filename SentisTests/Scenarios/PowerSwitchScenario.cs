using System;
using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A grid's power follows its switches within a moment (SentisOptimisations DistributorUpdate).
    ///
    /// The distributor of a grid recomputes who gets power only when something changed: a source or a sink switched
    /// (its "needs recompute" flag) or a block added or removed (its queues). This puts a battery and a light on a
    /// grid, beside a player, and checks every kind of change: the battery off and on again, the light off and on
    /// again, a second light built onto the grid, and that light ground away. Each has to show in the lights' working
    /// state within <see cref="FollowSeconds"/>.
    /// </summary>
    public sealed class PowerSwitchScenario : TestScenario
    {
        public const string ScenarioName = "power_switch";
        private const string Prefix = "power-";
        private const double FollowSeconds = 1;
        private const double HeightM = 200;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var forward = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var site = anchorM.Translation + up * HeightM;
            var lightSubtype = WorldApi.FindSubtype(MyCubeSize.Large, "light", "1corner");

            var grid = WorldApi.SpawnGrid(WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "grid", MyCubeSize.Large, true, site, new[]
            {
                new BlockSpec("LargeBlockBatteryBlock", new Vector3I(0, 0, 0)),
                new BlockSpec(lightSubtype, new Vector3I(1, 0, 0)),
            }, forward, up));
            Track(grid);
            FakeClients.Add(1, Network, i => (site + up * 5, 0, 0), withCharacters: true);
            yield return WaitForTicks(30);

            WorldApi.ChargeBatteries(grid);
            var battery = Require<Sandbox.ModAPI.IMyBatteryBlock>(WorldApi.FindFunctional<Sandbox.ModAPI.IMyBatteryBlock>(grid), "the battery");
            var light = Require<Sandbox.ModAPI.IMyLightingBlock>(WorldApi.FindFunctional<Sandbox.ModAPI.IMyLightingBlock>(grid), "the light");
            battery.Enabled = true;
            light.Enabled = true;

            var steps = new System.Collections.Generic.List<string>();
            IEnumerator Follow(string what, Func<bool> state)
            {
                var start = MySession.Static.ElapsedPlayTime.TotalSeconds;
                var waiting = Wait(state, what, (int)Math.Ceiling(FollowSeconds + 1));
                while (waiting.MoveNext()) yield return waiting.Current;
                var took = MySession.Static.ElapsedPlayTime.TotalSeconds - start;
                steps.Add(what + " " + took.ToString("F2") + " s");
                Check(took <= FollowSeconds, what + " took " + took.ToString("F2") + " s");
            }

            IEnumerator step;
            step = Follow("the light works", () => light.IsWorking); while (step.MoveNext()) yield return step.Current;

            battery.Enabled = false;
            step = Follow("battery off: the light goes out", () => !light.IsWorking); while (step.MoveNext()) yield return step.Current;
            battery.Enabled = true;
            step = Follow("battery on: the light works", () => light.IsWorking); while (step.MoveNext()) yield return step.Current;

            light.Enabled = false;
            step = Follow("light off", () => !light.IsWorking); while (step.MoveNext()) yield return step.Current;
            light.Enabled = true;
            step = Follow("light on", () => light.IsWorking); while (step.MoveNext()) yield return step.Current;

            var added = WorldApi.AddRealBlock(grid, lightSubtype, new Vector3I(-1, 0, 0));
            var second = Require<Sandbox.ModAPI.IMyLightingBlock>(added as Sandbox.ModAPI.IMyLightingBlock, "the light built on");
            second.Enabled = true;
            step = Follow("a light built on works", () => second.IsWorking); while (step.MoveNext()) yield return step.Current;

            grid.RazeBlock(new Vector3I(-1, 0, 0));
            battery.Enabled = false;
            step = Follow("that light ground away, battery off: the first light goes out", () => !light.IsWorking); while (step.MoveNext()) yield return step.Current;
            battery.Enabled = true;
            step = Follow("battery on again: the light works", () => light.IsWorking); while (step.MoveNext()) yield return step.Current;

            Note("POWER SWITCH RESULT | " + string.Join(" | ", steps));
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
