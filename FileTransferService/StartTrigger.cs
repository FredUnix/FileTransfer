using System.Threading;
using System.Threading.Tasks;

namespace FileTransfer
{
    public interface IStartTrigger
    {
        /// <summary>Blocks until Signal() is called or cancellation is requested.</summary>
        Task WaitAsync(CancellationToken ct);

        /// <summary>Releases all waiters. Idempotent — safe to call more than once.</summary>
        void Signal();
    }

    public sealed class StartTrigger : IStartTrigger
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsSignalled => _tcs.Task.IsCompletedSuccessfully;

        public Task WaitAsync(CancellationToken ct)
        {
            if (ct.CanBeCanceled)
            {
                // Complete early if cancellation fires before Signal()
                var reg = ct.Register(() => _tcs.TrySetCanceled(ct));
                return _tcs.Task.ContinueWith(_ => reg.Dispose(), TaskScheduler.Default);
            }
            return _tcs.Task;
        }

        public void Signal() => _tcs.TrySetResult();
    }
}
