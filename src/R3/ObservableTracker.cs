using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace R3;

public static class ObservableTracker
{
    static int trackingIdCounter = 0;

    public static bool EnableTracking = false;
    public static bool EnableStackTrace = false;

    /// <summary>
    /// Frames kept per subscription when EnableStackTrace is on, counted from the first frame outside R3.
    /// Every frame is one stack walk, so keep it small: the full trace made a project with 127k live
    /// subscriptions spend minutes inside a single frame.
    /// </summary>
    public static int StackTraceFrames = 6;

    const int MaxExaminedFrames = 48;

    // Observers running a callback on this thread, innermost last. Lets code inside a handler find the
    // subscription it belongs to, see TryGetCurrentSubscription. Only maintained while EnableTracking is on.
    [ThreadStatic] static object?[]? currentObservers;
    [ThreadStatic] static int currentDepth;

    static readonly WeakDictionary<TrackableDisposable, TrackingState> tracking = new();

    // for iterationg
    static List<TrackingState> iterateCache = new();

    // flag for polling performance
    static bool dirty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [DebuggerStepThrough]
    internal static bool TryTrackActiveSubscription(IDisposable subscription, int skipFrame, [NotNullWhen(true)] out TrackableDisposable? trackableDisposable)
    {
        if (!EnableTracking)
        {
            trackableDisposable = default;
            return false;
        }
        return TryTrackActiveSubscriptionCore(subscription, skipFrame, out trackableDisposable);
    }

    [DebuggerStepThrough]
    internal static bool TryTrackActiveSubscriptionCore(IDisposable subscription, int skipFrame, [NotNullWhen(true)] out TrackableDisposable? trackableDisposable)
    {
        dirty = true;

        string stackTrace = "";
        if (EnableStackTrace)
        {
            stackTrace = CaptureStackTrace(skipFrame);
        }

        var unwrappedSubscription = UnwrapTrackableDisposable(subscription);
        string typeName;
        if (EnableStackTrace)
        {
            var sb = new StringBuilder();
            TypeBeautify(unwrappedSubscription.GetType(), sb);
            typeName = sb.ToString();
        }
        else
        {
            typeName = unwrappedSubscription.GetType().Name;
        }

        var id = Interlocked.Increment(ref trackingIdCounter);
        var state = new TrackingState(id, typeName, DateTime.Now, stackTrace); // use local now.
        trackableDisposable = new TrackableDisposable(subscription, id) { State = state };
        tracking.TryAdd(trackableDisposable, state);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RemoveTracking(TrackableDisposable subscription)
    {
        if (!EnableTracking) return;

        dirty = true;
        tracking.TryRemove(subscription);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool EnterObserver(object observer)
    {
        if (!EnableTracking) return false;
        EnterObserverCore(observer);
        return true;
    }

    static void EnterObserverCore(object observer)
    {
        var stack = currentObservers;
        if (stack == null)
        {
            currentObservers = stack = new object?[16];
        }
        else if (currentDepth == stack.Length)
        {
            Array.Resize(ref stack, stack.Length * 2);
            currentObservers = stack;
        }

        stack[currentDepth++] = observer;
    }

    internal static void ExitObserver()
    {
        if (currentDepth > 0)
        {
            currentObservers![--currentDepth] = null;
        }
    }

    /// <summary>
    /// The tracked subscription whose observer is running a callback on this thread, innermost first.
    /// False when tracking is off, no observer is running, or none of the running observers was
    /// subscribed while tracking was on.
    /// </summary>
    public static bool TryGetCurrentSubscription(out TrackingState state)
    {
        var stack = currentObservers;
        if (stack != null)
        {
            for (var i = currentDepth - 1; i >= 0; i--)
            {
                if (stack[i] is ISubscriptionOwner owner && owner.TrackedSubscription is TrackableDisposable trackable)
                {
                    state = trackable.State;
                    return true;
                }
            }
        }

        state = default;
        return false;
    }

    public static bool CheckAndResetDirty()
    {
        var current = dirty;
        dirty = false;
        return current;
    }

    public static void ForEachActiveTask(Action<TrackingState> action)
    {
        lock (iterateCache)
        {
            var count = tracking.CaptureSnapshot(ref iterateCache, clear: false);
            iterateCache.Sort(0, count, Comparer<TrackingState>.Default);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    action(iterateCache[i]);
                }
            }
            finally
            {
                iterateCache.Clear();
            }
        }
    }

    // Bounded replacement for new StackTrace(skip, true).ToString(). Mono builds a StackTrace one StackFrame
    // at a time and each StackFrame is a full stack walk plus a pdb lookup, so the full trace costs
    // O(depth^2) per subscription. This walks at most MaxExaminedFrames frames, drops the leading R3 frames
    // and keeps StackTraceFrames of the caller's chain; when the walk never leaves R3 it keeps the R3 frames
    // closest to the caller instead.
    [DebuggerStepThrough]
    static string CaptureStackTrace(int skipFrame)
    {
        var frames = StackTraceFrames;
        if (frames <= 0) return "";

        var sb = new StringBuilder();
        var kept = 0;
        List<(StackFrame frame, MethodBase method)>? leading = null;
        for (var i = 0; i < MaxExaminedFrames && kept < frames; i++)
        {
            var frame = new StackFrame(skipFrame + 1 + i, true);
            var method = frame.GetMethod();
            if (method == null) break;

            if (kept == 0 && IsLibraryFrame(method))
            {
                (leading ??= new()).Add((frame, method));
                continue;
            }

            if (kept > 0) sb.Append('\n');
            AppendFrame(sb, frame, method);
            kept++;
        }

        if (kept == 0 && leading != null)
        {
            var start = Math.Max(0, leading.Count - frames);
            for (var i = start; i < leading.Count; i++)
            {
                if (i > start) sb.Append('\n');
                AppendFrame(sb, leading[i].frame, leading[i].method);
            }
        }

        return sb.ToString();
    }

    static bool IsLibraryFrame(MethodBase method)
    {
        var ns = method.DeclaringType?.Namespace;
        return ns != null && (ns == "R3" || ns.StartsWith("R3.", StringComparison.Ordinal));
    }

    static void AppendFrame(StringBuilder sb, StackFrame frame, MethodBase method)
    {
        var type = method.DeclaringType;
        var name = method.Name;

        // async and iterator state machines: <Method>d__N.MoveNext -> Outer.Method
        if (type != null && type.DeclaringType != null && type.Name.StartsWith("<", StringComparison.Ordinal))
        {
            var end = type.Name.IndexOf('>');
            if (end > 1 && name == "MoveNext")
            {
                name = type.Name.Substring(1, end - 1);
                type = type.DeclaringType;
            }
        }

        if (type != null)
        {
            if (!string.IsNullOrEmpty(type.Namespace))
            {
                sb.Append(type.Namespace).Append('.');
            }
            TypeBeautify(type, sb);
            sb.Append('.');
        }
        sb.Append(name);

        var file = frame.GetFileName();
        if (!string.IsNullOrEmpty(file))
        {
            sb.Append(" (at ").Append(file).Append(':').Append(frame.GetFileLineNumber()).Append(')');
        }
    }

    static void TypeBeautify(Type type, StringBuilder sb)
    {
        if (type.IsNested)
        {
            // TypeBeautify(type.DeclaringType, sb);
            sb.Append(type.DeclaringType!.Name.ToString());
            sb.Append(".");
        }

        if (type.IsGenericType)
        {
            var genericsStart = type.Name.IndexOf("`");
            if (genericsStart != -1)
            {
                sb.Append(type.Name.Substring(0, genericsStart));
            }
            else
            {
                sb.Append(type.Name);
            }
            sb.Append("<");
            var first = true;
            foreach (var item in type.GetGenericArguments())
            {
                if (!first)
                {
                    sb.Append(", ");
                }
                first = false;
                TypeBeautify(item, sb);
            }
            sb.Append(">");
        }
        else
        {
            sb.Append(type.Name);
        }
    }

    static IDisposable UnwrapTrackableDisposable(IDisposable disposable)
    {
        while (disposable is TrackableDisposable t)
        {
            disposable = t.Disposable;
        }
        return disposable;
    }
}

// Implemented by Observer<T> so the tracker can map a running observer back to its tracked subscription.
internal interface ISubscriptionOwner
{
    IDisposable? TrackedSubscription { get; }
}

internal sealed class TrackableDisposable(IDisposable disposable, int trackingId) : IDisposable
{
    public IDisposable Disposable => disposable;
    public int TrackingId => trackingId;
    public TrackingState State { get; set; }
    int disposed;

    public void Dispose()
    {
        var field = Interlocked.CompareExchange(ref disposed, 1, 0);
        if (field == 0)
        {
            ObservableTracker.RemoveTracking(this);
        }

        disposable.Dispose();
    }

    public override string? ToString()
    {
        return disposable.ToString();
    }
}

public record struct TrackingState(int TrackingId, string FormattedType, DateTime AddTime, string StackTrace) : IComparable<TrackingState>
{
    public int CompareTo(TrackingState other)
    {
        return TrackingId.CompareTo(other.TrackingId);
    }
}
