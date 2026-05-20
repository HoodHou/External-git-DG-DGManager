namespace SVNManager;

internal sealed record DiffProgress(string Stage, int Percent, string Message)
{
    public static DiffProgress Indeterminate(string stage, string message)
    {
        return new DiffProgress(stage, -1, message);
    }
}
