using SubRealTeam.ConsoleUtility.Common.Logging;
using System.Security.Cryptography;
using System.Text.Json;


namespace SimpleBackupSystem
{
    public class TargetConfig { public required string Path { get; set; } public required string Id { get; set; } public string? DeleteFolder { get; set; } }
    public class BackupJob { public required string Source { get; set; } public List<TargetConfig>? Targets { get; set; } }
    public class BackupConfig
    {
        public bool DryRun { get; set; }
        public double DeletedFolderLimitGb { get; set; }
        public List<BackupJob> Jobs { get; set; }
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

            Logger.Info($"--- START SimpleBackupSystem v.0.0.1");

            var jobNumber = 0;
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

                foreach (var target in job.Targets)
                {
                    Logger.Info($"TARGET Folder: \"{target.Path}\" (Id={target.Id})");
                    Logger.Debug($"Check target: \"{target.Path}\"");
                    //  Does the disk/root of the path exist?
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
                        SyncDirectories(job.Source, target.Path);
                        Logger.Info($"Cleanup...");
                        CleanupTarget(job.Source, target.Path, currentDeletedDir);
                    }
                    Logger.Info($"--- SOURCE {jobNumber} FINISHED ---");

                    //TODO: add job folder statistic
                }
            }
            // TODO: add time working
            Logger.Info($"=== TOTAL REPORT ===");
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

        static void SyncDirectories(string source, string target)
        {
            foreach (string sFile in Directory.GetFiles(source))
            {
                string tFile = Path.Combine(target, Path.GetFileName(sFile));
                if (ShouldCopy(sFile, tFile))
                {
                    try 
                    {
                        string targetFolder = Path.GetDirectoryName(tFile);
                        if (!Directory.Exists(targetFolder))
                        {
                            Directory.CreateDirectory(targetFolder);
                        }
                        File.Copy(sFile, tFile, true); 
                        Logger.Info($"   [COPY OK] \"{Path.GetFileName(sFile)}\""); 
                        filesCopied++; 
                    }
                    catch (Exception ex) 
                    { 
                        Logger.Error($"   [COPY ERROR] \"{sFile}\": {ex.Message}"); 
                        errorsCount++; 
                    }
                }
            }

            foreach (string sSubDir in Directory.GetDirectories(source))
                SyncDirectories(sSubDir, Path.Combine(target, Path.GetFileName(sSubDir)));
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
            // TODO: Clean up before re-marking??
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
            // TODO : Deleting early files instead of cleaning up 

            if (currentDeletedDir != null)
            {   
                var dir = new DirectoryInfo(currentDeletedDir);
                if (!dir.Exists) return;
                var files = dir.GetFiles().OrderBy(f => f.CreationTime).ToList();
                long currentSize = files.Sum(f => f.Length);
                long limit = (long)(config.DeletedFolderLimitGb * 1024 * 1024 * 1024); // GB

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
            try {
                using var md5 = MD5.Create();
                using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return Convert.ToHexString(md5.ComputeHash(s));
            } catch { return "ERROR"; }
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
