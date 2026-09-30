using System;
using System.Collections;
using System.Reflection;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.GUI.TextPanel;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What a dedicated server does for a text panel still happens (SentisOptimisations ServerGridRender: the panel's
    /// drawing is skipped on the server).
    ///
    /// Two things of a panel's screen update matter on the server: text written through the mod API is applied to the
    /// panel's synced text, and the sprites a programmable block draws are queued and sent to the clients. This writes
    /// text to an LCD and draws a frame of sprites on it the way a programmable block does, and checks that the text
    /// arrives and the sprites go out within <see cref="FollowSeconds"/>. The screen is a programmable block's own: an LCD
    /// has one surface and no MyMultiTextPanelComponent, which is what the plugin changes.
    /// </summary>
    public sealed class PanelServerScenario : TestScenario
    {
        public const string ScenarioName = "panel_server";
        private const string Prefix = "panel-";
        private const double FollowSeconds = 1;
        private const double HeightM = 200;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;

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

            var grid = WorldApi.SpawnGrid(WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "grid", MyCubeSize.Large, true, site, new[]
            {
                new BlockSpec("LargeBlockBatteryBlock", new Vector3I(0, 0, 0)),
                new BlockSpec("LargeProgrammableBlock", new Vector3I(1, 0, 0)),
            }, forward, up));
            Track(grid);
            FakeClients.Add(1, Network, i => (site + up * 5, 0, 0), withCharacters: true);
            yield return WaitForTicks(30);
            WorldApi.ChargeBatteries(grid);

            // a block with several screens (MyMultiTextPanelComponent): a programmable block's own
            var pb = Require<Sandbox.ModAPI.IMyProgrammableBlock>(WorldApi.FindFunctional<Sandbox.ModAPI.IMyProgrammableBlock>(grid), "the programmable block");
            pb.Enabled = true;
            var lcd = Require<Sandbox.ModAPI.Ingame.IMyTextSurface>((pb as Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider)?.GetSurface(0), "the programmable block's screen");
            object component = lcd;
            var textProp = component.GetType().GetProperty("Text", Any);
            var lastSprites = component.GetType().GetField("m_lastSpriteQueue", Any);
            Check(textProp != null && lastSprites != null, "the text panel component changed: no Text or m_lastSpriteQueue");

            // text through the mod API
            lcd.ContentType = ContentType.TEXT_AND_IMAGE;
            lcd.WriteText("panel_server " + DateTime.UtcNow.Ticks);
            var expected = lcd.GetText();
            var started = DateTime.UtcNow;
            var text = Wait(() => (textProp.GetValue(component)?.ToString() ?? "") == expected, "the written text is applied", 3);
            while (text.MoveNext()) yield return text.Current;
            var textTook = (DateTime.UtcNow - started).TotalSeconds;

            // sprites the way a programmable block draws them: a script screen with no script selected
            lcd.ContentType = ContentType.SCRIPT;
            lcd.Script = "";
            yield return WaitForTicks(15);
            using (var frame = lcd.DrawFrame())
                frame.Add(MySprite.CreateText("hello", "Debug", Color.White, 1f, TextAlignment.CENTER));
            started = DateTime.UtcNow;
            var sprites = Wait(() => SpriteCount(lastSprites.GetValue(component)) > 0, "the drawn sprites are sent", 3);
            while (sprites.MoveNext()) yield return sprites.Current;
            var spritesTook = (DateTime.UtcNow - started).TotalSeconds;

            Check(textTook <= FollowSeconds, "the text took " + textTook.ToString("F2") + " s");
            Check(spritesTook <= FollowSeconds, "the sprites took " + spritesTook.ToString("F2") + " s");
            Note("PANEL SERVER RESULT | text applied after " + textTook.ToString("F2") + " s | sprites sent after " + spritesTook.ToString("F2") + " s");
        }

        private static int SpriteCount(object collection)
        {
            var sprites = collection?.GetType().GetField("Sprites")?.GetValue(collection) as Array;
            return sprites?.Length ?? 0;
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
