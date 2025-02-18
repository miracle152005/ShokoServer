﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.SQLite;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Timers;
using LeanWork.IO.FileSystem;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Extensions.Logging;
using NHibernate;
using NLog.Targets;
using NLog.Web;
using NutzCode.CloudFileSystem;
using NutzCode.CloudFileSystem.OAuth2;
using Shoko.Commons.Properties;
using Shoko.Models.Enums;
using Shoko.Models.Server;
using Shoko.Server.Commands;
using Shoko.Server.Commands.Azure;
using Shoko.Server.Commands.Plex;
using Shoko.Server.Databases;
using Shoko.Server.Extensions;
using Shoko.Server.FileHelper;
using Shoko.Server.FileHelper.Subtitles;
using Shoko.Server.ImageDownload;
using Shoko.Server.Models;
using Shoko.Server.MyAnime2Helper;
using Shoko.Server.Providers.JMMAutoUpdates;
using Shoko.Server.Repositories;
using Shoko.Server.Settings;
using Shoko.Server.UI;
using Trinet.Core.IO.Ntfs;
using Shoko.Server.API;
using Action = System.Action;
using LogLevel = NLog.LogLevel;
using Timer = System.Timers.Timer;

namespace Shoko.Server
{
    public class ShokoServer
    {
        //private static bool doneFirstTrakTinfo = false;
        private static Logger logger = LogManager.GetCurrentClassLogger();
        internal static LogRotator logrotator = new LogRotator();
        private static DateTime lastTraktInfoUpdate = DateTime.Now;
        private static DateTime lastVersionCheck = DateTime.Now;

        public static DateTime? StartTime = null;

        public static TimeSpan? UpTime => StartTime == null ? null : DateTime.Now - StartTime;

        internal static BlockingList<FileSystemEventArgs> queueFileEvents = new BlockingList<FileSystemEventArgs>();
        private static BackgroundWorker workerFileEvents = new BackgroundWorker();

        public static string PathAddressREST = "api/Image";
        public static string PathAddressPlex = "api/Plex";
        public static string PathAddressKodi = "Kodi";

        private static IWebHost webHost;

        private static BackgroundWorker workerImport = new BackgroundWorker();
        private static BackgroundWorker workerScanFolder = new BackgroundWorker();
        private static BackgroundWorker workerScanDropFolders = new BackgroundWorker();
        private static BackgroundWorker workerRemoveMissing = new BackgroundWorker();
        private static BackgroundWorker workerDeleteImportFolder = new BackgroundWorker();
        private static BackgroundWorker workerMyAnime2 = new BackgroundWorker();
        private static BackgroundWorker workerMediaInfo = new BackgroundWorker();

        private static BackgroundWorker workerSyncHashes = new BackgroundWorker();
        private static BackgroundWorker workerSyncMedias = new BackgroundWorker();

        internal static BackgroundWorker workerSetupDB = new BackgroundWorker();
        internal static BackgroundWorker LogRotatorWorker = new BackgroundWorker();

        private static Timer autoUpdateTimer;
        private static Timer autoUpdateTimerShort;
        private static Timer cloudWatchTimer;
        internal static Timer LogRotatorTimer;

        DateTime lastAdminMessage = DateTime.Now.Subtract(new TimeSpan(12, 0, 0));
        private static List<RecoveringFileSystemWatcher> watcherVids;

        BackgroundWorker downloadImagesWorker = new BackgroundWorker();

        public static List<UserCulture> userLanguages = new List<UserCulture>();

        public IOAuthProvider OAuthProvider { get; set; } = new AuthProvider();
        
        private Mutex mutex;

        public string[] GetSupportedDatabases()
        {
            return new[]
            {
                "SQLite",
                "Microsoft SQL Server 2014",
                "MySQL/MariaDB"
            };
        }

        private ShokoServer() { }

        public void InitLogger()
        {
            var target = (FileTarget) LogManager.Configuration?.FindTargetByName("file");
            if (target != null)
            {
                target.FileName = ServerSettings.ApplicationPath + "/logs/${shortdate}.log";
                LogManager.ReconfigExistingLoggers();
            }
        }

        public bool StartUpServer()
        {
            Analytics.PostEvent("Server", "Startup");
            if (Utils.IsLinux)
                Analytics.PostEvent("Server", "Linux Startup");

            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(ServerSettings.Instance.Culture);

            // Check if any of the DLL are blocked, common issue with daily builds
            if (!CheckBlockedFiles())
            {
                Utils.ShowErrorMessage(Resources.ErrorBlockedDll);
                Environment.Exit(1);
            }

            // Migrate programdata folder from JMMServer to ShokoServer
            // this needs to run before UnhandledExceptionManager.AddHandler(), because that will probably lock the log file
            if (!MigrateProgramDataLocation())
            {
                Utils.ShowErrorMessage(Resources.Migration_LoadError,
                    Resources.ShokoServer);
                Environment.Exit(1);
            }

            // First check if we have a settings.json in case migration had issues as otherwise might clear out existing old configurations
            string path = Path.Combine(ServerSettings.ApplicationPath, "settings.json");
            if (File.Exists(path))
            {
                Thread t = new Thread(UninstallJMMServer) {IsBackground = true};
                t.Start();
            }

            //HibernatingRhinos.Profiler.Appender.NHibernate.NHibernateProfiler.Initialize();
            CommandHelper.LoadCommands();

            try
            {
                UnhandledExceptionManager.AddHandler();
            }
            catch (Exception e)
            {
                logger.Log(NLog.LogLevel.Error, e);
            }

            try
            {
                mutex = Mutex.OpenExisting(ServerSettings.DefaultInstance + "Mutex");
                //since it hasn't thrown an exception, then we already have one copy of the app open.
                return false;
                //MessageBox.Show(Shoko.Commons.Properties.Resources.Server_Running,
                //    Shoko.Commons.Properties.Resources.ShokoServer, MessageBoxButton.OK, MessageBoxImage.Error);
                //Environment.Exit(0);
            }
            catch (Exception Ex)
            {
                //since we didn't find a mutex with that name, create one
                Debug.WriteLine("Exception thrown:" + Ex.Message + " Creating a new mutex...");
                mutex = new Mutex(true, ServerSettings.DefaultInstance + "Mutex");
            }
            ServerSettings.Instance.DebugSettingsToLog();
            RenameFileHelper.InitialiseRenamers();

            workerFileEvents.WorkerReportsProgress = false;
            workerFileEvents.WorkerSupportsCancellation = false;
            workerFileEvents.DoWork += WorkerFileEvents_DoWork;
            workerFileEvents.RunWorkerCompleted += WorkerFileEvents_RunWorkerCompleted;

            //logrotator worker setup
            LogRotatorWorker.WorkerReportsProgress = false;
            LogRotatorWorker.WorkerSupportsCancellation = false;
            LogRotatorWorker.DoWork += LogRotatorWorker_DoWork;
            LogRotatorWorker.RunWorkerCompleted +=
                LogRotatorWorker_RunWorkerCompleted;

            ServerState.Instance.DatabaseAvailable = false;
            ServerState.Instance.ServerOnline = false;
            ServerState.Instance.ServerStarting = false;
            ServerState.Instance.StartupFailed = false;
            ServerState.Instance.StartupFailedMessage = string.Empty;
            ServerState.Instance.BaseImagePath = ImageUtils.GetBaseImagesPath();

            downloadImagesWorker.DoWork += DownloadImagesWorker_DoWork;
            downloadImagesWorker.WorkerSupportsCancellation = true;

            workerMyAnime2.DoWork += WorkerMyAnime2_DoWork;
            workerMyAnime2.RunWorkerCompleted += WorkerMyAnime2_RunWorkerCompleted;
            workerMyAnime2.ProgressChanged += WorkerMyAnime2_ProgressChanged;
            workerMyAnime2.WorkerReportsProgress = true;

            workerMediaInfo.DoWork += WorkerMediaInfo_DoWork;

            workerImport.WorkerReportsProgress = true;
            workerImport.WorkerSupportsCancellation = true;
            workerImport.DoWork += WorkerImport_DoWork;

            workerScanFolder.WorkerReportsProgress = true;
            workerScanFolder.WorkerSupportsCancellation = true;
            workerScanFolder.DoWork += WorkerScanFolder_DoWork;


            workerScanDropFolders.WorkerReportsProgress = true;
            workerScanDropFolders.WorkerSupportsCancellation = true;
            workerScanDropFolders.DoWork += WorkerScanDropFolders_DoWork;


            workerSyncHashes.WorkerReportsProgress = true;
            workerSyncHashes.WorkerSupportsCancellation = true;
            workerSyncHashes.DoWork += WorkerSyncHashes_DoWork;

            workerSyncMedias.WorkerReportsProgress = true;
            workerSyncMedias.WorkerSupportsCancellation = true;
            workerSyncMedias.DoWork += WorkerSyncMedias_DoWork;

            workerRemoveMissing.WorkerReportsProgress = true;
            workerRemoveMissing.WorkerSupportsCancellation = true;
            workerRemoveMissing.DoWork += WorkerRemoveMissing_DoWork;

            workerDeleteImportFolder.WorkerReportsProgress = false;
            workerDeleteImportFolder.WorkerSupportsCancellation = true;
            workerDeleteImportFolder.DoWork += WorkerDeleteImportFolder_DoWork;

            workerSetupDB.WorkerReportsProgress = true;
            workerSetupDB.ProgressChanged += (sender, args) => WorkerSetupDB_ReportProgress();
            workerSetupDB.DoWork += WorkerSetupDB_DoWork;
            workerSetupDB.RunWorkerCompleted += WorkerSetupDB_RunWorkerCompleted;

            ServerState.Instance.LoadSettings();

            InitCulture();
            Instance = this;

            // run rotator once and set 24h delay
            logrotator.Start();
            StartLogRotatorTimer();

            try
            {
                if (!CloudFileSystemPluginFactory.Instance.List.Any())
                {
                    logger.Error(
                        "No Filesystem Handlers were loaded. THIS IS A PROBLEM. The most likely cause is permissions issues in the installation directory.");
                    return false;
                }

                var localHandler = CloudFileSystemPluginFactory.Instance.List.FirstOrDefault(handler =>
                    handler.Name.EqualsInvariantIgnoreCase("Local File System"));
                if (localHandler == null)
                {
                    string handlers = string.Join(", ", CloudFileSystemPluginFactory.Instance.List.Select(a => a.Name));
                    logger.Warn(
                        $"The local filesystem handler could not be found. These Filesystem Handlers were loaded: {handlers}");
                }

                var initResult = localHandler.Init("", null, null);
                if (initResult == null || !initResult.IsOk || initResult.Result == null)
                {
                    logger.Warn("The Local Filesystem handler failed to init. This will likely cause issues.");
                    if (!string.IsNullOrWhiteSpace(initResult?.Error))
                        logger.Error($"The error was: {initResult.Error}");
                }
            }
            catch (Exception e)
            {
                logger.Error("There was an error loading any Filesystem handlers. CloudFileSystem is missing, has bad permissions, or has a fatal error in its loading sequence (not likely).");
                logger.Error(e);
                return false;
            }

            SetupNetHosts();

            Analytics.PostEvent("Server", "StartupFinished");
            return true;
        }

        private bool CheckBlockedFiles()
        {
            if (Utils.IsRunningOnMono()) return true;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                // do stuff on windows only
                return true;
            }
            string programlocation =
                Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            string[] dllFiles = Directory.GetFiles(programlocation, "*.dll", SearchOption.AllDirectories);
            bool result = true;

            foreach (string dllFile in dllFiles)
            {
                if (FileSystem.AlternateDataStreamExists(dllFile, "Zone.Identifier"))
                {
                    try
                    {
                        FileSystem.DeleteAlternateDataStream(dllFile, "Zone.Identifier");
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            foreach (string dllFile in dllFiles)
            {
                if (FileSystem.AlternateDataStreamExists(dllFile, "Zone.Identifier"))
                {
                    logger.Log(LogLevel.Error, "Found blocked DLL file: " + dllFile);
                    result = false;
                }
            }


            return result;
        }

        public bool MigrateProgramDataLocation()
        {
            string oldApplicationPath =
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "JMMServer");
            string newApplicationPath =
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    Assembly.GetEntryAssembly().GetName().Name);
            if (Directory.Exists(oldApplicationPath) && !Directory.Exists(newApplicationPath))
            {
                try
                {
                    List<MigrationDirectory> migrationdirs = new List<MigrationDirectory>
                    {
                        new MigrationDirectory
                        {
                            From = oldApplicationPath,
                            To = newApplicationPath
                        }
                    };

                    foreach (MigrationDirectory md in migrationdirs)
                    {
                        if (!md.SafeMigrate())
                        {
                            break;
                        }
                    }

                    logger.Log(LogLevel.Info, "Successfully migrated programdata folder.");
                }
                catch (Exception e)
                {
                    logger.Log(LogLevel.Error, "Error occured during MigrateProgramDataLocation()");
                    logger.Error(e);
                    return false;
                }
            }
            return true;
        }

        void UninstallJMMServer()
        {
            if (Utils.IsRunningOnMono()) return; //This will be handled by the OS or user, as we cannot reliably learn what package management system they use.
            try
            {
                // Check in registry if installed
                string jmmServerUninstallPath =
                    (string)
                    Registry.GetValue(
                        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{898530ED-CFC7-4744-B2B8-A8D98A2FA06C}_is1",
                        "UninstallString", null);

                if (!string.IsNullOrEmpty(jmmServerUninstallPath))
                {

                    // Ask if user wants to uninstall first
                    bool res = Utils.ShowYesNo(Resources.DuplicateInstallDetectedQuestion, Resources.DuplicateInstallDetected);

                    if (res)
                    {
                        try
                        {
                            ProcessStartInfo startInfo = new ProcessStartInfo
                            {
                                FileName = jmmServerUninstallPath,
                                Arguments = " /SILENT"
                            };
                            Process.Start(startInfo);

                            logger.Log(LogLevel.Info, "JMM Server successfully uninstalled");
                        }
                        catch
                        {
                            logger.Log(LogLevel.Error, "Error occured during uninstall of JMM Server");
                        }
                    }
                    else
                    {
                        logger.Log(LogLevel.Info, "User cancelled JMM Server uninstall");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Error, "Error occured during UninstallJMMServer: " + ex);
            }
        }

        public bool NetPermissionWrapper(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                if (Utils.IsAdministrator())
                {
                    Utils.ShowMessage(null, "Settings the ports, after that JMMServer will quit, run again in normal mode");

                    try
                    {
                        action();
                    }
                    catch (Exception exception)
                    {
                        Utils.ShowErrorMessage("Unable start hosting");
                        logger.Error("Unable to run task: " + (action.Method?.Name ?? action.ToString()));
                        logger.Error(exception);
                    }
                    finally
                    {
                        ShutDown();
                    }
                    return false;
                }
                Utils.ShowErrorMessage("Unable to start hosting, please run JMMServer as administrator once.");
                logger.Error(e);
                ShutDown();
                return false;
            }
            return true;
        }

        public void ApplicationShutdown()
        {
            try
            {
                ThreadStart ts = () =>
                {

                    ServerSettings.DoServerShutdown(new ServerSettings.ReasonedEventArgs());
                    Environment.Exit(0);
                };
                new Thread(ts).Start();
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Error, $"Error occured during ApplicationShutdown: {ex.Message}");
            }
        }

        private void LogRotatorWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // for later use
        }

        private void LogRotatorWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            logrotator.Start();
        }

        public static ShokoServer Instance { get; private set; } = new ShokoServer();

        private void WorkerSyncHashes_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                Importer.SyncHashes();
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        private void WorkerSyncMedias_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                Importer.SyncMedia();
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        void WorkerFileEvents_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            logger.Info("Stopped thread for processing file creation events");
        }

        void WorkerFileEvents_DoWork(object sender, DoWorkEventArgs e)
        {
            logger.Info("Started thread for processing file events");
            FileSystemEventArgs evt;
            try
            {
                evt = queueFileEvents.GetNextItem();
            }
            catch (Exception exception)
            {
                logger.Error(exception);
                evt = null;
            }
            while(evt != null)
            {
                try
                {
                    // this is a message to stop processing
                    if (evt == null)
                    {
                        return;
                    }
                    if (evt.ChangeType == WatcherChangeTypes.Created || evt.ChangeType == WatcherChangeTypes.Renamed)
                    {
                        if (evt.FullPath.StartsWith("|CLOUD|"))
                        {
                            int shareid = int.Parse(evt.Name);
                            Importer.RunImport_ImportFolderNewFiles(RepoFactory.ImportFolder.GetByID(shareid));
                        }
                        else
                        {
                            // When the path that was created represents a directory we need to manually get the contained files to add.
                            // The reason for this is that when a directory is moved into a source directory (from the same drive) we will only recieve
                            // an event for the directory and not the contained files. However, if the folder is copied from a different drive then
                            // a create event will fire for the directory and each file contained within it (As they are all treated as separate operations)

                            // This is faster and doesn't throw on weird paths. I've had some UTF-16/UTF-32 paths cause serious issues
                            if (Directory.Exists(evt.FullPath)) // filter out invalid events
                            {
                                logger.Info("New folder detected: {0}: {1}", evt.FullPath, evt.ChangeType);

                                string[] files = Directory.GetFiles(evt.FullPath, "*.*", SearchOption.AllDirectories);

                                foreach (string file in files)
                                {
                                    if (FileHashHelper.IsVideo(file))
                                    {
                                        logger.Info("Found file {0} under folder {1}", file, evt.FullPath);

                                        CommandRequest_HashFile cmd = new CommandRequest_HashFile(file, false);
                                        cmd.Save();
                                    }
                                }
                            }
                            else if (File.Exists(evt.FullPath))
                            {
                                logger.Info("New file detected: {0}: {1}", evt.FullPath, evt.ChangeType);

                                if (FileHashHelper.IsVideo(evt.FullPath))
                                {
                                    logger.Info("Found file {0}", evt.FullPath);

                                    CommandRequest_HashFile cmd = new CommandRequest_HashFile(evt.FullPath, false);
                                    cmd.Save();
                                }
                            }
                            // else it was deleted before we got here
                        }
                    }
                    queueFileEvents.Remove(evt);
                    try
                    {
                        evt = queueFileEvents.GetNextItem();
                    }
                    catch (Exception exception)
                    {
                        logger.Error(exception);
                        evt = null;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "FSEvents_DoWork file: {0}\n{1}", evt.Name, ex);
                    queueFileEvents.Remove(evt);
                    Thread.Sleep(1000);
                }
            }
        }

        void InitCulture()
        {

        }


        #region Database settings and initial start up

        public event EventHandler LoginFormNeeded;
        public event EventHandler DatabaseSetup;
        public event EventHandler DBSetupCompleted;
        void WorkerSetupDB_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            ServerState.Instance.ServerStarting = false;
            bool setupComplete = bool.Parse(e.Result.ToString());
            if (!setupComplete)
            {
                ServerState.Instance.ServerOnline = false;
                if (!string.IsNullOrEmpty(ServerSettings.Instance.Database.Type)) return;
                ServerSettings.Instance.Database.Type = Constants.DatabaseType.Sqlite;
                ShowDatabaseSetup();
            }
        }

        void WorkerSetupDB_ReportProgress()
        {
            logger.Info("Starting Server: Complete!");
            ServerInfo.Instance.RefreshImportFolders();
            ServerInfo.Instance.RefreshCloudAccounts();
            ServerState.Instance.CurrentSetupStatus = Resources.Server_Complete;
            ServerState.Instance.ServerOnline = true;
            ServerSettings.Instance.FirstRun = false;
            ServerSettings.Instance.SaveSettings();
            if (string.IsNullOrEmpty(ServerSettings.Instance.AniDb.Username) ||
                string.IsNullOrEmpty(ServerSettings.Instance.AniDb.Password))
                LoginFormNeeded?.Invoke(Instance, null);
            DBSetupCompleted?.Invoke(Instance, null);
        }

        private void ShowDatabaseSetup() => DatabaseSetup?.Invoke(Instance, null);

        public static void StartFileWorker()
        {
            if (!workerFileEvents.IsBusy)
                workerFileEvents.RunWorkerAsync();
        }

        public static void StartCloudWatchTimer()
        {
            cloudWatchTimer = new Timer
            {
                AutoReset = true,
                Interval = ServerSettings.Instance.CloudWatcherTime * 60 * 1000
            };
            cloudWatchTimer.Elapsed += CloudWatchTimer_Elapsed;
            cloudWatchTimer.Start();
        }

        public static void StartLogRotatorTimer()
        {
            LogRotatorTimer = new Timer
            {
                AutoReset = true,
                // 86400000 = 24h
                Interval = 86400000
            };
            LogRotatorTimer.Elapsed += LogRotatorTimer_Elapsed;
            LogRotatorTimer.Start();
        }

        private static void LogRotatorTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            logrotator.Start();
        }

        public static void StopCloudWatchTimer()
        {
            cloudWatchTimer?.Stop();
        }

        private static void CloudWatchTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                foreach (SVR_ImportFolder share in RepoFactory.ImportFolder.GetAll()
                    .Where(a => a.CloudID.HasValue && a.FolderIsWatched))
                {
                    //Little hack in there to reuse the file queue
                    FileSystemEventArgs args = new FileSystemEventArgs(WatcherChangeTypes.Created, "|CLOUD|",
                        share.ImportFolderID.ToString());
                    queueFileEvents.Add(args);
                    StartFileWorker();
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        public void SetupNetHosts()
        {
            logger.Info("Initializing Web Hosts...");
            ServerState.Instance.CurrentSetupStatus = Resources.Server_InitializingHosts;
            bool started = true;
            started &= NetPermissionWrapper(StartWebHost);
            if (!started)
            {
                StopHost();
                throw new Exception("Failed to start all of the network hosts");
            }
        }
        
        public void RestartAniDBSocket()
        {
            AniDBDispose();
            SetupAniDBProcessor();
        }

        void WorkerSetupDB_DoWork(object sender, DoWorkEventArgs e)
        {
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(ServerSettings.Instance.Culture);

            try
            {
                ServerState.Instance.ServerOnline = false;
                ServerState.Instance.ServerStarting = true;
                ServerState.Instance.StartupFailed = false;
                ServerState.Instance.StartupFailedMessage = string.Empty;
                ServerState.Instance.CurrentSetupStatus = Resources.Server_Cleaning;

                StopWatchingFiles();

                RestartAniDBSocket();

                ShokoService.CmdProcessorGeneral.Stop();
                ShokoService.CmdProcessorHasher.Stop();
                ShokoService.CmdProcessorImages.Stop();


                // wait until the queue count is 0
                // ie the cancel has actuall worked
                while (true)
                {
                    if (ShokoService.CmdProcessorGeneral.QueueCount == 0 &&
                        ShokoService.CmdProcessorHasher.QueueCount == 0 &&
                        ShokoService.CmdProcessorImages.QueueCount == 0) break;
                    Thread.Sleep(250);
                }

                if (autoUpdateTimer != null) autoUpdateTimer.Enabled = false;
                if (autoUpdateTimerShort != null) autoUpdateTimerShort.Enabled = false;

                DatabaseFactory.CloseSessionFactory();

                ServerState.Instance.CurrentSetupStatus = Resources.Server_Initializing;
                Thread.Sleep(1000);

                ServerState.Instance.CurrentSetupStatus = Resources.Server_DatabaseSetup;

                logger.Info("Setting up database...");
                if (!DatabaseFactory.InitDB(out string errorMessage))
                {
                    ServerState.Instance.DatabaseAvailable = false;

                    if (string.IsNullOrEmpty(ServerSettings.Instance.Database.Type))
                        ServerState.Instance.CurrentSetupStatus =
                            Resources.Server_DatabaseConfig;
                    e.Result = false;
                    ServerState.Instance.StartupFailed = true;
                    ServerState.Instance.StartupFailedMessage = errorMessage;
                    return;
                }

                ServerState.Instance.DatabaseAvailable = true;

                logger.Info("Initializing Session Factory...");
                //init session factory
                ServerState.Instance.CurrentSetupStatus = Resources.Server_InitializingSession;
                ISessionFactory _ = DatabaseFactory.SessionFactory;

                // We need too much of the database initialized to do this anywhere else.
                DatabaseFixes.FixAniDB_EpisodesWithMissingTitles();

                Scanner.Instance.Init();

                ServerState.Instance.CurrentSetupStatus = Resources.Server_InitializingQueue;
                ShokoService.CmdProcessorGeneral.Init();
                ShokoService.CmdProcessorHasher.Init();
                ShokoService.CmdProcessorImages.Init();


                // timer for automatic updates
                autoUpdateTimer = new Timer
                {
                    AutoReset = true,
                    Interval = 5 * 60 * 1000 // 5 * 60 seconds (5 minutes)
                };
                autoUpdateTimer.Elapsed += AutoUpdateTimer_Elapsed;
                autoUpdateTimer.Start();

                // timer for automatic updates
                autoUpdateTimerShort = new Timer
                {
                    AutoReset = true,
                    Interval = 5 * 1000 // 5 seconds, later we set it to 30 seconds
                };
                autoUpdateTimerShort.Elapsed += AutoUpdateTimerShort_Elapsed;
                autoUpdateTimerShort.Start();

                ServerState.Instance.CurrentSetupStatus = Resources.Server_InitializingFile;

                StartFileWorker();

                StartWatchingFiles();

                DownloadAllImages();

                IReadOnlyList<SVR_ImportFolder> folders = RepoFactory.ImportFolder.GetAll();

                if (ServerSettings.Instance.Import.ScanDropFoldersOnStart) ScanDropFolders();
                if (ServerSettings.Instance.Import.RunOnStart && folders.Count > 0) RunImport();

                ServerState.Instance.ServerOnline = true;
                workerSetupDB.ReportProgress(100);

                StartTime = DateTime.Now;

                e.Result = true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
                ServerState.Instance.CurrentSetupStatus = ex.Message;
                ServerState.Instance.StartupFailed = true;
                ServerState.Instance.StartupFailedMessage = $"Startup Failed: {ex}";
                e.Result = false;
            }
        }

        #endregion

        #region Update all media info

        void WorkerMediaInfo_DoWork(object sender, DoWorkEventArgs e)
        {
            // first build a list of files that we already know about, as we don't want to process them again
            IReadOnlyList<SVR_VideoLocal> filesAll = RepoFactory.VideoLocal.GetAll();
            foreach (SVR_VideoLocal vl in filesAll)
            {
                CommandRequest_ReadMediaInfo cr = new CommandRequest_ReadMediaInfo(vl.VideoLocalID);
                cr.Save();
            }
        }

        public static void RefreshAllMediaInfo()
        {
            if (workerMediaInfo.IsBusy) return;
            workerMediaInfo.RunWorkerAsync();
        }

        #endregion

        #region MyAnime2 Migration

        public event EventHandler<ProgressChangedEventArgs> MyAnime2ProgressChanged;

        void WorkerMyAnime2_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            MyAnime2ProgressChanged?.Invoke(Instance, e);
        }

        void WorkerMyAnime2_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
        }

        void WorkerMyAnime2_DoWork(object sender, DoWorkEventArgs e)
        {
            MA2Progress ma2Progress = new MA2Progress
            {
                CurrentFile = 0,
                ErrorMessage = string.Empty,
                MigratedFiles = 0,
                TotalFiles = 0
            };
            try
            {
                string databasePath = e.Argument as string;

                string connString = string.Format(@"data source={0};useutf16encoding=True", databasePath);
                SQLiteConnection myConn = new SQLiteConnection(connString);
                myConn.Open();

                // get a list of unlinked files


                List<SVR_VideoLocal> vids = RepoFactory.VideoLocal.GetVideosWithoutEpisode();
                ma2Progress.TotalFiles = vids.Count;

                foreach (SVR_VideoLocal vid in vids.Where(a => !string.IsNullOrEmpty(a.Hash)))
                {
                    ma2Progress.CurrentFile = ma2Progress.CurrentFile + 1;
                    workerMyAnime2.ReportProgress(0, ma2Progress);

                    // search for this file in the XrossRef table in MA2
                    string sql =
                        string.Format(
                            "SELECT AniDB_EpisodeID from CrossRef_Episode_FileHash WHERE Hash = '{0}' AND FileSize = {1}",
                            vid.ED2KHash, vid.FileSize);
                    SQLiteCommand sqCommand = new SQLiteCommand(sql)
                    {
                        Connection = myConn
                    };
                    SQLiteDataReader myReader = sqCommand.ExecuteReader();
                    while (myReader.Read())
                    {
                        if (!int.TryParse(myReader.GetValue(0).ToString(), out int episodeID)) continue;
                        if (episodeID <= 0) continue;

                        sql = string.Format("SELECT AnimeID from AniDB_Episode WHERE EpisodeID = {0}", episodeID);
                        sqCommand = new SQLiteCommand(sql)
                        {
                            Connection = myConn
                        };
                        SQLiteDataReader myReader2 = sqCommand.ExecuteReader();
                        while (myReader2.Read())
                        {
                            int animeID = myReader2.GetInt32(0);

                            // so now we have all the needed details we can link the file to the episode
                            // as long as wehave the details in JMM
                            SVR_AniDB_Anime anime = null;
                            AniDB_Episode ep = RepoFactory.AniDB_Episode.GetByEpisodeID(episodeID);
                            if (ep == null)
                            {
                                logger.Debug("Getting Anime record from AniDB....");
                                anime = ShokoService.AnidbProcessor.GetAnimeInfoHTTP(animeID, true,
                                    ServerSettings.Instance.AutoGroupSeries);
                            }
                            else
                                anime = RepoFactory.AniDB_Anime.GetByAnimeID(animeID);

                            // create the group/series/episode records if needed
                            SVR_AnimeSeries ser = null;
                            if (anime == null) continue;

                            logger.Debug("Creating groups, series and episodes....");
                            // check if there is an AnimeSeries Record associated with this AnimeID
                            ser = RepoFactory.AnimeSeries.GetByAnimeID(animeID);
                            if (ser == null)
                            {
                                // create a new AnimeSeries record
                                ser = anime.CreateAnimeSeriesAndGroup();
                            }


                            ser.CreateAnimeEpisodes();

                            // check if we have any group status data for this associated anime
                            // if not we will download it now
                            if (RepoFactory.AniDB_GroupStatus.GetByAnimeID(anime.AnimeID).Count == 0)
                            {
                                CommandRequest_GetReleaseGroupStatus cmdStatus =
                                    new CommandRequest_GetReleaseGroupStatus(anime.AnimeID, false);
                                cmdStatus.Save();
                            }

                            // update stats
                            ser.EpisodeAddedDate = DateTime.Now;
                            RepoFactory.AnimeSeries.Save(ser, false, false);

                            foreach (SVR_AnimeGroup grp in ser.AllGroupsAbove)
                            {
                                grp.EpisodeAddedDate = DateTime.Now;
                                RepoFactory.AnimeGroup.Save(grp, false, false);
                            }


                            SVR_AnimeEpisode epAnime = RepoFactory.AnimeEpisode.GetByAniDBEpisodeID(episodeID);
                            CrossRef_File_Episode xref =
                                new CrossRef_File_Episode();

                            try
                            {
                                xref.PopulateManually(vid, epAnime);
                            }
                            catch (Exception ex)
                            {
                                string msg = string.Format("Error populating XREF: {0} - {1}", vid.ToStringDetailed(),
                                    ex);
                                throw;
                            }

                            RepoFactory.CrossRef_File_Episode.Save(xref);
                            vid.Places.ForEach(a => { a.RenameAndMoveAsRequired(); });

                            // update stats for groups and series
                            if (ser != null)
                            {
                                // update all the groups above this series in the heirarchy
                                ser.QueueUpdateStats();
                                //StatsCache.Instance.UpdateUsingSeries(ser.AnimeSeriesID);
                            }


                            // Add this file to the users list
                            if (ServerSettings.Instance.AniDb.MyList_AddFiles)
                            {
                                CommandRequest_AddFileToMyList cmd = new CommandRequest_AddFileToMyList(vid.ED2KHash);
                                cmd.Save();
                            }

                            ma2Progress.MigratedFiles = ma2Progress.MigratedFiles + 1;
                            workerMyAnime2.ReportProgress(0, ma2Progress);
                        }
                        myReader2.Close();


                        //Console.WriteLine(myReader.GetString(0));
                    }
                    myReader.Close();
                }


                myConn.Close();

                ma2Progress.CurrentFile = ma2Progress.CurrentFile + 1;
                workerMyAnime2.ReportProgress(0, ma2Progress);
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
                ma2Progress.ErrorMessage = ex.Message;
                workerMyAnime2.ReportProgress(0, ma2Progress);
            }
        }

        private void ImportLinksFromMA2(string databasePath)
        {
        }

        #endregion

        public void DownloadAllImages()
        {
            if (!downloadImagesWorker.IsBusy)
                downloadImagesWorker.RunWorkerAsync();
        }

        void DownloadImagesWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            Importer.RunImport_GetImages();
        }

        public class UpdateEventArgs : EventArgs
        {
            public UpdateEventArgs(long newVersion, long oldVersion, bool force)
            {
                NewVersion = newVersion;
                OldVersion = oldVersion;
                Forced = force;
            }

            public bool Forced { get; }

            public long OldVersion { get; }
            public long NewVersion { get; }
        }

        public event EventHandler<UpdateEventArgs> UpdateAvailable;

        public void CheckForUpdatesNew(bool forceShowForm)
        {
            try
            {
                long verCurrent = 0;
                long verNew = 0;

                // get the latest version as according to the release

                // get the user's version
                Assembly a = Assembly.GetEntryAssembly();
                if (a == null)
                {
                    logger.Error("Could not get current version");
                    return;
                }
                AssemblyName an = a.GetName();

                //verNew = verInfo.versions.ServerVersionAbs;

                verNew =
                    JMMAutoUpdatesHelper.ConvertToAbsoluteVersion(
                        JMMAutoUpdatesHelper.GetLatestVersionNumber(ServerSettings.Instance.UpdateChannel))
                    ;
                verCurrent = an.Version.Revision * 100 +
                             an.Version.Build * 100 * 100 +
                             an.Version.Minor * 100 * 100 * 100 +
                             an.Version.Major * 100 * 100 * 100 * 100;

                if (forceShowForm || verNew > verCurrent)
                {
                    UpdateAvailable?.Invoke(this, new UpdateEventArgs(verNew, verCurrent, forceShowForm));
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        #region UI events and methods

        internal static string GetLocalIPv4(NetworkInterfaceType _type)
        {
            string output = string.Empty;
            foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (item.NetworkInterfaceType == _type && item.OperationalStatus == OperationalStatus.Up)
                {
                    IPInterfaceProperties adapterProperties = item.GetIPProperties();

                    if (adapterProperties.GatewayAddresses.FirstOrDefault() != null)
                    {
                        foreach (UnicastIPAddressInformation ip in adapterProperties.UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                output = ip.Address.ToString();
                            }
                        }
                    }
                }
            }

            return output;
        }

        public event EventHandler ServerShutdown;
        public event EventHandler ServerRestart;

        void ShutdownServer()
        {
            ServerShutdown?.Invoke(this, null);
        }

        void RestartServer()
        {
            ServerRestart?.Invoke(this, null);
        }

        #endregion

        void AutoUpdateTimerShort_Elapsed(object sender, ElapsedEventArgs e)
        {
            autoUpdateTimerShort.Enabled = false;
            ShokoService.CmdProcessorImages.NotifyOfNewCommand();

            CheckForAdminMesages();


            autoUpdateTimerShort.Interval = 30 * 1000; // 30 seconds
            autoUpdateTimerShort.Enabled = true;
        }

        private void CheckForAdminMesages()
        {
            try
            {
                TimeSpan lastUpdate = DateTime.Now - lastAdminMessage;

                if (lastUpdate.TotalHours > 5)
                {
                    lastAdminMessage = DateTime.Now;
                    ServerInfo.Instance.RefreshAdminMessages();
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        #region Tray Minimize

        private void ShutDown()
        {
            StopWatchingFiles();
            AniDBDispose();
            StopHost();
            ServerShutdown?.Invoke(this, null);
            Analytics.PostEvent("Server", "Shutdown");
        }

        #endregion

        static void AutoUpdateTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            Importer.CheckForDayFilters();
            Importer.CheckForCalendarUpdate(false);
            Importer.CheckForAnimeUpdate(false);
            Importer.CheckForTvDBUpdates(false);
            Importer.CheckForMyListSyncUpdate(false);
            Importer.CheckForTraktAllSeriesUpdate(false);
            Importer.CheckForTraktTokenUpdate(false);
            Importer.CheckForMyListStatsUpdate(false);
            Importer.CheckForAniDBFileUpdate(false);
            Importer.UpdateAniDBTitles();
            Importer.SendUserInfoUpdate(false);
        }

        public static void StartWatchingFiles(bool log = true)
        {
            StopWatchingFiles();
            StopCloudWatchTimer();
            watcherVids = new List<RecoveringFileSystemWatcher>();

            foreach (SVR_ImportFolder share in RepoFactory.ImportFolder.GetAll())
            {
                try
                {
                    if (share.FolderIsWatched && log)
                    {
                        logger.Info($"Watching ImportFolder: {share.ImportFolderName} || {share.ImportFolderLocation}");
                    }
                    if (share.CloudID == null && Directory.Exists(share.ImportFolderLocation) && share.FolderIsWatched)
                    {
                        if (log) logger.Info($"Parsed ImportFolderLocation: {share.ImportFolderLocation}");
                        RecoveringFileSystemWatcher fsw = new RecoveringFileSystemWatcher
                        {
                            Path = share.ImportFolderLocation
                        };

                        // Handle all type of events not just created ones
                        fsw.Created += Fsw_CreateHandler;
                        fsw.Renamed += Fsw_RenameHandler;

                        // Commented out buffer size as it breaks on UNC paths or mapped drives
                        //fsw.InternalBufferSize = 81920;
                        fsw.IncludeSubdirectories = true;
                        fsw.EnableRaisingEvents = true;
                        watcherVids.Add(fsw);
                    }
                    else if (!share.FolderIsWatched)
                    {
                        if (log) logger.Info("ImportFolder found but not watching: {0} || {1}", share.ImportFolderName,
                            share.ImportFolderLocation);
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, ex.ToString());
                }
            }
            StartCloudWatchTimer();
        }


        public static void StopWatchingFiles()
        {
            if (watcherVids == null)
                return;
            foreach (RecoveringFileSystemWatcher fsw in watcherVids)
            {
                fsw.EnableRaisingEvents = false;
                fsw.Dispose();
            }
            watcherVids.Clear();
        }

        static void Fsw_CreateHandler(object sender, FileSystemEventArgs e)
        {
            try
            {
                queueFileEvents.Add(e);
                StartFileWorker();
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        static void Fsw_RenameHandler(object sender, RenamedEventArgs e)
        {
            try
            {
                queueFileEvents.Add(e);
                StartFileWorker();
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        public static void ScanDropFolders()
        {
            if (!workerScanDropFolders.IsBusy)
                workerScanDropFolders.RunWorkerAsync();
        }

        public static void SyncHashes()
        {
            if (!workerSyncHashes.IsBusy)
                workerSyncHashes.RunWorkerAsync();
        }

        public static void SyncMedias()
        {
            if (!workerSyncMedias.IsBusy)
                workerSyncMedias.RunWorkerAsync();
        }

        public static void ScanFolder(int importFolderID)
        {
            if (!workerScanFolder.IsBusy)
                workerScanFolder.RunWorkerAsync(importFolderID);
        }

        public static void RunImport()
        {
            Analytics.PostEvent("Importer", "Run");

            if (!workerImport.IsBusy)
                workerImport.RunWorkerAsync();
        }

        public static void RemoveMissingFiles()
        {
            Analytics.PostEvent("Importer", "RemoveMissing");

            if (!workerRemoveMissing.IsBusy)
                workerRemoveMissing.RunWorkerAsync();
        }

        public static void SyncMyList()
        {
            Importer.CheckForMyListSyncUpdate(true);
        }

        public static void DeleteImportFolder(int importFolderID)
        {
            if (!workerDeleteImportFolder.IsBusy)
                workerDeleteImportFolder.RunWorkerAsync(importFolderID);
        }

        static void WorkerRemoveMissing_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                Importer.RemoveRecordsWithoutPhysicalFiles();
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        void WorkerDeleteImportFolder_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                int importFolderID = int.Parse(e.Argument.ToString());
                Importer.DeleteImportFolder(importFolderID);
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        static void WorkerScanFolder_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                Importer.RunImport_ScanFolder(int.Parse(e.Argument.ToString()));
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        void WorkerScanDropFolders_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                Importer.RunImport_DropFolders();
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        static void WorkerImport_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                Importer.RunImport_NewFiles();
                Importer.RunImport_IntegrityCheck();

                // drop folder
                Importer.RunImport_DropFolders();

                // TvDB association checks
                Importer.RunImport_ScanTvDB();

                // Trakt association checks
                Importer.RunImport_ScanTrakt();

                // MovieDB association checks
                Importer.RunImport_ScanMovieDB();

                // Check for missing images
                Importer.RunImport_GetImages();

                // Check for previously ignored files
                Importer.CheckForPreviouslyIgnored();
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }


        /// <summary>
        /// Running Nancy and Validating all require aspects before running it
        /// </summary>
        private static void StartWebHost()
        {
            if (webHost != null)
                return;
            webHost = new WebHostBuilder().UseKestrel(options => { options.ListenAnyIP(ServerSettings.Instance.ServerPort); }).UseStartup<Startup>()
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
#if DEBUG
                    logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
#else
                    logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Error);
#endif
                }).UseNLog()

                .Build();

            //JsonSettings.MaxJsonLength = int.MaxValue;

            // Even with error callbacks, this may still throw an error in some parts, so log it!
            try
            {
                webHost.Start();
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
        }


        private static void ReadFiles()
        {
            // Steps for processing a file
            // 1. Check if it is a video file
            // 2. Check if we have a VideoLocal record for that file
            // .........

            // get a complete list of files
            List<string> fileList = new List<string>();
            foreach (SVR_ImportFolder share in RepoFactory.ImportFolder.GetAll())
            {
                logger.Debug("Import Folder: {0} || {1}", share.ImportFolderName, share.ImportFolderLocation);
                Utils.GetFilesForImportFolder(share.BaseDirectory, ref fileList);
            }


            // get a list of all the shares we are looking at
            int filesFound = 0, videosFound = 0;
            int i = 0;

            // get a list of all files in the share
            foreach (string fileName in fileList)
            {
                i++;
                filesFound++;

                if (fileName.Contains("Sennou"))
                {
                    logger.Info("Processing File {0}/{1} --- {2}", i, fileList.Count, fileName);
                }

                if (!FileHashHelper.IsVideo(fileName)) continue;

                videosFound++;
            }
            logger.Debug("Found {0} files", filesFound);
            logger.Debug("Found {0} videos", videosFound);
        }

        public static void StopHost()
        {
            webHost?.Dispose();
            webHost = null;
        }

        private static void SetupAniDBProcessor()
        {
            ShokoService.AnidbProcessor.Init(ServerSettings.Instance.AniDb.Username, ServerSettings.Instance.AniDb.Password,
                ServerSettings.Instance.AniDb.ServerAddress,
                ServerSettings.Instance.AniDb.ServerPort, ServerSettings.Instance.AniDb.ClientPort);
        }

        public static void AniDBDispose()
        {
            logger.Info("Disposing...");
            if (ShokoService.AnidbProcessor != null)
            {
                ShokoService.AnidbProcessor.ForceLogout();
                ShokoService.AnidbProcessor.Dispose();
                Thread.Sleep(1000);
            }
        }

        public static int OnHashProgress(string fileName, int percentComplete)
        {
            //string msg = Path.GetFileName(fileName);
            //if (msg.Length > 35) msg = msg.Substring(0, 35);
            //logger.Info("{0}% Hashing ({1})", percentComplete, Path.GetFileName(fileName));
            return 1; //continue hashing (return 0 to abort)
        }

        /// <summary>
        /// Sync plex watch status.
        /// </summary>
        /// <returns>true if there was any commands added to the queue, flase otherwise</returns>
        public bool SyncPlex()
        {
            Analytics.PostEvent("Plex", "SyncAll");

            bool flag = false;
            foreach (SVR_JMMUser user in RepoFactory.JMMUser.GetAll())
            {
                if (!string.IsNullOrEmpty(user.PlexToken))
                {
                    flag = true;
                    new CommandRequest_PlexSyncWatched(user).Save();
                }
            }
            return flag;
        }

        public void EnableStartWithWindows()
        {
            ServerState state = ServerState.Instance;

            if (state.IsAutostartEnabled)
            {
                return;
            }

            if (state.autostartMethod == AutostartMethod.Registry)
            {
                try
                {
                    state.AutostartRegistryKey.SetValue(state.autostartKey,
                        "\"" + Assembly.GetEntryAssembly().Location + "\"");
                    state.LoadSettings();
                }
                catch (Exception ex)
                {
                    logger.Debug(ex , "Creating autostart key");
                }
            }
            else if (state.autostartMethod == AutostartMethod.TaskScheduler)
            {
                Task task = TaskService.Instance.GetTask(state.autostartTaskName);
                if (task != null)
                {
                    TaskService.Instance.RootFolder.DeleteTask(task.Name);
                }

                TaskDefinition td = TaskService.Instance.NewTask();
                td.RegistrationInfo.Description = "Auto start task for JMM Server";

                td.Principal.RunLevel = TaskRunLevel.Highest;

                td.Triggers.Add(new BootTrigger());
                td.Triggers.Add(new LogonTrigger());

                td.Actions.Add("\"" + Assembly.GetEntryAssembly().Location + "\"");

                TaskService.Instance.RootFolder.RegisterTaskDefinition(state.autostartTaskName, td);
                state.LoadSettings();
            }
        }

        public void DisableStartWithWindows()
        {
            ServerState state = ServerState.Instance;
            if (!state.IsAutostartEnabled)
            {
                return;
            }

            if (state.autostartMethod == AutostartMethod.Registry)
            {
                try
                {
                    state.AutostartRegistryKey.DeleteValue(state.autostartKey, false);
                    state.LoadSettings();
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, "Deleting autostart key");
                }
            }
            else if (state.autostartMethod == AutostartMethod.TaskScheduler)
            {
                Task task = TaskService.Instance.GetTask(state.autostartTaskName);

                if (task == null) return;
                TaskService.Instance.RootFolder.DeleteTask(task.Name);
                state.LoadSettings();
            }
        }

        public bool SetNancyPort(ushort port)
        {
            if (!Utils.IsAdministrator()) return false;

            ShokoService.CmdProcessorGeneral.Paused = true;
            ShokoService.CmdProcessorHasher.Paused = true;
            ShokoService.CmdProcessorImages.Paused = true;

            StopHost();

            ServerSettings.Instance.ServerPort = port;

            bool started = NetPermissionWrapper(StartWebHost);
            if (!started)
            {
                StopHost();
                throw new Exception("Failed to start all of the network hosts");
            }

            ShokoService.CmdProcessorGeneral.Paused = false;
            ShokoService.CmdProcessorHasher.Paused = false;
            ShokoService.CmdProcessorImages.Paused = false;
            return true;
        }

        public void CheckForUpdates()
        {
            Assembly a = Assembly.GetExecutingAssembly();
            ServerState.Instance.ApplicationVersion = Utils.GetApplicationVersion(a);
            ServerState.Instance.ApplicationVersionExtra = Utils.GetApplicationExtraVersion(a);

            logger.Info("Checking for updates...");
            CheckForUpdatesNew(false);
        }

        public static bool IsMyAnime2WorkerBusy() => workerMyAnime2.IsBusy;

        public static void RunMyAnime2Worker(string filename) => workerMyAnime2.RunWorkerAsync(filename);
        public static void RunWorkSetupDB() => workerSetupDB.RunWorkerAsync();

        #region Tests

        private static void ReviewsTest()
        {
            CommandRequest_GetReviews cmd = new CommandRequest_GetReviews(7525, true);
            cmd.Save();

            //CommandRequest_GetAnimeHTTP cmd = new CommandRequest_GetAnimeHTTP(7727, false);
            //cmd.Save();
        }

        private static void HashTest()
        {
            string fileName = @"C:\Code_Geass_R2_Ep14_Geass_Hunt_[720p,BluRay,x264]_-_THORA.mkv";
            //string fileName = @"M:\[ Anime Test ]\Code_Geass_R2_Ep14_Geass_Hunt_[720p,BluRay,x264]_-_THORA.mkv";

            DateTime start = DateTime.Now;
            Hashes hashes = Hasher.CalculateHashes(fileName, OnHashProgress, false, false, false);
            TimeSpan ts = DateTime.Now - start;

            double doubleED2k = ts.TotalMilliseconds;

            start = DateTime.Now;
            Hashes hashes2 = Hasher.CalculateHashes(fileName, OnHashProgress, true, false, false);
            ts = DateTime.Now - start;

            double doubleCRC32 = ts.TotalMilliseconds;

            start = DateTime.Now;
            Hashes hashes3 = Hasher.CalculateHashes(fileName, OnHashProgress, false, true, false);
            ts = DateTime.Now - start;

            double doubleMD5 = ts.TotalMilliseconds;

            start = DateTime.Now;
            Hashes hashes4 = Hasher.CalculateHashes(fileName, OnHashProgress, false, false, true);
            ts = DateTime.Now - start;

            double doubleSHA1 = ts.TotalMilliseconds;

            start = DateTime.Now;
            Hashes hashes5 = Hasher.CalculateHashes(fileName, OnHashProgress, true, true, true);
            ts = DateTime.Now - start;

            double doubleAll = ts.TotalMilliseconds;

            logger.Info("ED2K only took {0} ms --- {1}/{2}/{3}/{4}", doubleED2k, hashes.ED2K, hashes.CRC32, hashes.MD5,
                hashes.SHA1);
            logger.Info("ED2K + CRCR32 took {0} ms --- {1}/{2}/{3}/{4}", doubleCRC32, hashes2.ED2K, hashes2.CRC32,
                hashes2.MD5,
                hashes2.SHA1);
            logger.Info("ED2K + MD5 took {0} ms --- {1}/{2}/{3}/{4}", doubleMD5, hashes3.ED2K, hashes3.CRC32,
                hashes3.MD5,
                hashes3.SHA1);
            logger.Info("ED2K + SHA1 took {0} ms --- {1}/{2}/{3}/{4}", doubleSHA1, hashes4.ED2K, hashes4.CRC32,
                hashes4.MD5,
                hashes4.SHA1);
            logger.Info("Everything took {0} ms --- {1}/{2}/{3}/{4}", doubleAll, hashes5.ED2K, hashes5.CRC32,
                hashes5.MD5,
                hashes5.SHA1);
        }

        private static void HashTest2()
        {
            string fileName = @"C:\Anime\Code_Geass_R2_Ep14_Geass_Hunt_[720p,BluRay,x264]_-_THORA.mkv";
            FileInfo fi = new FileInfo(fileName);
            string fileSize1 = Utils.FormatByteSize(fi.Length);
            DateTime start = DateTime.Now;
            Hashes hashes = Hasher.CalculateHashes(fileName, OnHashProgress, false, false, false);
            TimeSpan ts = DateTime.Now - start;

            double doubleFile1 = ts.TotalMilliseconds;

            fileName = @"C:\Anime\[Coalgirls]_Bakemonogatari_01_(1280x720_Blu-Ray_FLAC)_[CA425D15].mkv";
            fi = new FileInfo(fileName);
            string fileSize2 = Utils.FormatByteSize(fi.Length);
            start = DateTime.Now;
            Hashes hashes2 = Hasher.CalculateHashes(fileName, OnHashProgress, false, false, false);
            ts = DateTime.Now - start;

            double doubleFile2 = ts.TotalMilliseconds;


            fileName = @"C:\Anime\Highschool_of_the_Dead_Ep01_Spring_of_the_Dead_[1080p,BluRay,x264]_-_gg-THORA.mkv";
            fi = new FileInfo(fileName);
            string fileSize3 = Utils.FormatByteSize(fi.Length);
            start = DateTime.Now;
            Hashes hashes3 = Hasher.CalculateHashes(fileName, OnHashProgress, false, false, false);
            ts = DateTime.Now - start;

            double doubleFile3 = ts.TotalMilliseconds;

            logger.Info("Hashed {0} in {1} ms --- {2}", fileSize1, doubleFile1, hashes.ED2K);
            logger.Info("Hashed {0} in {1} ms --- {2}", fileSize2, doubleFile2, hashes2.ED2K);
            logger.Info("Hashed {0} in {1} ms --- {2}", fileSize3, doubleFile3, hashes3.ED2K);
        }

        private static void UpdateStatsTest()
        {
            foreach (SVR_AnimeGroup grp in RepoFactory.AnimeGroup.GetAllTopLevelGroups())
            {
                grp.UpdateStatsFromTopLevel(true, true);
            }
        }

        private static void CreateImportFolders_Test()
        {
            logger.Debug("Creating import folders...");

            SVR_ImportFolder sn = RepoFactory.ImportFolder.GetByImportLocation(@"M:\[ Anime Test ]");
            if (sn == null)
            {
                sn = new SVR_ImportFolder
                {
                    ImportFolderName = "Anime",
                    ImportFolderType = (int)ImportFolderType.HDD,
                    ImportFolderLocation = @"M:\[ Anime Test ]"
                };
                RepoFactory.ImportFolder.Save(sn);
            }

            logger.Debug("Complete!");
        }

        private static void ProcessFileTest()
        {
            //CommandRequest_HashFile cr_hashfile = new CommandRequest_HashFile(@"M:\[ Anime Test ]\[HorribleSubs] Dragon Crisis! - 02 [720p].mkv", false);
            //CommandRequest_ProcessFile cr_procfile = new CommandRequest_ProcessFile(@"M:\[ Anime Test ]\[Doki] Saki - 01 (720x480 h264 DVD AAC) [DC73ACB9].mkv");
            //cr_hashfile.Save();

            CommandRequest_ProcessFile cr_procfile = new CommandRequest_ProcessFile(15350, false);
            cr_procfile.Save();
        }

        private static void CreateImportFolders()
        {
            logger.Debug("Creating shares...");

            SVR_ImportFolder sn = RepoFactory.ImportFolder.GetByImportLocation(@"M:\[ Anime 2011 ]");
            if (sn == null)
            {
                sn = new SVR_ImportFolder
                {
                    ImportFolderType = (int)ImportFolderType.HDD,
                    ImportFolderName = "Anime 2011",
                    ImportFolderLocation = @"M:\[ Anime 2011 ]"
                };
                RepoFactory.ImportFolder.Save(sn);
            }

            sn = RepoFactory.ImportFolder.GetByImportLocation(@"M:\[ Anime - DVD and Bluray IN PROGRESS ]");
            if (sn == null)
            {
                sn = new SVR_ImportFolder
                {
                    ImportFolderType = (int)ImportFolderType.HDD,
                    ImportFolderName = "Anime - DVD and Bluray IN PROGRESS",
                    ImportFolderLocation = @"M:\[ Anime - DVD and Bluray IN PROGRESS ]"
                };
                RepoFactory.ImportFolder.Save(sn);
            }

            sn = RepoFactory.ImportFolder.GetByImportLocation(@"M:\[ Anime - DVD and Bluray COMPLETE ]");
            if (sn == null)
            {
                sn = new SVR_ImportFolder
                {
                    ImportFolderType = (int)ImportFolderType.HDD,
                    ImportFolderName = "Anime - DVD and Bluray COMPLETE",
                    ImportFolderLocation = @"M:\[ Anime - DVD and Bluray COMPLETE ]"
                };
                RepoFactory.ImportFolder.Save(sn);
            }

            sn = RepoFactory.ImportFolder.GetByImportLocation(@"M:\[ Anime ]");
            if (sn == null)
            {
                sn = new SVR_ImportFolder
                {
                    ImportFolderType = (int)ImportFolderType.HDD,
                    ImportFolderName = "Anime",
                    ImportFolderLocation = @"M:\[ Anime ]"
                };
                RepoFactory.ImportFolder.Save(sn);
            }

            logger.Debug("Creating shares complete!");
        }

        private static void CreateImportFolders2()
        {
            logger.Debug("Creating shares...");

            SVR_ImportFolder sn = RepoFactory.ImportFolder.GetByImportLocation(@"F:\Anime1");
            if (sn == null)
            {
                sn = new SVR_ImportFolder
                {
                    ImportFolderType = (int)ImportFolderType.HDD,
                    ImportFolderName = "Anime1",
                    ImportFolderLocation = @"F:\Anime1"
                };
                RepoFactory.ImportFolder.Save(sn);
            }

            sn = RepoFactory.ImportFolder.GetByImportLocation(@"H:\Anime2");
            if (sn == null)
            {
                sn = new SVR_ImportFolder
                {
                    ImportFolderType = (int)ImportFolderType.HDD,
                    ImportFolderName = "Anime2",
                    ImportFolderLocation = @"H:\Anime2"
                };
                RepoFactory.ImportFolder.Save(sn);
            }

            sn = RepoFactory.ImportFolder.GetByImportLocation(@"G:\Anime3");
            if (sn == null)
            {
                sn = new SVR_ImportFolder
                {
                    ImportFolderType = (int)ImportFolderType.HDD,
                    ImportFolderName = "Anime3",
                    ImportFolderLocation = @"G:\Anime3"
                };
                RepoFactory.ImportFolder.Save(sn);
            }

            logger.Debug("Creating shares complete!");
        }

        private static void CreateTestCommandRequests()
        {
            CommandRequest_GetAnimeHTTP cr_anime = new CommandRequest_GetAnimeHTTP(5415, false, true);
            cr_anime.Save();

            /*
			cr_anime = new CommandRequest_GetAnimeHTTP(7382); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(6239); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(69); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(6751); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(3168); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(4196); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(634); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(2002); cr_anime.Save();



			cr_anime = new CommandRequest_GetAnimeHTTP(1); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(2); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(3); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(4); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(5); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(6); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(7); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(8); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(9); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(10); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(11); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(12); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(13); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(14); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(15); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(16); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(17); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(18); cr_anime.Save();
			cr_anime = new CommandRequest_GetAnimeHTTP(19); cr_anime.Save();*/
        }

        #endregion
    }
}