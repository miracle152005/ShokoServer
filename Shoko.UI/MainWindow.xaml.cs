﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
//using Infralution.Localization.Wpf;

using Microsoft.WindowsAPICodePack.Dialogs;
using NLog;
using Shoko.Commons.Extensions;
using Shoko.Server;

using Shoko.Server.ImageDownload;
using Shoko.Server.Import;
using Shoko.Server.CommandQueue;
using Shoko.Server.CommandQueue.Commands;
using Shoko.Server.CommandQueue.Commands.AniDB;
using Shoko.Server.CommandQueue.Commands.Trakt;
using Shoko.Server.CommandQueue.Commands.WebCache;
using Shoko.Server.Models;
using Shoko.Server.Providers.AniDB;
using Shoko.Server.Providers.TraktTV;
using Shoko.Server.Repositories;
using Shoko.Server.Settings;
using Shoko.Server.Utilities;
using Shoko.UI.Forms;
using Application = System.Windows.Application;
using Cursors = System.Windows.Input.Cursors;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Forms.MouseEventArgs;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;

namespace Shoko.UI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private NotifyIcon TippuTrayNotify;
        private ContextMenuStrip ctxTrayMenu;
        private bool isAppExiting;

        public static List<UserCulture> userLanguages = new List<UserCulture>();
        private static Logger logger = LogManager.GetCurrentClassLogger();
        private static InitialSetupForm LoginWindow;
        public static bool AniDBLoginOpen;

        public MainWindow()
        {
            InitializeComponent();

            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(ServerSettings.Instance.Culture);
            ShokoServer.Instance.OAuthProvider=new AuthProvider(this);
            if (!ShokoServer.Instance.StartUpServer())
            {
                MessageBox.Show(Commons.Properties.Resources.Server_Running,
                    Commons.Properties.Resources.ShokoServer, MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(0);
            }

            //Create an instance of the NotifyIcon Class
            TippuTrayNotify = new NotifyIcon();

            // This icon file needs to be in the bin folder of the application
            TippuTrayNotify = new NotifyIcon();
            Stream iconStream =
                Application.GetResourceStream(new Uri("pack://application:,,,/ShokoServer;component/db.ico")).Stream;
            TippuTrayNotify.Icon = new Icon(iconStream);
            iconStream.Dispose();

            //show the Tray Notify IconbtnRemoveMissingFiles.Click
            TippuTrayNotify.Visible = true;

            //-- for winforms applications
            System.Windows.Forms.Application.ThreadException -= UnhandledExceptionManager.ThreadExceptionHandler;
            System.Windows.Forms.Application.ThreadException += UnhandledExceptionManager.ThreadExceptionHandler;

            CreateMenus();

            ServerState.Instance.DatabaseAvailable = false;
            ServerState.Instance.ServerOnline = false;
            ServerState.Instance.BaseImagePath = ImageUtils.GetBaseImagesPath();

            Closing += MainWindow_Closing;
            StateChanged += MainWindow_StateChanged;
            TippuTrayNotify.MouseDoubleClick += TippuTrayNotify_MouseDoubleClick;

            btnToolbarShutdown.Click += btnToolbarShutdown_Click;
            btnHasherPause.Click += btnHasherPause_Click;
            btnHasherResume.Click += btnHasherResume_Click;
            btnGeneralPause.Click += btnGeneralPause_Click;
            btnGeneralResume.Click += btnGeneralResume_Click;
            btnImagesPause.Click += btnImagesPause_Click;
            btnImagesResume.Click += btnImagesResume_Click;
            btnAdminMessages.Click += btnAdminMessages_Click;

            btnRemoveMissingFiles.Click += btnRemoveMissingFiles_Click;
            btnRunImport.Click += btnRunImport_Click;
            btnSyncHashes.Click += BtnSyncHashes_Click;
            btnSyncMedias.Click += BtnSyncMedias_Click;
            btnSyncMyList.Click += btnSyncMyList_Click;
            btnSyncVotes.Click += btnSyncVotes_Click;
            btnUpdateTvDBInfo.Click += btnUpdateTvDBInfo_Click;
            btnUpdateAllStats.Click += btnUpdateAllStats_Click;
            btnSyncTrakt.Click += btnSyncTrakt_Click;
            btnImportManualLinks.Click += btnImportManualLinks_Click;
            btnUpdateAniDBInfo.Click += btnUpdateAniDBInfo_Click;
            btnLaunchWebUI.Click += btnLaunchWebUI_Click;
            btnUpdateImages.Click += btnUpdateImages_Click;
            btnUploadAzureCache.Click += btnUploadAzureCache_Click;
            btnUpdateTraktInfo.Click += BtnUpdateTraktInfo_Click;
            btnSyncPlex.Click += BtnSyncPlexOn_Click;
            btnValidateImages.Click += BtnValidateAllImages_Click;

            Loaded += MainWindow_Loaded;

            txtServerPort.Text = ServerSettings.Instance.ServerPort.ToString(CultureInfo.InvariantCulture);

            btnToolbarHelp.Click += btnToolbarHelp_Click;
            btnApplyServerPort.Click += btnApplyServerPort_Click;
            btnUpdateMediaInfo.Click += btnUpdateMediaInfo_Click;

            //StartUp();

            cboDatabaseType.Items.Clear();
            ShokoServer.Instance.GetSupportedDatabases().ForEach(s => cboDatabaseType.Items.Add(s));
            cboDatabaseType.SelectionChanged +=
                cboDatabaseType_SelectionChanged;

            btnChooseImagesFolder.Click += btnChooseImagesFolder_Click;
            btnSetDefault.Click += BtnSetDefault_Click;


            btnSaveDatabaseSettings.Click += btnSaveDatabaseSettings_Click;
            btnRefreshMSSQLServerList.Click += btnRefreshMSSQLServerList_Click;
            // btnInstallMSSQLServer.Click += new RoutedEventHandler(btnInstallMSSQLServer_Click);
            btnMaxOnStartup.Click += toggleMinimizeOnStartup;
            btnMinOnStartup.Click += toggleMinimizeOnStartup;
            btnLogs.Click += btnLogs_Click;
            btnChooseVLCLocation.Click += btnChooseVLCLocation_Click;
            btnJMMEnableStartWithWindows.Click += btnJMMEnableStartWithWindows_Click;
            btnJMMDisableStartWithWindows.Click += btnJMMDisableStartWithWindows_Click;
            btnUpdateAniDBLogin.Click += btnUpdateAniDBLogin_Click;

            btnHasherClear.Click += btnHasherClear_Click;
            btnGeneralClear.Click += btnGeneralClear_Click;
            btnImagesClear.Click += btnImagesClear_Click;

            //automaticUpdater.MenuItem = mnuCheckForUpdates;

            ServerState.Instance.LoadSettings();

            cboLanguages.SelectionChanged += cboLanguages_SelectionChanged;

            InitCulture();
            Instance = this;

            if (!ServerSettings.Instance.FirstRun && ShokoService.AnidbProcessor.ValidAniDBCredentials())
            {
                logger.Info("Already been set up... Initializing DB...");
                ShokoServer.RunWorkSetupDB();
                cboLanguages.IsEnabled = true;
            }

            SubscribeEvents();
        }

        private void SubscribeEvents()
        {
            Utils.YesNoRequired += (sender, args) =>
            {
                DialogResult dr =
                    System.Windows.Forms.MessageBox.Show(args.Reason, args.FormTitle,
                        MessageBoxButtons.YesNo);
                if (dr == System.Windows.Forms.DialogResult.No) args.Cancel = true;
            };
            /* This no longer exists.
            ServerSettings.Instance.LocateFile += (sender, args) =>
            {
                OpenFileDialog openFileDialog = new OpenFileDialog();
                openFileDialog.Filter = @"JMM config|JMMServer.exe.config;settings.json";
                DialogResult browseFile = openFileDialog.ShowDialog();
                if (browseFile == System.Windows.Forms.DialogResult.OK && !string.IsNullOrEmpty(openFileDialog.FileName.Trim()))
                {
                    args.FileName = openFileDialog.FileName;
                }
            };*/

            Utils.ErrorMessage +=
                (sender, args) => MessageBox.Show(this, args.Message, args.Title ?? (args.IsError ? "Error" : "Message"), MessageBoxButton.OK, args.IsError ? MessageBoxImage.Error : MessageBoxImage.Information);
            Utils.OnEvents +=
                (sender, args) =>
                {
                    DoEvents();
                };
            Utils.OnDispatch +=
                action =>
                {
                    Application.Current.Dispatcher.Invoke(action);
                };
            AniDBHelper.LoginFailed += (a, e) => Application.Current.Dispatcher.Invoke(ShowAniDBLoginDialog);
            ShokoServer.Instance.LoginFormNeeded +=
                (a, e) => Application.Current.Dispatcher.Invoke(ShowAniDBLoginDialog);

            ShokoServer.Instance.
                DBSetupCompleted += DBSetupCompleted;
            ShokoServer.Instance.DatabaseSetup += (sender, args) => ShowDatabaseSetup();
        }

        public void ShowAniDBLoginDialog()
        {
            if (AniDBLoginOpen && LoginWindow != null)
            {
                LoginWindow.Focus();
                return;
            }

            LoginWindow = new InitialSetupForm {Owner = this};
            LoginWindow.ShowDialog();
        }
        
        public void DoEvents()
        {
            DispatcherFrame frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new DispatcherOperationCallback(ExitFrame), frame);
            Dispatcher.PushFrame(frame);
        }

        public object ExitFrame(object f)
        {
            ((DispatcherFrame)f).Continue = false;

            return null;
        }
        private void BtnSyncPlexOn_Click(object sender, RoutedEventArgs routedEventArgs)
        {
            if (!ShokoServer.Instance.SyncPlex())
            {
                MessageBox.Show(Commons.Properties.Resources.Plex_NoneAuthenticated, Commons.Properties.Resources.Error,
                    MessageBoxButton.OK, MessageBoxImage.Asterisk);
            }
        }

        private void BtnValidateAllImages_Click(object sender, RoutedEventArgs routedEventArgs)
        {
            Importer.ValidateAllImages();
            MessageBox.Show(string.Format(Commons.Properties.Resources.Command_ValidateAllImages, ""),
                Commons.Properties.Resources.Success,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnSetDefault_Click(object sender, RoutedEventArgs e)
        {
            string imagePath = ServerSettings.DefaultImagePath;
            if (!Directory.Exists(imagePath))
                Directory.CreateDirectory(imagePath);
            ServerSettings.Instance.ImagesPath = imagePath;
        }

        public static MainWindow Instance { get; private set; }

        private void BtnSyncHashes_Click(object sender, RoutedEventArgs e)
        {
            ShokoServer.SyncHashes();
            MessageBox.Show(Commons.Properties.Resources.Server_SyncHashesRunning,
                Commons.Properties.Resources.Success,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnSyncMedias_Click(object sender, RoutedEventArgs e)
        {
            ShokoServer.SyncMedias();
            MessageBox.Show(Commons.Properties.Resources.Server_SyncMediasRunning,
                Commons.Properties.Resources.Success,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnUpdateTraktInfo_Click(object sender, RoutedEventArgs e)
        {
            Cursor = Cursors.Wait;
            TraktTVHelper.UpdateAllInfo();
            Cursor = Cursors.Arrow;
            MessageBox.Show(Commons.Properties.Resources.Command_UpdateTrakt,
                Commons.Properties.Resources.Success,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void btnUploadAzureCache_Click(object sender, RoutedEventArgs e)
        {
            IReadOnlyList<SVR_AniDB_Anime> allAnime = Repo.Instance.AniDB_Anime.GetAll();
            int cnt = 0;
            foreach (SVR_AniDB_Anime anime in allAnime)
            {
                cnt++;
                logger.Info($"Uploading anime {cnt} of {allAnime.Count} - {anime.MainTitle}");

                try
                {
                    Queue.Instance.Add(new CmdWebCacheSendAnimeFull(anime.AnimeID));
                }
                catch
                {
                }
            }
        }

        async void btnImagesClear_Click(object sender, RoutedEventArgs e)
        {
            btnImagesClear.IsEnabled = true;
            Task task = new Task(() =>
            {
                Queue.Instance.ClearWorkTypes(WorkTypes.Image);
            });

            try
            {
                task.Start();
                await task;
            }
            catch (Exception ex)
            {
                Utils.ShowErrorMessage(ex.Message);
            }
            finally
            {
                btnImagesClear.IsEnabled = true;
            }
        }

        async void btnGeneralClear_Click(object sender, RoutedEventArgs e)
        {
            btnGeneralClear.IsEnabled = false;
            Task task = new Task(() =>
            {
                Queue.Instance.ClearWorkTypes(Queue.GeneralWorkTypesExceptSchedule);
            });

            try
            {
                task.Start();
                await task;
            }
            catch (Exception ex)
            {
                Utils.ShowErrorMessage(ex.Message);
            }
            finally
            {
                btnGeneralClear.IsEnabled = true;
            }
        }

        async void btnHasherClear_Click(object sender, RoutedEventArgs e)
        {
            btnHasherClear.IsEnabled = false;
            Task task = new Task(() =>
            {
                Queue.Instance.ClearWorkTypes(WorkTypes.Hashing);
            });

            try
            {
                task.Start();
                await task;
            }
            catch (Exception ex)
            {
                Utils.ShowErrorMessage(ex.Message);
            }
            finally
            {
                btnHasherClear.IsEnabled = true;
            }
        }


        void btnAdminMessages_Click(object sender, RoutedEventArgs e)
        {
            AdminMessagesForm frm = new AdminMessagesForm();
            frm.Owner = this;
            frm.ShowDialog();
        }


        void btnLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logPath = Path.Combine(ServerSettings.ApplicationPath, "logs");

                Process.Start(new ProcessStartInfo(logPath));
            }
            catch (Exception ex)
            {
                Utils.ShowErrorMessage(ex);
            }
        }

        void toggleMinimizeOnStartup(object sender, RoutedEventArgs e)
        {
            ServerSettings.Instance.MinimizeOnStartup = !ServerSettings.Instance.MinimizeOnStartup;
            ServerState.Instance.MinOnStartup = !ServerState.Instance.MinOnStartup;
            ServerState.Instance.MaxOnStartup = !ServerState.Instance.MaxOnStartup;
        }

        /*
		void btnInstallMSSQLServer_Click(object sender, RoutedEventArgs e)
		{
			System.Diagnostics.Process.Start("http://www.microsoft.com/web/gallery/install.aspx?appsxml=&appid=SQLExpressTools");
		}
        */

        void btnJMMEnableStartWithWindows_Click(object sender, RoutedEventArgs e)
        {
            ShokoServer.Instance.EnableStartWithWindows();
        }

        void btnJMMDisableStartWithWindows_Click(object sender, RoutedEventArgs e)
        {
            ShokoServer.Instance.DisableStartWithWindows();
        }

        void btnUpdateAniDBLogin_Click(object sender, RoutedEventArgs e)
        {
            ShowAniDBLoginDialog();
        }

        void cboLanguages_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SetCulture();
        }

        void InitCulture()
        {
            try
            {
                string currentCulture = ServerSettings.Instance.Culture;

                cboLanguages.ItemsSource = UserCulture.SupportedLanguages;

                for (int i = 0; i < cboLanguages.Items.Count; i++)
                {
                    UserCulture ul = cboLanguages.Items[i] as UserCulture;
                    if (ul.Culture.Trim().ToUpper() == currentCulture.Trim().ToUpper())
                    {
                        cboLanguages.SelectedIndex = i;
                        break;
                    }
                }
                if (cboLanguages.SelectedIndex < 0)
                    cboLanguages.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                Utils.ShowErrorMessage(ex);
            }
        }

        private void SetCulture()
        {
            if (cboLanguages.SelectedItem == null) return;
            UserCulture ul = cboLanguages.SelectedItem as UserCulture;
            bool isLanguageChanged = ServerSettings.Instance.Culture != ul.Culture;
            DialogResult result;

            try
            {
                CultureInfo ci = new CultureInfo(ul.Culture);
                CultureInfo.DefaultThreadCurrentUICulture = ci;
                //CultureManager.UICulture = ci;
                ServerSettings.Instance.Culture = ul.Culture;
                if (isLanguageChanged)
                {
                    result = System.Windows.Forms.MessageBox.Show(Commons.Properties.Resources.Language_Info,
                        Commons.Properties.Resources.Language_Switch,
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Information);

                    if (result != System.Windows.Forms.DialogResult.OK) return;

                    System.Windows.Forms.Application.Restart();
                    //ShokoServer.Instance.s();
                }
            }
            catch (Exception ex)
            {
                Utils.ShowErrorMessage(ex);
            }
        }


        void btnChooseVLCLocation_Click(object sender, RoutedEventArgs e)
        {
            /*string errorMsg = "";
            string streamingAddress = "";

            Utils.StartStreamingVideo("localhost",
                @"e:\test\[Frostii]_K-On!_-_S5_(1280x720_Blu-ray_H264)_[8B9E0A76].mkv",
                "12000", "30", "1280",
                "128", "44100", "8088", ref errorMsg, ref streamingAddress);

            return;*/

            OpenFileDialog dialog = new OpenFileDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                ServerSettings.Instance.VLCLocation = dialog.FileName;
            }
        }

        void btnChooseImagesFolder_Click(object sender, RoutedEventArgs e)
        {
            //needed check, 
            if (CommonFileDialog.IsPlatformSupported)
            {
                var dialog = new CommonOpenFileDialog();
                dialog.IsFolderPicker = true;
                //CommonFileDialogResult result = dialog.ShowDialog();
                if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    ServerSettings.Instance.ImagesPath = dialog.FileName;
                }
            }
            else
            {
                System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog();
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    ServerSettings.Instance.ImagesPath = dialog.SelectedPath;
                }
            }
        }


        #region Database settings and initial start up

        void btnSaveDatabaseSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                btnSaveDatabaseSettings.IsEnabled = false;
                cboDatabaseType.IsEnabled = false;
                btnRefreshMSSQLServerList.IsEnabled = false;

                if (ServerState.Instance.DatabaseIsSQLite)
                {
                    ServerSettings.Instance.Database.Type = DatabaseTypes.Sqlite;
                }
                else if (ServerState.Instance.DatabaseIsSQLServer)
                {
                    if (string.IsNullOrEmpty(txtMSSQL_DatabaseName.Text) ||
                        string.IsNullOrEmpty(txtMSSQL_Password.Password)
                        || string.IsNullOrEmpty(cboMSSQLServerList.Text) ||
                        string.IsNullOrEmpty(txtMSSQL_Username.Text))
                    {
                        MessageBox.Show(Commons.Properties.Resources.Server_FillOutSettings,
                            Commons.Properties.Resources.Error,
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        txtMSSQL_DatabaseName.Focus();
                        return;
                    }

                    ServerSettings.Instance.Database.Type = DatabaseTypes.SqlServer;
                    ServerSettings.Instance.Database.Schema = txtMSSQL_DatabaseName.Text;
                    ServerSettings.Instance.Database.Password = txtMSSQL_Password.Password;
                    ServerSettings.Instance.Database.Hostname = cboMSSQLServerList.Text;
                    ServerSettings.Instance.Database.Username = txtMSSQL_Username.Text;
                }
                else if (ServerState.Instance.DatabaseIsMySQL)
                {
                    if (string.IsNullOrEmpty(txtMySQL_DatabaseName.Text) ||
                        string.IsNullOrEmpty(txtMySQL_Password.Password)
                        || string.IsNullOrEmpty(txtMySQL_ServerAddress.Text) ||
                        string.IsNullOrEmpty(txtMySQL_Username.Text))
                    {
                        MessageBox.Show(Commons.Properties.Resources.Server_FillOutSettings,
                            Commons.Properties.Resources.Error,
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        txtMySQL_DatabaseName.Focus();
                        return;
                    }

                    ServerSettings.Instance.Database.Type = DatabaseTypes.MySql;
                    ServerSettings.Instance.Database.Schema = txtMySQL_DatabaseName.Text;
                    ServerSettings.Instance.Database.Password = txtMySQL_Password.Password;
                    ServerSettings.Instance.Database.Hostname = txtMySQL_ServerAddress.Text;
                    ServerSettings.Instance.Database.Username = txtMySQL_Username.Text;
                }

                logger.Info("Initializing DB...");

                ShokoServer.RunWorkSetupDB();
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
                MessageBox.Show(Commons.Properties.Resources.Server_FailedToStart + ex.Message,
                    Commons.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void cboDatabaseType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            switch (cboDatabaseType.SelectedIndex)
            {
                case 0:
                    ServerState.Instance.DatabaseIsSQLite = true;
                    ServerState.Instance.DatabaseIsSQLServer = false;
                    ServerState.Instance.DatabaseIsMySQL = false;
                    break;
                case 1:
                    bool anySettingsMSSQL = !string.IsNullOrEmpty(ServerSettings.Instance.Database.Schema) ||
                                            !string.IsNullOrEmpty(ServerSettings.Instance.Database.Password)
                                            || !string.IsNullOrEmpty(ServerSettings.Instance.Database.Hostname) ||
                                            !string.IsNullOrEmpty(ServerSettings.Instance.Database.Username);
                    if (anySettingsMSSQL)
                    {
                        txtMSSQL_DatabaseName.Text = ServerSettings.Instance.Database.Schema;
                        txtMSSQL_Password.Password = ServerSettings.Instance.Database.Password;

                        cboMSSQLServerList.Text = ServerSettings.Instance.Database.Hostname;
                        txtMSSQL_Username.Text = ServerSettings.Instance.Database.Username;
                    }
                    else
                    {
                        txtMSSQL_DatabaseName.Text = "JMMServer";
                        txtMSSQL_Password.Password = "";
                        cboMSSQLServerList.Text = "localhost";
                        txtMSSQL_Username.Text = "sa";
                    }
                    ServerState.Instance.DatabaseIsSQLServer = true;
                    ServerState.Instance.DatabaseIsSQLite = false;
                    ServerState.Instance.DatabaseIsMySQL = false;
                    break;
                case 2:
                    bool anySettingsMySQL = !string.IsNullOrEmpty(ServerSettings.Instance.Database.Schema) ||
                                            !string.IsNullOrEmpty(ServerSettings.Instance.Database.Password)
                                            || !string.IsNullOrEmpty(ServerSettings.Instance.Database.Hostname) ||
                                            !string.IsNullOrEmpty(ServerSettings.Instance.Database.Username);
                    if (anySettingsMySQL)
                    {
                        txtMySQL_DatabaseName.Text = ServerSettings.Instance.Database.Schema;
                        txtMySQL_Password.Password = ServerSettings.Instance.Database.Password;
                        txtMySQL_ServerAddress.Text = ServerSettings.Instance.Database.Hostname;
                        txtMySQL_Username.Text = ServerSettings.Instance.Database.Username;
                    }
                    else
                    {
                        txtMySQL_DatabaseName.Text = "JMMServer";
                        txtMySQL_Password.Password = "";
                        txtMySQL_ServerAddress.Text = "localhost";
                        txtMySQL_Username.Text = "root";
                    }
                    ServerState.Instance.DatabaseIsMySQL = true;
                    ServerState.Instance.DatabaseIsSQLServer = false;
                    ServerState.Instance.DatabaseIsSQLite = false;
                    break;
            }
        }

        void  DBSetupCompleted(object sender, EventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                btnSaveDatabaseSettings.IsEnabled = true;
                cboDatabaseType.IsEnabled = true;
                btnRefreshMSSQLServerList.IsEnabled = true;
                cboLanguages.IsEnabled = true;
            });
        }

        private List<string> GetSQLServers()
        {
            List<string> ls=new List<string>();
            RegistryKey rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server");
            String[] instances = (String[])rk.GetValue("InstalledInstances");
            if (instances.Length > 0)
            {
                foreach (String element in instances)
                {
                    if (element == "MSSQLSERVER")
                        ls.Add(Environment.MachineName);
                    else
                        ls.Add(Environment.MachineName+"\\"+element);
                }
            }

            return ls;
        }
        void btnRefreshMSSQLServerList_Click(object sender, RoutedEventArgs e)
        {
            btnSaveDatabaseSettings.IsEnabled = false;
            cboDatabaseType.IsEnabled = false;
            btnRefreshMSSQLServerList.IsEnabled = false;

            try
            {
                Cursor = Cursors.Wait;
                cboMSSQLServerList.Items.Clear();
                GetSQLServers().ForEach(a=> cboMSSQLServerList.Items.Add(a));
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
                MessageBox.Show(ex.Message, Commons.Properties.Resources.Error, MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            Cursor = Cursors.Arrow;
            btnSaveDatabaseSettings.IsEnabled = true;
            cboDatabaseType.IsEnabled = true;
            btnRefreshMSSQLServerList.IsEnabled = true;
        }

        private void ShowDatabaseSetup()
        {
            if (ServerSettings.Instance.Database.Type == DatabaseTypes.Sqlite)
                cboDatabaseType.SelectedIndex = 0;
            if (ServerSettings.Instance.Database.Type == DatabaseTypes.SqlServer)
                cboDatabaseType.SelectedIndex = 1;
            if (ServerSettings.Instance.Database.Type == DatabaseTypes.MySql)
                cboDatabaseType.SelectedIndex = 2;
            cboDatabaseType.IsEnabled = true;
            btnSaveDatabaseSettings.IsEnabled = true;
        }

        #endregion

        #region Update all media info

        void btnUpdateMediaInfo_Click(object sender, RoutedEventArgs e)
        {
            ShokoServer.RefreshAllMediaInfo();
            MessageBox.Show(Commons.Properties.Resources.Serrver_VideoMediaUpdate,
                Commons.Properties.Resources.Success,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }


        #endregion

        #region MyAnime2 Migration
        void btnImportManualLinks_Click(object sender, RoutedEventArgs e)
        {
            //TODO: Work out if we need MyAnime2
            if (/*ShokoServer.IsMyAnime2WorkerBusy()*/ true)
            {
                MessageBox.Show(Commons.Properties.Resources.Server_Import,
                    Commons.Properties.Resources.Error,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            txtMA2Progress.Visibility = Visibility.Visible;
            txtMA2Success.Visibility = Visibility.Visible;

            Microsoft.Win32.OpenFileDialog ofd =
                new Microsoft.Win32.OpenFileDialog {Filter = "Sqlite Files (*.DB3)|*.db3"};
            ofd.ShowDialog();
            if (!string.IsNullOrEmpty(ofd.FileName))
            {
                //ShokoServer.RunMyAnime2Worker(ofd.FileName);
            }
        }

        #endregion

        void btnApplyServerPort_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(txtServerPort.Text))
            {
                MessageBox.Show(Commons.Properties.Resources.Server_EnterAnyValue,
                    Commons.Properties.Resources.Error,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                txtServerPort.Focus();
                return;
            }

            bool success = ushort.TryParse(txtServerPort.Text, out ushort port);
            if (!success || port <= 0 || port > 65535)
            {
                MessageBox.Show(Commons.Properties.Resources.Server_EnterCertainValue,
                    Commons.Properties.Resources.Error,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                txtServerPort.Focus();
                return;
            }
            if (!Utils.IsAdministrator())
            {
                MessageBox.Show(Commons.Properties.Resources.Settings_ChangeServerPortFail,
                    Commons.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            try
            {
                Cursor = Cursors.Wait;
                if (!ShokoServer.Instance.SetNancyPort(port)){
                    MessageBox.Show(Commons.Properties.Resources.Settings_ChangeServerPortFail,
                        Commons.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                Cursor = Cursors.Arrow;
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
                MessageBox.Show(ex.Message, Commons.Properties.Resources.Error, MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        void btnToolbarHelp_Click(object sender, RoutedEventArgs e)
        {
            AboutForm frm = new AboutForm();
            frm.Owner = this;
            frm.ShowDialog();
        }

        void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            //ServerInfo.Instance.RefreshImportFolders();

            if (ServerSettings.Instance.MinimizeOnStartup) MinimizeToTray();

            tabControl1.SelectedIndex = 4; // Settings

            logger.Info("Clearing Cache...");

            Utils.ClearAutoUpdateCache();

            if (ServerSettings.Instance.FirstRun || !ShokoService.AnidbProcessor.ValidAniDBCredentials())
            {
                ShokoServer.Instance.RestartAniDBSocket();
                
                if (!ShokoService.AnidbProcessor.Login()) ShowAniDBLoginDialog();
                
                while (AniDBLoginOpen) Thread.Sleep(100);

                if (ShokoService.AnidbProcessor.ValidAniDBCredentials())
                {
                    logger.Info("Initializing DB...");
                    ShokoServer.RunWorkSetupDB();
                }
            }

            ShokoServer.Instance.CheckForUpdates();
            ShokoServer.Instance.UpdateAvailable += (s, args) => new UpdateForm {Owner = Instance}.ShowDialog();
        }

        #region UI events and methods

        private void CommandBinding_ScanFolder(object sender, ExecutedRoutedEventArgs e)
        {
            object obj = e.Parameter;
            if (obj == null) return;

            try
            {
                if (obj.GetType() == typeof(SVR_ImportFolder))
                {
                    SVR_ImportFolder fldr = (SVR_ImportFolder) obj;

                    ShokoServer.ScanFolder(fldr.ImportFolderID);
                    MessageBox.Show(Commons.Properties.Resources.Server_ScanFolder,
                        Commons.Properties.Resources.Success,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Utils.ShowErrorMessage(ex);
            }
        }

        void btnLaunchWebUI_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string IP = GetLocalIPv4(NetworkInterfaceType.Ethernet);
                if (string.IsNullOrEmpty(IP))
                    IP = "127.0.0.1";

                string url = $"http://{IP}:{ServerSettings.Instance.ServerPort}";
                Process.Start(url);
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        internal static string GetLocalIPv4(NetworkInterfaceType _type)
        {
            string output = "";
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

        private void MsgBox(Func<NetworkInterfaceType, string> getLocalIPv4)
        {
            throw new NotImplementedException();
        }

        void btnUpdateAniDBInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Cursor = Cursors.Wait;
                Importer.RunImport_UpdateAllAniDB();
                Cursor = Cursors.Arrow;
                MessageBox.Show(Commons.Properties.Resources.Server_AniDBInfoUpdate,
                    Commons.Properties.Resources.Success,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        void btnUpdateTvDBInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Cursor = Cursors.Wait;
                Importer.RunImport_UpdateTvDB(false);
                Cursor = Cursors.Arrow;
                MessageBox.Show(Commons.Properties.Resources.Server_TvDBInfoUpdate,
                    Commons.Properties.Resources.Success,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        void btnUpdateAllStats_Click(object sender, RoutedEventArgs e)
        {
            Cursor = Cursors.Wait;
            Importer.UpdateAllStats();
            Cursor = Cursors.Arrow;
            MessageBox.Show(Commons.Properties.Resources.Server_StatsInfoUpdate,
                Commons.Properties.Resources.Success,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // This forces an update of TVDb and tries to get any new Images
        void btnUpdateImages_Click(object sender, RoutedEventArgs e)
        {
            Cursor = Cursors.Wait;
            Importer.RunImport_UpdateTvDB(true);
            ShokoServer.Instance.DownloadAllImages();
            Cursor = Cursors.Arrow;
            MessageBox.Show(Commons.Properties.Resources.Server_UpdateImages,
                Commons.Properties.Resources.Success,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void btnSyncVotes_Click(object sender, RoutedEventArgs e)
        {
            Queue.Instance.Add(new CmdAniDBSyncMyVotes());
            MessageBox.Show(Commons.Properties.Resources.Server_SyncVotes,
                Commons.Properties.Resources.Success,
                MessageBoxButton.OK, MessageBoxImage.Information);
            //JMMService.AnidbProcessor.IsBanned = true;
        }

        void btnSyncMyList_Click(object sender, RoutedEventArgs e)
        {
            ShokoServer.SyncMyList();
            MessageBox.Show(Commons.Properties.Resources.Server_SyncMyList,
                Commons.Properties.Resources.Success,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void btnSyncTrakt_Click(object sender, RoutedEventArgs e)
        {
            Cursor = Cursors.Wait;
            if (ServerSettings.Instance.TraktTv.Enabled && !string.IsNullOrEmpty(ServerSettings.Instance.TraktTv.AuthToken))
            {
                Queue.Instance.Add(new CmdTraktSyncCollection(true));
            }
            Cursor = Cursors.Arrow;
            MessageBox.Show(Commons.Properties.Resources.Server_SyncTrakt,
                Commons.Properties.Resources.Success,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void btnRunImport_Click(object sender, RoutedEventArgs e)
        {
            ShokoServer.RunImport();
            MessageBox.Show(Commons.Properties.Resources.Server_ImportRunning,
                Commons.Properties.Resources.Success,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void btnRemoveMissingFiles_Click(object sender, RoutedEventArgs e)
        {
            ShokoServer.RemoveMissingFiles();
            MessageBox.Show(Commons.Properties.Resources.Server_RemoveMissingFiles,
                Commons.Properties.Resources.Success,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void btnGeneralResume_Click(object sender, RoutedEventArgs e)
        {
            Queue.Instance.ResumeWorkTypes(Queue.GeneralWorkTypesExceptSchedule);
        }

        void btnGeneralPause_Click(object sender, RoutedEventArgs e)
        {
            Queue.Instance.PauseWorkTypes(Queue.GeneralWorkTypesExceptSchedule);
        }

        void btnHasherResume_Click(object sender, RoutedEventArgs e)
        {
            Queue.Instance.ResumeWorkTypes(WorkTypes.Hashing);
        }

        void btnHasherPause_Click(object sender, RoutedEventArgs e)
        {
            Queue.Instance.PauseWorkTypes(WorkTypes.Hashing);
        }

        void btnImagesResume_Click(object sender, RoutedEventArgs e)
        {
            Queue.Instance.ResumeWorkTypes(WorkTypes.Image);
        }

        void btnImagesPause_Click(object sender, RoutedEventArgs e)
        {
            Queue.Instance.PauseWorkTypes(WorkTypes.Image);
        }

        void btnToolbarShutdown_Click(object sender, RoutedEventArgs e)
        {
            shutdownServer();
        }

        void shutdownServer()
        {
            isAppExiting = true;
            try
            {
                Close();
            }
            catch
            {
            }
            TippuTrayNotify.Visible = false;
            TippuTrayNotify.Dispose();
        }

        void restartServer()
        {
            System.Windows.Forms.Application.Restart();
        }

        #endregion

        #region Tray Minimize

        void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
                Hide();
            else
                Show();
        }

        void TippuTrayNotify_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            this.Show();
        }

        private void CreateMenus()
        {
            //Create a object for the context menu
            ctxTrayMenu = new ContextMenuStrip();

            //Add the Menu Item to the context menu
            ToolStripMenuItem mnuShow = new ToolStripMenuItem();
            mnuShow.Text = Commons.Properties.Resources.Toolbar_Show;
            mnuShow.Click += mnuShow_Click;
            ctxTrayMenu.Items.Add(mnuShow);

            //Add Web UI item to the context menu.
            ToolStripMenuItem mnuWebUI = new ToolStripMenuItem();
            mnuWebUI.Text = Commons.Properties.Resources.Toolbar_WebUI;
            mnuWebUI.Click += mnuWebUI_Click;
            ctxTrayMenu.Items.Add(mnuWebUI);

            //Add the Menu Item to the context menu
            ToolStripMenuItem mnuExit = new ToolStripMenuItem();
            mnuExit.Text = Commons.Properties.Resources.Toolbar_Shutdown;
            mnuExit.Click += mnuExit_Click;
            ctxTrayMenu.Items.Add(mnuExit);

            //Add the Context menu to the Notify Icon Object
            TippuTrayNotify.ContextMenuStrip = ctxTrayMenu;
        }

        void mnuShow_Click(object sender, EventArgs e)
        {
            Show();
        }

        void mnuWebUI_Click(object sender, EventArgs e)
        {
            try
            {
                string IP = GetLocalIPv4(NetworkInterfaceType.Ethernet);
                if (string.IsNullOrEmpty(IP))
                    IP = "127.0.0.1";

                string url = $"http://{IP}:{ServerSettings.Instance.ServerPort}";
                Process.Start(url);
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        private void ShutDown()
        {
            ShokoServer.StopWatchingFiles();
            ShokoServer.AniDBDispose();
            ShokoServer.StopHost();
        }

        private void MinimizeToTray()
        {
            Hide();
            TippuTrayNotify.BalloonTipIcon = ToolTipIcon.Info;
            TippuTrayNotify.BalloonTipTitle = Commons.Properties.Resources.ShokoServer;
            TippuTrayNotify.BalloonTipText = Commons.Properties.Resources.Server_MinimizeInfo;
            //TippuTrayNotify.ShowBalloonTip(400);
        }

        void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            //When the application is closed, check wether the application is
            //exiting from menu or forms close button
            if (!isAppExiting)
            {
                //if the forms close button is triggered, cancel the event and hide the form
                //then show the notification ballon tip
                e.Cancel = true;
                MinimizeToTray();
            }
            else
            {
                ShutDown();
            }
        }

        void mnuExit_Click(object sender, EventArgs e)
        {
            isAppExiting = true;
            Close();
            TippuTrayNotify.Visible = false;
            TippuTrayNotify.Dispose();
        }

        #endregion
    }
}
