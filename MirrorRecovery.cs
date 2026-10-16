namespace StationeersMirror;

// Continue restoring independent mutations even if one Unity object was destroyed
// or a modded callback throws during recovery. Report all failures in one log entry.
internal sealed class MirrorRecovery
{
    private readonly List<Exception> _errors = new();

    internal void Try(Action restore)
    {
        try { restore(); }
        catch (Exception error) { _errors.Add(error); }
    }

    internal Exception Combine(Exception cause) => _errors.Count == 0
        ? cause : new AggregateException("Mirror failed; some recovery operations also failed.", new[] { cause }.Concat(_errors));

    internal void ThrowIfFailed()
    {
        if (_errors.Count != 0)
            throw new AggregateException("Mirror recovery was incomplete.", _errors);
    }
}
