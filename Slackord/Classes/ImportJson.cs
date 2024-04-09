﻿using CommunityToolkit.Maui.Storage;
using MenuApp;
using Newtonsoft.Json.Linq;

namespace Slackord.Classes
{
    public class ImportJson
    {
        public static string RootFolderPath { get; private set; }
        public static List<Channel> Channels { get; set; } = new List<Channel>();
        public static int TotalHiddenFileCount { get; internal set; } = 0;

        public static async Task ImportJsonAsync(bool isFullExport, CancellationToken cancellationToken)
        {
            ApplicationWindow.HideProgressBar();

            // Clear existing data and reset counts
            Channels.Clear();
            TotalHiddenFileCount = 0;
            ApplicationWindow.ResetProgressBar();

            try
            {
                FolderPickerResult picker = await FolderPicker.Default.PickAsync(cancellationToken);

                // Check if picker is null or if its Folder property is null.
                if (picker == null || picker.Folder == null)
                {
                    // Check if the cancellation was requested
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // Log the cancellation message
                        _ = Application.Current.Dispatcher.Dispatch(() => { ApplicationWindow.WriteToDebugWindow("Folder selection was cancelled.\n"); });
                        return;
                    }
                    else
                    {
                        // Import was cancelled
                        return;
                    }
                }

                string folderPath = picker.Folder.Path;
                RootFolderPath = folderPath;
                Dictionary<string, DeconstructedUser> usersDict = null;
                if (!string.IsNullOrEmpty(folderPath))
                {
                    (List<Channel> Channels, Dictionary<string, DeconstructedUser> UsersDict) result = await ConvertAsync(isFullExport, folderPath, cancellationToken);
                    Channels = result.Channels;
                    usersDict = result.UsersDict;
                }

                // Populate UsersDict in Reconstruct before calling ReconstructAsync.
                Reconstruct.InitializeUsersDict(usersDict);

                // This checks whether any folder was selected and whether any channels were deconstructed.
                if (!string.IsNullOrEmpty(RootFolderPath) && Channels.Count != 0)
                {
                    // Call ReconstructAsync to reconstruct messages for Discord.
                    await Reconstruct.ReconstructAsync(Channels, cancellationToken);

                    // Write the total count of hidden files to the debug window.
                    if (TotalHiddenFileCount > 0)
                    {
                        _ = Application.Current.Dispatcher.Dispatch(() =>
                        {
                            ApplicationWindow.WriteToDebugWindow($"Total files hidden by Slack due to limits: {TotalHiddenFileCount}\n");
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Handle the cancellation exception
                _ = Application.Current.Dispatcher.Dispatch(() => { ApplicationWindow.WriteToDebugWindow("Import operation was cancelled.\n"); });
            }
            catch (Exception ex)
            {
                _ = Application.Current.Dispatcher.Dispatch(() => { ApplicationWindow.WriteToDebugWindow($"ImportJsonAsync() : {ex.Message}\n"); });
                return;
            }
        }

        public static async Task<(List<Channel> Channels, Dictionary<string, DeconstructedUser> UsersDict)> ConvertAsync(bool isFullExport, string folderPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DirectoryInfo directoryInfo = new(folderPath);
            DirectoryInfo rootDirectory = isFullExport ? directoryInfo : directoryInfo.Parent;

            // Fetch users.json and channels.json from the appropriate root directory.
            FileInfo usersFile = rootDirectory.GetFiles("users.json").FirstOrDefault();
            FileInfo channelsFile = rootDirectory.GetFiles("channels.json").FirstOrDefault();

            _ = Application.Current.Dispatcher.Dispatch(() => { ApplicationWindow.WriteToDebugWindow($"Parsing Users for import...\n"); });

            Dictionary<string, DeconstructedUser> usersDict = usersFile != null ? DeconstructedUsers.ParseUsersFile(usersFile) : new Dictionary<string, DeconstructedUser>();
            Dictionary<string, string> channelDescriptions = new();

            if (channelsFile != null)
            {
                string channelsJsonContent = await File.ReadAllTextAsync(channelsFile.FullName, cancellationToken).ConfigureAwait(false);
                JArray channelsJson = JArray.Parse(channelsJsonContent);
                channelDescriptions = channelsJson.ToDictionary(
                    jChannel => jChannel["name"].ToString(),
                    jChannel => jChannel["purpose"]["value"].ToString()
                );
            }

            DirectoryInfo[] channelDirectories = isFullExport ? rootDirectory.GetDirectories() : new DirectoryInfo[] { directoryInfo };

            List<Channel> channels = new();
            int totalFiles = CountTotalJsonFiles(channelDirectories);
            int filesProcessed = 0;

            foreach (DirectoryInfo channelDirectory in channelDirectories)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Channel channel = new() { Name = channelDirectory.Name };
                    FileInfo[] jsonFiles = channelDirectory.GetFiles("*.json");
                    int jsonFileCount = jsonFiles.Length;
                    _ = Application.Current.Dispatcher.Dispatch(() => { ApplicationWindow.WriteToDebugWindow($"Begin parsing JSON data for {channel.Name} with {jsonFileCount} JSON files...\n"); });

                    if (jsonFileCount > 400)
                    {
                        _ = Application.Current.Dispatcher.Dispatch(() => { ApplicationWindow.WriteToDebugWindow($"This import appears to be quite large. Reconstructing will take a very long time and the UI may freeze until completed. Please be patient!\nDeconstruction/Reconstruction process started...\n"); });
                    }

                    foreach (FileInfo jsonFile in jsonFiles)
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            string jsonContent = await File.ReadAllTextAsync(jsonFile.FullName, cancellationToken).ConfigureAwait(false);
                            JArray messagesArray = JArray.Parse(jsonContent);

                            foreach (JObject slackMessage in messagesArray.Cast<JObject>())
                            {
                                DeconstructedMessage deconstructedMessage = Deconstruct.DeconstructMessage(slackMessage);
                                channel.DeconstructedMessagesList.Add(deconstructedMessage);
                            }

                            filesProcessed++;
                            ApplicationWindow.UpdateProgressBar(filesProcessed, totalFiles, "files");
                        }
                        catch (Exception ex)
                        {
                            _ = Application.Current.Dispatcher.Dispatch(() => { ApplicationWindow.WriteToDebugWindow($"Exception processing file {jsonFile.Name}: {ex.Message}\n"); });
                        }
                    }

                    if (channelDescriptions != null && channelDescriptions.TryGetValue(channel.Name, out string description))
                    {
                        channel.Description = description;
                    }
                    channels.Add(channel);
                    _ = Application.Current.Dispatcher.Dispatch(() => { ApplicationWindow.WriteToDebugWindow($"Completed importing channel {channel.Name}.\n\n"); });
                }
                catch (Exception ex)
                {
                    _ = Application.Current.Dispatcher.Dispatch(() => { ApplicationWindow.WriteToDebugWindow($"Exception processing channel {channelDirectory.Name}: {ex.Message}\n"); });
                }
            }

            return (channels, usersDict);
        }

        private static int CountTotalJsonFiles(DirectoryInfo[] channelDirectories)
        {
            int totalFiles = 0;
            foreach (DirectoryInfo channelDirectory in channelDirectories)
            {
                totalFiles += channelDirectory.GetFiles("*.json").Length;
            }

            ApplicationWindow.ResetProgressBar();
            ApplicationWindow.ShowProgressBar();
            return totalFiles;
        }
    }
}
