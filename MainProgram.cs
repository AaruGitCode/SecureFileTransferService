using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SecureFileTransferService
{
    public class MainProgram
    {
        private readonly IConfiguration _config;
        private readonly ILogger<MainProgram> _logger;
        private readonly IFileTransferService _transferService;

        private readonly string sourceFolder;
        private readonly string processedFolder;
        private readonly string errorFolder;
        private readonly int maxParallel;

        public MainProgram(
            IConfiguration config,
            ILogger<MainProgram> logger,
            IFileTransferService transferService)
            {
            _config = config;
            _logger = logger;
            _transferService = transferService;

            sourceFolder = _config["SourceSettings:RootFolder"]
                ?? throw new Exception("❌ SourceFolder missing");

            processedFolder = _config["SourceSettings:ProcessedFolder"]
                ?? throw new Exception("❌ ProcessedFolder missing");

            errorFolder = _config["SourceSettings:ErrorFolder"]
                ?? throw new Exception("❌ ErrorFolder missing");


            maxParallel = _config.GetValue<int>("WorkerSettings:MaxParallelFolders", 4);

            CreateSourceFolderStructure(_config);
        }
        public async Task RunAsync(CancellationToken token)
        {
            string mode =
                _config["TransferMode"] ?? "Upload";

            if (mode.Equals("Upload",
                StringComparison.OrdinalIgnoreCase))
            {
                await StartProgram(token);
            }
            else if (mode.Equals("Download",
                StringComparison.OrdinalIgnoreCase))
            {
                await StartDownloadProgram(token);
            }
        }

        public static void CreateSourceFolderStructure(IConfiguration config)
        {
            var sourceSettings = config
                .GetSection("SourceSettings")
                .Get<SourceSettings>();

            Directory.CreateDirectory(sourceSettings.RootFolder);

            foreach (var main in sourceSettings.FolderStructure)
            {
                string mainPath = Path.Combine(sourceSettings.RootFolder, main.Key);
                Directory.CreateDirectory(mainPath);

                foreach (var sub in main.Value)
                {
                    Directory.CreateDirectory(Path.Combine(mainPath, sub));
                }
            }

            Directory.CreateDirectory(sourceSettings.ProcessedFolder);
            Directory.CreateDirectory(sourceSettings.ErrorFolder);
        }

       
        public async Task StartProgram(CancellationToken token)
        {
            //_logger.LogInformation("SOURCE: {path}", sourceFolder);
            if (!_transferService.TestConnection())
                return;

            var folders = Directory.GetDirectories(sourceFolder, "*", SearchOption.AllDirectories);

            if (folders.Length == 0)
            {
                _logger.LogInformation("No folders found");
                return;
            }

            int batchSize = _config.GetValue<int>("WorkerSettings:BatchSize", 20);

            //STEP 1: Prepare batches per folder
            var folderBatches = new Dictionary<string, List<List<string>>>();
            _logger.LogInformation("====================================================");
            _logger.LogInformation("Processing Pending Folder");
            _logger.LogInformation("====================================================");
            foreach (var folder in folders)
            {

                var allFiles = Directory.GetFiles(folder);
                int stabilitySeconds = _config.GetValue<int>("WorkerSettings:FileModifiedBefore", 600);

                var files = allFiles
                    .Where(f => (DateTime.Now - File.GetLastWriteTime(f)).TotalSeconds > stabilitySeconds)
                    .ToArray();

                var batches = CreateBatches(files, batchSize);

                folderBatches[folder] = batches;
                
                if (files.Length != 0)
                {
                    _logger.LogInformation(
                        "Total Pending Files Found From Pending: {count} | Folder: {folder} | Batches: {batches}",
                        files.Length,
                        Path.GetRelativePath(sourceFolder, folder),
                        batches.Count
                    );
                }

            }
       
            int maxBatches = folderBatches.Max(f => f.Value.Count);

  
            if (maxBatches == 0)
            {
                _logger.LogInformation("====================================================");
                _logger.LogInformation("No Files found in Pending Folder");
                _logger.LogInformation("====================================================");
                _logger.LogInformation("");
                await RetryErrorFiles(token);
                return;
            }

            //STEP 2: Batch-by-batch processing
            for (int i = 0; i < maxBatches; i++)
            {
                _logger.LogInformation("====================================================");
                foreach (var folder in folders) //SEQUENTIAL folders
                {
                    var batches = folderBatches[folder];

                    if (i >= batches.Count)
                        continue;

                    var batch = batches[i];

                    var (success, skip, fail) = await _transferService.UploadBatchAsync(
                    batch,
                    sourceFolder,
                    GetDestinationRoot(),
                    token
                     );

                    foreach (var file in batch)
                    {
                        string relative = Path.GetRelativePath(sourceFolder, file);
                        string processedPath = Path.Combine(processedFolder, relative);

                        bool alreadyInProcessed = File.Exists(processedPath);

                        //If file already exists in processed → duplicate
                        if (alreadyInProcessed)
                        {
                            MoveToProcessedAsDuplicate(file);
                        }
                        else
                        {
                            MoveToProcessed(file);
                        }
                    }
                   
                    var shortFolder = Path.GetRelativePath(sourceFolder, folder);

                    //Build destination folder path
                    string destinationRoot = GetDestinationRoot().TrimEnd('/');
                    string destinationPath = $"{destinationRoot}/{shortFolder.Replace("\\", "/")}";

                    _logger.LogInformation(
                        "Pending Folder Batch uploaded| Folder: {folder} | Batch: {current}/{total} | Success: {success} | Skipped: {skip} | Failed: {fail} | Total: {total} | Destination Folder: {dest} ",
                        shortFolder,
                        i + 1,
                        batches.Count,
                        success,
                        skip,
                        fail,
                        batch.Count,
                        destinationPath
                    );
                    _logger.LogInformation("====================================================");
                    
                }

               
            }
        }
        private void MoveToProcessedAsDuplicate(string file)
        {
            try
            {
                string relative = Path.GetRelativePath(sourceFolder, file);
                string dest = Path.Combine(processedFolder, relative);

                string dir = Path.GetDirectoryName(dest)!;
                Directory.CreateDirectory(dir);

                string fileName = Path.GetFileName(dest);

                string newPath = Path.Combine(dir, "dup_" + fileName);

                int counter = 1;

                while (File.Exists(newPath))
                {
                    newPath = Path.Combine(dir, $"dup_{counter}_{fileName}");
                    counter++;
                }

                File.Move(file, newPath);

                //_logger.LogInformation("Moved Duplicate: {file} → {new}", file, newPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed duplicate move: {file}", file);
            }
        }

        //RETRY ERROR FILES
        private async Task RetryErrorFiles(CancellationToken token)
        {
            if (!Directory.Exists(errorFolder))
                return;

            var folders = Directory.GetDirectories(errorFolder, "*", SearchOption.AllDirectories);

            bool hasFiles = folders.Any(f => Directory.GetFiles(f).Length > 0);

            if (!hasFiles)
                return;

            _logger.LogInformation("====================================================");
            _logger.LogInformation("Processing Error Folder");
            _logger.LogInformation("====================================================");

            int batchSize = _config.GetValue<int>("WorkerSettings:BatchSize", 20);

            //STEP 1: Prepare batches per folder (same as pending)
            var folderBatches = new Dictionary<string, List<List<string>>>();

            foreach (var folder in folders)
            {
                var files = Directory.GetFiles(folder);

                if (files.Length == 0)
                    continue;

                var batches = CreateBatches(files, batchSize);
                folderBatches[folder] = batches;

                _logger.LogInformation(
                    "Total Pending Files Found From Pending: {count} | Folder: {folder} | Batches: {batches}",
                    files.Length,
                    Path.GetRelativePath(errorFolder, folder),
                    batches.Count
                );
            }

            if (folderBatches.Count == 0)
                return;

            int maxBatches = folderBatches.Max(f => f.Value.Count);

            //STEP 2: Batch-by-batch processing (same as pending)
            for (int i = 0; i < maxBatches; i++)
            {
                _logger.LogInformation("====================================================");

                foreach (var folder in folderBatches.Keys) // SEQUENTIAL folders
                {
                    var batches = folderBatches[folder];

                    if (i >= batches.Count)
                        continue;

                    var batch = batches[i];

                    try
                    {
                        var (success, skip, fail) = await _transferService.UploadBatchAsync(
                            batch,
                            errorFolder,
                            GetDestinationRoot(),
                            token
                        );

                        
                        if (fail == batch.Count)
                        {
                            //All files failed → send to error
                            foreach (var file in batch)
                                MoveToError(file);
                        }
                        else
                        {
                            //Full or partial success → mark processed
                            foreach (var file in batch)
                                MoveToProcessed(file);
                        }

                        var shortFolder = Path.GetRelativePath(errorFolder, folder);
                        _logger.LogInformation(
                            "Error Folder Batch uploaded| Folder: {folder} | Batch: {current}/{total} | Success: {success} | Skipped: {skip} | Failed: {fail} | Total: {total}",
                            shortFolder,
                            i + 1,
                            batches.Count,
                            success,
                            skip,
                            fail,
                            batch.Count
                        );
                        _logger.LogInformation("====================================================");
                    }
                    catch (ConnectionFailedException ex)
                    {
                        _logger.LogError("CONNECTION FAILED: {msg}", ex.Message);

                        foreach (var file in batch)
                            MoveToError(file);

                        return; // stop processing like pending would
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Batch failed in folder: {folder}", folder);

                        foreach (var file in batch)
                            MoveToError(file);
                    }

                    _logger.LogInformation("====================================================");
                    _logger.LogInformation("");
                }
            }
        }

        private string GetDestinationRoot()
        {
            return _config["DestinationSettings:DestinationFolder"] ?? "/";
        }

        private void MoveToProcessed(string file)
        {
            if (file.StartsWith(sourceFolder))
                MoveFile(file, sourceFolder, processedFolder);
            else if (file.StartsWith(errorFolder))
                MoveFile(file, errorFolder, processedFolder); //
        }

        private void MoveToError(string file)
        {
            MoveFile(file, sourceFolder, errorFolder);
        }

        private void MoveErrorToProcessed(string file)
        {
            MoveFile(file, errorFolder, processedFolder);
        }

        private void MoveFile(string file, string fromRoot, string toRoot)
        {
            try
            {
                string relative = Path.GetRelativePath(fromRoot, file);
                string dest = Path.Combine(toRoot, relative);

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                string fileName = Path.GetFileName(dest);
                string dir = Path.GetDirectoryName(dest)!;

                string finalPath = dest;

                //If file already exists → rename with dup_
                if (File.Exists(dest))
                {
                    finalPath = Path.Combine(dir, "dup_" + fileName);

                    int counter = 1;

                    // If dup already exists → make unique
                    while (File.Exists(finalPath))
                    {
                        finalPath = Path.Combine(dir, $"dup_{counter}_{fileName}");
                        counter++;
                    }
                }

                File.Move(file, finalPath);

                //_logger.LogInformation("Moved: {file} → {dest}", file, finalPath);
            }
            catch (Exception ex)
            {
                //_logger.LogError(ex, "Failed to move file: {file}", file);
            }
        }

        public static List<List<string>> CreateBatches(string[] files, int batchSize)
        {
            var batches = new List<List<string>>();

            for (int i = 0; i < files.Length; i += batchSize)
            {
                batches.Add(files.Skip(i).Take(batchSize).ToList());
            }

            return batches;
        }
        public async Task StartDownloadProgram(CancellationToken token)
        {
            string remoteFolder =
                _config["DownloadSettings:RemoteFolder"]!;

            string localFolder =
    _config["DownloadSettings:LocalFolder"]!;

            var (success, fail) =
                await _transferService.DownloadBatchAsync(
                    remoteFolder,
                    localFolder,
                    token);

            _logger.LogInformation(
                "Downloaded Success: {success} | Failed: {fail}",
                success,
                fail);
            _logger.LogInformation("====================================================");
            _logger.LogInformation(" ");
        }
        public class ConnectionFailedException : Exception
        {
            public ConnectionFailedException(string message) : base(message) { }
        }
        
    }
}