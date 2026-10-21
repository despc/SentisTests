using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage.Game;
using VRage.ModAPI;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;

namespace SentisTests.Regression.Checks
{
    /// <summary>A door, opened and closed from its terminal, opens and closes.</summary>
    public sealed class DoorCheck : RegressionCheck
    {
        public override string Name => Prefix + "door";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            var grid = Build("door", Site.Origin, true,
                new Part("AirtightSlideDoor/LargeBlockSlideDoor", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var door = (Sandbox.ModAPI.Ingame.IMyDoor)Block<MyDoorBase>(grid);
            ((Sandbox.ModAPI.IMyTerminalBlock)door).SetValueBool("Open", true);
            yield return Until(() => door.Status == Sandbox.ModAPI.Ingame.DoorStatus.Open ? 1 : 0, v => v > 0, "the door open", 10, () => "status " + door.Status);
            ((Sandbox.ModAPI.IMyTerminalBlock)door).SetValueBool("Open", false);
            yield return Until(() => door.Status == Sandbox.ModAPI.Ingame.DoorStatus.Closed ? 1 : 0, v => v > 0, "the door closed", 10, () => "status " + door.Status);
            Result("door opened and closed");
        }
    }

    /// <summary>A timer block with a light's "switch off" on its toolbar, started, switches the light off when its time is up.</summary>
    public sealed class TimerCheck : RegressionCheck
    {
        public override string Name => Prefix + "timer";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            var lightId = VRage.MyEntityIdentifier.AllocateId();
            var grid = Build("timer", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("InteriorLight/SmallLight", 1, 0, 0).With(b => b.EntityId = lightId),
                new Part("TimerBlock/TimerBlockLarge", 2, 0, 0).With(b =>
                {
                    var timer = (MyObjectBuilder_TimerBlock)b;
                    timer.Delay = 1000;
                    timer.Toolbar = new MyObjectBuilder_Toolbar
                    {
                        ToolbarType = MyToolbarType.ButtonPanel,
                        Slots = new List<MyObjectBuilder_Toolbar.Slot>
                        {
                            new MyObjectBuilder_Toolbar.Slot
                            {
                                Index = 0,
                                Item = "",
                                Data = new MyObjectBuilder_ToolbarItemTerminalBlock { BlockEntityId = lightId, _Action = "OnOff_Off" },
                            },
                        },
                    };
                }));
            var light = Block<MyLightingBlock>(grid);
            var timer = Block<SpaceEngineers.Game.Entities.Blocks.MyTimerBlock>(grid);
            Check(light.EntityId == lightId, "the light did not keep the id its builder gave it");
            yield return Until(() => light.IsWorking ? 1 : 0, v => v > 0, "the light working", 10);
            ((Sandbox.ModAPI.IMyTerminalBlock)timer).GetActionWithName("Start").Apply(timer);
            yield return Until(() => light.Enabled ? 0 : 1, v => v > 0, "the light switched off by the timer", 30,
                () => "counting down " + ((SpaceEngineers.Game.ModAPI.Ingame.IMyTimerBlock)timer).IsCountingDown);
            Result("timer ran out and switched the light off");
        }
    }

    /// <summary>A sensor block sees the player in its field and not out of it.</summary>
    public sealed class SensorCheck : RegressionCheck
    {
        public override string Name => Prefix + "sensor";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 60, 0));
            var grid = Build("sensor", Site.Origin, true,
                new Part("SensorBlock/LargeBlockSensor", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var sensor = (Sandbox.ModAPI.Ingame.IMySensorBlock)Block<MySensorBlock>(grid);
            ((Sandbox.ModAPI.IMyTerminalBlock)sensor).SetValueBool("Detect Players", true);
            yield return Seconds(2);
            Check(!sensor.IsActive, "the sensor sees something with the player 60 m off");
            StandAt(Site.At(0, 3, 0), Site.Origin);
            yield return Until(() => sensor.IsActive ? 1 : 0, v => v > 0, "the sensor seeing the player 3 m off", 10, () => State(Block<MySensorBlock>(grid)));
            StandAt(Site.At(0, 60, 0), Site.Origin);
            yield return Until(() => sensor.IsActive ? 0 : 1, v => v > 0, "the sensor clear once the player is gone", 10);
            Result("the sensor saw the player in its field and let go once they left");
        }
    }

    /// <summary>
    /// An air vent in the floor of a closed room of armor, an oxygen tank under it on its conveyor: the vent fills the
    /// room with air from the tank.
    /// </summary>
    public sealed class AirVentCheck : RegressionCheck
    {
        public override string Name => Prefix + "air_vent";

        protected override IEnumerator Check()
        {
            var settings = MySession.Static.Settings;
            SkipUnless(settings.EnableOxygen && settings.EnableOxygenPressurization, "oxygen pressurization is off in this world");
            yield return Arrive(Site.At(0, 6, -8));
            // the room: the cell (0,1,0) in a box of armor -1..1 x 0..2 x -1..1, the vent in the middle of the floor facing up
            var vent = new Part("AirVent/", 0, 0, 0).Facing(Direction.Up, Direction.Backward);
            Part tank = null;
            yield return ConveyorNeighbour(vent, "OxygenTank/", p => Cells(p).All(c => c.Y < 0 || Math.Abs(c.X) > 1 || Math.Abs(c.Z) > 1), p => tank = p);
            var tankCells = Cells(tank);
            var parts = new List<Part> { vent, tank.With(b => ((MyObjectBuilder_GasTank)b).FilledRatio = 1f) };
            for (var x = -1; x <= 1; x++)
            for (var y = 0; y <= 2; y++)
            for (var z = -1; z <= 1; z++)
            {
                var cell = new Vector3I(x, y, z);
                if (cell == Vector3I.Zero || cell == new Vector3I(0, 1, 0) || tankCells.Contains(cell)) continue;
                parts.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, y, z));
            }
            var battery = new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b));
            if (!tankCells.Contains(battery.Min)) parts.Add(battery);
            else parts.Add(new Part("BatteryBlock/LargeBlockBatteryBlock", -2, 0, 0).With(b => Motion.Charged(b)));
            var grid = Build("air-vent", Site.Origin, true, parts);
            var airVent = (SpaceEngineers.Game.ModAPI.Ingame.IMyAirVent)Block<SpaceEngineers.Game.Entities.Blocks.MyAirVent>(grid);
            var gasTank = (Sandbox.ModAPI.Ingame.IMyGasTank)Block<MyGasTank>(grid);
            airVent.Depressurize = false;
            yield return Until(() => airVent.CanPressurize ? 1 : 0, v => v > 0, "the room airtight for the vent", 10);
            var full = gasTank.FilledRatio;
            yield return Until(() => airVent.GetOxygenLevel(), v => v > 0.9, "the room's air", 30, () => "tank " + gasTank.FilledRatio.ToString("F4") + ", " + State((MyCubeBlock)airVent));
            Check(gasTank.FilledRatio < full, "the room filled and the tank gave nothing");
            Result("room filled with air (" + (airVent.GetOxygenLevel() * 100).ToString("F0") + " %), the tank at " + (gasTank.FilledRatio * 100).ToString("F1") + " % (tank under the vent at " + tank.Min + ")");
        }
    }

    /// <summary>A hurt player at a medical room, using it, is healed.</summary>
    public sealed class MedicalRoomCheck : RegressionCheck
    {
        public override string Name => Prefix + "medical_room";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -6));
            var grid = Build("medical", Site.Origin, true,
                new Part("MedicalRoom/LargeMedicalRoom", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)));
            var room = Block<SpaceEngineers.Game.Entities.Blocks.MyMedicalRoom>(grid);
            yield return Until(() => room.IsWorking ? 1 : 0, v => v > 0, "the medical room working", 10, () => State(room));
            StandAt(room.PositionComp.GetPosition() + Site.Up * 0.2, room.PositionComp.GetPosition() + Site.Forward);
            var health = Body.StatComp.Health;
            health.Value = health.MaxValue * 0.4f;
            var hurt = health.Value;
            var within = Within(15);
            while (health.Value < hurt + 5 && within())
            {
                Request(room, "RequestSupport", Body.EntityId);
                yield return null;
            }
            Check(health.Value >= hurt + 5, "the medical room healed " + F(health.Value - hurt) + " in 15 s");
            Result("healed " + F(hurt) + " -> " + F(health.Value) + " of " + F(health.MaxValue));
        }
    }
}
