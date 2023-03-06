using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KRPC.Client;
using KRPC.Client.Services.MechJeb;
using KRPC.Client.Services.SpaceCenter;
using KRPC.Client.Services.UI;
using NLog;
using ShellProgressBar;
using Service = KRPC.Client.Services.MechJeb.Service;

namespace ksp
{
    public class Launch
    {
        public readonly Connection Connection;
        public readonly Service Mechjeb;
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly Vessel vessel;

        private Part abortPod;
        private Module abortPodShroud;
        private Module abortPodEngineModule;
        private List<Part> abortSolidEngines;
        
        public Launch(Connection connection, Service mechjeb)
        {
            Connection = connection;
            Mechjeb = mechjeb;
            vessel = connection.SpaceCenter().ActiveVessel;
        }
        
        public Launch(Flight flight)
        {
            Connection = flight.Connection;
            Mechjeb = flight.Mechjeb;
            vessel = flight.Vessel;
        }

        private void LaunchConfig()
        {
            // Activate all fuel cells
            var cells = vessel.Parts.WithName("FuelCell");
            foreach (var cell in cells)
            {
                Logger.Debug($"Starting {cell.Name}#{cell.id}");
                cell.ResourceConverter?.Start(0);
            }
        }
        
        public void PrepareLaunchEscape()
        {
            abortPod = vessel.Parts.Controlling;
            var podEngine = abortPod.Modules.First(m => m.Name.Equals("ModuleEnginesFX"));
            Logger.Debug($"Found pod abort engine: {podEngine.Name}#{podEngine.id}");
            abortPodEngineModule = podEngine;
            
            // Find shroud
            abortPodShroud = abortPod.Modules.FirstOrDefault(m => m.Name.Equals("ModuleAnimateGeneric"));

            // Find solid engines
            abortSolidEngines = vessel.Parts.WithTag("LES_SOLID").ToList();
            Logger.Debug($"Found solid abort engines: {string.Join(", ", abortSolidEngines.Select(p => p.Name))}");
        }

        public async Task ActivateLaunchEscape()
        {
            Logger.Debug("In ActivateLaunchEscape");
            
            // Cancel autopilots, if running
            Mechjeb.AscentAutopilot.Autostage = false;
            Mechjeb.AscentAutopilot.Enabled = false;
            vessel.AutoPilot.Disengage();
            vessel.AutoPilot.SAS = false;
            
            // var names = pod.Modules.Select(p => p.Name);
            // Logger.Info($"Parts 2: {string.Join(", ", names)}");

            // First set throttle to 0
            vessel.Control.Throttle = 0;
            
            // Disengage decouplers
            try
            {
                var decouplers = vessel.Parts.WithTag("LES_DC");
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

            // Fire solid engines
            foreach (var engine in abortSolidEngines)
            {
                engine.Engine.Active = true;
            }

            // Set throttle to full
            vessel.Control.Throttle = 1;
            
            // Trigger pod engines
            abortPodEngineModule.Part.Engine.Active = true;

            // Open shroud, if equipped
            abortPodShroud?.TriggerEvent("Open Shroud");

            // Slight Normal burn
            Mechjeb.SmartASS.AutopilotMode = SmartASSAutopilotMode.NormalMinus;
            await Task.Delay(300);

            // Override a retrograde surface burn if we're very close to the ground
            if (vessel.Flight(vessel.SurfaceReferenceFrame).SurfaceAltitude < 1000)
            {
                Mechjeb.SmartASS.AutopilotMode = SmartASSAutopilotMode.SurfaceRetrograde;
                await Task.Delay(700);
            }

            // Surface prograde
            vessel.AutoPilot.ReferenceFrame = vessel.SurfaceVelocityReferenceFrame;
            vessel.AutoPilot.SASMode = SASMode.Prograde;
            vessel.AutoPilot.SAS = true;
            vessel.Control.Throttle = 1;
            await Task.Delay(400);

            // Switch to AP prograde
            // Mechjeb.SmartASS.AutopilotMode = SmartASSAutopilotMode.Prograde;
            vessel.AutoPilot.ReferenceFrame = vessel.OrbitalReferenceFrame;
            vessel.AutoPilot.SASMode = SASMode.Prograde;
            vessel.AutoPilot.SAS = true;
            vessel.Control.Throttle = 1;
            await Task.Delay(2000);
            vessel.AutoPilot.SASMode = SASMode.StabilityAssist;
            await Task.Delay(1200);
                
            // Quick burn retrograde to surface
            // Mechjeb.SmartASS.AutopilotMode = SmartASSAutopilotMode.SurfaceRetrograde;
            vessel.AutoPilot.ReferenceFrame = vessel.SurfaceVelocityReferenceFrame;
            vessel.AutoPilot.SASMode = SASMode.Retrograde;
            vessel.Control.Throttle = 0.8f;
            await Task.Delay(550);
            
            // Cut throttle
            vessel.Control.Throttle = 0;

            // Release drogue chutes, if equipped
            var drogues = vessel.Parts.All.Where(p =>p.Name.ToLower().Contains("drogue")).ToList();
            foreach (var drogue in drogues)
            {
                Logger.Info($"Releasing {drogue.Name}#{drogue.id}");
                // drogue.Parachute.Deploy();
                var drogueMod = drogue.Modules.First(m => m.Name.ToLower().Contains("chute"));
                drogueMod.SetAction("Deploy chute");
            }
            
            // Deploy main chutes
            var mains = vessel.Parts.All.Where(p => p.Name.ToLower().Contains("chute"));
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
            vessel.Control.Gear = true;
            
            // If we're slow enough, cut drogues
            var speed = Connection.AddStream(() => vessel.Flight(vessel.SurfaceReferenceFrame).Speed);
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
        
        public async Task Run(CancellationToken ct = default, int countdown = 10)
        {
            ct.ThrowIfCancellationRequested();

            var ascent = Mechjeb.AscentAutopilot;
            ascent.DesiredOrbitAltitude = 95_000;
            ascent.DesiredInclination = 6;
            ascent.ForceRoll = true;
            ascent.VerticalRoll = 90;
            ascent.TurnRoll = 90;
            ascent.Autostage = true;
            ascent.Enabled = true;
            
            var ui = Connection.UI();
            ct.ThrowIfCancellationRequested();

            const int totalTicks = 10;
            var options = new ProgressBarOptions
            {
                ProgressCharacter = '─',
                ProgressBarOnBottom = true
            };
            using (var countdownBar = new ProgressBar(totalTicks, "Launch countdown", options))
            {
                foreach (var i in Enumerable.Range(1, countdown))
                {
                    ct.ThrowIfCancellationRequested();
                    countdownBar.Tick();
                    // countdownBar.Tick("Step 2 of 10");
                    ui.Message($"Launching in T-{countdown - i}s");
                    await Task.Delay(1000, ct);
                }
            }

            LaunchConfig();
            ct.ThrowIfCancellationRequested();

            vessel.Control.ActivateNextStage();
            ct.ThrowIfCancellationRequested();
            
            await Connection.WaitFor(() => ascent.Enabled, false, ct);

            Console.WriteLine("Launch complete, thanks Jeb");
        }

        public async Task RunWithAbort()
        {
            var tokenSource = new CancellationTokenSource();
            var ct = tokenSource.Token;
            
            // Preload launch escape sequence
            PrepareLaunchEscape();
            
            // Cancel if abort
            var control = vessel.Control;
            var abort = Connection.AddStream(() => control.Abort);
            abort.AddCallback(x =>
            {
                if (!x) return;
                Logger.Warn($"Abort action triggered");
                tokenSource.Cancel();
                Task.WaitAll(ActivateLaunchEscape());
            });
            abort.Start();
            
            // Launch
            try
            {
                await Run(ct);
            }
            catch (OperationCanceledException)
            {
                Logger.Warn($"Launch canceled");
                var ui = Connection.UI();
                ui.Message("Launch canceled");
            }
        }
        
    }
}