using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Regression
{
    /// <summary>Where a regression check is built: in open space (no gravity) or on a planet's surface.</summary>
    public enum SiteKind
    {
        Space,
        Planet,
    }

    /// <summary>
    /// The place of one regression check: a frame whose <see cref="Up"/> points away from gravity (on a planet) or along
    /// the world's up (in space). Everything a check builds is put relative to it, so checks run side by side at sites of
    /// their own and never touch each other.
    /// </summary>
    public sealed class Site
    {
        public SiteKind Kind;
        public int Index;
        public MatrixD Frame;
        public MyPlanet Planet;

        public Vector3D Origin => Frame.Translation;
        public Vector3D Up => Frame.Up;
        public Vector3D Forward => Frame.Forward;
        public Vector3D Right => Frame.Right;

        /// <summary>A point of the site: metres to the right, up and forward of its origin.</summary>
        public Vector3D At(double right, double up, double forward) => Origin + Right * right + Up * up + Forward * forward;

        /// <summary>The ground under a point of a planet site (the point itself in space).</summary>
        public Vector3D Ground(Vector3D point)
        {
            if (Planet == null) return point;
            return Planet.GetClosestSurfacePointGlobal(ref point);
        }

        public override string ToString() => Kind.ToString().ToLowerInvariant() + " site " + Index + " at " + Origin.ToString("F0") +
                                             (Kind == SiteKind.Space ? ", natural gravity " + Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(Origin).Length().ToString("F3") : "");
    }

    /// <summary>
    /// The sites of the regression checks, <see cref="Spacing"/> apart: each in a cluster of the physics of its own (with
    /// SentisClusters' 5 km clusters), so the clusters' threads update the checks side by side.
    ///
    /// The game puts what moves in one cluster while the boxes round the objects, half a cluster wide on every side,
    /// touch, and parts a cluster only along the world's axes: it sorts the objects along the longest axis and cuts
    /// where the boxes leave a gap (MyClusterTree.ReorderClusters). A square turned to the world's axes casts its sites
    /// on each axis closer than a cluster and stays one cluster, so the squares here run along the world's axes:
    /// space sites in a square <see cref="SpaceSide"/> a side across a world axis out of the planet, far out of every
    /// planet's gravity, with nothing over them (checks send their grids up: the weapon lanes 5-36 km over the site,
    /// jumps); planet sites in a square <see cref="PlanetSide"/> a side round the point of the planet's surface where its
    /// up is a world axis - the one nearest the stand's anchor whose square stays clear of it and the scenarios there.
    /// </summary>
    public static class Sites
    {
        public const double Spacing = 8000;

        /// <summary>Space sites in a row of the square; the rows go on past it when there are more.</summary>
        public const int SpaceSide = 19;

        /// <summary>The rows of space sites checked to be out of gravity when the place is found.</summary>
        private const int SpaceRows = 24;

        /// <summary>Planet sites a side of the square; the rows go on past it when there are more.</summary>
        public const int PlanetSide = 6;

        private static readonly Vector3D[] Axes = { Vector3D.Right, Vector3D.Left, Vector3D.Up, Vector3D.Down, Vector3D.Backward, Vector3D.Forward };

        private static MatrixD? _space;
        private static MatrixD? _planet;
        private static MyPlanet _planetEntity;

        public static Site Get(SiteKind kind, int index) => kind == SiteKind.Space ? Space(index) : Planet(index);

        /// <summary>
        /// A site to watch a check at: 40 m in front of the real player nearest to the point (a run from the field), its
        /// forward across that player's view - what moves along it moves past their eyes. On a planet the site sits on the
        /// ground there. Without a player near, at the point itself.
        /// </summary>
        public static Site InFrontOfPlayer(SiteKind kind, Vector3D point)
        {
            var watcher = MyEntities.GetEntities().OfType<Sandbox.Game.Entities.Character.MyCharacter>()
                .Where(c => !c.IsDead && !c.MarkedForClose && c.ControllerInfo?.Controller?.Player != null &&
                            !FakeClients.IsFake(c.ControllerInfo.Controller.Player.Id.SteamId) && !(c.DisplayName ?? "").StartsWith("[BOT]"))
                .OrderBy(c => Vector3D.DistanceSquared(c.PositionComp.GetPosition(), point)).FirstOrDefault();
            var planet = MyGamePruningStructure.GetClosestPlanet(point);
            var gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(point);
            var up = gravity.LengthSquared() > 0.01 ? -Vector3D.Normalize(gravity) : (Vector3D)(watcher?.WorldMatrix.Up ?? Vector3D.Up);
            var look = watcher != null ? (Vector3D)watcher.WorldMatrix.Forward : Vector3D.CalculatePerpendicularVector(up);
            look = Vector3D.Normalize(Vector3D.Reject(look, up));
            var origin = watcher != null ? watcher.PositionComp.GetPosition() + look * 40 : point;
            var hasGround = kind == SiteKind.Planet && planet != null;
            if (hasGround) origin = planet.GetClosestSurfacePointGlobal(ref origin);
            else if (watcher != null) origin += up * 1.5;
            // forward across the view: from the watcher's left to their right
            var across = Vector3D.Normalize(Vector3D.Cross(look, up));
            WorldApi.Log.Info("regression: a site in front of {0} at {1}", watcher?.DisplayName ?? "nobody", origin.ToString("F0"));
            return new Site { Kind = kind, Index = 0, Planet = hasGround ? planet : null, Frame = MatrixD.CreateWorld(origin, across, up) };
        }

        public static Site Space(int index)
        {
            if (!_space.HasValue) _space = FindSpace();
            var basis = _space.Value;
            var origin = SpacePoint(basis, index);
            return new Site
            {
                Kind = SiteKind.Space,
                Index = index,
                Frame = MatrixD.CreateWorld(origin, basis.Forward, basis.Up),
            };
        }

        public static Site Planet(int index)
        {
            if (!_planet.HasValue) _planet = FindPlanet();
            var basis = _planet.Value;
            var planet = _planetEntity;
            var centre = planet.PositionComp.GetPosition();
            var radius = (basis.Translation - centre).Length();
            // a point of the square over the axis' point of the surface, straight down onto the ground: along the
            // world's axes the sites stay about Spacing apart (a little less towards the edges, where the ground falls away)
            var up = Vector3D.Normalize(SquarePoint(basis, index, PlanetSide) - centre);
            var probe = centre + up * radius;
            var surface = planet.GetClosestSurfacePointGlobal(ref probe);
            var forward = Vector3D.Normalize(Vector3D.Reject(basis.Forward, up));
            if (!forward.IsValid() || forward.LengthSquared() < 0.5) forward = Vector3D.CalculatePerpendicularVector(up);
            return new Site
            {
                Kind = SiteKind.Planet,
                Index = index,
                Planet = planet,
                Frame = MatrixD.CreateWorld(surface, forward, up),
            };
        }

        /// <summary>
        /// A point in open space: out from the stand's planet along the world axis nearest the anchor's up, past where
        /// its gravity ends, then on until nothing of any planet pulls at any site.
        /// </summary>
        private static MatrixD FindSpace()
        {
            var anchor = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + "regression-anchor");
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor.Translation);
            Vector3D start;
            Vector3D direction;
            if (planet == null)
            {
                start = anchor.Translation;
                direction = Vector3D.Up;
            }
            else
            {
                var centre = planet.PositionComp.GetPosition();
                direction = Axes.OrderByDescending(a => Vector3D.Dot(a, anchor.Translation - centre)).First();
                start = centre + direction * (planet.MaximumRadius + 10000);
            }
            var basis = MatrixD.CreateWorld(start, AxisAcross(direction), direction);
            // on out until no site of the first SpaceRows rows has gravity: the square is 144 km a side, and a moon may
            // stand by the planet
            for (var i = 0; i < 400 && !OutOfGravity(basis); i++)
                basis.Translation += direction * 5000;
            WorldApi.Log.Info("regression: space sites start at {0}, {1} km apart along the world's axes", basis.Translation.ToString("F0"), Spacing / 1000);
            return basis;
        }

        /// <summary>A world axis square to the given one.</summary>
        private static Vector3D AxisAcross(Vector3D axis) => Axes.First(a => Math.Abs(Vector3D.Dot(a, axis)) < 0.5);

        /// <summary>A site's point of a square: rows of the side along Right, the square centred on the basis.</summary>
        private static Vector3D SquarePoint(MatrixD basis, int index, int side)
        {
            var row = index / side - (side - 1) / 2.0;
            var column = index % side - (side - 1) / 2.0;
            return basis.Translation + basis.Right * (column * Spacing) + basis.Forward * (row * Spacing);
        }

        private static Vector3D SpacePoint(MatrixD basis, int index) => SquarePoint(basis, index, SpaceSide);

        /// <summary>The heavy clumps of <c>regression_heavy</c>: how many, the share of the space checks in them, their sites apart.</summary>
        public const int HeavyClumps = 3;
        public const double HeavyShare = 0.9;
        public const double ClumpSpacing = 1000;
        private const int ClumpSide = 10;
        private const double ClumpsApart = 40000;

        /// <summary>
        /// A space site of <c>regression_heavy</c>: <see cref="HeavyShare"/> of the space checks in <see cref="HeavyClumps"/>
        /// clumps, their sites <see cref="ClumpSpacing"/> apart (each clump one cluster of the physics, tens of grids and
        /// players: what SentisClusters detaches), the clumps 40 km apart along Right; the rest light, one a cluster,
        /// <see cref="Spacing"/> apart in rows of 6 well forward of the clumps. The checks go to the clumps in turn, so
        /// each clump gets a share of every kind.
        /// </summary>
        public static Site HeavySpace(int index, int count, out int clump)
        {
            if (!_space.HasValue) _space = FindSpace();
            var basis = _space.Value;
            var heavy = (int)Math.Round(count * HeavyShare);
            Vector3D origin;
            if (index < heavy)
            {
                clump = index % HeavyClumps;
                var k = index / HeavyClumps;
                var centre = basis.Translation + basis.Right * ((clump - (HeavyClumps - 1) / 2.0) * ClumpsApart) - basis.Forward * 20000;
                origin = centre + basis.Right * ((k % ClumpSide - (ClumpSide - 1) / 2.0) * ClumpSpacing) + basis.Forward * ((k / ClumpSide - 5) * ClumpSpacing);
            }
            else
            {
                clump = -1;
                var j = index - heavy;
                origin = basis.Translation + basis.Right * ((j % 6 - 2.5) * Spacing) + basis.Forward * (20000 + j / 6 * Spacing);
            }
            return new Site { Kind = SiteKind.Space, Index = index, Frame = MatrixD.CreateWorld(origin, basis.Forward, basis.Up) };
        }

        private static bool OutOfGravity(MatrixD basis)
        {
            for (var i = 0; i < SpaceSide * SpaceRows; i++)
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(SpacePoint(basis, i)).LengthSquared() > 0) return false;
            return true;
        }

        /// <summary>
        /// The point of the surface over which the planet square stands: where the planet's up is the world axis
        /// nearest the anchor's, unless its square comes within a cluster of the anchor - then the next nearest.
        /// </summary>
        private static MatrixD FindPlanet()
        {
            var anchor = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + "regression-anchor");
            _planetEntity = MyGamePruningStructure.GetClosestPlanet(anchor.Translation)
                            ?? throw new Core.ScenarioSkippedException("no planet in this world for the planet checks");
            var centre = _planetEntity.PositionComp.GetPosition();
            var radius = (anchor.Translation - centre).Length();
            var reach = PlanetSide * Spacing / Math.Sqrt(2) + Spacing;
            var axis = Axes.OrderByDescending(a => Vector3D.Dot(a, anchor.Translation - centre))
                .First(a => Vector3D.Distance(centre + a * radius, anchor.Translation) > reach);
            var point = centre + axis * radius;
            WorldApi.Log.Info("regression: planet sites round {0} on {1}, {2} km from the anchor", point.ToString("F0"), _planetEntity.StorageName,
                (Vector3D.Distance(point, anchor.Translation) / 1000).ToString("F0"));
            return MatrixD.CreateWorld(point, AxisAcross(axis), axis);
        }
    }
}
