using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.Contracts;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using SentisTests.Game;
using Sandbox.Definitions;
using Sandbox.Game.AI;
using Sandbox.ModAPI.Interfaces;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using VRageMath;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRage.Library.Utils;

namespace SentisTests.Regression.Checks
{
    /// <summary>A hauling contract of a player's with a deposit, for the checks of how such a contract ends without being done.</summary>
    public abstract class ContractEndCheck : PlayerContractCheck
    {
        protected const int Deposit = 700;
        protected MyContractBlock Start;
        protected long Owner0, Held;

        protected IEnumerator Post(string name)
        {
            yield return Arrive(Site.At(-2, 0, -3));
            yield return Setup();
            Start = ContractBlock(name + "-start", Site.Origin);
            var end = ContractBlock(name + "-end", Site.At(60, 0, 0));
            yield return Seconds(1);
            MyBankingSystem.ChangeBalance(WorkerIdentity, 10000);
            Owner0 = MyBankingSystem.GetBalance(Identity);
            ContractId = Post(Start, "MyContractCreationDataWrapper_Deliver", "CreateCustomContractDeliver", new Dictionary<string, object>
            {
                ["RewardMoney"] = Reward, ["StartingDeposit"] = Deposit, ["DurationInMin"] = 30, ["TargetBlockId"] = end.EntityId,
            });
            Held = Owner0 - MyBankingSystem.GetBalance(Identity);
            Check(Held == Reward, "posting the contract held " + Held + " from the owner, the reward is " + Reward);
        }

        protected long ContractId;

        /// <summary>The worker takes the contract and pays the deposit.</summary>
        protected long Take()
        {
            var worker0 = MyBankingSystem.GetBalance(WorkerIdentity);
            RequestAs(Worker, Start, "AcceptContract", WorkerIdentity, ContractId);
            Check(Contracts.IsContractActive(ContractId), "the contract is not active after the player took it");
            var deposit = worker0 - MyBankingSystem.GetBalance(WorkerIdentity);
            Check(deposit == Deposit, "taking the contract took " + deposit + " from the player, the deposit is " + Deposit);
            return worker0;
        }

        /// <summary>
        /// A failed player's contract: the owner gets the reward back and the player's deposit (the game's <c>MyContract.Fail</c>
        /// pays the deposit to the start block's owner), the player nothing.
        /// </summary>
        protected string Failed(long worker0, string how)
        {
            var owner = MyBankingSystem.GetBalance(Identity) - (Owner0 - Held);
            var worker = MyBankingSystem.GetBalance(WorkerIdentity) - worker0;
            Check(owner == Reward + Deposit, "the owner got " + owner + " after the contract " + how + ", the reward and the deposit are " + (Reward + Deposit));
            Check(worker == -Deposit, "the player is " + worker + " after the contract " + how + ", should have lost the deposit " + Deposit);
            return "the owner got back the reward and the player's deposit (" + owner + "), the player lost the deposit (" + worker + ")";
        }
    }

    /// <summary>A player takes a hauling contract with a deposit and abandons it: the contract is gone, the deposit goes to the owner, the reward back.</summary>
    public sealed class ContractAbandonCheck : ContractEndCheck
    {
        public override string Name => Prefix + "contract_abandon";

        protected override IEnumerator Check()
        {
            yield return Post("abandon");
            var worker0 = Take();
            RequestAs(Worker, Start, "AbandonContract", WorkerIdentity, ContractId);
            Check(!Contracts.IsContractActive(ContractId), "the contract is still active after the player abandoned it");
            Result("the contract abandoned: " + Failed(worker0, "abandoned"));
        }
    }

    /// <summary>
    /// A taken hauling contract with a deposit runs out of time (its time cut to 2 s, as a player's 30 minutes running
    /// out): the game fails it, the deposit goes to the owner, the reward back.
    /// </summary>
    public sealed class ContractExpireCheck : ContractEndCheck
    {
        public override string Name => Prefix + "contract_expire";

        protected override IEnumerator Check()
        {
            yield return Post("expire");
            var worker0 = Take();
            var contract = Active(ContractId);
            typeof(MyContract).GetProperty(nameof(MyContract.RemainingTime), BindingFlags.Instance | BindingFlags.Public)
                .SetValue(contract, (MyTimeSpan?)MyTimeSpan.FromSeconds(2));
            yield return Until(() => Contracts.IsContractActive(ContractId) ? 0 : 1, v => v > 0, "the contract running out of time", 15);
            Result("the contract ran out of time: " + Failed(worker0, "ran out of time"));
        }
    }

    /// <summary>The owner takes down a contract no one has taken: it leaves the block and the reward comes back.</summary>
    public sealed class ContractDeleteCheck : ContractEndCheck
    {
        public override string Name => Prefix + "contract_delete";

        protected override IEnumerator Check()
        {
            yield return Post("delete");
            Request(Start, "DeleteCustomContract", ContractId);
            var count = Contracts.GetAvailableContractsForBlockCount(Start.EntityId);
            Check(count == 0, "the contract is still on the block after the owner took it down (" + count + " there)");
            var back = MyBankingSystem.GetBalance(Identity) - (Owner0 - Held);
            Check(back == Reward, "the owner got " + back + " back of the reward " + Reward);
            Result("the contract taken down: off the block, " + back + " back to the owner");
        }
    }

    /// <summary>
    /// Two players' factions and how they stand, as their founders ask for it: at war to start with, a peace request
    /// sent is pending until the other accepts, then they are neutral; a war declared makes them enemies again. Friendship
    /// between players' factions is not a player's to ask for: the terminal has no button for it, and the server takes
    /// <c>AcceptFriendRequest</c> only from factions that are friends already.
    /// </summary>
    public sealed class FactionRelationsCheck : FactionCheck
    {
        public override string Name => Prefix + "faction_relations";

        private static object Change(string name) =>
            System.Enum.Parse(typeof(MyFactionCollection).GetMethod("FactionStateChangeRequest", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetParameters()[0].ParameterType, name);

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            var other = -1;
            yield return Join(Site.At(4, 4, -6), i => other = i);
            var a = Found(Me);
            var b = Found(other);
            var factions = MySession.Static.Factions;
            string Relation() => factions.GetRelationBetweenFactions(a.FactionId, b.FactionId).Item1.ToString();
            void Ask(int client, string change, long from, long to) =>
                RequestAs(client, typeof(MyFactionCollection), "FactionStateChangeRequest", Change(change), from, to, IdentityOf(client));
            var start = Relation();
            Check(factions.AreFactionsEnemies(a.FactionId, b.FactionId), "two new players' factions are " + start + ", not at war");
            Ask(Me, "SendPeaceRequest", a.FactionId, b.FactionId);
            Check(factions.IsPeaceRequestStateSent(a.FactionId, b.FactionId), "the peace request is not sent");
            Check(factions.AreFactionsEnemies(a.FactionId, b.FactionId), "the factions are " + Relation() + " on a peace request alone");
            Ask(other, "AcceptPeace", b.FactionId, a.FactionId);
            Check(factions.AreFactionsNeutrals(a.FactionId, b.FactionId), "the factions are " + Relation() + " after the peace was accepted, not neutral");
            Ask(other, "DeclareWar", b.FactionId, a.FactionId);
            Check(factions.AreFactionsEnemies(a.FactionId, b.FactionId), "the factions are " + Relation() + " after a war was declared, not enemies");
            yield return null;
            Result(a.Tag + " and " + b.Tag + ": " + start + ", peace requested and accepted (neutral), war declared (enemies)");
        }
    }

    /// <summary>
    /// A faction's account: a member puts money in and the leader takes some out (<c>MyBankingSystem.RequestDeposit_Server</c>,
    /// <c>RequestWithdraw_Server</c>), the money moving one for one; a member who is not a leader cannot take money out.
    /// </summary>
    public sealed class FactionBankCheck : FactionCheck
    {
        public override string Name => Prefix + "faction_bank";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            var member = -1;
            yield return Join(Site.At(4, 4, -6), i => member = i);
            var faction = Found(Me);
            Enter(member, faction, Me);
            var memberId = IdentityOf(member);
            MyBankingSystem.ChangeBalance(memberId, 5000);
            long Bank() => MyBankingSystem.GetBalance(faction.FactionId);
            var bank0 = Bank();
            var member0 = MyBankingSystem.GetBalance(memberId);
            RequestAs(member, typeof(MyBankingSystem), "RequestDeposit_Server", 3000L);
            Check(Bank() - bank0 == 3000 && member0 - MyBankingSystem.GetBalance(memberId) == 3000,
                "the member put 3000 in: the faction got " + (Bank() - bank0) + ", the member gave " + (member0 - MyBankingSystem.GetBalance(memberId)));
            var leader0 = MyBankingSystem.GetBalance(Identity);
            RequestAs(Me, typeof(MyBankingSystem), "RequestWithdraw_Server", 1000L);
            Check(MyBankingSystem.GetBalance(Identity) - leader0 == 1000 && Bank() - bank0 == 2000,
                "the leader took 1000 out: got " + (MyBankingSystem.GetBalance(Identity) - leader0) + ", the faction has " + (Bank() - bank0) + " of 2000 left");
            var member1 = MyBankingSystem.GetBalance(memberId);
            RequestAs(member, typeof(MyBankingSystem), "RequestWithdraw_Server", 500L);
            var took = MyBankingSystem.GetBalance(memberId) - member1;
            Check(took == 0, "a member who is not a leader took " + took + " out of the faction's account");
            yield return null;
            Result("faction " + faction.Tag + ": 3000 put in by a member, 1000 taken out by the leader, a member's withdrawal refused");
        }
    }

    /// <summary>
    /// Damage to an NPC faction's grid costs the player reputation with it: a block of heavy armor on a grid of an NPC
    /// trader's (with a cargo container, so the grid has an owner),
    /// hit by the player for 5 × the reputation settings' damage per point (<c>ReputationLossDamage</c>), takes 5 points
    /// (times the settings' damage step) off. The player's reputation with the faction and with the pirates is set back
    /// after.
    /// </summary>
    public sealed class ReputationDamageCheck : RegressionCheck
    {
        public override string Name => Prefix + "reputation_damage";
        protected override bool NeedsDamage => true;

        private MyFaction _faction;
        private int? _rep, _pirateRep;
        private long _pirates;

        protected override IEnumerator Check()
        {
            var factions = MySession.Static.Factions;
            _faction = factions.Select(f => f.Value).OfType<MyFaction>()
                .FirstOrDefault(f => f.FactionTypeString == "Trader" && !f.StaticReputation && MySession.Static.Players.IdentityIsNpc(f.FounderId));
            SkipUnless(_faction != null, "no NPC trader faction with a reputation that moves in this world");
            yield return Arrive(Site.At(0, 0, -4));
            var settings = typeof(MyFactionCollection).GetField("m_reputationSettings", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(factions);
            var damage = settings.GetType().GetField("DamageSettings").GetValue(settings);
            var perPoint = (float)damage.GetType().GetField("ReputationLossDamage").GetValue(damage);
            var step = (int)damage.GetType().GetField("Damaging").GetValue(damage);
            var grid = BuildFor(_faction.FounderId, "rep-target", Site.Origin,
                new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, 0, 0),
                // a grid of armor alone is nobody's (no big owner): the game takes the faction off its owned blocks
                new Part("CargoContainer/LargeBlockSmallContainer", 1, 0, 0));
            var block = grid.GetCubeBlock(grid.Min);
            Check(block.Integrity > 5.5f * perPoint, "a heavy armor block has " + block.Integrity + " integrity, less than the 5 × " + perPoint + " damage of the check");
            _pirates = MyPirateAntennas.GetPiratesId();
            var pirateFaction = factions.GetPlayerFaction(_pirates);
            _rep = factions.GetRelationBetweenPlayerAndFaction(Identity, _faction.FactionId).Item2;
            if (pirateFaction != null) _pirateRep = factions.GetRelationBetweenPlayerAndFaction(Identity, pirateFaction.FactionId).Item2;
            factions.SetReputationBetweenPlayerAndFaction(Identity, _faction.FactionId, 0, ReputationChangeReason.Admin);
            yield return null;
            var integrity0 = block.Integrity;
            for (var i = 0; i < 5; i++) block.DoDamage(perPoint * 1.01f, MyDamageType.Bullet, true, null, Body.EntityId);
            yield return null;
            var lost = integrity0 - block.Integrity;
            var rep = factions.GetRelationBetweenPlayerAndFaction(Identity, _faction.FactionId).Item2;
            Check(lost > 0, "the block took no damage");
            Check(rep < 0, "the player's reputation with " + _faction.Tag + " is " + rep + " after " + lost + " damage to its grid (" + perPoint + " a point)");
            Check(rep >= -5 * step * 2, "the player's reputation with " + _faction.Tag + " fell to " + rep + " for 5 points of damage");
            Result(lost + " damage to a grid of " + _faction.Tag + " (" + perPoint + " a point, step " + step + "): reputation 0 -> " + rep);
        }

        public override void Cleanup()
        {
            try
            {
                var factions = MySession.Static.Factions;
                if (_faction != null && _rep.HasValue) factions.SetReputationBetweenPlayerAndFaction(Identity, _faction.FactionId, _rep.Value, ReputationChangeReason.Admin);
                var pirateFaction = factions.GetPlayerFaction(_pirates);
                if (pirateFaction != null && _pirateRep.HasValue) factions.SetReputationBetweenPlayerAndFaction(Identity, pirateFaction.FactionId, _pirateRep.Value, ReputationChangeReason.Admin);
            }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// A wild animal of the planets (a wolf, a space spider) spawned by the game's AI next to a player on the ground goes
    /// for the player and bites: the player's health falls, the damage the animal's. The animal is taken away after.
    /// </summary>
    public sealed class FaunaCheck : RegressionCheck
    {
        private readonly string _animal;
        private readonly bool _wolf;
        private MyPlayer.PlayerId? _bot;
        private MyPlanet _spiders;

        public FaunaCheck(string animal, bool wolf)
        {
            _animal = animal;
            _wolf = wolf;
        }

        public override string Name => Prefix + "fauna_" + _animal.ToLowerInvariant();
        public override SiteKind Kind => SiteKind.Planet;
        public override int TimeoutSeconds => 90;
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            SkipUnless(_wolf ? MySession.Static.EnableWolfs : MySession.Static.EnableSpiders, (_wolf ? "wolves" : "spiders") + " are off in this world");
            var definition = MyDefinitionManager.Static.GetBotDefinition(new MyDefinitionId(typeof(VRage.Game.ObjectBuilders.AI.Bot.MyObjectBuilder_AnimalBot), _animal)) as MyAgentDefinition;
            SkipUnless(definition != null, "no animal " + _animal + " in this game");
            var ground = Site.Ground(Site.Origin);
            var up = Site.Up;
            var forward = Site.Forward;
            if (!_wolf)
            {
                // a spider comes up from under the ground next to the first online player on a planet where the game has
                // spiders live (MySpaceBotFactory.GetSpiderSpawnPosition): the check goes to such a planet
                bool Spiders(MyPlanetAnimalSpawnInfo info) => info?.Animals != null && info.Animals.Any(a => a.AnimalType.Contains("Spider"));
                var planet = MyEntities.GetEntities().OfType<MyPlanet>().FirstOrDefault(p => Spiders(p.Generator.AnimalSpawnInfo) || Spiders(p.Generator.NightAnimalSpawnInfo));
                SkipUnless(planet != null, "no planet with spiders in this world");
                _spiders = planet;
                var guess = planet.PositionComp.GetPosition() + Vector3D.Normalize(Site.Origin - planet.PositionComp.GetPosition()) * planet.MaximumRadius * 1.1;
                ground = planet.GetClosestSurfacePointGlobal(ref guess);
                up = Vector3D.Normalize(ground - planet.PositionComp.GetPosition());
                forward = Vector3D.CalculatePerpendicularVector(up);
                // the surface point of the planet's shape is not where the rock is to a metre or two: the ray finds it
                var hit = Sandbox.Engine.Physics.MyPhysics.CastRay(ground + up * 30, ground - up * 30, 28);
                if (hit.HasValue) ground = hit.Value.Position;
            }
            // on the ground itself: a client's character is moved by the client, the server does not let it fall
            yield return Arrive(ground + up * 0.1);
            // upright on this planet (Arrive stands the player up as the site is)
            Body.PositionComp.SetWorldMatrix(MatrixD.CreateWorld(ground + up * 0.1, forward, up));
            if (_spiders != null)
            {
                var first = Sync.Players.GetOnlinePlayers().FirstOrDefault(p => p.Id.SerialId == 0 && p.Character != null && p != FakeClients.PlayerOf(Me) &&
                                                                               MyGamePruningStructure.GetClosestPlanet(p.GetPosition()) == _spiders);
                SkipUnless(first == null, "another player, " + first?.DisplayName + ", is on the spiders' planet: the game brings a spider up next to the first one online");
            }
            Body.JetpackComp?.TurnOnJetpack(false);
            yield return Seconds(2);
            Body.StatComp.Health.Value = Body.StatComp.Health.MaxValue;
            var health = Body.StatComp.Health.Value;
            var serial = MyAIComponent.Static.SpawnNewBot(definition, ground + forward * 8 + up * 0.5, true);
            Check(serial > 0, "the game did not spawn the " + _animal);
            _bot = new MyPlayer.PlayerId(Sync.MyId, serial);
            MyCharacter animal = null;
            yield return Until(() => (animal = Sync.Players.GetPlayerById(_bot.Value)?.Character) != null ? 1 : 0, v => v > 0, "the " + _animal + " spawned", 10);
            yield return Until(() => Body.StatComp.Health.Value < health ? 1 : 0, v => v > 0, "the " + _animal + " biting the player", 60,
                () => "the animal " + F(Vector3D.Distance(animal.PositionComp.GetPosition(), Body.PositionComp.GetPosition())) + " m off, facing " + F(Vector3D.Dot(animal.WorldMatrix.Forward, Vector3D.Normalize(Body.PositionComp.GetPosition() - animal.PositionComp.GetPosition()))) +
                      ", to the player's middle " + F(Vector3D.Distance(animal.PositionComp.GetPosition(), Body.PositionComp.WorldVolume.Center)) +
                      ", the player " + F(Vector3D.Distance(Body.PositionComp.GetPosition(), ground)) + " m from where they stood, " + Body.GetCurrentMovementState() +
                      ", dead " + animal.IsDead + ", " + Mind(animal));
            var last = Body.StatComp.LastDamage;
            Check(last.AttackerId == animal.EntityId, "the player was hurt by " + last.AttackerId + " (" + last.Type + "), not the " + _animal + " " + animal.EntityId);
            Result("the " + _animal + " spawned 8 m off went for the player and bit: " + F(health - Body.StatComp.Health.Value) + " health (" + last.Type + ")");
            Body.StatComp.Health.Value = Body.StatComp.Health.MaxValue;
        }

        /// <summary>What the animal's AI is at: its logic's state and its target.</summary>
        private static string Mind(MyCharacter animal)
        {
            var bot = animal.BotLogic;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var logic = bot?.GetType().GetProperty("AgentLogic", flags)?.GetValue(bot);
            if (logic == null) return "no AI (" + bot?.GetType().Name + ")";
            var parts = new List<string> { logic.GetType().Name };
            foreach (var name in new[] { "IsBurrowed", "IsBurrowing", "IsDeburrowing", "CanBurrow" })
                if (logic.GetType().GetProperty(name, flags) is PropertyInfo property) parts.Add(name + " " + property.GetValue(logic));
            var target = logic.GetType().GetProperty("AiTarget", flags)?.GetValue(logic);
            parts.Add("attacking " + target?.GetType().GetProperty("IsAttacking", flags)?.GetValue(target));
            parts.Add("target " + target?.GetType().GetProperty("TargetType", flags)?.GetValue(target) + " " +
                      (target?.GetType().GetProperty("TargetEntity", flags)?.GetValue(target) as VRage.Game.Entity.MyEntity)?.DisplayName);
            return string.Join(", ", parts);
        }

        public override void Cleanup()
        {
            try
            {
                if (_bot.HasValue) MyAIComponent.Static.RemoveBot(_bot.Value, true);
            }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// A target dummy takes hits on its parts, not on the block: an arm shot through falls off, the dummy still stands; the
    /// head (a critical part) shot through brings it all down. Its restoration on at the shortest delay, it stands up whole
    /// for a steel plate of its own inventory each time: once just built (a new dummy stands down), once after the head.
    /// </summary>
    public sealed class TargetDummyCheck : RegressionCheck
    {
        public override string Name => Prefix + "target_dummy";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -6));
            var grid = Build("target-dummy", Site.Origin, true,
                new Part("TargetDummyBlock/TargetDummy", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var dummy = Block<MyTargetDummyBlock>(grid);
            // the terminal's switch and slider, as a player sets them
            ((Sandbox.ModAPI.IMyTerminalBlock)dummy).SetValueBool("Enable Restoration", true);
            ((Sandbox.ModAPI.IMyTerminalBlock)dummy).SetValueFloat("Delay", dummy.MinRestorationDelay);
            yield return Seconds(1);
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var states = (IDictionary)typeof(MyTargetDummyBlock).GetField("m_subpartStates", flags).GetValue(dummy);
            Check(states.Count == 4, "the dummy has " + states.Count + " parts, the definition 4");
            object Part(string name) => states.Values.Cast<object>().First(st => (string)st.GetType().GetField("Name").GetValue(st) == name);
            float Health(object st) => (float)st.GetType().GetProperty("IntegrityCurrent").GetValue(st);
            float Max(object st) => (float)st.GetType().GetField("IntegrityMax").GetValue(st);
            long Id(object st) => ((VRage.Game.Entity.MyEntity)st.GetType().GetField("Subpart").GetValue(st)).EntityId;
            bool Down() => (bool)typeof(MyTargetDummyBlock).GetProperty("IsDestroyed", flags).GetValue(dummy);
            var arm = Part("TargetD_RightArm");
            var head = Part("TargetD_Head");
            var integrity = dummy.SlimBlock.Integrity;
            string Parts() => string.Join(", ", states.Values.Cast<object>().Select(st => st.GetType().GetField("Name").GetValue(st) + " " + F(Health(st)) + "/" + F(Max(st)) +
                                                                                     (((bool?)st.GetType().GetField("IsCritical").GetValue(st)) == true ? " critical" : "")));
            // a dummy just built stands down, all parts at 0, until it has a plate to stand up with
            Check(Down(), "a dummy just built stands up with no plate (" + Parts() + ")");
            Put(Inventory(dummy), "Component/SteelPlate", 5);
            yield return Until(() => Down() ? 0 : 1, v => v > 0, "the dummy standing up with plates in it", 15);
            Check(states.Values.Cast<object>().All(st => Health(st) >= Max(st)), "the dummy stood up with parts not whole (" + Parts() + ")");

            dummy.SlimBlock.DoDamage(Max(arm) + 10, MyDamageType.Bullet, true, null, Body.EntityId, Id(arm));
            yield return null;
            Check(Health(arm) <= 0, "the arm shot for more than its " + Max(arm) + " health has " + Health(arm) + " left");
            Check(!Down(), "the dummy fell with only an arm shot off (" + Parts() + ")");
            Check(Health(Part("TargetD_Torso")) == Max(Part("TargetD_Torso")), "the torso was hurt by a shot at the arm");
            Check(dummy.SlimBlock.Integrity == integrity, "the block itself lost " + (integrity - dummy.SlimBlock.Integrity) + " integrity to a shot at a part");

            dummy.SlimBlock.DoDamage(Max(head) + 10, MyDamageType.Bullet, true, null, Body.EntityId, Id(head));
            yield return null;
            Check(Down(), "the dummy still stands with its head shot through");
            yield return Until(() => Down() ? 0 : 1, v => v > 0, "the dummy standing up again", 15);
            var whole = states.Values.Cast<object>().Count(st => Health(st) >= Max(st));
            Check(whole == 4, "the dummy stood up with " + whole + " of 4 parts whole");
            var plates = Amount(Inventory(dummy), "Component/SteelPlate");
            Check(plates == 3, "the dummy has " + plates + " plates of 5 after standing up twice (1 each time)");
            Result("built down, up with a plate; an arm shot off (the dummy stands, the block whole), the head shot through (all down), up again whole in " +
                   dummy.RestorationDelay + " s for 1 plate more");
        }
    }

    /// <summary>
    /// What the server lets a player at, as its network check of a client's request says (<see cref="RegressionCheck.Rights"/>):
    /// taking from a cargo container and renaming it. The player's own container by them, yes; another player's by them,
    /// no (ownership); the player's own 300 m off, no (out of reach); the same with an antenna on its grid
    /// broadcasting to the player, yes (the terminal through the antenna). And from the suit: into another player's
    /// container nothing goes, into the player's own it does.
    /// </summary>
    public sealed class AccessRightsCheck : RegressionCheck
    {
        public override string Name => Prefix + "access_rights";
        public override int TimeoutSeconds => 90;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -3));
            var stranger = -1;
            yield return Join(Site.At(0, 0, -40), i => stranger = i);
            var strangerId = FakeClients.PlayerOf(stranger).Identity.IdentityId;
            MyCubeGrid Cargo(string name, Vector3D at, long? owner, bool antenna)
            {
                var parts = new List<Part>
                {
                    new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0),
                    new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)),
                };
                if (antenna) parts.Add(new Part("RadioAntenna/LargeBlockRadioAntenna", 1, 0, 0));
                return owner.HasValue ? BuildFor(owner.Value, name, at, parts.ToArray()) : Build(name, at, true, parts.ToArray());
            }
            var near = Block<MyCargoContainer>(Cargo("rights-near", Site.At(4, 0, 0), null, false));
            var theirs = Block<MyCargoContainer>(Cargo("rights-theirs", Site.At(-4, 0, 0), strangerId, false));
            var far = Block<MyCargoContainer>(Cargo("rights-far", Site.At(300, 0, 0), null, false));
            var farGrid = Cargo("rights-antenna", Site.At(-300, 0, 0), null, true);
            var reached = Block<MyCargoContainer>(farGrid);
            var antenna = Block<MyRadioAntenna>(farGrid);
            ((Sandbox.ModAPI.IMyTerminalBlock)antenna).SetValueFloat("Radius", 500f);
            ((Sandbox.ModAPI.IMyTerminalBlock)antenna).SetValueBool("EnableBroadCast", true);
            yield return Seconds(1);

            string Ask(MyCargoContainer cargo) => Rights(Me, Inventory(cargo), "InventoryTransferItem_Implementation") + " / " + Rights(Me, cargo, "SetCustomNameEvent");
            const string passed = "Passed / Passed";
            var mine = Ask(near);
            Check(mine == passed, "the player's own container by them: take / rename " + mine);
            var other = Ask(theirs);
            Check(!other.Contains("Passed"), "another player's container (shared with nobody) by them: take / rename " + other);
            var away = Ask(far);
            Check(!away.Contains("Passed"), "the player's own container 300 m off with no antenna: take / rename " + away);
            var through = "";
            yield return Until(() => (through = Ask(reached)) == passed ? 1 : 0, v => v > 0, "the container 300 m off reached through its grid's antenna", 30,
                () => through + "; " + State(antenna));
            // into a container: the handler checks the destination itself (MyInventory.CheckCanAddItems)
            var suit = Inventory(Body);
            Put(suit, "Component/SteelPlate", 10);
            uint Plates() => suit.GetItems().First(i => i.Content.SubtypeName == "SteelPlate").ItemId;
            Request(suit, "InventoryTransferItem_Implementation", (VRage.MyFixedPoint)5, Plates(), theirs.EntityId, (byte)0, -1);
            var intoTheirs = Amount(Inventory(theirs), "Component/SteelPlate");
            Check(intoTheirs == 0, "the player put " + intoTheirs + " plates into another player's container");
            Request(suit, "InventoryTransferItem_Implementation", (VRage.MyFixedPoint)5, Plates(), near.EntityId, (byte)0, -1);
            var intoMine = Amount(Inventory(near), "Component/SteelPlate");
            Check(intoMine == 5, "the player put " + intoMine + " of 5 plates into their own container");
            Result("own container by the player " + mine + "; another's " + other + "; own 300 m off " + away + "; own 300 m off with an antenna " + through +
                   "; from the suit into another's container nothing, into their own 5 of 5");
        }
    }

    /// <summary>
    /// Lightning on a planet (<c>MySectorWeatherComponent.CreateLightning</c>, as a thunderstorm strikes): a strike at a
    /// block of armor harms it only when the world lets lightning do damage (<c>WeatherLightingDamage</c>; the check turns
    /// it on in memory for its strike and back after); a strike 15 m from a working decoy goes into the decoy (its lightning
    /// rod).
    /// </summary>
    public sealed class LightningCheck : RegressionCheck
    {
        public override string Name => Prefix + "lightning";
        public override SiteKind Kind => SiteKind.Planet;
        protected override bool NeedsDamage => true;

        private bool? _damage;

        protected override IEnumerator Check()
        {
            var weather = MySession.Static.GetComponent<MySectorWeatherComponent>();
            SkipUnless(MySession.Static.Settings.WeatherSystem && weather != null, "the weather is off in this world");
            var storm = MyDefinitionManager.Static.GetWeatherEffect("ThunderstormHeavy")?.Lightning;
            SkipUnless(storm != null, "no thunderstorm with lightning in this game");
            var ground = Site.Ground(Site.Origin);
            yield return Arrive(ground + Site.Up * 30 - Site.Forward * 30);
            var target = Build("lightning-target", ground + Site.Up * 1.25, true,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0));
            var rodGround = Site.Ground(Site.Origin + Site.Right * 150);
            var rod = Build("lightning-rod", rodGround + Site.Up * 1.25, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("Decoy/LargeDecoy", 0, 1, 0));
            var decoy = Block<MyDecoy>(rod);
            yield return Until(() => decoy.IsWorking ? 1 : 0, v => v > 0, "the decoy working", 10, () => State(decoy));
            var block = target.GetCubeBlock(Vector3I.Zero);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var strikes = (IList)typeof(MySectorWeatherComponent).GetField("m_lightnings", flags).GetValue(weather);
            Vector3D LastStrike()
            {
                var last = strikes[strikes.Count - 1];
                var builder = (VRage.Game.MyObjectBuilder_WeatherLightning)last.GetType().GetField("ObjectBuilder").GetValue(last);
                return builder.Position;
            }

            _damage = MySession.Static.Settings.WeatherLightingDamage;
            MySession.Static.Settings.WeatherLightingDamage = false;
            var whole = block.Integrity;
            weather.CreateLightning(block.WorldAABB.Center, storm);
            yield return Seconds(1);
            Check(block.Integrity == whole, "a strike with lightning damage off in the world took " + F(whole - block.Integrity) + " off the armor");

            MySession.Static.Settings.WeatherLightingDamage = true;
            weather.CreateLightning(block.WorldAABB.Center, storm);
            yield return Seconds(1);
            MySession.Static.Settings.WeatherLightingDamage = _damage.Value;
            var lost = whole - (target.GetCubeBlock(Vector3I.Zero)?.Integrity ?? 0);
            Check(lost > 0, "a strike at the armor with lightning damage on did nothing to it");

            var near = decoy.PositionComp.GetPosition() + Site.Forward * 15;
            var count = strikes.Count;
            weather.CreateLightning(near, storm);
            Check(strikes.Count > count, "no strike made 15 m from the decoy");
            var went = Vector3D.Distance(LastStrike(), decoy.PositionComp.GetPosition());
            Check(went < 0.5, "a strike 15 m from the decoy hit " + F(went) + " m from it, not the decoy (rod radius " + decoy.GetSafetyRodRadius() + " m)");
            Result("a strike at armor: nothing with the world's lightning damage off, " + F(lost) + " off with it on; a strike 15 m from a decoy went into the decoy (rod " +
                   decoy.GetSafetyRodRadius() + " m)");
        }

        public override void Cleanup()
        {
            try
            {
                if (_damage.HasValue) MySession.Static.Settings.WeatherLightingDamage = _damage.Value;
            }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// Ranks in a faction, as players ask for them: a member who is not a leader cannot kick another out; the founder
    /// promotes them to a leader, and then they can; the founder demotes them back; kicked, a member is out.
    /// </summary>
    public sealed class FactionRanksCheck : FactionCheck
    {
        public override string Name => Prefix + "faction_ranks";

        private static object Change(string name) =>
            System.Enum.Parse(typeof(MyFactionCollection).GetMethod("FactionStateChangeRequest", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetParameters()[0].ParameterType, name);

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            var a = -1;
            var b = -1;
            yield return Join(Site.At(4, 4, -6), i => a = i);
            yield return Join(Site.At(-4, 4, -6), i => b = i);
            var faction = Found(Me);
            Enter(a, faction, Me);
            Enter(b, faction, Me);
            void Ask(int client, string change, int whom) =>
                RequestAs(client, typeof(MyFactionCollection), "FactionStateChangeRequest", Change(change), faction.FactionId, faction.FactionId, IdentityOf(whom));
            Ask(a, "FactionMemberKick", b);
            Check(faction.IsMember(IdentityOf(b)), "a member who is not a leader kicked another out");
            Ask(Me, "FactionMemberPromote", a);
            Check(faction.IsLeader(IdentityOf(a)), "the member is not a leader after the founder promoted them");
            Ask(Me, "FactionMemberDemote", a);
            Check(!faction.IsLeader(IdentityOf(a)) && faction.IsMember(IdentityOf(a)), "the leader demoted by the founder is " + (faction.IsMember(IdentityOf(a)) ? "still a leader" : "out of the faction"));
            Ask(Me, "FactionMemberPromote", a);
            Ask(a, "FactionMemberKick", b);
            Check(!faction.IsMember(IdentityOf(b)), "a leader's kick left the member in the faction");
            yield return null;
            Result("faction " + faction.Tag + ": a plain member's kick refused; promoted to leader, demoted, promoted again; the leader kicked a member out");
        }
    }

    /// <summary>
    /// Blocks of armor shot apart: in the game their broken components come out as scrap metal (<c>MyComponentStack</c>
    /// puts 0.8 of a component's mass of scrap into the block for each one broken, at the component's drop chance, and the
    /// block's end spills it) when the game makes scrap (<c>MyFakes.ENABLE_SCRAP</c>) and the world has it on
    /// (<c>ScrapEnabled</c>). On the server none comes out: SentisOptimisations turns the game's scrap off at start, and
    /// SentisGameplayImprovements spawns no scrap in the world either (<c>NoScrapPatch</c>).
    /// </summary>
    public sealed class ScrapCheck : RegressionCheck
    {
        public override string Name => Prefix + "scrap";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -15));
            var sgi = System.AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType("SentisGameplayImprovements.NoScrapPatch") != null);
            SkipUnless(sgi || !Sandbox.Engine.Utils.MyFakes.ENABLE_SCRAP || MySession.Static.Settings.ScrapEnabled, "scrap is off in this world");
            var grid = Build("scrap", Site.Origin, true,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 2, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 3, 0, 0));
            var before = new HashSet<long>(MyEntities.GetEntities().OfType<MyFloatingObject>().Select(f => f.EntityId));
            yield return Seconds(1);
            for (var x = 0; x < 3; x++)
            {
                var block = grid.GetCubeBlock(new Vector3I(x, 0, 0));
                block?.DoDamage(block.MaxIntegrity * 2, MyDamageType.Bullet, true, null, Body.EntityId);
            }
            yield return Seconds(3);
            var loose = MyEntities.GetEntities().OfType<MyFloatingObject>()
                .Where(f => !before.Contains(f.EntityId) && Vector3D.Distance(f.PositionComp.GetPosition(), Site.Origin) < 20).ToList();
            foreach (var item in loose) Track(item);
            var scrap = loose.Where(f => f.Item.Content.SubtypeName == "Scrap").Sum(f => (double)f.Item.Amount);
            var gone = Enumerable.Range(0, 3).Count(x => grid.GetCubeBlock(new Vector3I(x, 0, 0)) == null);
            Check(gone == 3, "only " + gone + " of 3 blocks shot apart");
            var made = Sandbox.Engine.Utils.MyFakes.ENABLE_SCRAP;
            var off = !made ? "the game makes none (MyFakes.ENABLE_SCRAP off, as SentisOptimisations sets it)" : sgi ? "SentisGameplayImprovements spawns none (NoScrapPatch)" : null;
            if (off != null) Check(scrap == 0, F(scrap) + " kg of scrap came out of 3 blocks shot apart, though " + off);
            else Check(scrap > 0, "no scrap came out of 3 blocks shot apart, with scrap on in the game and the world");
            Result("3 blocks of armor shot apart: " + F(scrap) + " kg of scrap (" + (off ?? "the game's") + "), " + loose.Count + " loose items in all");
        }
    }

    /// <summary>
    /// A closed grid leaves the world soon, even when many are closed at once: 600 small grids closed together with one
    /// more, that one is out of the scene (no physics, nothing to hit) within <see cref="MaxSeconds"/>, the frames loaded
    /// meanwhile (<see cref="LoadMs"/> of the game thread each, as a server under load); in the whole suite, where the
    /// load is real and the others' cleanup closes thousands of grids at once, within <see cref="MaxSecondsInSuite"/>. A closed entity
    /// waits to be deleted, in the scene, its physics with it; SentisOptimisations spreads the deleting over frames
    /// (EntityDeleteBudget), and on a loaded server it once went one entity a frame - closed grids stood for most of a
    /// minute and the next check's guns shot a closed wall.
    /// </summary>
    public sealed class ClosedGridGoneCheck : RegressionCheck
    {
        public override string Name => Prefix + "closed_grid_gone";
        private const int Many = 600;
        private const double MaxSeconds = 4, MaxSecondsInSuite = 45;
        public override int TimeoutSeconds => 90;
        private const double LoadMs = 15;

        // in the suite the others' cleanup closes thousands of grids too (some 2600 heavy ones from the all-blocks checks),
        // and they go first: at the budget of a few milliseconds a frame they take tens of seconds (one a frame took over
        // a minute)
        private double Limit => InSuite ? MaxSecondsInSuite : MaxSeconds;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -10));
            var grids = new List<MyCubeGrid>();
            for (var i = 0; i < Many; i++)
                grids.Add(Build("closed-many", Site.At((i % 30) * 3 - 45, 20 + (i / 30) * 3, 30), true, new[] { new Part("CubeBlock/SmallBlockArmorBlock", 0, 0, 0) }, MyCubeSize.Small));
            var probe = Build("closed-probe", Site.Origin, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            yield return Seconds(1);
            foreach (var grid in grids) grid.Close();
            probe.Close();
            var closed = System.DateTime.UtcNow;
            // a loaded server: the frame has no time left by the time the closed ones are deleted, as on the stand most of
            // the time. The load goes in the game's own frame (an invoke runs at its start, before the entities' update and
            // the end-of-frame deleting); the check's step runs outside it
            yield return Until(() =>
                {
                    // in the whole suite the load is there already (and more of it would slow every other check)
                    if (!InSuite)
                        Sandbox.ModAPI.MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                        {
                            var busy = System.Diagnostics.Stopwatch.StartNew();
                            while (busy.Elapsed.TotalMilliseconds < LoadMs) { }
                        });
                    return probe.InScene ? 0 : 1;
                }, v => v > 0, "the closed grid out of the scene", (int)Limit + 2,
                () => "in scene " + probe.InScene + ", physics " + (probe.Physics?.Enabled == true) + ", of the " + Many + " closed with it still in the scene " + grids.Count(g => g.InScene));
            var took = (System.DateTime.UtcNow - closed).TotalSeconds;
            Check(took <= Limit + 0.5, "the closed grid stood in the scene " + F(took) + " s");
            Result("closed with " + Many + " others, out of the scene in " + F(took) + " s; of the others still in it " + grids.Count(g => g.InScene));
        }
    }

    /// <summary>
    /// A character deleted with its radiation damage still queued: the game does the damage a frame later
    /// (<c>MyRadiationStat.DoRadiationDamage</c> hands it to <c>MySandboxGame.Invoke</c>), and for a character gone by then
    /// the call threw a NullReferenceException that took the server down (21.10.2026). SentisOptimisations drops such a
    /// call (<c>CrashFixPatch</c>). The check makes sure the guard is on the queued call first (without it the next step
    /// would crash the server, so it is not taken), then queues the damage of another player's character and deletes the
    /// character in the same frame: the server goes on.
    /// </summary>
    public sealed class RadiationDeletedCharacterCheck : RegressionCheck
    {
        public override string Name => Prefix + "radiation_deleted_character";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -6));
            SkipUnless(MySession.Static.SurvivalMode, "the world is not survival: the game queues no radiation damage");
            var radiation = typeof(SpaceEngineers.Game.EntityComponents.GameLogic.MyRadiationStat);
            var queued = radiation.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name.StartsWith("<DoRadiationDamage>", System.StringComparison.Ordinal) && m.GetParameters().Length == 0);
            Check(queued != null, "MyRadiationStat's queued damage is not in this game");
            var guarded = HarmonyLib.Harmony.GetPatchInfo(queued)?.Prefixes.Any(p => p.PatchMethod.DeclaringType?.Assembly.GetName().Name == "SentisOptimisations") == true;
            Check(guarded, "no guard on MyRadiationStat's queued damage (SO CrashFixPatch): a character deleted with it queued takes the server down");

            var victim = -1;
            yield return Join(Site.At(6, 0, -6), i => victim = i);
            var character = FakeClients.Character(victim);
            var scripts = typeof(Sandbox.Game.Components.MyEntityStatComponent).GetField("m_scripts", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(character.StatComp) as System.Collections.IDictionary;
            var stat = scripts?.Values.OfType<SpaceEngineers.Game.EntityComponents.GameLogic.MyRadiationStat>().FirstOrDefault();
            SkipUnless(stat != null, "the character has no radiation stat in this world");
            var definition = radiation.GetField("m_radiationStatDefinition", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(stat);
            SkipUnless(definition != null, "the character's radiation stat has no definition: the game queues no damage");
            radiation.GetMethod("DoRadiationDamage", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(stat, null);
            character.Close();
            DeleteClosed();
            Check(character.Closed, "the character was not deleted in the frame its radiation damage was queued");
            yield return Seconds(1);
            Result("radiation damage queued, the character deleted in the same frame: the queued call came after it and the server went on");
        }
    }
}
