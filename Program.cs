using System;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using KRPC.Client;
using KRPC.Client.Services.KRPC;
using KRPC.Client.Services.MechJeb;
using KRPC.Client.Services.SpaceCenter;
using KRPC.Client.Services.UI;
using NLog;
using NLog.Config;
using NLog.Fluent;
using ShellProgressBar;
using ArgumentOutOfRangeException = System.ArgumentOutOfRangeException;
using Service = KRPC.Client.Services.MechJeb.Service;

namespace ksp
{
    public class Flight: IDisposable
    {
        public readonly Connection Connection;
        public readonly Service Mechjeb;
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public Flight()
        {

            Connection = new Connection(
                name: "DankConnection",
                address: IPAddress.Parse("127.0.0.1"),
                rpcPort: 5700,
                streamPort: 5701
            );

            Mechjeb = new Service(Connection);
        }
        
        public Vessel Vessel => Connection.SpaceCenter().ActiveVessel;
        
        public Launch Launch => new Launch(this);
        
        public Maneuver Maneuver (CancellationTokenSource cts = default) => new Maneuver(this, cts);

        public void Dispose()
        {
            Connection.Dispose();
        }

        private string FormatDirections(Tuple<double, double, double> current, Tuple<double, double, double> target)
        {
            // Format the directions with 2 decimal places, and with delta
            var deltaX = Math.Abs(target.Item1 - current.Item1);
            var deltaY = Math.Abs(target.Item2 - current.Item2);
            var deltaZ = Math.Abs(target.Item3 - current.Item3);
            return $"Current ({current.Item1:0.00}, {current.Item2:0.00}, {current.Item3:0.00})\n" +
                   $"Target  ({target.Item1:0.00}, {target.Item2:0.00}, {target.Item3:0.00})\n" +
                   $"Delta   ({deltaX:0.00}, {deltaY:0.00}, {deltaZ:0.00})";
        }

        private Tuple<double, double, double> ImpactPosition()
        {
            var orbit = Vessel.Orbit;
            var radius = orbit.Body.EquatorialRadius + Vessel.Flight().SurfaceAltitude;
            var trueAnomaly = orbit.TrueAnomalyAtRadius(radius);
            // Use descending side
            trueAnomaly *= -1;
            var impactTime = orbit.UTAtTrueAnomaly(trueAnomaly);
            var impactPosition = Vessel.Orbit.PositionAt(impactTime, orbit.Body.ReferenceFrame);
            return impactPosition;
        }

        public async Task BoosterLanding()
        {
            // Activate speed brakes
            Vessel.Control.Brakes = true;
            
            // Flip back to KSC
            Logger.Info($"Beginning flip back");
            Vessel.Control.RCS = true;

            var ap = Vessel.AutoPilot;
            var referenceFrame = Vessel.SurfaceVelocityReferenceFrame;
            ap.ReferenceFrame = referenceFrame;
            ap.TargetDirection = Tuple.Create(0.0, -1.0, 0.0); // Retrograde

            Vessel.Control.SpeedMode = SpeedMode.Surface;
            ap.SAS = true;
            ap.SASMode = SASMode.StabilityAssist;
            await Task.Delay(50);
            ap.SASMode = SASMode.Retrograde;

            // Mechjeb.SmartASS.AutopilotMode = SmartASSAutopilotMode.Retrograde;
            // Mechjeb.SmartRCS.RCSController.RCSForRotation = true;

            await Task.Delay(500);
            
            var tolerance = (0.1, 0.05, 0.1);
            
            // For 3 seconds, burst some throttle to help with vectoring
            // Stop early if our direction is good
            Logger.Info("Throttle for vectoring");
            for (var i = 0; i < 3; i++)
            {
                if (Vessel.InDirection(referenceFrame, ap.TargetDirection, tolerance.ToTuple()))
                    break;
                Vessel.Control.Throttle = 0.7f;
                await Task.Delay(100);
                Vessel.Control.Throttle = 0.5f;
                await Task.Delay(150);
                Vessel.Control.Throttle = 0;
                await Task.Delay(500);
            }
            
            Logger.Info("Waiting for retrograde...");
            for (var i = 0; i < 30; i++)
            {
                if (Vessel.InDirection(referenceFrame, ap.TargetDirection, tolerance.ToTuple()))
                    break;
                var current = Vessel.Direction(referenceFrame);
                var target = ap.TargetDirection;
                Console.WriteLine(FormatDirections(current, target));
                Vessel.Control.Throttle = 0.3f;
                await Task.Delay(150);
                Vessel.Control.Throttle = 0;
                await Task.Delay(500);

                // Take a break
                if (i % 10 != 0)
                    await Task.Delay(1000);
            }

            await Vessel.WaitForDirection(referenceFrame, ap.TargetDirection, tolerance.ToTuple());
            Logger.Info("-> [Finished retrograde]");

            // Burn until minimum delta v (1000)
            const int targetDeltaV = 1500;
            if (Vessel.DeltaV() > targetDeltaV)
            {
                Logger.Info($"Burn until minimum delta v {targetDeltaV} (current: {Vessel.DeltaV()})");
                
                var burnDeltaV = targetDeltaV - Vessel.DeltaV();
                // Lock current heading as node
                var ut = Connection.SpaceCenter().UT;
                var node = Vessel.Control.AddNode(ut, prograde: Convert.ToSingle(burnDeltaV));
                
                // Switch from SAS to Mechjeb
                // ap.SAS = false;
                ap.SASMode = SASMode.StabilityAssist;
                // Mechjeb.SmartASS.AutopilotMode = SmartASSAutopilotMode.Advanced;
                // Mechjeb.SmartASS.AdvancedReference = AttitudeReference.Inertial;
                // Mechjeb.SmartASS.AdvancedDirection = Direction.Back;

                var s = ImpactPosition();
                Logger.Info($"Estimated Impact position: {s.Item1}, {s.Item2}, {s.Item3}");

                // Do burn, until we hit target delta v or 7 seconds
                var burnTime = 0;
                var burnMax = 7 * 1000;
                while (Vessel.DeltaV() > targetDeltaV && burnTime < burnMax)
                {
                    // Map throttle inverse of diff
                    var diff = Vessel.DeltaV() - targetDeltaV;
                    Vessel.Control.Throttle = Convert.ToSingle(Math.Min(1, Math.Max(0.8, diff / 1000)));
                    await Task.Delay(50);
                    burnTime += 50;
                }

                Logger.Info("Changing to orbit mode");
                Vessel.Control.SpeedMode = SpeedMode.Orbit;
                Vessel.AutoPilot.SASMode = SASMode.Retrograde;

                await Task.Delay(1000);
                Vessel.Control.Throttle = 1.0f;
                await Task.Delay(5500);

                Logger.Info("Changing to stability assist for 1s");
                Vessel.AutoPilot.SASMode = SASMode.StabilityAssist;
                await Task.Delay(1000);
                
                Logger.Info("Final Prograde burn");
                Vessel.AutoPilot.SASMode = SASMode.Prograde;
                Vessel.AutoPilot.SAS = true;
                Vessel.Control.Throttle = 1.00f;
                // await Task.Delay(2050);
                await Task.Delay(2550);
                Vessel.AutoPilot.SAS = false;
                Vessel.Control.Throttle = 0.0f;

                node.Remove();
            }
            else
            {
                Logger.Info($"Already below minimum delta v {targetDeltaV} (current: {Vessel.DeltaV()})");
            }
            // Activate mechjeb landing guidance
            Logger.Info("Activating landing guidance");
            var landing = Mechjeb.LandingAutopilot;
            landing.DeployChutes = true;
            landing.DeployGears = true;
            landing.RcsAdjustment = true;
            landing.TouchdownSpeed = 2;
            var kerbin = Connection.SpaceCenter().Bodies["Kerbin"];
            Mechjeb.TargetController.SetPositionTarget(kerbin, -0.09694444, -74.5575);
            // Mechjeb.TargetController.SetPositionTarget(kerbin, -0.0979, -77.946667);
            
            landing.LandAtPositionTarget();
            landing.Enabled = true;

            await Connection.WaitFor(() => landing.Enabled, false);

            Logger.Info("Finished booster landing.");
        }
    }

    internal static class Program
    {
        private class Options
        {
            [Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")]
            public bool Verbose { get; set; }
        }

        public static void Main(string[] args)
        {
            var configuration = new LoggingConfiguration();
            var logConsole = new NLog.Targets.ConsoleTarget("logConsole");

            configuration.AddRule(LogLevel.Info, LogLevel.Fatal, logConsole);

            Parser.Default.ParseArguments<Options>(args)
                .WithParsed<Options>(opt =>
                {

                    if (opt.Verbose)
                    {
                        Console.WriteLine("Verbose output enabled.");
                        // Make DEBUG logging to console instead of file
                        configuration.AddRule(LogLevel.Debug, LogLevel.Fatal, logConsole);
                    }
                    else
                    {
                        var logFile = new NLog.Targets.FileTarget("logFile") { FileName = "log.txt" };
                        configuration.AddRule(LogLevel.Debug, LogLevel.Fatal, logFile);
                    }
                    
                    // LogManager.Configuration = configuration;
                    MainAsync(opt).GetAwaiter().GetResult();
                });
        }

        private static async Task StartMenu(Flight flight)
        {
            while (true)
            {
                Console.WriteLine("Options");
                Console.WriteLine("1. Launch");
                Console.WriteLine("2. Launch (with abort)");
                Console.WriteLine("3. Booster Landing");
                Console.WriteLine("4. Deorbit");
                Console.WriteLine("Q. Quit");

                var key = Console.ReadKey();
                
                switch (key.Key)
                {
                    case ConsoleKey.D1:
                        await flight.Launch.Run();
                        break;
                    case ConsoleKey.D2:
                        await flight.Launch.RunWithAbort();
                        break;
                    case ConsoleKey.D3:
                        await flight.BoosterLanding();
                        break;
                    case ConsoleKey.D4:
                        await flight.Maneuver().Deorbit();
                        break;
                    default:
                        return;
                }
            }
        }

        private static async Task MainAsync(Options options)
        {
            using (var flight = new Flight())
            {
                var logger = LogManager.GetCurrentClassLogger();
                logger.Info($"Connected to KSP version {flight.Connection.KRPC().GetStatus().Version}");

                try
                {
                    logger.Info($"Current vessel: {flight.Connection.SpaceCenter().ActiveVessel.Name}");
                }
                catch (RPCException e)
                {
                    if (e.Message.Contains("Procedure not available"))
                    {
                        logger.Error("No active vessel");
                        Environment.Exit(1);
                    }
                    throw;
                }

                var names = flight.Vessel.Parts.All.Select(p => p.Name);
                logger.Debug($"Parts: {string.Join(", ", names)}");
                
                await StartMenu(flight);
            }
        }
    }
}