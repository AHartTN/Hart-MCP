using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Hart.MCP.Core.Services.Optimized;

/// <summary>
/// High-performance batch processor for atom operations.
/// Uses channels for backpressure and parallel processing.
/// </summary>
/// <typeparam name="TInput">Input type</typeparam>
/// <typeparam name="TOutput">Output type</typeparam>
public sealed class BatchProcessor<TInput, TOutput> : IAsyncDisposable
{
    private readonly Channel<BatchItem> _channel;
    private readonly Func<TInput[], CancellationToken, Task<TOutput[]>> _processor;
    private readonly int _batchSize;
    private readonly TimeSpan _batchTimeout;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cts;
    private int _pendingCount;
    
    private readonly record struct BatchItem(TInput Input, TaskCompletionSource<TOutput> Completion);

    /// <summary>
    /// Create a new batch processor
    /// </summary>
    /// <param name="processor">Function to process a batch of inputs</param>
    /// <param name="batchSize">Maximum batch size (default 100)</param>
    /// <param name="batchTimeout">Maximum time to wait for batch to fill (default 10ms)</param>
    /// <param name="maxPending">Maximum pending items (backpressure, default 10000)</param>
    public BatchProcessor(
        Func<TInput[], CancellationToken, Task<TOutput[]>> processor,
        int batchSize = 100,
        TimeSpan? batchTimeout = null,
        int maxPending = 10000)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _batchSize = batchSize;
        _batchTimeout = batchTimeout ?? TimeSpan.FromMilliseconds(10);
        _cts = new CancellationTokenSource();
        
        _channel = Channel.CreateBounded<BatchItem>(new BoundedChannelOptions(maxPending)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        
        _processingTask = ProcessBatchesAsync(_cts.Token);
    }

    /// <summary>
    /// Submit item for batch processing
    /// </summary>
    public async Task<TOutput> ProcessAsync(TInput input, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<TOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        
        using var registration = cancellationToken.Register(() => 
            tcs.TrySetCanceled(cancellationToken));
        
        await _channel.Writer.WriteAsync(new BatchItem(input, tcs), cancellationToken);
        Interlocked.Increment(ref _pendingCount);
        
        return await tcs.Task;
    }

    /// <summary>
    /// Submit multiple items for batch processing
    /// </summary>
    public async Task<TOutput[]> ProcessManyAsync(TInput[] inputs, CancellationToken cancellationToken = default)
    {
        var tasks = new Task<TOutput>[inputs.Length];
        for (int i = 0; i < inputs.Length; i++)
        {
            tasks[i] = ProcessAsync(inputs[i], cancellationToken);
        }
        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Number of items waiting to be processed
    /// </summary>
    public int PendingCount => _pendingCount;

    private async Task ProcessBatchesAsync(CancellationToken cancellationToken)
    {
        var batch = new List<BatchItem>(_batchSize);
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                batch.Clear();
                
                // Wait for first item
                if (await _channel.Reader.WaitToReadAsync(cancellationToken))
                {
                    // Collect batch with timeout
                    using var timeoutCts = new CancellationTokenSource(_batchTimeout);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, timeoutCts.Token);
                    
                    while (batch.Count < _batchSize)
                    {
                        if (_channel.Reader.TryRead(out var item))
                        {
                            batch.Add(item);
                        }
                        else if (batch.Count > 0)
                        {
                            // Wait briefly for more items
                            try
                            {
                                if (await _channel.Reader.WaitToReadAsync(linkedCts.Token))
                                    continue;
                            }
                            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                            {
                                break; // Timeout - process what we have
                            }
                        }
                        else
                        {
                            break; // No items available
                        }
                    }
                }
                
                if (batch.Count > 0)
                {
                    await ProcessBatchInternalAsync(batch, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Complete all pending items with the exception
                foreach (var item in batch)
                {
                    item.Completion.TrySetException(ex);
                    Interlocked.Decrement(ref _pendingCount);
                }
            }
        }
        
        // Drain remaining items on shutdown
        while (_channel.Reader.TryRead(out var item))
        {
            item.Completion.TrySetCanceled();
            Interlocked.Decrement(ref _pendingCount);
        }
    }

    private async Task ProcessBatchInternalAsync(List<BatchItem> batch, CancellationToken cancellationToken)
    {
        var inputs = batch.Select(b => b.Input).ToArray();
        
        try
        {
            var outputs = await _processor(inputs, cancellationToken);
            
            if (outputs.Length != inputs.Length)
            {
                throw new InvalidOperationException(
                    $"Processor returned {outputs.Length} outputs for {inputs.Length} inputs");
            }
            
            for (int i = 0; i < batch.Count; i++)
            {
                batch[i].Completion.TrySetResult(outputs[i]);
                Interlocked.Decrement(ref _pendingCount);
            }
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            foreach (var item in batch)
            {
                item.Completion.TrySetCanceled(ex.CancellationToken);
                Interlocked.Decrement(ref _pendingCount);
            }
        }
        catch (Exception ex)
        {
            foreach (var item in batch)
            {
                item.Completion.TrySetException(ex);
                Interlocked.Decrement(ref _pendingCount);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.Complete();
        await _cts.CancelAsync();
        
        try
        {
            await _processingTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        
        _cts.Dispose();
    }
}

/// <summary>
/// Lock-free object pool for reducing allocations
/// </summary>
/// <typeparam name="T">Pooled type</typeparam>
public sealed class ObjectPool<T> where T : class
{
    private readonly ConcurrentBag<T> _pool;
    private readonly Func<T> _factory;
    private readonly Action<T>? _reset;
    private readonly int _maxSize;
    private int _count;

    public ObjectPool(Func<T> factory, Action<T>? reset = null, int maxSize = 1000)
    {
        _pool = new ConcurrentBag<T>();
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _reset = reset;
        _maxSize = maxSize;
    }

    public T Rent()
    {
        if (_pool.TryTake(out var item))
        {
            Interlocked.Decrement(ref _count);
            return item;
        }
        return _factory();
    }

    public void Return(T item)
    {
        if (Interlocked.Increment(ref _count) <= _maxSize)
        {
            _reset?.Invoke(item);
            _pool.Add(item);
        }
        else
        {
            Interlocked.Decrement(ref _count);
            // Let GC collect it
        }
    }

    public int Count => _count;
}

/// <summary>
/// Async semaphore with timeout support for concurrency limiting
/// </summary>
public sealed class AsyncSemaphore : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public AsyncSemaphore(int initialCount, int maxCount)
    {
        _semaphore = new SemaphoreSlim(initialCount, maxCount);
    }

    public int CurrentCount => _semaphore.CurrentCount;

    public async Task<IDisposable> WaitAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new ReleaseOnDispose(_semaphore);
    }

    public async Task<IDisposable?> TryWaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (await _semaphore.WaitAsync(timeout, cancellationToken))
            return new ReleaseOnDispose(_semaphore);
        return null;
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class ReleaseOnDispose : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public ReleaseOnDispose(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
