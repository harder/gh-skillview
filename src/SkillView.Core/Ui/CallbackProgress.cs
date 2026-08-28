namespace SkillView.Ui;

/// <summary>
/// Reports progress inline on the producer thread. TUI callers use this to
/// hand the update to <c>IApplication.Invoke</c> themselves, avoiding the
/// unbounded extra queue and ambiguous synchronization-context capture of
/// <see cref="Progress{T}"/>.
/// </summary>
internal sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
