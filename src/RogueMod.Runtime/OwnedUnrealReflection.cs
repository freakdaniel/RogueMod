using RogueMod.Abstractions;

namespace RogueMod.Runtime;

/// <summary>
/// Gives one managed mod ownership of its hook subscriptions while delegating
/// stateless reflection operations to the process-wide bridge.
/// </summary>
internal sealed class OwnedUnrealReflection(IUnrealReflection inner) : IUnrealReflection, IDisposable
{
    private readonly Lock gate = new();
    private readonly HashSet<TrackedSubscription> subscriptions = [];
    private bool disposed;

    public bool IsAvailable => inner.IsAvailable;
    public UnrealReflectionCapabilities Capabilities => inner.Capabilities;
    public UnrealObjectHandle FindFirstOf(string className) => inner.FindFirstOf(className);
    public IReadOnlyList<UnrealObjectHandle> FindAllOf(string className) => inner.FindAllOf(className);
    public bool IsValid(UnrealObjectHandle handle) => inner.IsValid(handle);
    public UnrealObjectHandle GetClass(UnrealObjectHandle handle) => inner.GetClass(handle);
    public string? GetPathName(UnrealObjectHandle handle) => inner.GetPathName(handle);
    public UnrealValue ReadProperty(UnrealObjectHandle handle, UnrealPropertyDescriptor property) => inner.ReadProperty(handle, property);
    public void WriteProperty(UnrealObjectHandle handle, UnrealPropertyDescriptor property, UnrealValue value) =>
        inner.WriteProperty(handle, property, value);
    public UnrealInvocationResult Invoke(
        UnrealObjectHandle handle,
        UnrealFunctionDescriptor function,
        IReadOnlyList<UnrealArgument> arguments) => inner.Invoke(handle, function, arguments);

    public IDisposable RegisterHook(
        UnrealFunctionDescriptor function,
        UnrealHookPhase phase,
        Action<UnrealHookContext> callback) =>
        RegisterHook(function, phase, default, callback);

    public IDisposable RegisterHook(
        UnrealFunctionDescriptor function,
        UnrealHookPhase phase,
        UnrealHookOptions options,
        Action<UnrealHookContext> callback)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var tracked = new TrackedSubscription(this, inner.RegisterHook(function, phase, options, callback));
            subscriptions.Add(tracked);
            return tracked;
        }
    }

    public void Dispose()
    {
        TrackedSubscription[] pending;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            pending = [.. subscriptions];
            subscriptions.Clear();
        }
        foreach (var subscription in pending)
        {
            subscription.DisposeInner();
        }
    }

    private void Remove(TrackedSubscription subscription)
    {
        lock (gate)
        {
            subscriptions.Remove(subscription);
        }
    }

    private sealed class TrackedSubscription(OwnedUnrealReflection owner, IDisposable inner) : IDisposable
    {
        private IDisposable? subscription = inner;

        public void Dispose()
        {
            DisposeInner();
            owner.Remove(this);
        }

        internal void DisposeInner() => Interlocked.Exchange(ref subscription, null)?.Dispose();
    }
}
