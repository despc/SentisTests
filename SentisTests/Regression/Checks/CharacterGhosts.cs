using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SentisTests.Core;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// SentisOptimisations' guards for a character without an identity (prod 25.11.2026: such a body threw in its weapon
    /// position's update every frame and stopped the entities' update pass) and <c>!ai_npc ghosts</c>
    /// (<c>CharacterGhosts</c>). Three bodies: one with no identity out in the world, one with no identity in a cryo
    /// chamber, one whose identity exists but does not hold it. The free one's weapon position updates without throwing,
    /// and nothing of that update is in the log while it stays in the world. The search finds all three and no one else's;
    /// a plain clean removes the two with no identity (the seated one taken out of its chamber) and keeps the third,
    /// <c>clean all</c> removes it too.
    /// </summary>
    public sealed class CharacterGhostsCheck : RegressionCheck
    {
        public override string Name => Prefix + "character_ghosts";
        public override int TimeoutSeconds => 90;

        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<MyCharacter> _bodies = new List<MyCharacter>();
        private MyIdentity _identity;

        protected override IEnumerator Check()
        {
            var ghosts = Integrations.TypeOf(Integrations.Optimisations, "SentisOptimisations.Commands.CharacterGhosts");
            SkipUnless(ghosts != null, "no CharacterGhosts in SentisOptimisations");
            SkipUnless(Sandbox.Game.Multiplayer.Sync.IsDedicated, "the weapon position's update reads the identity only on a dedicated server");
            var find = ghosts.GetMethod("Find", Any);
            var removes = ghosts.GetMethod("Removes", Any);
            var remove = ghosts.GetMethod("Remove", Any);
            var ghostType = ghosts.GetNestedType("Ghost", Any);
            var kindType = ghosts.GetNestedType("Kind", Any);
            var log = TorchLog();
            var start = new FileInfo(log).Length;

            yield return Arrive(Site.At(0, 4, -8));
            var grid = Build("ghosts-cryo", Site.Origin, true,
                new Part("CryoChamber/LargeBlockCryoChamber", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)));
            var cryo = Block<MyCryoChamber>(grid);
            Check(cryo != null, "no cryo chamber built");

            var free = Spawn("ST ghost free", Site.At(6, 2, 0), 0);
            var seated = Spawn("ST ghost seated", cryo.PositionComp.GetPosition() + cryo.WorldMatrix.Backward * 1.5, 0);
            _identity = MySession.Static.Players.CreateNewIdentity("ST ghost unheld");
            var unheld = Spawn("ST ghost unheld", Site.At(-6, 2, 0), _identity.IdentityId);
            cryo.AttachPilot(seated, -1);
            yield return Until(() => cryo.Pilot == seated ? 1 : 0, v => v > 0, "the body without an identity in the cryo chamber", 10);
            Check(free.GetIdentity() == null && seated.GetIdentity() == null, "a body made with no identity has one");
            Check(unheld.GetIdentity() == _identity && _identity.Character != unheld && _identity.SavedCharacters?.Contains(unheld.EntityId) != true,
                "the third body's identity holds it");

            // 1. the guard: the update of a body with no identity
            Exception thrown = null;
            try { free.WeaponPosition?.Update(true); }
            catch (Exception e) { thrown = e; }
            Check(free.WeaponPosition != null, "the free body has no weapon position");
            Check(thrown == null, "the weapon position's update of a body without an identity threw: " + thrown?.GetType().Name + " " + thrown?.Message);
            yield return Seconds(2); // the bodies in the world for a while, and NLog
            var text = Tail(log, start);
            Check(!text.Contains("MyCharacterWeaponPositionComponent.Update"), "the weapon position's update threw while the body without an identity was in the world");
            Check(text.Split('\n').Any(l => l.Contains("Character without an identity") && l.Contains(free.EntityId.ToString())), "the guard did not write the free body to the log");

            // 2. the search
            List<object> Find() => ((IEnumerable)find.Invoke(null, null)).Cast<object>().ToList();
            object Field(object ghost, string name) => ghostType.GetField(name).GetValue(ghost);
            var found = Find().Where(g => _bodies.Contains((MyCharacter)Field(g, "Character"))).ToList();
            string Of(MyCharacter body) => found.Where(g => Field(g, "Character") == body).Select(g => Field(g, "Kind") + (Field(g, "Seat") == null ? " in the world" : " in a seat")).FirstOrDefault() ?? "not found";
            Note("found: free " + Of(free) + ", seated " + Of(seated) + ", unheld " + Of(unheld));
            Check(Of(free) == "NoIdentity in the world", "the free body: " + Of(free));
            Check(Of(seated) == "NoIdentity in a seat", "the seated body: " + Of(seated));
            Check(Of(unheld) == "NotHeld in the world", "the body its identity does not hold: " + Of(unheld));
            Check(Find().All(g => Field(g, "Character") != Body), "the player's own body is taken for a ghost");

            // 3. a plain clean, then clean all
            void Clean(bool all)
            {
                foreach (var ghost in Find().Where(g => _bodies.Contains((MyCharacter)Field(g, "Character"))))
                    if ((bool)removes.Invoke(null, new[] { Field(ghost, "Kind"), (object)all }))
                    {
                        var failed = (Exception)remove.Invoke(null, new object[] { ghost, null });
                        Check(failed == null, "removing " + ((MyCharacter)Field(ghost, "Character")).DisplayName + " threw: " + failed?.Message);
                    }
            }
            Clean(false);
            yield return Seconds(1);
            Check(free.MarkedForClose && seated.MarkedForClose, "a plain clean left a body without an identity (free " + free.MarkedForClose + ", seated " + seated.MarkedForClose + ")");
            Check(cryo.Pilot == null, "the cryo chamber still holds the removed body");
            Check(!unheld.MarkedForClose, "a plain clean removed the body its identity does not hold");
            Clean(true);
            yield return Seconds(1);
            Check(unheld.MarkedForClose, "clean all left the body its identity does not hold");
            Result("a body without an identity updated its weapon position without throwing; the search found it in the world and in a cryo chamber, " +
                   "and the one its identity does not hold; a plain clean removed the first two, clean all the third");
        }

        private MyCharacter Spawn(string name, Vector3D at, long identityId)
        {
            var body = MyCharacter.CreateCharacter(MatrixD.CreateWorld(at, Vector3D.Forward, Vector3D.Up), Vector3.Zero, name, null, null, null,
                findNearPos: false, identityId: identityId);
            Check(body != null, "the body '" + name + "' was not made");
            _bodies.Add(body);
            Track(body);
            return body;
        }

        public override void Cleanup()
        {
            try
            {
                foreach (var body in _bodies.Where(b => !b.MarkedForClose)) body.Close();
                if (_identity != null) MySession.Static.Players.RemoveIdentity(_identity.IdentityId);
            }
            finally { base.Cleanup(); }
        }

        private static string TorchLog() =>
            Directory.GetFiles(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs"), "Torch-*.log").OrderByDescending(File.GetLastWriteTimeUtc).First();

        private static string Tail(string path, long from)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(Math.Min(from, stream.Length), SeekOrigin.Begin);
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }
    }
}
