using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Activity_Tracker
{
    /// <summary>
    /// Service for managing local storage of activity reports
    /// Provides thread-safe operations with offline support
    /// </summary>
    public class LocalStorageService
    {
        #region Fields

        private readonly ILogger<LocalStorageService>? _logger;
        private readonly string _storageDirectory;
        private readonly string _activeFilePath;
        private readonly string _sentFilePath;
        private readonly string _failedFilePath;
        private readonly object _fileLock = new();

        private const int MaxFileSizeMB = 10; // Maximum size for active file before rotation
        private const int MaxArchiveFiles = 10; // Keep last 10 archive files

        #endregion

        #region Constructor

        public LocalStorageService(ILogger<LocalStorageService>? logger = null)
        {
            _logger = logger;

            // Use AppData/Roaming for per-user storage
            _storageDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ActivityTracker"
            );

            // Create directory if it doesn't exist
            if (!Directory.Exists(_storageDirectory))
            {
                Directory.CreateDirectory(_storageDirectory);
                _logger?.LogInformation("📁 Created storage directory: {Directory}", _storageDirectory);
            }

            // Define file paths
            _activeFilePath = Path.Combine(_storageDirectory, "active_reports.json");
            _sentFilePath = Path.Combine(_storageDirectory, "sent_reports.json");
            _failedFilePath = Path.Combine(_storageDirectory, "failed_reports.json");

            _logger?.LogDebug("💾 Storage service initialized");
            _logger?.LogDebug("📂 Active file: {File}", _activeFilePath);
        }

        #endregion

        #region Core Operations

        /// <summary>
        /// Saves a report to local storage (thread-safe)
        /// </summary>
        public void Save(ActivityReport report)
        {
            lock (_fileLock)
            {
                try
                {
                    var reports = LoadActiveReports();

                    // Remove existing report with same ID (if any) to avoid duplicates
                    reports.RemoveAll(r => r.Id == report.Id);

                    // Add the new/modified report
                    reports.Add(report);

                    // Save to file
                    SaveReportsToFile(reports, _activeFilePath);

                    _logger?.LogDebug("💾 Saved report: {Id} ({App})", report.Id, report.AppName);

                    // Check if we need to rotate the file
                    CheckAndRotateFile(_activeFilePath);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "❌ Failed to save report {Id}", report.Id);
                    throw;
                }
            }
        }

        /// <summary>
        /// Loads all active (pending) reports
        /// </summary>
        public List<ActivityReport> LoadAll()
        {
            lock (_fileLock)
            {
                return LoadActiveReports();
            }
        }

        /// <summary>
        /// Clears all pending (unsent) reports from local storage
        /// </summary>
        public void ClearPendingReports()
        {
            lock (_fileLock)
            {
                try
                {
                    var activeReports = LoadActiveReports();
                    // Keep only already sent reports in the active file
                    var sentReportsOnly = activeReports.Where(r => r.Sent).ToList();
                    SaveReportsToFile(sentReportsOnly, _activeFilePath);
                    _logger?.LogInformation("🗑️ Cleared all pending reports from local storage");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "❌ Failed to clear pending reports");
                    throw;
                }
            }
        }

        #endregion

        #region Sync Service Methods

        /// <summary>
        /// Gets all reports that are pending to be sent
        /// </summary>
        public List<ActivityReport> GetPendingReports(int maxRetryAttempts)
        {
            lock (_fileLock)
            {
                var reports = LoadActiveReports();

                return reports
                    .Where(r => !r.Sent && r.SendAttempts < maxRetryAttempts)
                    .OrderBy(r => r.Timestamp) // Oldest first
                    .ToList();
            }
        }

        /// <summary>
        /// Archives a successfully sent report
        /// </summary>
        public void ArchiveReport(ActivityReport report)
        {
            lock (_fileLock)
            {
                try
                {
                    // Remove from active reports
                    var activeReports = LoadActiveReports();
                    activeReports.RemoveAll(r => r.Id == report.Id);
                    SaveReportsToFile(activeReports, _activeFilePath);

                    // Add to sent archive
                    var sentReports = LoadSentReports();
                    sentReports.Add(report);
                    SaveReportsToFile(sentReports, _sentFilePath);

                    _logger?.LogDebug("📦 Archived sent report: {Id}", report.Id);

                    // Cleanup old sent reports
                    CleanupOldReportsInFile(_sentFilePath, TimeSpan.FromDays(30));
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "❌ Failed to archive report {Id}", report.Id);
                }
            }
        }

        /// <summary>
        /// Moves a report to the failed queue (exceeded max retry attempts)
        /// </summary>
        public void MoveToFailed(ActivityReport report)
        {
            lock (_fileLock)
            {
                try
                {
                    // Remove from active reports
                    var activeReports = LoadActiveReports();
                    activeReports.RemoveAll(r => r.Id == report.Id);
                    SaveReportsToFile(activeReports, _activeFilePath);

                    // Add to failed reports
                    var failedReports = LoadFailedReports();
                    failedReports.Add(report);
                    SaveReportsToFile(failedReports, _failedFilePath);

                    _logger?.LogWarning("🚫 Moved to failed queue: {Id} ({App})", report.Id, report.AppName);

                    // Cleanup old failed reports (keep longer for debugging)
                    CleanupOldReportsInFile(_failedFilePath, TimeSpan.FromDays(90));
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "❌ Failed to move report to failed queue: {Id}", report.Id);
                }
            }
        }

        /// <summary>
        /// Cleans up old reports from all storage files
        /// </summary>
        public void CleanupOldReports(TimeSpan maxAge)
        {
            lock (_fileLock)
            {
                try
                {
                    // Cleanup active reports (keep pending reports regardless of age)
                    var activeReports = LoadActiveReports();
                    var activePending = activeReports.Where(r => !r.Sent).ToList();
                    var activeOld = activeReports.Where(r => r.Sent && (DateTime.Now - r.CreatedAt) > maxAge).ToList();

                    if (activeOld.Any())
                    {
                        activeReports = activePending;
                        SaveReportsToFile(activeReports, _activeFilePath);
                        _logger?.LogInformation("🧹 Cleaned up {Count} old active reports", activeOld.Count);
                    }

                    // Cleanup sent reports archive
                    CleanupOldReportsInFile(_sentFilePath, maxAge);

                    // Cleanup failed reports (keep longer)
                    CleanupOldReportsInFile(_failedFilePath, TimeSpan.FromDays(90));

                    // Cleanup old archive files
                    CleanupOldArchiveFiles();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "❌ Failed to cleanup old reports");
                }
            }
        }

        #endregion

        #region Query Methods

        /// <summary>
        /// Gets statistics about stored reports
        /// </summary>
        public StorageStatistics GetStatistics()
        {
            lock (_fileLock)
            {
                var active = LoadActiveReports();
                var sent = LoadSentReports();
                var failed = LoadFailedReports();

                return new StorageStatistics
                {
                    ActiveCount = active.Count,
                    PendingCount = active.Count(r => !r.Sent),
                    SentCount = sent.Count,
                    FailedCount = failed.Count,
                    TotalCount = active.Count + sent.Count + failed.Count,
                    StorageDirectory = _storageDirectory,
                    ActiveFileSize = GetFileSize(_activeFilePath),
                    SentFileSize = GetFileSize(_sentFilePath),
                    FailedFileSize = GetFileSize(_failedFilePath)
                };
            }
        }

        /// <summary>
        /// Gets reports for a specific date
        /// </summary>
        public List<ActivityReport> GetReportsForDate(DateTime date)
        {
            lock (_fileLock)
            {
                var allReports = new List<ActivityReport>();
                allReports.AddRange(LoadActiveReports());
                allReports.AddRange(LoadSentReports());
                allReports.AddRange(LoadFailedReports());

                return allReports
                    .Where(r => r.Timestamp.Date == date.Date)
                    .OrderBy(r => r.Timestamp)
                    .ToList();
            }
        }

        /// <summary>
        /// Gets reports for a specific application
        /// </summary>
        public List<ActivityReport> GetReportsForApp(string appName)
        {
            lock (_fileLock)
            {
                var allReports = new List<ActivityReport>();
                allReports.AddRange(LoadActiveReports());
                allReports.AddRange(LoadSentReports());
                allReports.AddRange(LoadFailedReports());

                return allReports
                    .Where(r => r.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.Timestamp)
                    .ToList();
            }
        }

        #endregion

        #region File Operations (Private)

        /// <summary>
        /// Loads active reports from file
        /// </summary>
        private List<ActivityReport> LoadActiveReports()
        {
            return LoadReportsFromFile(_activeFilePath);
        }

        /// <summary>
        /// Loads sent reports from archive file
        /// </summary>
        private List<ActivityReport> LoadSentReports()
        {
            return LoadReportsFromFile(_sentFilePath);
        }

        /// <summary>
        /// Loads failed reports from file
        /// </summary>
        private List<ActivityReport> LoadFailedReports()
        {
            return LoadReportsFromFile(_failedFilePath);
        }

        /// <summary>
        /// Loads reports from a JSON file
        /// </summary>
        private List<ActivityReport> LoadReportsFromFile(string filePath)
        {
            if (!File.Exists(filePath))
                return new List<ActivityReport>();

            try
            {
                var json = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<ActivityReport>();

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true
                };

                var reports = JsonSerializer.Deserialize<List<ActivityReport>>(json, options);
                return reports ?? new List<ActivityReport>();
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "❌ Corrupted JSON file: {File}", filePath);

                // Backup corrupted file
                BackupCorruptedFile(filePath);

                return new List<ActivityReport>();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "❌ Failed to load reports from {File}", filePath);
                return new List<ActivityReport>();
            }
        }

        /// <summary>
        /// Saves reports to a JSON file
        /// </summary>
        private void SaveReportsToFile(List<ActivityReport> reports, string filePath)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var json = JsonSerializer.Serialize(reports, options);

                // Write to temp file first, then move (atomic operation)
                var tempFile = filePath + ".tmp";
                File.WriteAllText(tempFile, json);
                File.Move(tempFile, filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "❌ Failed to save reports to {File}", filePath);
                throw;
            }
        }

        /// <summary>
        /// Cleans up old reports from a specific file
        /// </summary>
        private void CleanupOldReportsInFile(string filePath, TimeSpan maxAge)
        {
            if (!File.Exists(filePath))
                return;

            try
            {
                var reports = LoadReportsFromFile(filePath);
                var cutoff = DateTime.Now - maxAge;
                var filtered = reports.Where(r => r.CreatedAt >= cutoff).ToList();

                if (filtered.Count < reports.Count)
                {
                    SaveReportsToFile(filtered, filePath);
                    _logger?.LogDebug("🧹 Cleaned {Removed} old reports from {File}",
                        reports.Count - filtered.Count, Path.GetFileName(filePath));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "❌ Failed to cleanup {File}", filePath);
            }
        }

        /// <summary>
        /// Backs up a corrupted file for debugging
        /// </summary>
        private void BackupCorruptedFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return;

                var backupPath = $"{filePath}.corrupted.{DateTime.Now:yyyyMMddHHmmss}";
                File.Move(filePath, backupPath);
                _logger?.LogWarning("📂 Backed up corrupted file to: {Backup}", backupPath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "❌ Failed to backup corrupted file");
            }
        }

        /// <summary>
        /// Checks if file needs rotation and rotates if necessary
        /// </summary>
        private void CheckAndRotateFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return;

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > MaxFileSizeMB * 1024 * 1024)
                {
                    // Rotate file
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var rotatedFile = Path.Combine(
                        Path.GetDirectoryName(filePath)!,
                        $"{Path.GetFileNameWithoutExtension(filePath)}_{timestamp}.json"
                    );

                    File.Move(filePath, rotatedFile);
                    _logger?.LogInformation("🔄 Rotated file: {Original} → {Rotated}",
                        Path.GetFileName(filePath), Path.GetFileName(rotatedFile));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "❌ Failed to rotate file");
            }
        }

        /// <summary>
        /// Cleans up old archive files
        /// </summary>
        private void CleanupOldArchiveFiles()
        {
            try
            {
                var archiveFiles = Directory.GetFiles(_storageDirectory, "active_reports_*.json")
                    .OrderByDescending(f => f)
                    .Skip(MaxArchiveFiles)
                    .ToList();

                foreach (var file in archiveFiles)
                {
                    File.Delete(file);
                    _logger?.LogDebug("🗑️ Deleted old archive file: {File}", Path.GetFileName(file));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "❌ Failed to cleanup archive files");
            }
        }

        /// <summary>
        /// Gets file size in MB
        /// </summary>
        private double GetFileSize(string filePath)
        {
            if (!File.Exists(filePath))
                return 0;

            var fileInfo = new FileInfo(filePath);
            return Math.Round(fileInfo.Length / (1024.0 * 1024.0), 2);
        }

        #endregion

        #region Helper Classes

        /// <summary>
        /// Statistics about local storage
        /// </summary>
        public class StorageStatistics
        {
            public int ActiveCount { get; set; }
            public int PendingCount { get; set; }
            public int SentCount { get; set; }
            public int FailedCount { get; set; }
            public int TotalCount { get; set; }
            public string StorageDirectory { get; set; } = string.Empty;
            public double ActiveFileSize { get; set; }
            public double SentFileSize { get; set; }
            public double FailedFileSize { get; set; }

            public override string ToString()
            {
                return $"Active: {ActiveCount} (Pending: {PendingCount}), " +
                       $"Sent: {SentCount}, Failed: {FailedCount}, " +
                       $"Total: {TotalCount}, Size: {ActiveFileSize + SentFileSize + FailedFileSize:F2}MB";
            }
        }

        #endregion

        #region Maintenance Methods

        /// <summary>
        /// Performs storage maintenance (compaction, validation, cleanup)
        /// </summary>
        public void PerformMaintenance()
        {
            lock (_fileLock)
            {
                try
                {
                    _logger?.LogInformation("🔧 Performing storage maintenance...");

                    // Validate all files
                    ValidateStorageFiles();

                    // Remove duplicate reports
                    RemoveDuplicates();

                    // Compact storage (remove empty entries)
                    CompactStorage();

                    // Backup current state
                    CreateBackup();

                    _logger?.LogInformation("✅ Storage maintenance completed");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "❌ Storage maintenance failed");
                }
            }
        }

        /// <summary>
        /// Validates all storage files for consistency
        /// </summary>
        private void ValidateStorageFiles()
        {
            var files = new[] { _activeFilePath, _sentFilePath, _failedFilePath };

            foreach (var file in files)
            {
                if (File.Exists(file))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        JsonSerializer.Deserialize<List<ActivityReport>>(json);
                        _logger?.LogDebug("✅ Validated: {File}", Path.GetFileName(file));
                    }
                    catch (JsonException)
                    {
                        _logger?.LogWarning("⚠️ Invalid JSON in: {File}", Path.GetFileName(file));
                        BackupCorruptedFile(file);
                        File.WriteAllText(file, "[]"); // Reset to empty array
                    }
                }
            }
        }

        /// <summary>
        /// Removes duplicate reports based on ID
        /// </summary>
        private void RemoveDuplicates()
        {
            var files = new[]
            {
                (_activeFilePath, LoadActiveReports()),
                (_sentFilePath, LoadSentReports()),
                (_failedFilePath, LoadFailedReports())
            };

            foreach (var (filePath, reports) in files)
            {
                var uniqueReports = reports
                    .GroupBy(r => r.Id)
                    .Select(g => g.First())
                    .ToList();

                if (uniqueReports.Count < reports.Count)
                {
                    SaveReportsToFile(uniqueReports, filePath);
                    _logger?.LogInformation("🧹 Removed {Count} duplicates from {File}",
                        reports.Count - uniqueReports.Count, Path.GetFileName(filePath));
                }
            }
        }

        /// <summary>
        /// Compacts storage by removing invalid entries
        /// </summary>
        private void CompactStorage()
        {
            var activeReports = LoadActiveReports();
            var validReports = activeReports
                .Where(r => !string.IsNullOrEmpty(r.AppName) && r.DurationSeconds > 0)
                .ToList();

            if (validReports.Count < activeReports.Count)
            {
                SaveReportsToFile(validReports, _activeFilePath);
                _logger?.LogInformation("🧹 Compacted active reports: {Before} → {After}",
                    activeReports.Count, validReports.Count);
            }
        }

        /// <summary>
        /// Creates a backup of all storage files
        /// </summary>
        private void CreateBackup()
        {
            try
            {
                var backupDir = Path.Combine(_storageDirectory, "backups");
                if (!Directory.Exists(backupDir))
                    Directory.CreateDirectory(backupDir);

                var timestamp = DateTime.Now.ToString("yyyyMMdd");
                var backupSubDir = Path.Combine(backupDir, timestamp);

                if (!Directory.Exists(backupSubDir))
                    Directory.CreateDirectory(backupSubDir);

                var files = new[] { _activeFilePath, _sentFilePath, _failedFilePath };
                foreach (var file in files)
                {
                    if (File.Exists(file))
                    {
                        var dest = Path.Combine(backupSubDir, Path.GetFileName(file));
                        File.Copy(file, dest, true);
                    }
                }

                _logger?.LogDebug("💽 Created backup in: {BackupDir}", backupSubDir);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "❌ Failed to create backup");
            }
        }

        #endregion

        #region Blocked Apps Persistent Storage

        /// <summary>
        /// Saves the list of blocked applications
        /// </summary>
        public void SaveBlockedApps(HashSet<string> blockedApps)
        {
            lock (_fileLock)
            {
                try
                {
                    var filePath = Path.Combine(_storageDirectory, "blocked_apps.json");
                    var json = JsonSerializer.Serialize(blockedApps.ToList());
                    File.WriteAllText(filePath, json);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "❌ Failed to save blocked apps");
                }
            }
        }

        /// <summary>
        /// Loads the list of blocked applications
        /// </summary>
        public HashSet<string> LoadBlockedApps()
        {
            lock (_fileLock)
            {
                try
                {
                    var filePath = Path.Combine(_storageDirectory, "blocked_apps.json");
                    if (!File.Exists(filePath))
                        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    var json = File.ReadAllText(filePath);
                    if (string.IsNullOrWhiteSpace(json))
                        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    var list = JsonSerializer.Deserialize<List<string>>(json);
                    return list != null 
                        ? new HashSet<string>(list, StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "❌ Failed to load blocked apps");
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>
        /// Saves the list of blocked web domains
        /// </summary>
        public void SaveBlockedDomains(HashSet<string> blockedDomains)
        {
            lock (_fileLock)
            {
                try
                {
                    var filePath = Path.Combine(_storageDirectory, "blocked_domains.json");
                    var json = JsonSerializer.Serialize(blockedDomains.ToList());
                    File.WriteAllText(filePath, json);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "❌ Failed to save blocked domains");
                }
            }
        }

        /// <summary>
        /// Loads the list of blocked web domains
        /// </summary>
        public HashSet<string> LoadBlockedDomains()
        {
            lock (_fileLock)
            {
                try
                {
                    var filePath = Path.Combine(_storageDirectory, "blocked_domains.json");
                    if (!File.Exists(filePath))
                        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    var json = File.ReadAllText(filePath);
                    if (string.IsNullOrWhiteSpace(json))
                        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    var list = JsonSerializer.Deserialize<List<string>>(json);
                    return list != null 
                        ? new HashSet<string>(list, StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "❌ Failed to load blocked domains");
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        #endregion

        #region Recovery Operations

        /// <summary>
        /// Resets the send attempts for all unsent active reports to 0.
        /// </summary>
        public void ResetSendAttempts()
        {
            lock (_fileLock)
            {
                try
                {
                    var reports = LoadActiveReports();
                    bool modified = false;

                    foreach (var report in reports.Where(r => !r.Sent && r.SendAttempts > 0))
                    {
                        report.SendAttempts = 0;
                        report.LastError = null;
                        modified = true;
                    }

                    if (modified)
                    {
                        SaveReportsToFile(reports, _activeFilePath);
                        _logger?.LogInformation("🔄 Reset send attempts for unsent reports to enable retry.");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "❌ Failed to reset send attempts");
                }
            }
        }

        #endregion
    }
}