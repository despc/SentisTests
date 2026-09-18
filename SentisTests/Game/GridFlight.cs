using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using VRageMath;

namespace SentisTests.Game
{
    /// <summary>
    /// Keeps test ships flying on horizontal circles by steering their physics velocity every frame,
    /// so they stay inside a chosen area and their physics state keeps changing (replication state
    /// sync). Game thread.
    /// </summary>
    public static class GridFlight
    {
        private sealed class Flight
        {
            public MyCubeGrid Grid;
            public Vector3D Center;
            public double Radius;
            public double AngularSpeed;
            public double Phase;
            public Vector3 Spin;
        }

        private const double PositionGain = 0.5;
        private static readonly List<Flight> Flights = new List<Flight>();
        private static long _frame;

        public static int Count => Flights.Count;

        public static void Add(MyCubeGrid grid, Vector3D center, double radius, double speedMps, Vector3 spin)
        {
            var offset = grid.PositionComp.GetPosition() - center;
            Flights.Add(new Flight
            {
                Grid = grid,
                Center = center,
                Radius = radius,
                AngularSpeed = speedMps / Math.Max(1, radius),
                Phase = Math.Atan2(offset.Z, offset.X) - (speedMps / Math.Max(1, radius)) * _frame / 60.0,
                Spin = spin,
            });
        }

        public static void Clear() => Flights.Clear();

        public static void Tick()
        {
            _frame++;
            if (Flights.Count == 0) return;
            var t = _frame / 60.0;
            for (var i = Flights.Count - 1; i >= 0; i--)
            {
                var flight = Flights[i];
                var grid = flight.Grid;
                if (grid == null || grid.MarkedForClose || grid.Closed)
                {
                    Flights.RemoveAt(i);
                    continue;
                }
                var physics = grid.Physics;
                if (physics == null || grid.IsStatic) continue;
                var angle = flight.Phase + flight.AngularSpeed * t;
                var target = flight.Center + new Vector3D(Math.Cos(angle), 0, Math.Sin(angle)) * flight.Radius;
                var tangent = new Vector3D(-Math.Sin(angle), 0, Math.Cos(angle)) * flight.AngularSpeed * flight.Radius;
                var velocity = tangent + (target - grid.PositionComp.GetPosition()) * PositionGain;
                physics.LinearVelocity = velocity;
                physics.AngularVelocity = flight.Spin;
            }
        }
    }
}
