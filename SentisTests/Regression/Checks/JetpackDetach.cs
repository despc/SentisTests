using System;
using System.Collections;
using System.Collections.Generic;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A player flies on the jetpack in a cluster detached by hand (SentisClusters' <c>!clusters detach</c>, kept so), as an
    /// admin did on the stand and could not fly, and then the cluster attached itself again (25.10.2026):
    /// - forward 3 s and the dampeners stop him, three times: the jetpack's thrust is worked out in the cluster's tick
    ///   (its component, MyEntityComponentUpdater's), not on the game thread beside it;
    /// - then he flies 3 km away, out of the cluster: the cluster detached by hand (known by its grid, a container by
    ///   the player) must still be detached, however the game remade the clusters meanwhile.
    /// Keys are sent as a client's update brings them (MyCharacter.SetNetState). Focus <c>nodetach=1</c>: the same with
    /// nothing detached. Not part of the regression: it detaches a cluster.
    /// </summary>
    public sealed class JetpackDetachCheck : RegressionCheck
    {
        public const string ScenarioName = "jetpack_detach";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 240;

        protected override IEnumerator Check()
        {
            SkipUnless(Integrations.IsLoaded("SentisClusters"), "SentisClusters is not loaded");
            var detach = !System.Text.RegularExpressions.Regex.IsMatch(TestRunner.Focus ?? "", @"nodetach=1");
            var grid = Build("jetpack-detach-box", Site.At(0, 0, -15), true, new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0));
            yield return Arrive(Site.Origin);
            FakeClients.SetFree(Me, true);
            yield return Hold(1, Vector3.Zero, jetpack: true, dampeners: true, face: Site.Forward);
            var body = Body;
            Check(body.JetpackComp?.TurnedOn == true, "the jetpack did not come on");

            var at = body.PositionComp.GetPosition();
            if (detach)
            {
                var answer = string.Join(" ", Command($"!clusters detach {at.X:F0} {at.Y:F0} {at.Z:F0}"));
                Note("detach: " + answer);
                Check(answer.Contains("detached"), "the player's cluster was not detached: " + answer);
            }
            yield return Hold(2, Vector3.Zero, jetpack: true, dampeners: true, face: Site.Forward);

            var report = new List<string>();
            for (var round = 1; round <= 3; round++)
            {
                var from = body.PositionComp.GetPosition();
                yield return Hold(3, new Vector3(0, 0, -1), jetpack: true, dampeners: true, face: Site.Forward);
                var speed = Motion.Speed(body);
                var gone = Vector3D.Dot(body.PositionComp.GetPosition() - from, Site.Forward);
                Note($"round {round}: {F(gone)} m forward at {F(speed)} m/s; {Inside(body)}");
                Check(gone > 3, $"round {round}: 3 s on the jetpack forward took the astronaut {F(gone)} m forward (speed {F(speed)} m/s); {Inside(body)}; {CharacterState(body)}");
                var within = Within(10);
                while (Motion.Speed(body) > 0.3 && within())
                {
                    Keys(Vector3.Zero, jetpack: true, dampeners: true, face: Site.Forward);
                    yield return null;
                }
                Check(Motion.Speed(body) <= 0.3, $"round {round}: the dampeners did not stop the astronaut: {F(Motion.Speed(body))} m/s after 10 s");
                report.Add(F(gone) + " m");
            }

            // away, out of the cluster: the one detached by hand stays detached
            var start = body.PositionComp.GetPosition();
            var flight = Within(60);
            while (Vector3D.Distance(body.PositionComp.GetPosition(), start) < 3000 && flight())
            {
                Keys(new Vector3(0, 0, -1), jetpack: true, dampeners: true, face: Site.Forward);
                yield return null;
            }
            var away = Vector3D.Distance(body.PositionComp.GetPosition(), start);
            yield return Hold(3, Vector3.Zero, jetpack: true, dampeners: true, face: Site.Forward);
            var box = grid.PositionComp.GetPosition();
            var boxCluster = string.Join(" ", Command($"!clusters here {box.X:F0} {box.Y:F0} {box.Z:F0}"));
            var mine = body.PositionComp.GetPosition();
            var myCluster = string.Join(" ", Command($"!clusters here {mine.X:F0} {mine.Y:F0} {mine.Z:F0}"));
            Note($"flew {F(away)} m away; the box's cluster: {boxCluster}; the player's: {myCluster}");
            Check(away > 1000, $"the astronaut flew only {F(away)} m away in 60 s");
            if (detach) Check(boxCluster.Contains("detached by hand"), "the cluster detached by hand is not detached after the player flew away: " + boxCluster);
            Result($"{(detach ? "in a detached cluster" : "nothing detached:")} the astronaut flew {string.Join(", ", report)} in 3 s each and stopped each time; " +
                   $"{F(away)} m away the box's cluster: {boxCluster}");
        }

        /// <summary>Where the keys got to: the move indicator, the jetpack's thrust, the body's velocity.</summary>
        private static string Inside(Sandbox.Game.Entities.Character.MyCharacter c)
        {
            var jet = c.JetpackComp;
            return $"move indicator {c.MoveIndicator}, jetpack on {jet?.TurnedOn}, final thrust {jet?.FinalThrust}, velocity {c.Physics?.LinearVelocity}";
        }

        private static List<string> Command(string text)
        {
            var answers = new List<string>();
            var commands = (Torch.Commands.CommandManager)Torch.TorchBase.Instance.CurrentSession.Managers.GetManager(typeof(Torch.Commands.CommandManager));
            commands?.HandleCommandFromServer(text, message => answers.Add(message.Message));
            return answers;
        }

        public override void Cleanup()
        {
            try { Command("!clusters attach all"); }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
