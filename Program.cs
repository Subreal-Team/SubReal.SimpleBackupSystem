using SubRealTeam.ConsoleUtility.Common.Logging;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;

namespace SimpleBackupSystem
{
    public class TargetConfig 
    { 
        public required string Path { get; set; } 
        public required string Id { get; set; } 
        public string? DeleteFolder { get; set; }
        public bool UseMD5ForChecking { get; set; }
    }
    public class BackupJob { public required string Source { get; set; } public List<TargetConfig>? Targets { get; set; } }
    public class BackupConfig
    {
        public bool DryRun { get; set; }
        public double DeletedFolderLimitGb { get; set; }
        public List<BackupJob> Jobs { get; set; }
    }

    public class FileMetadata
    {
        public string RelativePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime LastWriteTime { get; set; }
        public string? MD5Hash { get; set; }
        public bool HashCalculated { get; set; }
    }

    class Program
    {
        private const string BackupIdFileName = ".backup_id";
        static BackupConfig? config;

        static int filesProcessed = 0;
        static int filesCopied = 0;
        static int filesDeleted = 0;
        static int errorsCount = 0;

        static void Main()
        {
            var logsDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            if (!Directory.Exists(logsDir))
                Directory.CreateDirectory(logsDir);

            if (!LoadConfig()) return;

            Logger.AddInstance(new FileLogger(filePath: logsDir));
            Logger.SetLogLevelForInstance<FileLogger>(LogLevel.Debug);

            Logger.AddInstance(new ConsoleLogger());
            Logger.SetLogLevelForInstance<ConsoleLogger>(LogLevel.Debug);

            Logger.Info($"--- START SimpleBackupSystem v.0.0.2 ---");

            var jobNumber = 0;
            Stopwatch stopwatch = Stopwatch.StartNew();

            foreach (var job in config.Jobs)
            {
                Logger.Info($"--- SOURCE {++jobNumber} ---");
                Logger.Info($"SOURCE Folder: \"{job.Source}\"");

                if (!Directory.Exists(job.Source))
                {
                    Logger.Error($"Source not found: \"{job.Source}\"");
                    errorsCount++;
                    continue;
                }

                // Создаем кэш метаданных для текущего источника
                Logger.Info($"Building file cache for source...");
                var sourceFileCache = BuildSourceFileCache(job.Source);
                Logger.Info($"Cached {sourceFileCache.Count} files from source");

                foreach (var target in job.Targets)
                {
                    Logger.Info($"TARGET Folder: \"{target.Path}\" (Id={target.Id})");
                    Logger.Debug($"Check target: \"{target.Path}\"");

                    string root = Path.GetPathRoot(target.Path);
                    if (!Directory.Exists(root))
                    {
                        Logger.Info($"   [SKIPPED] Disk \"{root}\" not connected or unavailable.");
                        errorsCount++;
                        continue;
                    }

                    if (ValidateTargetId(target))
                    {
                        string currentDeletedDir = target.DeleteFolder;
                        Logger.Info($"Getting file list...");
                        SyncDirectories(job.Source, target.Path, sourceFileCache, target.UseMD5ForChecking);
                        Logger.Info($"Cleanup...");
                        CleanupTarget(job.Source, target.Path, currentDeletedDir);
                    }
                    Logger.Info($"--- SOURCE {jobNumber} FINISHED ---");
                }
            }

            stopwatch.Stop();
            TimeSpan ts = stopwatch.Elapsed;
            string elapsedTime = string.Format("{0:00}:{1:00}:{2:00}",
                ts.Hours, ts.Minutes, ts.Seconds);
            Logger.Info($"=== TOTAL REPORT ===");
            Logger.Info($"Processing time: {elapsedTime}");
            Logger.Info($"Processed: {filesProcessed}");
            Logger.Info($"Copied: {filesCopied}");
            Logger.Info($"Moved to Trash Folder: {filesDeleted}");
            Logger.Info($"Errors: {errorsCount}\n");
            Logger.Info($"--- FINISHED ---\n");
        }

        static bool ValidateTargetId(TargetConfig target)
        {
            string idPath = Path.Combine(target.Path, BackupIdFileName);


            if (File.Exists(idPath))
            {
                string actualId = File.ReadAllText(idPath).Trim();
                if (actualId == target.Id) return true;
                Logger.Error($"ID It didn't match! Expected: \"{target.Id}\", Found: \"{actualId}\"");
                return false;
            }
            else
            {
                Logger.Error($"Missed \"{BackupIdFileName}\" for folder. Expected: \"{target.Id}\"");
                return false;
            }
        }

        static Dictionary<string, FileMetadata> BuildSourceFileCache(string sourcePath)
        {
            var cache = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);
            BuildSourceFileCacheRecursive(sourcePath, sourcePath, cache);
            return cache;
        }

        static void BuildSourceFileCacheRecursive(string currentDir, string sourceRoot, Dictionary<string, FileMetadata> cache)
        {
            foreach (string file in Directory.GetFiles(currentDir))
            {
                var fi = new FileInfo(file);
                var relativePath = GetRelativePath(sourceRoot, file);

                var metadata = new FileMetadata
                {
                    RelativePath = relativePath,
                    FileName = Path.GetFileName(file),
                    Size = fi.Length,
                    LastWriteTime = fi.LastWriteTime,
                    MD5Hash = null,
                    HashCalculated = false
                };

                cache[relativePath] = metadata;
            }

            foreach (string subDir in Directory.GetDirectories(currentDir))
            {
                BuildSourceFileCacheRecursive(subDir, sourceRoot, cache);
            }
        }

        static string GetRelativePath(string root, string fullPath)
        {
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return fullPath;

            var relative = fullPath.Substring(root.Length);
            if (relative.StartsWith(Path.DirectorySeparatorChar.ToString()))
                relative = relative.Substring(1);
            return relative;
        }

        static bool ShouldCopyWithCache(string sourceFullPath, string targetFile, FileMetadata sourceMetadata, bool UseMD5)
        {
            filesProcessed++;
            Logger.Debug($"Check ({filesProcessed}): {sourceMetadata.RelativePath}");

            if (!File.Exists(targetFile)) return true;

            FileInfo fiT = new FileInfo(targetFile);

            // Сначала сравниваем размер
            if (sourceMetadata.Size != fiT.Length) return true;

            // Сравниваем дату изменения
            if (sourceMetadata.LastWriteTime != fiT.LastWriteTime) return true;

            if (UseMD5)
            {
                // Если размер и дата совпадают, вычисляем MD5 (если еще не вычислен)
                if (!sourceMetadata.HashCalculated)
                {
                    Logger.Debug($"Computing MD5 for: {sourceMetadata.RelativePath}");
                    sourceMetadata.MD5Hash = GetFileHash(sourceFullPath);
                    sourceMetadata.HashCalculated = true;
                }

                string targetHash = GetFileHash(targetFile);
                return sourceMetadata.MD5Hash != targetHash; 
            }
            else
            {
                return false;
            }
        }

        static void SyncDirectories(string source, string target, Dictionary<string, FileMetadata> sourceCache, bool UseMD5)
        {
            SyncDirectoriesRecursive(source, target, source, sourceCache, UseMD5);
        }

        static void SyncDirectoriesRecursive(string sourceDir, string targetDir, string sourceRoot, Dictionary<string, FileMetadata> sourceCache, bool UseMD5)
        {
            foreach (string sFile in Directory.GetFiles(sourceDir))
            {
                string relativePath = GetRelativePath(sourceRoot, sFile);
                string tFile = Path.Combine(targetDir, Path.GetFileName(sFile));

                if (!sourceCache.TryGetValue(relativePath, out var metadata))
                {
                    // Если файла нет в кэше (маловероятно), создаем временный
                    var fi = new FileInfo(sFile);
                    metadata = new FileMetadata
                    {
                        RelativePath = relativePath,
                        FileName = Path.GetFileName(sFile),
                        Size = fi.Length,
                        LastWriteTime = fi.LastWriteTime,
                        MD5Hash = null,
                        HashCalculated = false
                    };
                }

                if (ShouldCopyWithCache(sFile, tFile, metadata, UseMD5))
                {
                    try
                    {
                        string targetFolder = Path.GetDirectoryName(tFile);
                        if (!Directory.Exists(targetFolder))
                        {
                            Directory.CreateDirectory(targetFolder);
                        }
                        File.Copy(sFile, tFile, true);
                        Logger.Info($"   [COPY OK] \"{metadata.FileName}\"");
                        filesCopied++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"   [COPY ERROR] \"{sFile}\": {ex.Message}");
                        errorsCount++;
                    }
                }
            }

            foreach (string sSubDir in Directory.GetDirectories(sourceDir))
            {
                SyncDirectoriesRecursive(sSubDir, Path.Combine(targetDir, Path.GetFileName(sSubDir)), sourceRoot, sourceCache, UseMD5);
            }
        }

        static void CleanupTarget(string source, string target, string currentDeletedDir)
        {
            if (!Directory.Exists(target)) return;

            foreach (string tFile in Directory.GetFiles(target))
            {
                if (Path.GetFileName(tFile) == BackupIdFileName) continue;
                if (!File.Exists(Path.Combine(source, Path.GetFileName(tFile))))
                    MoveToDeleted(tFile, currentDeletedDir);
            }

            foreach (string tSubDir in Directory.GetDirectories(target))
            {
                string sSubDir = Path.Combine(source, Path.GetFileName(tSubDir));
                CleanupTarget(sSubDir, tSubDir, currentDeletedDir);
                if (!Directory.Exists(sSubDir) && Directory.GetFileSystemEntries(tSubDir).Length == 0)
                    Directory.Delete(tSubDir);
            }
        }

        static void MoveToDeleted(string filePath, string currentDeletedDir)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(currentDeletedDir))
                {
                    Directory.CreateDirectory(currentDeletedDir);
                    string dest = Path.Combine(currentDeletedDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(filePath)}");
                    File.Move(filePath, dest);
                    Logger.Info($"   [MOVED] {Path.GetFileName(filePath)} -> local {currentDeletedDir}");
                    EnforceDeletedLimit(currentDeletedDir);
                }
                else
                {
                    File.Delete(filePath);
                    Logger.Info($"   [DELETED] {Path.GetFileName(filePath)} deleted");
                }

                filesDeleted++;
            }
            catch (Exception ex)
            {
                Logger.Error($"   [DELETE ERROR] {filePath}: {ex.Message}");
                errorsCount++;
            }
        }

        static void EnforceDeletedLimit(string currentDeletedDir)
        {
            if (currentDeletedDir != null)
            {
                var dir = new DirectoryInfo(currentDeletedDir);
                if (!dir.Exists) return;
                var files = dir.GetFiles().OrderBy(f => f.CreationTime).ToList();
                long currentSize = files.Sum(f => f.Length);
                long limit = (long)(config.DeletedFolderLimitGb * 1024 * 1024 * 1024);

                foreach (var file in files)
                {
                    if (currentSize <= limit) break;
                    currentSize -= file.Length;
                    file.Delete();
                    Logger.Info($"   [CLEANUP] The old file was deleted from the trash folder: {file.Name}");
                }
            }
        }

        static bool ShouldCopy(string sourceFile, string targetFile)
        {
            filesProcessed++;
            Logger.Debug($"Check ({filesProcessed}): {sourceFile}");
            if (!File.Exists(targetFile)) return true;
            FileInfo fiS = new FileInfo(sourceFile);
            FileInfo fiT = new FileInfo(targetFile);
            if (fiS.Length != fiT.Length) return true;
            return GetFileHash(sourceFile) != GetFileHash(targetFile);
        }

        static string GetFileHash(string path)
        {
            try
            {
                using var md5 = MD5.Create();
                using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return Convert.ToHexString(md5.ComputeHash(s));
            }
            catch { return "ERROR"; }
        }

        static bool LoadConfig()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (!File.Exists(path))
            {
                Console.WriteLine("Config not found!"); return false;
            }
            config = JsonSerializer.Deserialize<BackupConfig>(File.ReadAllText(path));
            return true;
        }
    }
}