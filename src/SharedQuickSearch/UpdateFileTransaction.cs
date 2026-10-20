using System.IO;

namespace SharedQuickSearch;

/// <summary>Keeps the previous executable until the replacement has reported a successful startup.</summary>
public sealed class UpdateFileTransaction : IDisposable
{
    public string Target { get; }
    public string? Backup { get; }
    private bool _finished;

    public UpdateFileTransaction(string target)
    {
        Target = Path.GetFullPath(target);
        if (File.Exists(Target))
        {
            Backup = Target + ".rollback-" + Guid.NewGuid().ToString("N");
            File.Copy(Target, Backup, overwrite: false);
        }
    }

    public void Commit()
    {
        _finished = true;
        try { if (Backup is not null) File.Delete(Backup); } catch { /* A harmless backup can be cleaned later. */ }
    }

    public void Rollback()
    {
        if (_finished) return;
        if (Backup is not null)
        {
            if (!File.Exists(Backup)) throw new IOException("旧程序备份不见了，不能安全回退。");
            var restored = Target + ".restore-" + Guid.NewGuid().ToString("N");
            File.Copy(Backup, restored, overwrite: false);
            File.Move(restored, Target, overwrite: true);
        }
        else if (File.Exists(Target)) File.Delete(Target); // Only the newly created executable, never its directory/data.
        Commit();
    }

    public void Dispose() { if (!_finished) Rollback(); }
}
