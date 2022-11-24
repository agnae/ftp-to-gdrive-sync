using FluentFTP;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using MimeTypes;
using System.Security.Cryptography;
using System.Text.Json;
using Slack.Webhooks;
using Microsoft.Extensions.Configuration;
using ftp_to_gdrive_sync.Types;
using Sentry;
using Serilog;
using Serilog.Sinks.Graylog;

internal class Program
{
    private static DriveService _driveService;
    private static Google.Apis.Drive.v3.Data.File _rootFolder;
    private static List<ConfirmedFileUpload> _confirmedFileUploads;
    private static AppSettings _appSettings;
    private static SemaphoreSlim _folderSemaphore = new SemaphoreSlim(1, 1);
    private static SemaphoreSlim _confirmationsSemaphore = new SemaphoreSlim(1, 1);
    private static List<Task<bool>> _slackMessageTasks = new List<Task<bool>>();

    private static void Main(string[] args)
    {
        IConfigurationBuilder builder = new ConfigurationBuilder().AddJsonFile("appsettings.json", false, true);

        IConfigurationRoot root = builder.Build();
        var configuration = builder.Build();

        _appSettings = new AppSettings();
        configuration.Bind(_appSettings);

        IDisposable sentryDisposable = null;
        if (!string.IsNullOrWhiteSpace(_appSettings.Sentry.Dsn))
        {
            sentryDisposable = SentrySdk.Init(o =>
            {
                o.Dsn = _appSettings.Sentry.Dsn;
                // When configuring for the first time, to see what the SDK is doing:
                o.Debug = false;
                // Set traces_sample_rate to 1.0 to capture 100% of transactions for performance monitoring.
                // We recommend adjusting this value in production.
                o.TracesSampleRate = 1.0;
                // Enable Global Mode if running in a client app
                o.IsGlobalModeEnabled = true;
            });
        }

        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console();

        if (!string.IsNullOrWhiteSpace(_appSettings.Sentry.Dsn))
        {
            logConfig = logConfig.WriteTo.Sentry(dsn: _appSettings.Sentry.Dsn);
        }
        if (_appSettings.Graylog != null)
        {
            logConfig = logConfig.WriteTo.Graylog(_appSettings.Graylog);
        }

        Log.Logger = logConfig.CreateLogger();

        CancellationTokenSource cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            Log.Information("Canceling...");
            cts.Cancel();
            e.Cancel = true;
        };

        var authOnly = false;
        if (args.Length != 0)
        {
            if (args[0].ToUpperInvariant() == "AUTH")
            {
                authOnly = true;
            }
        }

        Log.Information("🤖 starting...");

        try
        {
            Task.Run(async () => await Run(authOnly, cts.Token)).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
        }

        Log.Information("🤖 exiting...");
        sentryDisposable?.Dispose();
    }

    private static async Task Run(bool authOnly, CancellationToken ct)
    {
        UserCredential credential;
        var tokenStore = new FileDataStore(_appSettings.GDrive.TokenDataStorePath);
        using (var stream = new FileStream(_appSettings.GDrive.ClientSecretsPath, FileMode.Open, FileAccess.Read))
        {
            credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                GoogleClientSecrets.FromStream(stream).Secrets,
                new[] { Google.Apis.Drive.v3.DriveService.Scope.DriveAppdata, Google.Apis.Drive.v3.DriveService.Scope.DriveFile },
                "user", CancellationToken.None, tokenStore);
        }

        Log.Information($"Using token store location: {tokenStore.FolderPath}");

        if (authOnly)
        {
            return;
        }

        _driveService = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = _appSettings.ApplicationName
        });

        var uploads = new List<Task<IUploadProgress>>();

        _rootFolder = await GetOrCreateFolder(_appSettings.GDrive.RootFolder);

        var countFilesAttempted = 0;
        do
        {
            countFilesAttempted = 0;
            foreach (var ftpSource in _appSettings.FtpSources)
            {
                using var ftpClient = new FtpClient(ftpSource.Host);
                ftpClient.AutoConnect();

                _confirmedFileUploads = await GetConfirmedUploads();

                foreach (var ftpFolder in ftpSource.Folders)
                {
                    foreach (FtpListItem item in ftpClient.GetListing(ftpFolder))
                    {
                        if (ct.IsCancellationRequested)
                        {
                            return;
                        }

                        long size = 0L;
                        if (item.Type == FtpObjectType.File)
                        {
                            size = ftpClient.GetFileSize(item.FullName);
                        }

                        if (size > 0)
                        {
                            DateTime time = item.RawModified;

                            string targetPath = Path.Combine(_appSettings.DownloadPath, item.Name);
                            string sourcePath = item.FullName;

                            // skip confirmed uploads
                            if (_confirmedFileUploads.Any(c => c.FileName == item.Name
                                && c.Year == time.Year.ToString()
                                && c.Month == time.Month.ToString().PadLeft(2, '0')
                                && c.Day == time.Day.ToString().PadLeft(2, '0')))
                            {
                                if (System.IO.File.Exists(targetPath))
                                {
                                    System.IO.File.Delete(targetPath);
                                }
                                continue;
                            }
                            countFilesAttempted++;

                            // download if file does not exist
                            if (!System.IO.File.Exists(targetPath))
                            {
                                Log.Information($"{item.Name}: starting download");

                                var downloadStatus = ftpClient.DownloadFile(targetPath, sourcePath);
                                if (downloadStatus == FtpStatus.Success)
                                {
                                    var uploadTask = UploadFile(targetPath, item.Name, time);
                                    uploads.Add(uploadTask);
                                    var postUploadVerifyTask = uploadTask.ContinueWith(async _ => await UploadFile(targetPath, item.Name, time));
                                }
                                else
                                {
                                    Log.Information($"{item.Name}: ftp status: {downloadStatus}");
                                }
                            }
                            else if (new System.IO.FileInfo(targetPath).Length != size) // ... or if filesize is mismatched between local and ftp:/
                            {
                                Log.Information($"{item.Name}: redownloading due to mismatch in filesizes. Remote: {size}bytes, Local: {new System.IO.FileInfo(targetPath).Length}bytes");

                                var downloadStatus = ftpClient.DownloadFile(targetPath, sourcePath);
                                if (downloadStatus == FtpStatus.Success)
                                {
                                    uploads.Add(UploadFile(targetPath, item.Name, time));
                                }
                                else
                                {
                                    Log.Information($"{item.Name}: ftp status: {downloadStatus}");
                                }
                            }
                            else
                            {
                                uploads.Add(UploadFile(targetPath, item.Name, time));
                            }
                        }
                    }
                }
            }

            Task.WaitAll(uploads.ToArray());
        } while (countFilesAttempted != 0);
    }

    private static async Task<Google.Apis.Drive.v3.Data.File> GetOrCreateFolder(string folderName, string parentId = "")
    {
        Google.Apis.Drive.v3.Data.File folder = null;

        await _folderSemaphore.WaitAsync();
        try
        {
            var listFoldersRequest = _driveService.Files.List();
            listFoldersRequest.Q = "mimeType = 'application/vnd.google-apps.folder' and trashed != true";

            if (!string.IsNullOrWhiteSpace(parentId))
            {
                listFoldersRequest.Q += $" and '{parentId}' in parents";
            }

            var result = await listFoldersRequest.ExecuteAsync();
            folder = result.Files.FirstOrDefault(f => f.Name == folderName);

            if (folder == null)
            {
                var fileMetadata = new Google.Apis.Drive.v3.Data.File()
                {
                    Name = folderName,
                    MimeType = "application/vnd.google-apps.folder",
                };

                if (!string.IsNullOrWhiteSpace(parentId))
                {
                    fileMetadata.Parents = new[] { parentId };
                }

                var request = _driveService.Files.Create(fileMetadata);
                folder = await request.ExecuteAsync();
            }
        }
        finally
        {
            _folderSemaphore.Release();
        }

        return folder;
    }

    private static async Task<IUploadProgress> UploadFile(string path, string fileName, DateTime modDate, bool verifyOnly = false)
    {
        var yearFolder = await GetOrCreateFolder(modDate.Year.ToString(), _rootFolder.Id);
        var monthFolder = await GetOrCreateFolder(modDate.Month.ToString().PadLeft(2, '0'), yearFolder.Id);
        var dayFolder = await GetOrCreateFolder(modDate.Day.ToString().PadLeft(2, '0'), monthFolder.Id);

        string hash = string.Empty;
        using (var bufferedStream = new BufferedStream(System.IO.File.Open(path, FileMode.Open), 1024 * 32))
        {
            var sha = HashAlgorithm.Create(HashAlgorithmName.SHA256.ToString());
            byte[] checksum = sha.ComputeHash(bufferedStream);
            hash = BitConverter.ToString(checksum).Replace("-", String.Empty).ToLower();
        };

        var uploadedFile = await FindFile(fileName, dayFolder.Id);

        using var stream = System.IO.File.Open(path, FileMode.Open);
        var uploadRequest = new FilesResource.CreateMediaUpload(_driveService, new Google.Apis.Drive.v3.Data.File
        {
            Name = fileName,
            Parents = new[] { dayFolder.Id },
        }, stream, MimeTypeMap.GetMimeType(System.IO.Path.GetExtension(fileName)));

        if (uploadedFile == null)
        {
            if (!verifyOnly)
            {
                Log.Information($"{fileName}: starting upload");
                var uploadTask = await uploadRequest.UploadAsync();
                if (uploadTask.Status == UploadStatus.Completed)
                {
                    // call uploadfile again to verify upload
                    uploadTask = await UploadFile(path, fileName, modDate, true);
                }
                return uploadTask;
            }
        }
        else if (uploadedFile.Sha256Checksum != hash)
        {
            if (!verifyOnly)
            {
                Log.Information($"{fileName}: reuploading due to hash mismatch. Remote: {uploadedFile.Sha256Checksum}, Local: {hash}");

                var uploadTask = await uploadRequest.UploadAsync();
                if (uploadTask.Status == UploadStatus.Completed)
                {
                    // call uploadfile again to verify upload
                    uploadTask = await UploadFile(path, fileName, modDate, true);
                }
                return uploadTask;
            }
        }
        else
        {
            AddConfirmedUpload(new ConfirmedFileUpload
            {
                FileName = uploadedFile.Name,
                Year = yearFolder.Name,
                Month = monthFolder.Name,
                Day = dayFolder.Name,
                FileSize = uploadedFile.Size.Value,
                Hash = uploadedFile.Sha256Checksum
            });

            SendSlackMessage($"<{uploadedFile.WebViewLink}|{fileName}>: upload completed");
        }

        return null;
    }

    private static async Task<Google.Apis.Drive.v3.Data.File> FindFile(string fileName, string parentId = "")
    {
        var listFilesRequest = _driveService.Files.List();
        listFilesRequest.Q = $"trashed != true";

        if (!string.IsNullOrWhiteSpace(parentId))
        {
            listFilesRequest.Q += $" and '{parentId}' in parents";
        }

        listFilesRequest.Fields = "*";

        var result = await listFilesRequest.ExecuteAsync();
        var file = result.Files.FirstOrDefault(f => f.Name == fileName);

        return file;
    }

    private static async Task<List<ConfirmedFileUpload>> GetConfirmedUploads()
    {
        var result = new List<ConfirmedFileUpload>();
        var listFilesRequest = _driveService.Files.List();
        listFilesRequest.Spaces = "appDataFolder";
        listFilesRequest.Q = $"name = 'confirmations.json'";
        listFilesRequest.Fields = "*";

        var response = await listFilesRequest.ExecuteAsync();
        var file = response.Files.FirstOrDefault();
        if (file != null)
        {
            using var stream = new MemoryStream();
            var downloaded = await _driveService.Files.Get(file.Id).DownloadAsync(stream);
            stream.Position = 0; // rewind!!!
            result = await JsonSerializer.DeserializeAsync<List<ConfirmedFileUpload>>(stream);
        }

        return result;
    }

    private static async void AddConfirmedUpload(ConfirmedFileUpload confirmedFileUpload)
    {
        await _confirmationsSemaphore.WaitAsync();
        try
        {
            _confirmedFileUploads.Add(confirmedFileUpload);
            await PersistConfirmedUploads();
        }
        finally
        {
            _confirmationsSemaphore.Release();
        }
    }

    private static async Task<IUploadProgress> PersistConfirmedUploads()
    {
        var listFilesRequest = _driveService.Files.List();
        listFilesRequest.Spaces = "appDataFolder";
        listFilesRequest.Q = $"name = 'confirmations.json'";

        var response = await listFilesRequest.ExecuteAsync();
        var file = response.Files.FirstOrDefault();
        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync<List<ConfirmedFileUpload>>(stream, _confirmedFileUploads);
        if (file != null)
        {
            return await _driveService.Files.Update(new Google.Apis.Drive.v3.Data.File(), file.Id, stream, "application/json").UploadAsync();
        }
        else
        {
            return await _driveService.Files.Create(new Google.Apis.Drive.v3.Data.File
            {
                Name = "confirmations.json",
                Parents = new[] { "appDataFolder" }
            }, stream, "application/json").UploadAsync();
        }
    }

    private static void SendSlackMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(_appSettings.Slack.WebhookUrl))
        {
            return;
        }

        var slackClient = new SlackClient(_appSettings.Slack.WebhookUrl);
        var slackMessage = new SlackMessage
        {
            Text = message,
            Markdown = true,
        };

        _slackMessageTasks.Add(slackClient.PostAsync(slackMessage));
    }
}