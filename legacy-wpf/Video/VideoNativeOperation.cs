using Windows.Foundation;
using SnipFlow.Services;

namespace SnipFlow.Video;

/// <summary>Cancellation requests native cancellation, then waits for actual terminal status.</summary>
internal static class VideoNativeOperation
{
    internal static async Task<T> AwaitAsync<T>(IAsyncOperation<T> operation, CancellationToken token)
    {
        using var registration = token.Register(() => CancelSafely(operation));
        try
        {
            // CsWinRT AsTask(token) can cancel its managed task before native I/O ends.
            var result = await operation.AsTask();
            if (token.IsCancellationRequested)
            {
                if (result is IDisposable disposable) disposable.Dispose();
                token.ThrowIfCancellationRequested();
            }
            return result;
        }
        finally
        {
            registration.Dispose();
            await FinishAsync(operation);
        }
    }

    internal static async Task AwaitAsync(IAsyncAction operation, CancellationToken token)
    {
        using var registration = token.Register(() => CancelSafely(operation));
        try { await operation.AsTask(); token.ThrowIfCancellationRequested(); }
        finally { registration.Dispose(); await FinishAsync(operation); }
    }

    internal static async Task<T> AwaitAsync<T>(IAsyncOperationWithProgress<T, double> operation,
        CancellationToken token, IProgress<double>? progress)
    {
        using var registration = token.Register(() => CancelSafely(operation));
        try
        {
            var result = await operation.AsTask(CancellationToken.None, progress);
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally { registration.Dispose(); await FinishAsync(operation); }
    }

    private static async Task FinishAsync(IAsyncInfo operation)
    {
        try
        {
            if (operation.Status == AsyncStatus.Started)
            {
                CancelSafely(operation);
                while (operation.Status == AsyncStatus.Started) await Task.Delay(50);
            }
        }
        finally { operation.Close(); }
    }

    private static void CancelSafely(IAsyncInfo operation)
    {
        try { operation.Cancel(); }
        catch (Exception error)
        {
            try { SettingsStore.Log(error); }
            catch (Exception) { }
        }
    }
}
