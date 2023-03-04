using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using KRPC.Client;

namespace ksp
{
    public static class Extensions
    {
        public static T WaitFor<T>(this Stream<T> stream, Func<T, bool> condition)
        {
            lock (stream.Condition)
            {
                while (true)
                {
                    var value = stream.Get();
                    if (condition(value)) return value;
                    stream.Wait();
                }
            }
        }

        public static async Task WaitFor<T>(this Connection connection, Expression<Func<T>> expression, Func<T, bool> condition)
        {
            var stream = connection.AddStream(expression);
            await Task.Run(() => stream.WaitFor(condition));
        }
        
        public static async Task WaitFor<T>(this Connection connection, Expression<Func<T>> expression, T value) where T: IEquatable<T>
        {
            await WaitFor(connection, expression, m => m.Equals(value));
        }
    }
}