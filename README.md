# SecureFileTransferService

## Overview
SecureFileTransferService is a .NET Worker Service that automates file transfers between local folders and remote FTP/SFTP servers.

## Features
- Supports FTP and SFTP
- Batch file upload and download
- Parallel folder processing
- Automatic folder synchronization
- Configurable through appsettings.json
- Logging with NLog
- Windows Service support

## Technologies
- C#
- .NET
- FluentFTP
- Renci.SshNet
- NLog

## Configuration
Update the `appsettings.json` file with your own:
- FTP/SFTP server details
- Source and destination folders
- Worker settings

> Do not commit real usernames, passwords, or server details.

## How to Run

1. Clone the repository.
2. Open the solution in Visual Studio.
3. Restore NuGet packages.
4. Update `appsettings.json`.
5. Build and run the project.

## Project Structure

- `Services/` – File transfer logic
- `Configuration/` – Configuration classes
- `Helpers/` – Utility classes

## License

This project is for learning and demonstration purposes.
