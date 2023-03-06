using System;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using KRPC.Client;
using KRPC.Client.Services.SpaceCenter;
using NLog;
using Service = KRPC.Client.Services.MechJeb.Service;

namespace ksp
{
    public class Burn
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly Connection _connection;
        private readonly float _throttleMax;
        private readonly float _throttleMin;
        private readonly CancellationToken _ctsToken;

        public Burn(Connection connection, float throttleMax = 1f, float throttleMin = 0.05f,
            CancellationToken ctsToken = default)
        {
            if (throttleMax > 1 || throttleMax < 0 || throttleMin > 1 || throttleMin < 0)
                throw new ArgumentOutOfRangeException(nameof(throttleMax), "Throttle must be between 0 and 1");
            _connection = connection;
            _throttleMax = throttleMax;
            _throttleMin = throttleMin;
            _ctsToken = ctsToken;
        }

        /// <summary>
        ///     Burn until the conditional expression is true
        /// </summary>
        /// <param name="expression"></param>
        public async Task Until<T>(Expression<Func<bool>> expression)
        {
            var exprBody = (BinaryExpression) expression.Body;

            // Compile expression
            var compiled = expression.Compile();

            // Either expressions must be (property) <op> (constant)
            var propertyExpr = (MemberExpression) exprBody.Left;
            var constantExpr = (MemberExpression) exprBody.Right;
            var target = Convert.ToSingle(Expression.Lambda(constantExpr).Compile().DynamicInvoke());

            // Convert to lambda to get a stream
            var converted = Expression.Lambda<Func<T>>(propertyExpr);
            var stream = _connection.AddStream(converted);

            // Initial delta, throttle tapers off as delta approaches 0
            var initialDelta = Math.Abs(target - Convert.ToSingle(stream.Get()));

            // Do Burn
            var vesselControl = _connection.SpaceCenter().ActiveVessel.Control;
            try
            {
                while (!_ctsToken.IsCancellationRequested)
                {
                    lock (stream.Condition)
                    {
                        // When the expression is true, stop immediately
                        if (compiled()) break;
                        var streamValue = Convert.ToSingle(stream.Get());
                        var diff = Math.Abs(target - streamValue);
                        // Calculate throttle
                        var throttle = Math.Min(_throttleMax, Math.Max(_throttleMin, diff / initialDelta));
                        vesselControl.Throttle = throttle;
                    }

                    await Task.Delay(50, _ctsToken);
                }
            }
            finally
            {
                vesselControl.Throttle = 0;
            }
        }
        
        /// <summary>
        ///     Burn until the conditional expression is true
        /// </summary>
        /// <param name="expression"></param>
        public async Task UntilPID<T>(Expression<Func<bool>> expression)
        {
            var exprBody = (BinaryExpression) expression.Body;

            // Compile expression
            var compiledFunc = expression.Compile();

            // Either expressions must be (property) <op> (constant)
            var propertyExpr = (MemberExpression) exprBody.Left;
            var constantExpr = (MemberExpression) exprBody.Right;
            var target = Convert.ToSingle(Expression.Lambda(constantExpr).Compile().DynamicInvoke());

            // Convert to lambda to get a stream
            var converted = Expression.Lambda<Func<T>>(propertyExpr);
            var stream = _connection.AddStream(converted);
            stream.Rate = 200;  // 5ms
            stream.Start(wait: true);

            // Controller
            // var pid = new PID(0.25, 0.025, 0.0025, 200, _throttleMax, _throttleMin);
            var pid = new PID(0.25, 0.025, 0.0025, 1, 1, 0);
            var samplePeriod = TimeSpan.FromMilliseconds(10);
            var pidTimer = new Stopwatch();
            pidTimer.Start();
            var iterTimer = new Stopwatch();

            // Do Burn
            var vesselControl = _connection.SpaceCenter().ActiveVessel.Control;
            var lastThrottle = 0f;
            try
            {
                var iters = 0;
                while (!_ctsToken.IsCancellationRequested)
                {
                    var logCycle = iters % 100 <= 1;
                    iterTimer.Restart();
                    // When the expression is true, stop immediately
                    if (compiledFunc()) break;
                    var streamValue = Convert.ToSingle(stream.Get());

                    // Calculate throttle with PID
                    // ts is fixed to samplePeriod if this is the first iteration
                    var ts = iters == 0 ? samplePeriod : pidTimer.Elapsed;
                    
                    if (logCycle) Logger.Debug($"Iter[{iters}] ElapsedTS: {ts.TotalMilliseconds:0.0}ms");

                    var throttle = Convert.ToSingle(pid.PID_iterate(target, streamValue, ts));
                    pidTimer.Restart();
                    
                    if (logCycle)
                    {
                        Logger.Debug($"-> PID out: {throttle:0.0000} | Target: {target:0.0000} | Current: {streamValue:0.0000}");
                        Logger.Debug($"-> Setting throttle, time in cycle: {iterTimer.Elapsed.TotalMilliseconds:0.0}ms");
                    }

                    // Skip throttle if it's within 5% of last
                    if (Math.Abs(throttle - lastThrottle) > 0.05)
                    {
                        vesselControl.Throttle = throttle;
                        lastThrottle = throttle;   
                    }

                    var timeToWait = samplePeriod - iterTimer.Elapsed;
                    if (logCycle)
                    {
                        Logger.Debug($"-> Calling task delay now, time in cycle: {iterTimer.Elapsed.TotalMilliseconds:0.0}ms");
                        Logger.Debug($"-> Delaying for {timeToWait.TotalMilliseconds:0.0}ms");
                    }
                    if (timeToWait > TimeSpan.Zero)
                    {
                        await Task.Delay(timeToWait, _ctsToken);
                    }
                    iters += 1;
                }
            }
            finally
            {
                vesselControl.Throttle = 0;
            }
        }
    }

    public class Maneuver
    {
        public readonly Connection Connection;
        public readonly Service Mechjeb;
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly CancellationTokenSource cts;

        public Maneuver(Connection connection, Service mechjeb, CancellationTokenSource cts = default)
        {
            Connection = connection;
            Mechjeb = mechjeb;
            this.cts = cts;
        }

        public Maneuver(Flight flight, CancellationTokenSource cts = default)
        {
            Connection = flight.Connection;
            Mechjeb = flight.Mechjeb;
            this.cts = cts;
        }

        public Burn Burn(float throttleMax = 1f, float throttleMin = 0.05f)
        {
            return new Burn(Connection, throttleMax, throttleMin, cts?.Token ?? CancellationToken.None);
        }

        public async Task Deorbit(double eccentricity = 0.1)
        {
            var ctsToken = cts?.Token ?? CancellationToken.None;
            Logger.Info("Starting Deorbit");
            var vessel = Connection.SpaceCenter().ActiveVessel;

            var ap = vessel.AutoPilot;
            var referenceFrame = vessel.OrbitalReferenceFrame;
            ap.ReferenceFrame = referenceFrame;
            ap.TargetDirection = Tuple.Create(0.0, -1.0, 0.0); // Retrograde

            vessel.Control.SpeedMode = SpeedMode.Orbit;
            ap.SAS = true;
            await Connection.WaitFor(() => ap.SAS, true, ctsToken);
            ap.SASMode = SASMode.Retrograde;

            await vessel.WaitForDirection(vessel.OrbitalReferenceFrame, ap.TargetDirection, 0.05);

            // while (!ctsToken.IsCancellationRequested &&
            //       (vessel.Orbit.Eccentricity > eccentricity || vessel.Orbit.PeriapsisAltitude > 0))
            //    await Task.Delay(1000, ctsToken);

            // await Burn(() => vessel.Orbit.Eccentricity > eccentricity || vessel.Orbit.PeriapsisAltitude > 0);
            // await Burn().Until(vessel.Orbit.Eccentricity > eccentricity).Or(vessel.Orbit.PeriapsisAltitude > 0);

            Logger.Info(
                $"Starting Burn until orbit eccentricity {vessel.Orbit.Eccentricity:0.00} > {eccentricity:0.00}");

            await Burn().UntilPID<double>(() => vessel.Orbit.Eccentricity > eccentricity);

            Logger.Info($"Finished Burn, current orbit eccentricity = {vessel.Orbit.Eccentricity:0.00}");

            vessel.Control.Throttle = 0;
        }
    }
}