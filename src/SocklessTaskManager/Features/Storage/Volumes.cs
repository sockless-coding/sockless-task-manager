namespace Sockless.TaskManager.Features.Storage;

/// <summary>A drive letter with a mounted file system.</summary>
internal sealed record VolumeInfo(string Root, string Name, string FileSystem, long TotalBytes, long FreeBytes, bool IsRemovable)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    /// <summary>"C:".</summary>
    public string Letter => Root.TrimEnd('\\');
}

internal static class Volumes
{
    /// <summary>Fixed and removable drives that are ready. Network and optical drives are left out.</summary>
    public static List<VolumeInfo> Read()
    {
        var volumes = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                continue;

            try
            {
                if (!drive.IsReady)
                    continue;

                var letter = drive.Name.TrimEnd('\\');
                var label = drive.VolumeLabel;
                if (string.IsNullOrWhiteSpace(label))
                    label = drive.DriveType == DriveType.Removable ? "Removable disk" : "Local disk";

                volumes.Add(new VolumeInfo(
                    drive.Name,
                    $"{label} ({letter})",
                    drive.DriveFormat,
                    drive.TotalSize,
                    drive.TotalFreeSpace,
                    drive.DriveType == DriveType.Removable));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ejected or locked (BitLocker) between listing and reading: leave it out.
            }
        }

        return volumes;
    }
}
