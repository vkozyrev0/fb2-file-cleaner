using System.Threading;
using System.Threading.Tasks;

namespace Fb2CleanerApp.Models
{
    public static class Statistics
    {
        private static readonly SemaphoreSlim ErrorCountLocker = new SemaphoreSlim(1);
        private static readonly SemaphoreSlim FixedCountLocker = new SemaphoreSlim(1);
        private static readonly SemaphoreSlim DeletedCountLocker = new SemaphoreSlim(1);

        public static int ParsingErrorCount { get; internal set; }
        public static int FixedCount { get; internal set; }
        public static int DeletedCount { get; internal set; }

        public static async Task IncrementPersinFileErrorCount()
        {
            try
            {
                await ErrorCountLocker.WaitAsync();
                ParsingErrorCount++;
            }
            finally
            {
                ErrorCountLocker.Release();
            }
        }
        public static async Task IncrementFixedFilesCount()
        {
            try
            {
                await FixedCountLocker.WaitAsync();
                FixedCount++;
            }
            finally
            {
                FixedCountLocker.Release();
            }
        }
        public static async Task IncrementDeletedFilesCount()
        {
            try
            {
                await DeletedCountLocker.WaitAsync();
                DeletedCount++;
            }
            finally
            {
                DeletedCountLocker.Release();
            }
        }
    }
}
