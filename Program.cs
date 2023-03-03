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
        
        public void Launch()
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
                // ui.Message($"Launching in {5 - i} seconds");
                Thread.Sleep(1000);
            }

            LaunchConfig();

            vessel.Control.ActivateNextStage();

            var enabledStream = Connection.AddStream(() => ascent.Enabled);
            while (enabledStream.Get())
            {
                Thread.Sleep(1000);
            }
                
            Console.WriteLine("Launch complete, thanks Jeb");
        }
    }

    internal class UIPanel: IDisposable
    {
        private Connection connection;
        private Canvas canvas;
        private Panel panel;
        private CancellationTokenSource cancelToken;
        
        public UIPanel(Connection connection)
        {
            this.connection = connection;
            canvas = connection.UI().StockCanvas;
            panel = canvas.AddPanel();
            cancelToken = new CancellationTokenSource();
            
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                cancelToken.Cancel();
            };
        }

        public void Dispose()
        {
            Console.WriteLine("Running UIPanel Dispose");
            cancelToken.Cancel();
            panel.Remove();
        }
        
        public Task MakeUI()
        {
            // Get the size of the game window in pixels
            var screenSize = canvas.RectTransform.Size;

            // Position the panel on the left of the screen
            var rect = panel.RectTransform;
            rect.Size = Tuple.Create(200.0, 100.0);
            rect.Position = Tuple.Create((110 - (screenSize.Item1) / 2), 0.0);

            // Add a button to set the throttle to maximum
            var button = panel.AddButton("Full Throttle");
            button.RectTransform.Position = Tuple.Create(0.0, 20.0);

            // Add some text displaying the total engine thrust
            var text = panel.AddText("Thrust: 0 kN");
            text.RectTransform.Position = Tuple.Create(0.0, -20.0);
            text.Color = Tuple.Create(1.0, 1.0, 1.0);
            text.Size = 18;

            // Set up a stream to monitor the throttle button
            var buttonClicked = connection.AddStream(() => button.Clicked);

            var vessel = connection.SpaceCenter().ActiveVessel;
            var res = Task.Run(async () =>
            {
                while (!cancelToken.IsCancellationRequested)
                {
                    // Handle the throttle button being clicked
                    if (buttonClicked.Get())
                    {
                        vessel.Control.Throttle = 1;
                        button.Clicked = false;
                    }

                    // Update the thrust text
                    text.Content = "Thrust: " + (vessel.Thrust / 1000) + " kN";

                    await Task.Delay(1000, cancelToken.Token);
                }
            }, cancelToken.Token);

            return res;
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
            using (var flight = new Flight())
            {
                var logger = LogManager.GetCurrentClassLogger();
                logger.Info($"Connected to KSP version {flight.Connection.KRPC().GetStatus().Version}");
                logger.Info($"Current vessel: {flight.Connection.SpaceCenter().ActiveVessel.Name}");
                
                var names = flight.Vessel.Parts.All.Select(p => p.Name);
                logger.Debug($"Parts: {string.Join(", ", names)}");

                var pod = flight.Vessel.Parts.Controlling;
                logger.Info($"Controlling modules: {pod.Name}#{pod.id}");
                var podMods = pod.Modules.Select(m => m.Name);
                logger.Info($"{string.Join(", ", podMods)}");
                
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