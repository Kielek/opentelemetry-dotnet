// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using OpenTelemetry.Context;

namespace OpenTelemetry;

/// <summary>
/// Contains methods managing instrumentation of internal operations.
/// </summary>
public sealed class SuppressInstrumentationScope : IDisposable
{
    // An integer value which controls whether instrumentation should be suppressed (disabled).
    // * 0: instrumentation is not suppressed
    // * Depth = [int.MinValue, -1]: instrumentation is always suppressed
    // * Depth = [1, int.MaxValue]: instrumentation is suppressed in a reference-counting mode
    private static readonly RuntimeContextSlot<int> Slot = RuntimeContext.RegisterSlot<int>("otel.suppress_instrumentation");

    private readonly int previousDepth;
    private bool disposed;

    internal SuppressInstrumentationScope(bool value = true)
    {
        this.previousDepth = Slot.Get();
        Slot.Set(value ? -1 : 0);
    }

    internal static bool IsSuppressed => Slot.Get() != 0;

    /// <summary>
    /// Begins a new scope in which instrumentation is suppressed (disabled).
    /// </summary>
    /// <param name="value">Value indicating whether to suppress instrumentation.</param>
    /// <returns>Object to dispose to end the scope.</returns>
    /// <remarks>
    /// This is typically used to prevent infinite loops created by
    /// collection of internal operations, such as exporting traces over HTTP.
    /// <code>
    ///     public override async Task&lt;ExportResult&gt; ExportAsync(
    ///         IEnumerable&lt;Activity&gt; batch,
    ///         CancellationToken cancellationToken)
    ///     {
    ///         using (SuppressInstrumentationScope.Begin())
    ///         {
    ///             // Instrumentation is suppressed (i.e., Sdk.SuppressInstrumentation == true)
    ///         }
    ///
    ///         // Instrumentation is not suppressed (i.e., Sdk.SuppressInstrumentation == false)
    ///     }
    /// </code>
    /// </remarks>
    public static IDisposable Begin(bool value = true)
    {
        return new SuppressInstrumentationScope(value);
    }

    /// <summary>
    /// Enters suppression mode.
    /// If suppression mode is enabled (slot.Depth is a negative integer), do nothing.
    /// If suppression mode is not enabled (slot is null), enter reference-counting suppression mode.
    /// If suppression mode is enabled (slot.Depth is a positive integer), increment the ref count.
    /// </summary>
    /// <returns>The updated suppression slot value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Enter()
    {
        var currentDepth = Slot.Get();

        if (currentDepth >= 0)
        {
            Slot.Set(++currentDepth);
        }

        return currentDepth;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!this.disposed)
        {
            Slot.Set(this.previousDepth);
            this.disposed = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int IncrementIfTriggered()
    {
        var currentDepth = Slot.Get();

        if (currentDepth > 0)
        {
            Slot.Set(++currentDepth);
        }

        return currentDepth;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int DecrementIfTriggered()
    {
        var currentDepth = Slot.Get();

        if (currentDepth > 0)
        {
            Slot.Set(--currentDepth);
        }

        return currentDepth;
    }
}
