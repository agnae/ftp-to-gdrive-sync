# ftp-to-gdrive-sync
Download files from an FTP source (anonymous) and upload to Google Drive. w/ Slack notification when completed



## Usage
 1. Obtain `client_secrets.json` from Google Drive API
 1. Create `appsettings.config` (example: `appsettings.example.config`)

### config options
 - ftpSources
   - An array of ftp servers to download files from
      - each one can have an array of folders to download files from
 - gDrive
    - clientSecretsPath: the path to the client_secrets.json provided after configuring Google Drive API (as client_type: desktop)
    - tokenDataStorePath: path to store (File) refresh token
    - rootFolder: the name of the root folder to upload files to in the google drive account
 - slack
   - webhookUrl: the webhook url to send a message to after file has completed uploading with the sharelink, leave as an empty string to disable
 - sentry
   - dsn: Sentry's Data Source Name. Leave empty to disable
 - graylog
   - See for possible options (public fields): https://github.com/whir1/serilog-sinks-graylog/blob/78102039a1568c744cd62be07abba4f2aa1cfb4b/src/Serilog.Sinks.Graylog.Core/GraylogSinkOptions.cs
   - leave as an empty object to disable
   - applicationName
     - name of application (mainly used in the calls to gdrive)
   - downloadPath
     - the local path to download files from the ftp server
   - ignoreDotFiles
     - wether or not to ignore files starting with a '.'
     
### Execute
#### First run, authenticate
Running `./ftp-to-gdrive-sync auth` will open up a browser with to begin the oath flow. Once authenticated, program will end.

#### Subsequent runs
Run `./ftp-to-gdrive-sync` with no args. If authentication was not previously performed, oauth flow will begin and program will continue running as normal
