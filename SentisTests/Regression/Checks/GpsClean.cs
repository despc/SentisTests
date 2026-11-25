using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// SentisOptimisations' <c>!gps clean</c> (25.11.2026): the player is given marks of every kind - his own, one 0.3 m from
    /// it of the same name, an encounter's signal whose encounter (a grid) is gone, a contract's mark of a contract not
    /// active, a mark that followed a grid now gone, a mark of a name asked for - and a made-up identity, gone from the
    /// world, has a mark. Without <c>apply</c> nothing goes; with it the gone identity's marks, the signal, the contract's
    /// mark and the second of the same place go, his own, the linked and the named stay; then <c>linked name ... apply</c>
    /// takes those two, and his own mark still stays.
    /// </summary>
    public sealed class GpsCleanCheck : RegressionCheck
    {
        public override string Name => Prefix + "gps_clean";
        public override int TimeoutSeconds => 60;

        private const long GoneIdentity = 77700000000047351;
        private readonly List<int> _mine = new List<int>();

        private static Dictionary<long, Dictionary<int, MyGps>> All() =>
            typeof(MyGpsCollection).GetField("m_playerGpss", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(MySession.Static.Gpss) as Dictionary<long, Dictionary<int, MyGps>>;

        private static List<string> Command(string text)
        {
            var answers = new List<string>();
            var commands = (Torch.Commands.CommandManager)Torch.TorchBase.Instance.CurrentSession.Managers.GetManager(typeof(Torch.Commands.CommandManager));
            commands?.HandleCommandFromServer(text, message => answers.Add(message.Message));
            return answers;
        }

        private int Add(long identity, string name, Vector3D at, System.Action<MyGps> set = null)
        {
            var gps = new MyGps { Name = name, Coords = at, ShowOnHud = false };
            set?.Invoke(gps);
            gps.UpdateHash();
            Check(MySession.Static.Gpss.AddPlayerGps(identity, ref gps), "the mark '" + name + "' not added");
            if (identity == Identity) _mine.Add(gps.Hash);
            return gps.Hash;
        }

        private bool Has(int hash) => All().TryGetValue(Identity, out var marks) && marks.ContainsKey(hash);

        public override void Cleanup()
        {
            try
            {
                foreach (var hash in _mine) if (Has(hash)) MySession.Static.Gpss.SendDeleteGpsRequest(Identity, hash);
                All()?.Remove(GoneIdentity);
            }
            catch (System.Exception) { }
            finally { base.Cleanup(); }
        }

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -8));
            Check(All() != null, "MyGpsCollection.m_playerGpss not found");
            Check(MySession.Static.Players.TryGetIdentity(GoneIdentity) == null, "the made-up identity exists");
            var grid = Build("gps-target", Site.Origin, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            var goneId = grid.EntityId;
            grid.Close();
            yield return WaitForTicks(3);
            Check(!MyEntities.EntityExists(goneId), "the grid not gone");

            var p = Site.At(0, 50, 0);
            var own = Add(Identity, "ST gps own", p);
            var twin = Add(Identity, "ST gps own", p + new Vector3D(0.3, 0, 0));
            var signal = Add(Identity, "Unidentified Signal", p + Vector3D.Up * 10, g => { g.IsGlobalEncounterGPS = true; g.EntityId = goneId; });
            var contract = Add(Identity, "Acquisition Drop-Off", p + Vector3D.Up * 20, g => g.ContractId = 4242004247351);
            var linked = Add(Identity, "ST gps linked", p + Vector3D.Up * 30, g => g.EntityId = goneId);
            var named = Add(Identity, "ST gps named", p + Vector3D.Up * 40);
            Add(GoneIdentity, "ST gps of nobody", p);
            int Mine() => new[] { own, twin, signal, contract, linked, named }.Count(Has);
            Check(Mine() == 6, Mine() + " of 6 marks given");

            var dry = string.Join(" ", Command("!gps clean"));
            Check(dry.Contains("Будет удалено"), "the dry run answered: " + dry);
            Check(Mine() == 6 && All().ContainsKey(GoneIdentity), "the dry run removed marks: " + Mine() + " of 6 left");

            var applied = string.Join(" ", Command("!gps clean apply"));
            yield return WaitForTicks(2);
            Check(applied.Contains("Удалено"), "apply answered: " + applied);
            Check(!All().ContainsKey(GoneIdentity), "the gone identity's marks stayed");
            Check(!Has(signal), "the signal of a gone encounter stayed");
            Check(!Has(contract), "the mark of a contract not active stayed");
            Check(Has(own) ^ Has(twin), "of the two marks at one place " + (Has(own) && Has(twin) ? "both stayed" : "none stayed"));
            var kept = Has(own) ? own : twin;
            Check(Has(linked), "the linked mark went without linked");
            Check(Has(named), "the named mark went without name");

            var asked = string.Join(" ", Command("!gps clean linked name ST gps named apply"));
            yield return WaitForTicks(2);
            Check(!Has(linked), "the linked mark of a gone grid stayed with linked: " + asked);
            Check(!Has(named), "the named mark stayed with name: " + asked);
            Check(Has(kept), "the player's own mark went");
            Result("dry run: nothing; apply: the gone identity's, the signal, the contract's, the twin gone, own/linked/named kept; " +
                   "linked + name: those two gone, own kept | " + applied);
        }
    }
}
