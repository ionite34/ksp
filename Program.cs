using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using KRPC.Client;
using KRPC.Client.Services.KRPC;
using KRPC.Client.Services.MechJeb;
using KRPC.Client.Services.SpaceCenter;
using KRPC.Client.Services.UI;
using NLog;
using Service = KRPC.Client.Services.MechJeb.Service;

namespace ksp
{
    internal class Flight: IDisposable
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

        public void Dispose()
        {
            Connection.Dispose();
        }

        public void LaunchConfig()
        {
            // Activate all fuel cells
            var cells = Vessel.Parts.WithName("FuelCell");
            foreach (var cell in cells)
            {
                Logger.Debug($"Starting {cell.Name}#{cell.id}");
                cell.ResourceConverter?.Start(0);
            }
        }

        public void ArmLaunchEscape()
        {
            var control = Vessel.Control;
            var abort = Connection.AddStream(() => control.Abort);

            abort.AddCallback(async x =>
            {
                Logger.Warn($"Abort action triggered");
                await ActivateLaunchEscape();
            });

            abort.Start();
        }

        public async Task ActivateLaunchEscape()
        {
            var pod = Vessel.Parts.Controlling;
            
            var names = pod.Modules.Select(p => p.Name);
            Logger.Info($"Parts 2: {string.Join(", ", names)}");

            var podEngine = pod.Modules.First(m => m.Name.Equals("ModuleEnginesFX"));
            Logger.Debug($"Found abort engine: {podEngine.Name}#{podEngine.id}");
         
            // First set throttle to 0
            Vessel.Control.Throttle = 0;
            
            // Disengage decouplers
            try
            {
                var decouplers = Vessel.Parts.WithTag("LES_DC");
                foreach (var decoupler in decouplers)
                {
                    Logger.Debug($"Disengaging {decoupler.Name}#{decoupler.id}");
                    decoupler.Decoupler?.Decouple();
                }
            }
            catch (Exception e)
            {
                Logger.Error(e);
            }

            // Fire solid engine 1
            var solids = Vessel.Parts.WithTag("LES_SOLID");
            solids[0].Engine.Active = true;

            // Set throttle to full
            podEngine.SetAction("Toggle Independent Throttle", false);
            Vessel.Control.Throttle = 1;
            
            // Trigger pod engines
            pod.Engine.Active = true;

            // Open shroud, if equipped
            var shroud = pod.Modules.FirstOrDefault(m => m.Name.Equals("ModuleAnimateGeneric"));
            shroud?.TriggerEvent("Open Shroud");

            // Slight Normal burn
            Mechjeb.SmartASS.AutopilotMode = SmartASSAutopilotMode.NormalPlus;
            await Task.Delay(100);

            // Fire second solid
            solids[1].Engine.Active = true;
            
            // Override a retrograde surface burn if we're very close to the ground
            if (Vessel.Flight(Vessel.SurfaceReferenceFrame).SurfaceAltitude < 1000)
            {
                await Task.Delay(300);
                Mechjeb.SmartASS.AutopilotMode = SmartASSAutopilotMode.SurfaceRetrograde;
                await Task.Delay(700);
            }
            else
            {
                await Task.Delay(800);
            }

            Mechjeb.SmartASS.AutopilotMode = SmartASSAutopilotMode.SurfacePrograde;
            await Task.Delay(1600);

            // Switch to AP climb at 80% throttle
            Mechjeb.SmartASS.AutopilotMode = SmartASSAutopilotMode.RadialPlus;
            await Task.Delay(2000);
            Vessel.Control.Throttle = 0.8f;
            await Task.Delay(500);
            
            // Switch to AP prograde
            Mechjeb.SmartASS.AutopilotMode = SmartASSAutopilotMode.Prograde;
            Vessel.Control.Throttle = 1;
            await Task.Delay(500);
            
            // Quick burn retrograde to surface
            Mechjeb.SmartASS.AutopilotMode = SmartASSAutopilotMode.SurfaceRetrograde;
            Vessel.Control.Throttle = 0.8f;
            await Task.Delay(500);
            
            // Cut throttle
            Vessel.Control.Throttle = 0;

            // Release drogue chutes, if equipped
            var drogues = Vessel.Parts.All.Where(p =>p.Name.ToLower().Contains("drogue")).ToList();
            foreach (var drogue in drogues)
            {
                Logger.Info($"Releasing {drogue.Name}#{drogue.id}");
                // drogue.Parachute.Deploy();
                var drogueMod = drogue.Modules.First(m => m.Name.ToLower().Contains("chute"));
                drogueMod.SetAction("Deploy chute");
            }
            
            // Deploy main chutes
            var mains = Vessel.Parts.All.Where(p => p.Name.ToLower().Contains("chute"));
            foreach (var main in mains)
            {
                Logger.Info($"Deploying {main.Name}#{main.id}");
                var mainMod = main.Modules.First(m => m.Name.ToLower().Contains("chute"));
                mainMod.SetAction("Deploy chute");
            }
            
            // Switch to land somewhere
            var landing = Mechjeb.LandingAutopilot;
            landing.TouchdownSpeed = 1.0;
            landing.LandUntargeted();
            landing.Enabled = true;

            // Deploy gears
            Vessel.Control.Gear = true;
            
            // If we're slow enough, cut drogues
            var speed = Connection.AddStream(() => Vessel.Flight(Vessel.SurfaceReferenceFrame).Speed);
            while (speed.Get() > 200)
            {
                await Task.Delay(500);
            }

            // Cut drogues
            foreach (var drogue in drogues)
            {
                Logger.Info($"Cutting {drogue.Name}#{drogue.id}");
                var drogueMod = drogue.Modules.First(m => m.Name.ToLower().Contains("chute"));
                drogueMod.SetAction("Cut chute");
            }
        }
        
        public async Task Launch()
        {
            var ascent = Mechjeb.AscentAutopilot;

            ascent.DesiredOrbitAltitude = 95_000;
            ascent.DesiredInclination = 6;
            ascent.ForceRoll = true;
            ascent.VerticalRoll = 90;
            ascent.TurnRoll = 90;

            ascent.Autostage = true;
            ascent.Enabled = true;

            var vessel = Connection.SpaceCenter().ActiveVessel;
            var ui = Connection.UI();
            foreach (var i in Enumerable.Range(1, 5))
            {
                ui.Message($"Launching in {5 - i} seconds");
                await Task.Delay(1000);
            }

            LaunchConfig();

            vessel.Control.ActivateNextStage();
            
            await Connection.WaitFor(() => ascent.Enabled, false);

            Console.WriteLine("Launch complete, thanks Jeb");
        }
    }

    internal class Program
    {
        public static void Main(string[] args)
        {
            new Program().MainAsync().GetAwaiter().GetResult();
        }

        private async Task MainAsync()
        {
            // Prompt
            Console.WriteLine("Press any key to connect to KSP");
            Console.ReadKey();
            
            using (var flight = new Flight())
            {
                var logger = LogManager.GetCurrentClassLogger();
                logger.Info($"Connected to KSP version {flight.Connection.KRPC().GetStatus().Version}");
                logger.Info($"Current vessel: {flight.Connection.SpaceCenter().ActiveVessel.Name}");
                
                var names = flight.Vessel.Parts.All.Select(p => p.Name);
                logger.Debug($"Parts: {string.Join(", ", names)}");
                
                var box = new UIPanel(flight.Connection);
                box.AddButton("Cancel", () => Environment.Exit(0));
                box.Visible = true;
                
                // Detect abort
                var vessel = flight.Vessel;
                var stream = flight.Connection.AddStream(() => vessel.Control.Abort);

                stream.AddCallback((x) =>
                {
                    Console.WriteLine($"Abort called {x}");
                    if (x)
                    {
                        flight.ActivateLaunchEscape().Wait();
                    }
                });

                logger.Info("Starting Get");
                stream.Get();

                var ui = flight.Connection.UI();
                foreach (var i in Enumerable.Range(1, 65))
                {
                    // ui.Message($"LES Test in {15 - i} seconds");
                    logger.Info($"LES Test in {65 - i} seconds");
                    await Task.Delay(1000);
                }
                
                // flight.ArmLaunchEscape();
                // await flight.ActivateLaunchEscape();

                // flight.Launch();
            }
        }
    }
}