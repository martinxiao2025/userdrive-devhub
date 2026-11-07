namespace Devhubd.Core;

/// <summary>
/// 守护进程的运行时路径集合：数据目录、SQLite 文件、发现文件与单实例锁文件。
/// 默认落在 %LOCALAPPDATA%\userdrive-devhub（该目录 ACL 默认仅当前用户与 SYSTEM 可访问）。
/// </summary>
public sealed class DevhubPaths
{
    /// <summary>数据目录的完整路径。</summary>
    public string DataDirectory { get; }

    /// <summary>SQLite 数据库文件路径。</summary>
    public string DatabasePath => Path.Combine(DataDirectory, "devhub.db");

    /// <summary>写给 VS Code 扩展的发现文件（pid / 端口 / 令牌）。</summary>
    public string RuntimeFilePath => Path.Combine(DataDirectory, "runtime.json");

    /// <summary>单实例锁文件，以 FileShare.None 独占打开。</summary>
    public string RuntimeLockPath => Path.Combine(DataDirectory, "runtime.lock");

    /// <summary>
    /// 构造路径集合。传入空值时使用 %LOCALAPPDATA%\userdrive-devhub。
    /// </summary>
    /// <param name="dataDirectory">自定义数据目录（绝对路径或相对路径）。</param>
    public DevhubPaths(string? dataDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "userdrive-devhub")
            : Path.GetFullPath(dataDirectory);

        DataDirectory = root;
    }

    /// <summary>
    /// 创建数据目录并做一次写入探针，用于在迁移前尽早发现"数据目录不可写"。
    /// </summary>
    /// <exception cref="IOException">目录无法创建或不可写。</exception>
    public void EnsureWritable()
    {
        Directory.CreateDirectory(DataDirectory);

        var probePath = Path.Combine(DataDirectory, ".write-probe");
        try
        {
            File.WriteAllText(probePath, "ok");
        }
        finally
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
    }
}
