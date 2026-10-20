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

        public override string ToString() => Kind.ToString().ToLowerInvariant() + " site " + Index + " at " + Origin.ToString("F0");
    }

    /// <summary>
    /// The sites of the regression checks. Space sites stand in a square 1 km apart, far out of every planet's gravity;
    /// planet sites lie 300 m apart on the surface of the planet by the stand's anchor, a few km off it, so the other
    /// scenarios built there stay clear.
    /// </summary>
    public static class Sites
    {
        public const double SpaceSpacing = 1000;
        public const double PlanetSpacing = 300;

        /// <summary>How far from the wheel_perf anchor the planet sites start, along the surface.</summary>
        private const double PlanetOffset = 3000;

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
            var row = index / 8;
            var column = index % 8;
            var origin = basis.Translation + basis.Right * (column * SpaceSpacing) + basis.Forward * (row * SpaceSpacing);
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
            // along the surface: one row of sites, each a turn of PlanetSpacing metres about the planet's centre
            var angle = (PlanetOffset + index * PlanetSpacing) / radius;
            var axis = Vector3D.Normalize(basis.Right);
            var up = Vector3D.Transform(Vector3D.Normalize(basis.Translation - centre), MatrixD.CreateFromAxisAngle(axis, angle));
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
        /// A point in open space: straight out from the stand's planet, past where its gravity ends, then on until
        /// nothing of any planet pulls there.
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
                direction = Vector3D.Normalize(anchor.Translation - centre);
                start = centre + direction * (planet.MaximumRadius + 10000);
            }
            var point = start;
            for (var i = 0; i < 400 && MyGravityProviderSystem.CalculateNaturalGravityInPoint(point).LengthSquared() > 0; i++)
                point += direction * 5000;
            // and clear of the planet's influence on the sides too: the square of sites spans 8 km
            point += direction * 5000;
            var forward = Vector3D.CalculatePerpendicularVector(direction);
            WorldApi.Log.Info("regression: space sites start at {0}", point.ToString("F0"));
            return MatrixD.CreateWorld(point, forward, direction);
        }

        private static MatrixD FindPlanet()
        {
            var anchor = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + "regression-anchor");
            _planetEntity = MyGamePruningStructure.GetClosestPlanet(anchor.Translation)
                            ?? throw new Core.ScenarioSkippedException("no planet in this world for the planet checks");
            var centre = _planetEntity.PositionComp.GetPosition();
            var up = Vector3D.Normalize(anchor.Translation - centre);
            var forward = Vector3D.CalculatePerpendicularVector(up);
            return MatrixD.CreateWorld(anchor.Translation, forward, up);
        }
    }
}
