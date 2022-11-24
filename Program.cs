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

internal class Program
{
    private static DriveService _driveService;
    private static Google.Apis.Drive.v3.Data.File _rootFolder;
    private static List<ConfirmedFileUpload> _confirmedFileUploads;
    private static AppSettings _appSettings;

    private static void Main(string[] args)
    {
        IConfigurationBuilder builder = new ConfigurationBuilder().AddJsonFile("appsettings.json", false, true);

        IConfigurationRoot root = builder.Build();
        var configuration = builder.Build();

        _appSettings = new AppSettings();
        configuration.Bind(_appSettings);

        CancellationTokenSource cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            Console.WriteLine("Canceling...");
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

        SendSlackMessageNonBlocking("🤖 starting...");

        try
        {
            Task.Run(async () => await Run(authOnly, cts.Token)).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Task.Run(async () => await SendSlackMessageAsync($"🤖 exception! {e.Message}")).GetAwaiter().GetResult();
#pragma warning disable CA2200
            throw e;
#pragma warning disable CA2200
        }

        Task.Run(async () => await SendSlackMessageAsync("🤖 exiting...")).GetAwaiter().GetResult();
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

        Console.WriteLine($"Using token store location: {tokenStore.FolderPath}");

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

        _rootFolder = await GetOrCreateFolder(_appSettings.RootFolder);

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
                                SendSlackMessageNonBlocking($"{item.Name}: starting download");

                                var downloadStatus = ftpClient.DownloadFile(targetPath, sourcePath);
                                if (downloadStatus == FtpStatus.Success)
                                {
                                    var uploadTask = UploadFile(targetPath, item.Name, time);
                                    uploads.Add(uploadTask);
                                    var postUploadVerifyTask = uploadTask.ContinueWith(async _ => await UploadFile(targetPath, item.Name, time));
                                }
                                else
                                {
                                    SendSlackMessageNonBlocking($"{item.Name}: ftp status: {downloadStatus}");
                                }
                            }
                            else if (new System.IO.FileInfo(targetPath).Length != size) // ... or if filesize is mismatched between local and ftp:/
                            {
                                SendSlackMessageNonBlocking($"{item.Name}: redownloading due to mismatch in filesizes. Remote: {size}bytes, Local: {new System.IO.FileInfo(targetPath).Length}bytes");

                                var downloadStatus = ftpClient.DownloadFile(targetPath, sourcePath);
                                if (downloadStatus == FtpStatus.Success)
                                {
                                    uploads.Add(UploadFile(targetPath, item.Name, time));
                                }
                                else
                                {
                                    SendSlackMessageNonBlocking($"{item.Name}: ftp status: {downloadStatus}");
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
            await PersistConfirmedUploads();

        } while (countFilesAttempted != 0);
    }

    private static async Task<Google.Apis.Drive.v3.Data.File> GetOrCreateFolder(string folderName, string parentId = "")
    {
        var listFoldersRequest = _driveService.Files.List();
        listFoldersRequest.Q = "mimeType = 'application/vnd.google-apps.folder' and trashed != true";

        if (!string.IsNullOrWhiteSpace(parentId))
        {
            listFoldersRequest.Q += $" and '{parentId}' in parents";
        }

        var result = await listFoldersRequest.ExecuteAsync();
        var folder = result.Files.FirstOrDefault(f => f.Name == folderName);

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
                SendSlackMessageNonBlocking($"{fileName}: starting upload");
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
                SendSlackMessageNonBlocking($"{fileName}: reuploading due to hash mismatch. Remote: {uploadedFile.Sha256Checksum}, Local: {hash}");

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

            SendSlackMessageNonBlocking($"<{uploadedFile.WebViewLink}|{fileName}>: upload completed", true);
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

    private static void AddConfirmedUpload(ConfirmedFileUpload confirmedFileUpload)
    {
        _confirmedFileUploads.Add(confirmedFileUpload);
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

    private static void SendSlackMessageNonBlocking(string message, bool force = false)
    {
#pragma warning disable CS4014
        SendSlackMessageAsync(message, force);
#pragma warning restore CS4014
    }

    private static async Task SendSlackMessageAsync(string message, bool force = false)
    {
        Console.WriteLine(message);

        if (!_appSettings.LogProgressToSlack && !force)
        {
            return;
        }

        var slackClient = new SlackClient(_appSettings.Slack.WebhookUrl);
        var slackMessage = new SlackMessage
        {
            Text = message,
            Markdown = true,
        };
        await slackClient.PostAsync(slackMessage);
    }
}