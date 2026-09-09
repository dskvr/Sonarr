using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace NzbDrone.Core.MediaFiles
{
    internal static class MediaFileOperationLock
    {
        private static readonly object[] Locks = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

        public static object ForSeries(int seriesId)
        {
            return Locks[(uint)seriesId % Locks.Length];
        }

        public static IDisposable Acquire(IEnumerable<int> seriesIds)
        {
            return new LockScope(seriesIds.Select(id => (int)((uint)id % Locks.Length)).Distinct().OrderBy(index => index).ToArray());
        }

        public static IDisposable AcquireAll()
        {
            return new LockScope(Enumerable.Range(0, Locks.Length).ToArray());
        }

        private sealed class LockScope : IDisposable
        {
            private readonly int[] _indexes;
            private int _acquired;

            public LockScope(int[] indexes)
            {
                _indexes = indexes;
                try
                {
                    foreach (var index in indexes)
                    {
                        Monitor.Enter(Locks[index]);
                        _acquired++;
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                while (_acquired > 0)
                {
                    Monitor.Exit(Locks[_indexes[--_acquired]]);
                }
            }
        }
    }
}
