using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using KRPC.Client;
using KRPC.Client.Services.SpaceCenter;

namespace ksp
{
    public static class Extensions
    {
        public static T WaitFor<T>(this Stream<T> stream, Func<T, bool> condition, CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                lock (stream.Condition)
                {
                    var value = stream.Get();
                    if (condition(value))
                    {
                        stream.Remove();
                        return value;
                    }
                    stream.Wait(100);
                }
            }
            throw new TaskCanceledException();
        }

        public static async Task WaitFor<T>(this Connection connection, Expression<Func<T>> expression, Func<T, bool> condition, CancellationToken token = default)
        {
            var stream = connection.AddStream(expression);
            await Task.Run(() => stream.WaitFor(condition), token);
        }
        
        public static async Task WaitFor<T>(this Connection connection, Expression<Func<T>> expression, T value, CancellationToken token = default) where T: IEquatable<T>
        {
            await WaitFor(connection, expression, m => m.Equals(value), token);
        }
    }
    
    public class Debouncer : IDisposable
    {
        private Thread thread;
        private volatile Action action;
        private volatile int delay = 0;
        private volatile int frequency;

        public void Debounce(Action action, int delay = 250, int frequency = 10)
        {
            this.action = action;
            this.delay = delay;
            this.frequency = frequency;

            if (this.thread == null)
            {
                this.thread = new Thread(() => this.RunThread());
                this.thread.IsBackground = true;
                this.thread.Start();
            }
        }

        private void RunThread()
        {
            while (true)
            {
                this.delay -= this.frequency;
                Thread.Sleep(this.frequency);

                if (this.delay <= 0 && this.action != null)
                {
                    this.action();
                    this.action = null;
                }
            }
        }

        public void Dispose()
        {
            if (this.thread != null)
            {
                this.thread.Abort();
                this.thread = null;
            }
        }
    }
}