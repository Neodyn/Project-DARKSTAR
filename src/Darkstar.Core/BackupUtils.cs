namespace Darkstar;

/// <summary>
/// Creates a timestamped backup copy of a file into a "Backup" subfolder (next to the file
/// itself) right before the bot overwrites it. Used whenever config.json or phrases.json get
/// rewritten automatically (e.g. the config-merge step), so a previous version is never lost.
/// </summary>
public static class BackupUtils
{
    /// <summary>
    /// Copies filePath into a "Backup" subfolder (created if needed) before it gets overwritten.
    /// Does nothing if filePath doesn't exist yet (nothing to back up). Never throws - a failed
    /// backup is logged as a warning but must not stop the bot from continuing to write the file.
    /// </summary>
    public static void BackupBeforeWrite(string filePath)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            var backupDir = string.IsNullOrEmpty(directory) ? "Backup" : Path.Combine(directory, "Backup");
            Directory.CreateDirectory(backupDir);

            var fileName = Path.GetFileName(filePath);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var backupPath = Path.Combine(backupDir, $"{fileName}.{timestamp}.bak");

            File.Copy(filePath, backupPath, overwrite: true);
            Logger.Log($"Backed up {fileName} to {Path.GetFullPath(backupPath)} before overwriting it.");
        }
        catch (Exception ex)
        {
            // A failed backup must never prevent the bot from writing the actual file -
            // just make the failure visible instead of silently skipping the backup.
            Logger.Log($"WARNING: Could not create a backup of {filePath} before overwriting it: {ex.Message}");
        }
    }
}
