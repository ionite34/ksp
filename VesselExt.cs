using System;
using System.Linq;
using System.Threading.Tasks;
using KRPC.Client;
using KRPC.Client.Services.SpaceCenter;

namespace ksp
{
    public static class VesselExt
    {
        public static async Task WaitForDirection(this Vessel vessel, ReferenceFrame referenceFrame, Tuple<double, double, double> target, Tuple<double, double, double> tolerance)
        {
            var conn = vessel.connection as Connection;
            var (x, y, z) = target;
            await conn.WaitFor(
                () => vessel.Direction(referenceFrame),
                t => Math.Abs(t.Item1 - x) < tolerance.Item1 && Math.Abs(t.Item2 - y) < tolerance.Item2 && Math.Abs(t.Item3 - z) < tolerance.Item3
            );
        }
        
        public static async Task WaitForDirection(this Vessel vessel, ReferenceFrame referenceFrame, Tuple<double, double, double> target, double tolerance)
        {
            await WaitForDirection(vessel, referenceFrame, target, Tuple.Create(tolerance, tolerance, tolerance));
        }

        public static bool InDirection(this Vessel vessel, ReferenceFrame referenceFrame,
            Tuple<double, double, double> target, Tuple<double, double, double> tolerance)
        {
            var (x, y, z) = target;
            var direction = vessel.Direction(referenceFrame);
            return Math.Abs(direction.Item1 - x) < tolerance.Item1 && Math.Abs(direction.Item2 - y) < tolerance.Item2 &&
                   Math.Abs(direction.Item3 - z) < tolerance.Item3;
        }
        
        public static double DeltaV(this Vessel vessel)
        {
            var gravity = vessel.Orbit.Body.SurfaceGravity;
            var totalMass = vessel.Mass;
            var dryMass = vessel.DryMass;
            
            // Using DeltaV = Isp * g * ln(m0 / m1)
            var stage = vessel.Control.CurrentStage;
            var engines = vessel.Parts.Engines;
            var totalIsp = engines
               .Where(engine => engine.Active && engine.Part.Stage == stage)
               .Sum(engine => engine.SpecificImpulse);
            
            return totalIsp * gravity * Math.Log(totalMass / dryMass);
        }
    }
}