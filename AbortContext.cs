using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using KRPC.Client;

namespace ksp
{
    internal class AbortContext<T>
    {
        public bool WasAborted { get; private set; }
        private CancellationTokenSource Canceller { get; set; }
        private Task<object> Worker { get; set; }

        private readonly Connection connection;
        private readonly Expression<Func<T>> expression;
        private readonly Func<T, bool> condition;
        private readonly Action onAbort;

        public AbortContext(Connection connection, Expression<Func<T>> expression, Func<T, bool> condition, Action onAbort = null)
        {
            this.connection = connection;
            this.expression = expression;
            this.condition = condition;
            this.onAbort = onAbort;
        }

        private async Task CheckAbort(CancellationToken token, int delayMilliseconds = 50)
        {
            var stream = connection.AddStream(expression);

            try
            {
                var task = Task.Run(() => stream.WaitFor(condition), token);

                // Check abort condition 
                while (!token.IsCancellationRequested)
                {
                    if (task.IsCompleted)
                    {
                        // If task completes, trigger abort
                        Abort();
                        return;
                    }
                    await Task.Delay(delayMilliseconds, token);
                }
            }
            finally
            {
                stream.Remove();
            }
        }

        public void Start(Func<object> func)
        {
            WasAborted = false;

            // start abort check
            var abortCancel = new CancellationTokenSource();
            var abortTask = CheckAbort(abortCancel.Token);
            
            // start a task with a means to do a hard abort
            Canceller = new CancellationTokenSource();
            Worker = Task.Factory.StartNew(() =>
            {
                try
                {
                    // specify this thread's Abort() as the cancel delegate
                    using (Canceller.Token.Register(Thread.CurrentThread.Abort))
                    {
                        var res = func();
                        // Finished without being aborted, stop checking
                        abortCancel.Cancel();
                        return res;
                    }
                }
                catch (ThreadAbortException)
                {
                    WasAborted = true;
                    return false;
                }
            }, Canceller.Token);
        }

        public void Abort()
        {
            Canceller.Cancel();
            onAbort?.Invoke();
        }
    }
}