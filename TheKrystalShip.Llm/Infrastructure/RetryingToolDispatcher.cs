namespace TheKrystalShip.Llm.Infrastructure;

using System;
using System.Threading;
using System.Threading.Tasks;
using TheKrystalShip.Llm.Interfaces;
using TheKrystalShip.Llm.Models;

/// <summary>
/// A decorator for IToolDispatcher that adds retry logic with exponential backoff
/// and jitter to handle transient failures during tool execution.
/// </summary>
public class RetryingToolDispatcher : IToolDispatcher
{
    private readonly IToolDispatcher _inner;
    private readonly int _maxRetries;
    private readonly TimeSpan _initialDelay;

    public RetryingToolDispatcher(
        IToolDispatcher inner, 
        int maxRetries = 3, 
        int initialDelayMs = 1000)
    {
        _inner = inner;
        _maxRetries = maxRetries > 0 ? maxRetries : 3;
        _initialDelay = TimeSpan.FromMilliseconds(initialDelayMs);
    }

    public async Task<string> ExecuteAsync(LlmToolCall call, CancellationToken cancellationToken = default)
    {
        int attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                return await _inner.ExecuteAsync(call, cancellationToken);
            }
            catch (Exception ex) when (IsTransient(ex) && attempts < _maxRetries)
            {
                var delay = CalculateBackoff(attempts);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception)
            {
                return "Error: Tool execution failed after multiple attempts.";
            }
        }
    }

    private TimeSpan CalculateBackoff(int attempt)
    {
        // Exponential backoff: initial * 2^(attempt-1)
        double exp = Math.Pow(2, attempt - 1);
        var baseDelay = _initialDelay.TotalMilliseconds * exp;
        
        // Add jitter (±10% of the current delay)
        var rand = new Random();
        double jitterFactor = 0.9 + (rand.NextDouble() * 0.2);
        return TimeSpan.FromMilliseconds(baseDelay * jitterFactor);
    }

    private bool IsTransient(Exception ex)
    {
        if (ex is TaskCanceledException || ex is OperationCanceledException) return true;
        return false;
    }
}
