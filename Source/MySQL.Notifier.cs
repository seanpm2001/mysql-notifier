﻿// Copyright (c) 2012-2013, Oracle and/or its affiliates. All rights reserved.
//
// This program is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License as
// published by the Free Software Foundation; version 2 of the
// License.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 51 Franklin St, Fifth Floor, Boston, MA
// 02110-1301  USA

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using MySql.Notifier.Forms;
using MySql.Notifier.Properties;
using MySQL.Utility.Classes;
using MySQL.Utility.Classes.MySQLInstaller;
using MySQL.Utility.Classes.MySQLWorkbench;
using MySQL.Utility.Forms;

namespace MySql.Notifier
{
  internal class Notifier : IDisposable
  {
    /// <summary>
    /// Default connections file load retry wait interval in milliseconds.
    /// </summary>
    private const int DEFAULT_FILE_LOAD_RETRY_WAIT = 333;

    /// <summary>
    /// Default framework constraint for notify icons.
    /// </summary>
    private const int MAX_TOOLTIP_LENGHT = 63;

    /// <summary>
    /// Gets the default name of the task which usually will be MySQLNotifierTask.
    /// </summary>
    /// <value>
    /// The default name of the task.
    /// </value>
    public static string DefaultTaskName
    {
      get
      {
        return AssemblyInfo.AssemblyTitle.Replace(" ", string.Empty) + "Task";
      }
    }

    /// <summary>
    /// Gets the default task path. Usually the path where the executable MySQLNotifier.exe is.
    /// </summary>
    /// <value>
    /// The default task path.
    /// </value>
    public static string DefaultTaskPath
    {
      get
      {
        return Utility.GetInstallLocation(AssemblyInfo.AssemblyTitle) + Assembly.GetExecutingAssembly().ManifestModule.Name;
      }
    }

    #region Fields

    private System.ComponentModel.IContainer _components;
    private ToolStripSeparator _hasUpdatesSeparator;
    private ToolStripMenuItem _ignoreAvailableUpdateMenuItem;
    private ToolStripMenuItem _installAvailablelUpdatesMenuItem;
    private ToolStripMenuItem _launchInstallerMenuItem;
    private ToolStripMenuItem _launchWorkbenchUtilitiesMenuItem;
    private MachinesList _machinesList;
    private MySqlInstancesList _mySQLInstancesList;
    private NotifyIcon _notifyIcon;
    private OptionsDialog _optionsDialog;
    private ManageItemsDialog _manageItemsDialog;
    private AboutDialog _aboutDialog;
    private FileSystemWatcher _settingsFileWatcher;
    private FileSystemWatcher _connectionsFileWatcher;
    private FileSystemWatcher _serversFileWatcher;
    private bool _closing;
    private ToolStripSeparator _refreshStatusSeparator;
    private ToolStripMenuItem _refreshStatusMenuItem;
    private ToolStripMenuItem _manageServicesMenuItem;
    private ToolStripMenuItem _checkForUpdatesMenuItem;
    private ToolStripMenuItem _optionsMenuItem;
    private ToolStripMenuItem _aboutMenuItem;
    private ToolStripMenuItem _exitMenuItem;
    private ToolStripMenuItem _actionsMenuItem;
    private ContextMenuStrip _staticMenu;
    private int _previousMachineCount;

    /// <summary>
    /// The timer that fires the connection status checks.
    /// </summary>
    private System.Timers.Timer _globalTimer;

    /// <summary>
    /// Background worker that performs the refresh of machines, services and MySQL instances.
    /// </summary>
    private BackgroundWorker _worker;

    #endregion Fields

    /// <summary>
    /// Initializes a new instance of the <see cref="Notifier"/> class.
    /// </summary>
    public Notifier()
    {
      // Fields initializations.
      _closing = false;
      _globalTimer = null;
      _optionsDialog = null;
      _manageItemsDialog = null;
      _aboutDialog = null;
      _serversFileWatcher = null;
      _connectionsFileWatcher = null;
      _settingsFileWatcher = null;
      _refreshStatusSeparator = null;
      _refreshStatusMenuItem = null;
      _manageServicesMenuItem = null;
      _checkForUpdatesMenuItem = null;
      _optionsMenuItem = null;
      _aboutMenuItem = null;
      _exitMenuItem = null;
      _actionsMenuItem = null;
      _staticMenu = null;
      _worker = null;
      StatusRefreshInProgress = false;

      // Static initializations.
      CustomizeInfoDialog();
      InitializeMySqlWorkbenchStaticSettings();

      _components = new System.ComponentModel.Container();
      _notifyIcon = new NotifyIcon(_components);
      _notifyIcon.Visible = true;
      RefreshNotifierIcon();
      _notifyIcon.MouseClick += notifyIcon_MouseClick;
      _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
      _notifyIcon.BalloonTipTitle = Properties.Resources.BalloonTitleServiceStatus;

      // Setup instances list
      _mySQLInstancesList = new MySqlInstancesList();
      _mySQLInstancesList.InstanceStatusChanged += MySqlInstanceStatusChanged;
      _mySQLInstancesList.InstancesListChanged += MySqlInstancesListChanged;
      _mySQLInstancesList.InstanceConnectionStatusTestErrorThrown += MySqlInstanceConnectionStatusTestErrorThrown;

      _machinesList = new MachinesList();
      _machinesList.MachineListChanged += machinesList_MachineListChanged;
      _machinesList.MachineServiceStatusChangeError += machinesList_MachineServiceStatusChangeError;
      _machinesList.MachineServiceListChanged += machinesList_MachineServiceListChanged;
      _machinesList.MachineServiceStatusChanged += machinesList_MachineServiceStatusChanged;
      _machinesList.MachineStatusChanged += machinesList_MachineStatusChanged;

      SetupContextMenu();
      SetNotifyIconToolTip();

      // This method ▼ populates services with post-load information, we need to execute it after the Popup-Menu has been initialized at RefreshMenuIfNeeded(bool).
      _machinesList.LoadMachinesServices();
      PreviousServicesAndInstancesCount = CurrentServicesAndInstancesCount;
      _previousMachineCount = _machinesList.Machines.Count;

      // Create watcher for WB files
      string applicationDataFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
      if (MySqlWorkbench.AllowsExternalConnectionsManagement)
      {
        string file = String.Format(@"{0}\MySQL\Workbench\connections.xml", applicationDataFolderPath);
        if (File.Exists(file))
        {
          _connectionsFileWatcher = StartWatcherForFile(file, connectionsFile_Changed);
        }

        file = String.Format(@"{0}\MySQL\Workbench\server_instances.xml", applicationDataFolderPath);
        if (File.Exists(file))
        {
          _serversFileWatcher = StartWatcherForFile(file, ServersFileChanged);
        }
      }

      // Load instances
      _mySQLInstancesList.RefreshInstances(true);
      StartGlobalTimer();

      _settingsFileWatcher = StartWatcherForFile(applicationDataFolderPath + @"\Oracle\MySQL Notifier\settings.config", SettingsFileChanged);
      RefreshNotifierIcon();

      // Migrate Notifier connections to the MySQL Workbench connections file if possible.
      MySqlWorkbench.MigrateExternalConnectionsToWorkbench();
    }

    /// <summary>
    /// Gets the current total count of monitored services in all machines plus all mysql instances.
    /// </summary>
    public int CurrentServicesAndInstancesCount
    {
      get
      {
        return (_machinesList != null ? _machinesList.ServicesCount : 0) + (_mySQLInstancesList != null ? _mySQLInstancesList.Count : 0);
      }
    }

    /// <summary>
    /// Gets a value indicating whether a status refresh is still ongoing.
    /// </summary>
    public bool StatusRefreshInProgress { get; private set; }

    /// <summary>
    /// Gets the total count of monitored services in all machines plus all mysql instances stored at the time of building menu items.
    /// </summary>
    public int PreviousServicesAndInstancesCount { get; private set; }

    /// <summary>
    /// Cancels the asynchronous status refresh.
    /// </summary>
    /// <returns>true if the background status refresh was cancelled, false otherwise</returns>
    public void CancelAsynchronousStatusRefresh()
    {
      if (_worker != null && _worker.WorkerSupportsCancellation && (StatusRefreshInProgress || _worker.IsBusy))
      {
        _worker.CancelAsync();
      }
    }

    /// <summary>
    /// Releases all resources used by the <see cref="Notifier"/> class
    /// </summary>
    public void Dispose()
    {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Refreshes the status .
    /// </summary>
    /// <param name="asynchronous">Flag indicating if the status check is run asynchronously or synchronously.</param>
    public void RefreshStatus(bool asynchronous)
    {
      if (StatusRefreshInProgress)
      {
        return;
      }

      StatusRefreshInProgress = true;
      _refreshStatusMenuItem.Text = Resources.CancelStatusRefreshMenuText;
      _refreshStatusMenuItem.Image = Resources.CancelRefresh;

      if (asynchronous)
      {
        SetupStatusRefreshBackgroundWorker();
        _worker.RunWorkerAsync();
      }
      else
      {
        Cursor.Current = Cursors.WaitCursor;
        StatusRefreshWorkerDoWork(this, new DoWorkEventArgs(null));
        StatusRefreshWorkerCompleted(this, new RunWorkerCompletedEventArgs(null, null, false));
        Cursor.Current = Cursors.Default;
      }
    }

    /// <summary>
    /// Releases all resources used by the <see cref="Notifier"/> class
    /// </summary>
    /// <param name="disposing">If true this is called by Dispose(), otherwise it is called by the finalizer</param>
    protected virtual void Dispose(bool disposing)
    {
      if (disposing)
      {
        // Free managed resources
        if (_components != null)
        {
          _components.Dispose();
        }

        if (_hasUpdatesSeparator != null)
        {
          _hasUpdatesSeparator.Dispose();
        }

        if (_ignoreAvailableUpdateMenuItem != null)
        {
          _ignoreAvailableUpdateMenuItem.Dispose();
        }

        if (_installAvailablelUpdatesMenuItem != null)
        {
          _installAvailablelUpdatesMenuItem.Dispose();
        }

        if (_launchInstallerMenuItem != null)
        {
          _launchInstallerMenuItem.Dispose();
        }

        if (_launchWorkbenchUtilitiesMenuItem != null)
        {
          _launchWorkbenchUtilitiesMenuItem.Dispose();
        }

        if (_refreshStatusSeparator != null)
        {
          _refreshStatusSeparator.Dispose();
        }

        if (_refreshStatusMenuItem != null)
        {
          _refreshStatusMenuItem.Dispose();
        }

        if (_manageServicesMenuItem != null)
        {
          _manageServicesMenuItem.Dispose();
        }

        if (_checkForUpdatesMenuItem != null)
        {
          _checkForUpdatesMenuItem.Dispose();
        }

        if (_optionsMenuItem != null)
        {
          _optionsMenuItem.Dispose();
        }

        if (_aboutMenuItem != null)
        {
          _aboutMenuItem.Dispose();
        }

        if (_exitMenuItem != null)
        {
          _exitMenuItem.Dispose();
        }

        if (_actionsMenuItem != null)
        {
          _actionsMenuItem.Dispose();
        }

        if (_staticMenu != null)
        {
          _staticMenu.Dispose();
        }

        if (_mySQLInstancesList != null)
        {
          _mySQLInstancesList.Dispose();
        }

        if (_machinesList != null)
        {
          _machinesList.Dispose();
        }

        if (_notifyIcon != null)
        {
          _notifyIcon.Dispose();
        }

        if (_connectionsFileWatcher != null)
        {
          _connectionsFileWatcher.Dispose();
        }

        if (_serversFileWatcher != null)
        {
          _serversFileWatcher.Dispose();
        }

        if (_settingsFileWatcher != null)
        {
          _settingsFileWatcher.Dispose();
        }

        if (_worker != null)
        {
          if (_worker.IsBusy)
          {
            _worker.CancelAsync();
            ushort cancelAsyncWait = 0;
            while (_worker.IsBusy && cancelAsyncWait < Machine.DEFAULT_CANCEL_ASYNC_WAIT)
            {
              Thread.Sleep(Machine.DEFAULT_CANCEL_ASYNC_STEP);
              cancelAsyncWait += Machine.DEFAULT_CANCEL_ASYNC_STEP;
            }
          }

          _worker.DoWork -= StatusRefreshWorkerDoWork;
          _worker.RunWorkerCompleted -= StatusRefreshWorkerCompleted;
          _worker.Dispose();
        }
      }

      // Add class finalizer if unmanaged resources are added to the class
      // Free unmanaged resources if there are any
    }

    /// <summary>
    /// Notifies that the Notifier wants to quit
    /// </summary>
    public event EventHandler Exit;

    /// <summary>
    /// Initializes settings for the <see cref="MySqlWorkbench"/> and <see cref="MySqlWorkbenchPasswordVault"/> classes.
    /// </summary>
    public static void InitializeMySqlWorkbenchStaticSettings()
    {
      string applicationDataFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
      MySqlWorkbench.ExternalApplicationName = AssemblyInfo.AssemblyTitle;
      MySqlWorkbenchPasswordVault.ApplicationPasswordVaultFilePath = applicationDataFolderPath + @"\Oracle\MySQL Notifier\user_data.dat";
      MySqlWorkbench.ExternalApplicationConnectionsFilePath = applicationDataFolderPath + @"\Oracle\MySQL Notifier\connections.xml";
      MySqlSourceTrace.LogFilePath = applicationDataFolderPath + @"\Oracle\MySQL Notifier\MySQLNotifier.log";
      MySqlSourceTrace.SourceTraceClass = "MySqlNotifier";
    }

    /// <summary>
    /// Sets the text displayed in the notify icon's tooltip
    /// </summary>
    public void SetNotifyIconToolTip()
    {
      var version = Assembly.GetExecutingAssembly().GetName().Version.ToString().Split('.');
      string toolTipText = string.Format("{0} ({1})\n{2}.",
                                         Properties.Resources.AppName,
                                         string.Format("{0}.{1}.{2}", version[0], version[1], version[2]),
                                         string.Format(Properties.Resources.ToolTipText, _machinesList.ServicesCount, _mySQLInstancesList.Count));
      _notifyIcon.Text = (toolTipText.Length >= MAX_TOOLTIP_LENGHT ? toolTipText.Substring(0, MAX_TOOLTIP_LENGHT - 3) + "..." : toolTipText);
    }

    /// <summary>
    /// Forces the Notifier to quit, called from the Application Context.
    /// </summary>
    public void ForceExit()
    {
      if (_closing)
      {
        return;
      }

      OnExit(EventArgs.Empty);
    }

    /// <summary>
    /// Invokes the Exit event
    /// </summary>
    /// <param name="e">Event arguments</param>
    protected virtual void OnExit(EventArgs e)
    {
      _closing = true;
      _notifyIcon.Visible = false;

      StopBackgroundActions();

      if (this.Exit != null)
      {
        Exit(this, e);
      }
    }

    private void aboutMenu_Click(object sender, EventArgs e)
    {
      if (_aboutDialog == null)
      {
        using (AboutDialog aboutDialog = new AboutDialog())
        {
          _aboutDialog = aboutDialog;
          aboutDialog.ShowDialog();
          _aboutDialog = null;
        }
      }
      else
      {
        _aboutDialog.Activate();
      }
    }

    private void refreshStatus_Click(object sender, EventArgs e)
    {
      if (StatusRefreshInProgress)
      {
        CancelAsynchronousStatusRefresh();
      }
      else
      {
        RefreshStatus(true);
      }
    }

    private void checkUpdatesItem_Click(object sender, EventArgs e)
    {
      if (string.IsNullOrEmpty(MySqlInstaller.GetInstallerPath()) || Convert.ToDouble(MySqlInstaller.GetInstallerVersion().Substring(0, 3)) < 1.1)
      {
        InfoDialog.ShowErrorDialog(Resources.MissingMySQLInstaller, string.Format(Resources.Installer11RequiredForCheckForUpdates, Environment.NewLine));
        return;
      }

      string path = @MySqlInstaller.GetInstallerPath();
      Process proc = new Process();
      ProcessStartInfo startInfo = new ProcessStartInfo
      {
        FileName = @String.Format(@"{0}\MySQLInstaller.exe", @path),
        Arguments = "checkforupdates"
      };

      Process.Start(startInfo);
    }

    /// <summary>
    /// Method to handle the change events in the Workbench's connections file.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void connectionsFile_Changed(object sender, FileSystemEventArgs e)
    {
      if (!ReloadWorkbenchConnectionsFile())
      {
        return;
      }

      // If the application is exiting (so the Notifier icon was hidden), then don't continue on refreshing instances.
      if (!_notifyIcon.Visible)
      {
        return;
      }

      MarkOrphanInstancesForRemoval();
      _mySQLInstancesList.RefreshInstances(false);

      foreach (var item in _machinesList.Machines.SelectMany(machine => machine.Services))
      {
        item.MenuGroup.RefreshMenu(_notifyIcon.ContextMenuStrip);
      }
    }

    /// <summary>
    /// Reloads Workbench's connections file to get the latest changes.
    /// </summary>
    private bool ReloadWorkbenchConnectionsFile()
    {
      bool workbenchConnectionsLoadSuccessful = false;
      Exception loadException = null;
      for (int retryCount = 0; retryCount < 3 && !workbenchConnectionsLoadSuccessful; retryCount++)
      {
        try
        {
          MySqlWorkbench.LoadData();
          workbenchConnectionsLoadSuccessful = true;
          loadException = null;
        }
        catch (Exception ex)
        {
          loadException = ex;
          Debug.WriteLine(ex.Message);
          Thread.Sleep(DEFAULT_FILE_LOAD_RETRY_WAIT);
        }
      }

      if (loadException != null)
      {
        InfoDialog.ShowErrorDialog(Resources.ConnectionsFileLoadingErrorTitle, Resources.ConnectionsFileLoadingErrorDetail, null, Resources.ConnectionsFileLoadingErrorMoreInfo, true, InfoDialog.DefaultButtonType.AcceptButton, 30);
        MySqlSourceTrace.WriteAppErrorToLog(loadException);
      }

      return workbenchConnectionsLoadSuccessful;
    }

    /// <summary>
    /// Sets instances with related workbench connections that were deleted at workbench for further deletion.
    /// </summary>
    private void MarkOrphanInstancesForRemoval()
    {
      foreach (var i in _mySQLInstancesList.InstancesList.Where(i => MySqlWorkbench.Connections.All(wbc => wbc.Id != i.WorkbenchConnection.Id)))
      {
        i.ClearWorkbenchConnection();
      }
    }

    private void ContextMenuStrip_Opening(object sender, CancelEventArgs e)
    {
      UpdateStaticMenuItems();
    }

    /// <summary>
    /// Customizes the looks of the <see cref="MySQL.Utility.Forms.InfoDialog"/> form for the MySQL Notifier.
    /// </summary>
    private static void CustomizeInfoDialog()
    {
      InfoDialog.ApplicationName = AssemblyInfo.AssemblyTitle;
      InfoDialog.SuccessLogo = Properties.Resources.ApplicationLogo;
      InfoDialog.ErrorLogo = Properties.Resources.NotifierErrorImage;
      InfoDialog.WarningLogo = Properties.Resources.NotifierWarningImage;
      InfoDialog.InformationLogo = Properties.Resources.ApplicationLogo;
      InfoDialog.ApplicationIcon = Properties.Resources.MySqlNotifierIcon;
    }

    /// <summary>
    /// When the exit menu itemText is clicked, make a call to terminate the ApplicationContext.
    /// </summary>
    /// <param name="sender">Sender object.</param>
    /// <param name="e">Event arguments.</param>
    private void exitItem_Click(object sender, EventArgs e)
    {
      OnExit(EventArgs.Empty);
    }

    /// <summary>
    /// Returns a new tray icon for the Notifier based on current Services/Instances/Updates statuses.
    /// </summary>
    /// <returns>A bitmap of the updated tray icon.</returns>
    private Bitmap GetIconForNotifier()
    {
      if (_machinesList == null || _mySQLInstancesList == null)
      {
        return Properties.Resources.NotifierIcon;
      }

      bool hasUpdates = (Settings.Default.UpdateCheck & (int)SoftwareUpdateStaus.HasUpdates) != 0;
      bool useColorfulIcon = Settings.Default.UseColorfulStatusIcons;

      // Create a list of instances and of services where the UpdateTrayIconOnStatusChange is true.
      var updateIconServicesList = _machinesList.Machines.SelectMany(machine => machine.Services).Where(service => service.UpdateTrayIconOnStatusChange);
      var updateIconInstancesList = _mySQLInstancesList.InstancesList.Where(instance => instance.UpdateTrayIconOnStatusChange);

      // Stopped or update+stopped notifier icon.
      if (updateIconServicesList.Count(t => t.Status == MySqlServiceStatus.Stopped || t.Status == MySqlServiceStatus.Unavailable) + updateIconInstancesList.Count(i => i.ConnectionStatus == MySqlWorkbenchConnection.ConnectionStatusType.RefusingConnections) > 0)
      {
        return useColorfulIcon ? (hasUpdates ? Properties.Resources.NotifierIconStoppedAlertStrong : Properties.Resources.NotifierIconStoppedStrong) : (hasUpdates ? Properties.Resources.NotifierIconStoppedAlert : Properties.Resources.NotifierIconStopped);
      }

      // Starting or update+starting notifier icon.
      if (updateIconServicesList.Count(t => t.Status == MySqlServiceStatus.StartPending) > 0)
      {
        return useColorfulIcon ? (hasUpdates ? Properties.Resources.NotifierIconStartingAlertStrong : Properties.Resources.NotifierIconStartingStrong) : (hasUpdates ? Properties.Resources.NotifierIconStartingAlert : Properties.Resources.NotifierIconStarting);
      }

      // Running or update+running notifier icon.
      if (updateIconServicesList.Count(t => t.Status == MySqlServiceStatus.Running) + updateIconInstancesList.Count(i => i.ConnectionStatus == MySqlWorkbenchConnection.ConnectionStatusType.AcceptingConnections) > 0)
      {
        return hasUpdates ? Properties.Resources.NotifierIconRunningAlert : Properties.Resources.NotifierIconRunning;
      }

      // Clean or update+clean notifier icon.
      return hasUpdates ? Properties.Resources.NotifierIconAlert : Properties.Resources.NotifierIcon;
    }

    private void IgnoreAvailableUpdateItem_Click(object sender, EventArgs e)
    {
      DialogResult result = InfoDialog.ShowYesNoDialog(InfoDialog.InfoType.Warning, "Available Updates", Resources.IgnoreAvailableUpdatesText);
      if (result != DialogResult.Yes)
      {
        return;
      }

      Settings.Default.UpdateCheck = 0;
      Settings.Default.Save();
      RefreshNotifierIcon();
    }

    private void InstallAvailablelUpdates_Click(object sender, EventArgs e)
    {
      LaunchInstallerItem_Click(null, EventArgs.Empty);
      Settings.Default.UpdateCheck = 0;
      Settings.Default.Save();
      RefreshNotifierIcon();
    }

    private void LaunchInstallerItem_Click(object sender, EventArgs e)
    {
      string path = MySqlInstaller.GetInstallerPath();
      if (string.IsNullOrEmpty(path))
      {
        // this should not happen since our menu itemText is enabled
        return;
      }

      Process proc = new Process();
      ProcessStartInfo startInfo = new ProcessStartInfo {FileName = String.Format(@"{0}\MySQLInstaller.exe", path)};
      Process.Start(startInfo);
    }

    private void LaunchWorkbenchUtilities_Click(object sender, EventArgs e)
    {
      MySqlWorkbench.LaunchUtilitiesShell();
    }

    private int LoadUpdateCheck()
    {
      for (int i = 0; i < 3; i++)
      {
        try
        {
          Settings.Default.Reload();
          return Settings.Default.UpdateCheck;
        }
        catch (IOException ex)
        {
          MySqlSourceTrace.WriteToLog(Resources.SettingsFileFailedToLoad + " - " + ex.Message + " " + ex.InnerException, SourceLevels.Warning);
          System.Threading.Thread.Sleep(1000);
        }
      }

      InfoDialog.ShowErrorDialog(Resources.HighSeverityError, Resources.SettingsFileFailedToLoad);
      return -1;
    }

    /// <summary>
    /// Event delegate method fired when a machine is added to or removed from the <see cref="_machinesList"/>.
    /// </summary>
    /// <param name="machine">Added or removed machine.</param>
    /// <param name="changeType">List change type.</param>
    private void machinesList_MachineListChanged(Machine machine, ChangeType changeType)
    {
      switch (changeType)
      {
        case ChangeType.AddByUser:
        case ChangeType.AddByLoad:
        case ChangeType.AutoAdd:
          ResetContextMenuStructure(changeType == ChangeType.RemoveByEvent || changeType == ChangeType.RemoveByUser);
          if (machine.IsLocal && !machine.HasServices && changeType == ChangeType.AutoAdd)
          {
            break;
          }

          machine.SetupMenuGroup(_notifyIcon.ContextMenuStrip);
          if (changeType == ChangeType.AddByUser)
          {
            ShowTooltip(false, Resources.BalloonTitleMachinesList, string.Format(Resources.BalloonTextMachineAdded, machine.Name));
          }

          break;

        case ChangeType.RemoveByUser:
        case ChangeType.RemoveByEvent:
          machine.RemoveMenuGroup(_notifyIcon.ContextMenuStrip);
          if (changeType == ChangeType.RemoveByEvent)
          {
            ShowTooltip(false, Resources.BalloonTitleMachinesList, string.Format(Resources.BalloonTextMachineRemoved, machine.Name));
          }

          if (machine != _machinesList.LocalMachine)
          {
            machine.Dispose();
          }

          ResetContextMenuStructure(changeType == ChangeType.RemoveByEvent || changeType == ChangeType.RemoveByUser);
          break;
      }
    }

    /// <summary>
    /// Event delegate method fired when a machine in the <see cref="_machinesList"/> has a service added or removed.
    /// </summary>
    /// <param name="machine">Machine with an added or removed service.</param>
    /// <param name="service">Service added or removed.</param>
    /// <param name="changeType">List change type.</param>
    private void machinesList_MachineServiceListChanged(Machine machine, MySqlService service, ChangeType changeType)
    {
      ResetContextMenuStructure(changeType == ChangeType.RemoveByEvent || changeType == ChangeType.RemoveByUser);
      switch (changeType)
      {
        case ChangeType.AddByLoad:
        case ChangeType.AddByUser:
        case ChangeType.AutoAdd:
          service.MenuGroup.AddToContextMenu(_notifyIcon.ContextMenuStrip);
          if (changeType == ChangeType.AutoAdd && Settings.Default.NotifyOfAutoServiceAddition)
          {
            ShowTooltip(false, Resources.BalloonTitleServiceList, string.Format(Resources.BalloonTextServiceList, service.DisplayName));
          }

          break;

        case ChangeType.Cleared:
        case ChangeType.RemoveByUser:
        case ChangeType.RemoveByEvent:
          service.MenuGroup.RemoveFromContextMenu(_notifyIcon.ContextMenuStrip);
          if (changeType == ChangeType.RemoveByEvent)
          {
            ShowTooltip(false, Resources.BalloonTitleServiceList, String.Format(Resources.BalloonTextServiceRemoved, service.ServiceName));
          }
          service.Dispose();
          break;

        case ChangeType.Updated:
          if (service.MenuGroup != null)
          {
            service.MenuGroup.Update();
          }
          break;
      }

      // Update icon and tooltip
      RefreshNotifierIcon();
      SetNotifyIconToolTip();
    }

    /// <summary>
    /// Event delegate method fired when a machine in the <see cref="_machinesList"/> has a service that changes its status.
    /// </summary>
    /// <param name="machine">Machine with a service whose status changed.</param>
    /// <param name="service">Service whose status changed.</param>
    private void machinesList_MachineServiceStatusChanged(Machine machine, MySqlService service)
    {
      if (service.NotifyOnStatusChange && Settings.Default.NotifyOfStatusChange && service.PreviousStatus != MySqlServiceStatus.Unavailable && service.Status != MySqlServiceStatus.Unavailable)
      {
        ShowTooltip(false, Resources.BalloonTitleServiceStatus, string.Format(Resources.BalloonTextServiceStatus, service.DisplayName, service.PreviousStatus.ToString(), service.Status.ToString()));
      }

      if (service.MenuGroup != null)
      {
        service.MenuGroup.Update();
      }

      if (service.UpdateTrayIconOnStatusChange)
      {
        RefreshNotifierIcon();
      }
    }

    /// <summary>
    /// Event delegate method fired when an error is thrown while trying to change the status of a service in a machine in the <see cref="_machinesList"/>.
    /// </summary>
    /// <param name="machine">Machine containing the service with a status change.</param>
    /// <param name="service">Service with a status change.</param>
    /// <param name="ex">Exception thrown by the service status change.</param>
    private void machinesList_MachineServiceStatusChangeError(Machine machine, MySqlService service, Exception ex)
    {
      string errorText = string.Format(Resources.BalloonTextFailedStatusChange, service.DisplayName, Environment.NewLine + ex.Message + Environment.NewLine + Resources.AskRestartApplication);
      ShowTooltip(true, Resources.BalloonTitleFailedStatusChange, errorText);
      MySqlSourceTrace.WriteToLog("Critical Error when trying to update the service status - " + ex.Message + " " + ex.InnerException, SourceLevels.Critical);
    }

    /// <summary>
    /// Event delegate method fired when a machine in the <see cref="_machinesList"/> has a connection status change.
    /// </summary>
    /// <param name="machine">Machine with a connection status change.</param>
    /// <param name="oldConnectionStatus">Previous machine status.</param>
    private void machinesList_MachineStatusChanged(Machine machine, Machine.ConnectionStatusType oldConnectionStatus)
    {
      if (machine.IsLocal || machine.ConnectionStatus == Machine.ConnectionStatusType.Unknown)
      {
        return;
      }

      machine.UpdateMenuGroup();
      if (machine.OldConnectionStatus != machine.ConnectionStatus
          && machine.OldConnectionStatus != Machine.ConnectionStatusType.Unknown
          && (machine.IsOnline || machine.ConnectionStatus == Machine.ConnectionStatusType.Unavailable))
      {
        ShowTooltip(false, Resources.BalloonTitleMachineStatus, string.Format(Resources.BalloonTextMachineStatus, machine.Name, machine.ConnectionStatus.ToString()));
      }
    }

    private void manageServicesDialogItem_Click(object sender, EventArgs e)
    {
      if (_manageItemsDialog == null)
      {
        // Stop background actions while the user opens the dialog to manage machines, services or instances.
        StopBackgroundActions();

        bool instancesListChanged = false;
        using (ManageItemsDialog manageItemsDialog = new ManageItemsDialog(_mySQLInstancesList, _machinesList))
        {
          _manageItemsDialog = manageItemsDialog;
          manageItemsDialog.ShowDialog();
          instancesListChanged = manageItemsDialog.InstancesListChanged;
          _manageItemsDialog = null;
        }

        ResumeBackgroundActions(instancesListChanged);
      }
      else
      {
        _manageItemsDialog.Activate();
      }
    }

    /// <summary>
    /// Event delegate method fired when an error is thrown while testing a MySQL Instance's status witin the <see cref="_mySQLInstancesList"/>.
    /// </summary>
    /// <param name="sender">Sender object.</param>
    /// <param name="args">Event arguments.</param>
    private void MySqlInstanceConnectionStatusTestErrorThrown(object sender, InstanceConnectionStatusTestErrorThrownArgs args)
    {
      ShowTooltip(true, Resources.ErrorTitle, string.Format(Resources.BalloonTextFailedStatusCheck, args.Instance.HostIdentifier, args.ErrorException.Message));
      MySqlSourceTrace.WriteToLog("Critical Error when trying to update the instance status - " + args.ErrorException.Message + " " + args.ErrorException.InnerException, SourceLevels.Critical);
    }

    /// <summary>
    /// Event delegate method fired when the <see cref="_mySQLInstancesList"/> list changes.
    /// </summary>
    /// <param name="sender">Sender object.</param>
    /// <param name="args">Event arguments.</param>
    private void MySqlInstancesListChanged(object sender, InstancesListChangedArgs args)
    {
      bool instanceRemoved = args.ListChange == ListChangedType.ItemDeleted;
      ResetContextMenuStructure(instanceRemoved);
      switch (args.ListChange)
      {
        case ListChangedType.ItemAdded:
          SetupMySqlInstancesMainMenuItem();
          args.Instance.MenuGroup.AddToContextMenu(_notifyIcon.ContextMenuStrip);
          break;

        case ListChangedType.ItemDeleted:
          SetupMySqlInstancesMainMenuItem();
          args.Instance.MenuGroup.RemoveFromContextMenu(_notifyIcon.ContextMenuStrip);
          RefreshNotifierIcon();
          args.Instance.Dispose();
          break;

        case ListChangedType.ItemChanged:
          args.Instance.MenuGroup.Update(false);
          RefreshNotifierIcon();
          break;

        case ListChangedType.Reset:
          SetupMySqlInstancesMainMenuItem();
          _mySQLInstancesList.RefreshInstances(false);
          RefreshNotifierIcon();
          break;
      }

      SetNotifyIconToolTip();
    }

    /// <summary>
    /// Event delegate method fired when a MySQL Instance's status witin the <see cref="_mySQLInstancesList"/> changes.
    /// </summary>
    /// <param name="sender">Sender object.</param>
    /// <param name="args">Event arguments.</param>
    private void MySqlInstanceStatusChanged(object sender, InstanceStatusChangedArgs args)
    {
      args.Instance.MenuGroup.Update(false);
      if (_notifyIcon.ContextMenuStrip.InvokeRequired)
      {
        _notifyIcon.ContextMenuStrip.Invoke(new MethodInvoker(() => _notifyIcon.ContextMenuStrip.Refresh()));
      }
      else
      {
        _notifyIcon.ContextMenuStrip.Refresh();
      }

      if (args.OldInstanceStatus != MySqlWorkbenchConnection.ConnectionStatusType.Unknown && args.Instance.MonitorAndNotifyStatus && Settings.Default.NotifyOfStatusChange)
      {
        ShowTooltip(false, Resources.BalloonTitleInstanceStatus, string.Format(Resources.BalloonTextInstanceStatus, args.Instance.HostIdentifier, args.NewInstanceStatusText));
      }

      if (args.Instance.UpdateTrayIconOnStatusChange)
      {
        RefreshNotifierIcon();
      }
    }

    private void notifyIcon_MouseClick(object sender, MouseEventArgs e)
    {
      if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right)
      {
        return;
      }

      MethodInfo mi = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
      mi.Invoke(_notifyIcon, null);
    }

    private void optionsItem_Click(object sender, EventArgs e)
    {
      var usecolorfulIcons = Properties.Settings.Default.UseColorfulStatusIcons;
      if (_optionsDialog == null)
      {
        using (OptionsDialog optionsDialog = new OptionsDialog())
        {
          _optionsDialog = optionsDialog;
          optionsDialog.ShowDialog();
          _optionsDialog = null;
        }

        // If there was a change in the setting for the icons then refresh Icon
        if (usecolorfulIcons != Properties.Settings.Default.UseColorfulStatusIcons)
        {
          RefreshNotifierIcon();
        }
      }
      else
      {
        _optionsDialog.Activate();
      }
    }

    /// <summary>
    /// Creates the context Notifier context menu.
    /// </summary>
    private void SetupContextMenu()
    {
      _staticMenu = new ContextMenuStrip();
      _refreshStatusSeparator = new ToolStripSeparator();
      _refreshStatusMenuItem = new ToolStripMenuItem(Resources.RefreshStatusMenuText);
      _refreshStatusMenuItem.Click += refreshStatus_Click;
      _refreshStatusMenuItem.Image = Resources.RefreshStatus;
      _manageServicesMenuItem = new ToolStripMenuItem(Resources.ManageItemsMenuText);
      _manageServicesMenuItem.Click += new EventHandler(manageServicesDialogItem_Click);
      _manageServicesMenuItem.Image = Resources.ManageServicesIcon;
      _launchInstallerMenuItem = new ToolStripMenuItem(Resources.LaunchInstallerMenuText);
      _launchInstallerMenuItem.Click += new EventHandler(LaunchInstallerItem_Click);
      _launchInstallerMenuItem.Image = Resources.StartInstallerIcon;
      _checkForUpdatesMenuItem = new ToolStripMenuItem(Resources.CheckUpdatesMenuText);
      _checkForUpdatesMenuItem.Click += new EventHandler(checkUpdatesItem_Click);
      _checkForUpdatesMenuItem.Image = Resources.CheckForUpdatesIcon;
      _launchWorkbenchUtilitiesMenuItem = new ToolStripMenuItem(Resources.UtilitiesShellMenuText);
      _launchWorkbenchUtilitiesMenuItem.Click += new EventHandler(LaunchWorkbenchUtilities_Click);
      _launchWorkbenchUtilitiesMenuItem.Image = Resources.LaunchUtilities;
      _optionsMenuItem = new ToolStripMenuItem(Resources.OptionsMenuText);
      _optionsMenuItem.Click += new EventHandler(optionsItem_Click);
      _aboutMenuItem = new ToolStripMenuItem(Resources.AboutMenuText);
      _aboutMenuItem.Click += new EventHandler(aboutMenu_Click);
      _exitMenuItem = new ToolStripMenuItem(Resources.CloseNotifierMenuText);
      _exitMenuItem.Click += new EventHandler(exitItem_Click);
      _hasUpdatesSeparator = new ToolStripSeparator();
      _installAvailablelUpdatesMenuItem = new ToolStripMenuItem(Resources.InstallAvailableUpdatesMenuText, Resources.InstallAvailableUpdatesIcon);
      _installAvailablelUpdatesMenuItem.Click += new EventHandler(InstallAvailablelUpdates_Click);
      _ignoreAvailableUpdateMenuItem = new ToolStripMenuItem(Resources.IgnoreUpdateMenuText);
      _ignoreAvailableUpdateMenuItem.Click += new EventHandler(IgnoreAvailableUpdateItem_Click);
      _actionsMenuItem = new ToolStripMenuItem(Resources.Actions, null);
      _actionsMenuItem.Tag = Resources.Actions;

      _staticMenu.Items.Add(_manageServicesMenuItem);
      _staticMenu.Items.Add(_launchInstallerMenuItem);
      _staticMenu.Items.Add(_checkForUpdatesMenuItem);
      _staticMenu.Items.Add(_launchWorkbenchUtilitiesMenuItem);
      _staticMenu.Items.Add(_refreshStatusSeparator);
      _staticMenu.Items.Add(_refreshStatusMenuItem);
      _staticMenu.Items.Add(_hasUpdatesSeparator);
      _staticMenu.Items.Add(_installAvailablelUpdatesMenuItem);
      _staticMenu.Items.Add(_ignoreAvailableUpdateMenuItem);
      _staticMenu.Items.Add(new ToolStripSeparator());
      _staticMenu.Items.Add(_optionsMenuItem);
      _staticMenu.Items.Add(_aboutMenuItem);
      _staticMenu.Items.Add(_exitMenuItem);
      _actionsMenuItem.DropDown = _staticMenu;

      ResetContextMenuStructure(false);
      UpdateStaticMenuItems();
    }

    /// <summary>
    /// Resets the appearance of the context menu by having the static menu items under an Actions sub-menu or directly on the main menu.
    /// </summary>
    /// <param name="itemRemoved">Flag indicating if a service or instance was removed.</param>
    private void ResetContextMenuStructure(bool itemRemoved)
    {
      if (_actionsMenuItem.DropDown.InvokeRequired && CurrentServicesAndInstancesCount > 0)
      {
        _actionsMenuItem.DropDown.Invoke(new MethodInvoker(() => ResetContextMenuStructure(itemRemoved)));
      }
      else if (_staticMenu.InvokeRequired && CurrentServicesAndInstancesCount <= 0)
      {
        _staticMenu.Invoke(new MethodInvoker(() => ResetContextMenuStructure(itemRemoved)));
      }
      else
      {
        if ((CurrentServicesAndInstancesCount + _machinesList.Machines.Count != 0 || !itemRemoved) &&
            (PreviousServicesAndInstancesCount + _previousMachineCount != 0 || itemRemoved))
        {
          return;
        }

        PreviousServicesAndInstancesCount = CurrentServicesAndInstancesCount;
        _previousMachineCount = _machinesList.Machines.Count;
        _notifyIcon.ContextMenuStrip = new ContextMenuStrip();
        _notifyIcon.ContextMenuStrip.Opening += new CancelEventHandler(ContextMenuStrip_Opening);
        if (CurrentServicesAndInstancesCount + _machinesList.Machines.Count > 0)
        {
          _notifyIcon.ContextMenuStrip.Items.Add(_actionsMenuItem);
        }
        else
        {
          _notifyIcon.ContextMenuStrip = _staticMenu;
        }
      }
    }

    /// <summary>
    /// Refreshes the Notifier main icon based on current services and instances statuses.
    /// </summary>
    private void RefreshNotifierIcon()
    {
      _notifyIcon.Icon = Icon.FromHandle(GetIconForNotifier().GetHicon());
    }

    /// <summary>
    /// Resume background connection activities like connection tests, etc.
    /// </summary>
    /// <param name="instancesListChanged">Flag indicating whether MySQL instances were added or deleted shile background actions were paused.</param>
    private void ResumeBackgroundActions(bool instancesListChanged)
    {
      // Resume the global timer.
      _globalTimer.Start();

      // Resume the connections file watcher and refresh manually if a change on instances took place.
      if (_connectionsFileWatcher == null)
      {
        return;
      }

      if (instancesListChanged)
      {
        connectionsFile_Changed(this, new FileSystemEventArgs(WatcherChangeTypes.Changed, string.Empty, string.Empty));
      }

      _connectionsFileWatcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Method to handle the change events in the Workbench's server instances file, no changes in UI.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void ServersFileChanged(object sender, FileSystemEventArgs e)
    {
      MySqlWorkbench.Servers = new MySqlWorkbenchServerCollection();
      MySqlWorkbench.LoadData();
    }

    private void SettingsFileChanged(object sender, FileSystemEventArgs e)
    {
      int settingsUpdateCheck = LoadUpdateCheck();

      // If we have already notified our user then noting more to do
      if (settingsUpdateCheck == 0 || (settingsUpdateCheck & (int)SoftwareUpdateStaus.Notified) != 0)
      {
        return;
      }

      // If we are supposed to check forupdates but the installer is too old then notify the user and exit
      if (string.IsNullOrEmpty(MySqlInstaller.GetInstallerPath()) || !MySqlInstaller.GetInstallerVersion().StartsWith("1.1"))
      {
        ShowTooltip(false, Resources.SoftwareUpdate, Resources.ScheduledCheckRequiresInstaller11);
        settingsUpdateCheck = 0;
      }

      // Let them know we are checking for updates
      if ((settingsUpdateCheck & (int)SoftwareUpdateStaus.Checking) != 0)
      {
        ShowTooltip(false, Resources.SoftwareUpdate, Resources.CheckingForUpdates);
        bool hasUpdates = MySqlInstaller.HasUpdates(10 * 1000);
        Settings.Default.UpdateCheck = hasUpdates ? (int)SoftwareUpdateStaus.HasUpdates : 0;
        settingsUpdateCheck = Settings.Default.UpdateCheck;
      }

      if ((settingsUpdateCheck & (int)SoftwareUpdateStaus.HasUpdates) != 0)
      {
        ShowTooltip(false, Resources.SoftwareUpdate, Resources.HasUpdatesLaunchInstaller);
      }

      // Set that we have notified our user
      Settings.Default.UpdateCheck |= (int)SoftwareUpdateStaus.Notified;

      Settings.Default.Save();
      RefreshNotifierIcon();
    }

    /// <summary>
    /// Adds or removes a context menu item that represents the parent of the MySQL instances menu items.
    /// </summary>
    private void SetupMySqlInstancesMainMenuItem()
    {
      if (_notifyIcon.ContextMenuStrip.InvokeRequired)
      {
        _notifyIcon.ContextMenuStrip.Invoke(new MethodInvoker(SetupMySqlInstancesMainMenuItem));
      }
      else
      {
        int index = MySqlInstanceMenuGroup.FindMenuItemWithinMenuStrip(_notifyIcon.ContextMenuStrip,
          Resources.MySQLInstances);
        if (index < 0 && _mySQLInstancesList.Count > 0)
        {
          index = MySqlInstanceMenuGroup.FindMenuItemWithinMenuStrip(_notifyIcon.ContextMenuStrip, Resources.Actions);
          if (index < 0)
          {
            index = 0;
          }

          // Hide the separator just above this new menu item.
          if (index > 0 && _notifyIcon.ContextMenuStrip.Items[index - 1] is ToolStripSeparator)
          {
            _notifyIcon.ContextMenuStrip.Items[index - 1].Visible = false;
          }

          ToolStripMenuItem instancesMainMenuItem = new ToolStripMenuItem(Resources.MySQLInstances)
          {
            Tag = Resources.MySQLInstances
          };

          Font boldFont = new Font(instancesMainMenuItem.Font, FontStyle.Bold);
          instancesMainMenuItem.Font = boldFont;
          instancesMainMenuItem.BackColor = SystemColors.MenuText;
          instancesMainMenuItem.ForeColor = SystemColors.Menu;
          _notifyIcon.ContextMenuStrip.Items.Insert(index, instancesMainMenuItem);
          _notifyIcon.ContextMenuStrip.Refresh();
        }
        else if (index >= 0 && _mySQLInstancesList.Count == 0)
        {
          // Show the separator just above this new menu item if it's hidden.
          if (_notifyIcon.ContextMenuStrip.Items[index - 1] is ToolStripSeparator)
          {
            _notifyIcon.ContextMenuStrip.Items[index - 1].Visible = true;
          }
          _notifyIcon.ContextMenuStrip.Items.RemoveAt(index);
          _notifyIcon.ContextMenuStrip.Refresh();
        }
      }
    }

    /// <summary>
    /// Initializes the background worker used to refresh services and instances statuses asynchronously.
    /// </summary>
    private void SetupStatusRefreshBackgroundWorker()
    {
      if (_worker == null)
      {
        _worker = new BackgroundWorker {WorkerSupportsCancellation = true, WorkerReportsProgress = false};
        _worker.DoWork += StatusRefreshWorkerDoWork;
        _worker.RunWorkerCompleted += StatusRefreshWorkerCompleted;
      }
    }

    /// <summary>
    /// Generic routine to help with showing tooltips
    /// </summary>
    /// <param name="error">Flag indicating if the message displayed is an error message.</param>
    /// <param name="title">Balloon notification title.</param>
    /// <param name="text">Balloon notification text.</param>
    /// <param name="delay">Time during which the balloon is displayed to users, defaulted to 1.5 seconds.</param>
    private void ShowTooltip(bool error, string title, string text, int delay = 1500)
    {
      _notifyIcon.BalloonTipIcon = error ? ToolTipIcon.Error : ToolTipIcon.Info;
      _notifyIcon.BalloonTipTitle = title;
      _notifyIcon.BalloonTipText = text;
      _notifyIcon.ShowBalloonTip(delay);
    }

    private FileSystemWatcher StartWatcherForFile(string filePath, FileSystemEventHandler method)
    {
      FileSystemWatcher watcher = new FileSystemWatcher
      {
        Path = Path.GetDirectoryName(filePath),
        Filter = Path.GetFileName(filePath),
        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName,
        EnableRaisingEvents = true,
        IncludeSubdirectories = true
      };

      watcher.Changed += method;
      watcher.Deleted += method;
      return watcher;
    }

    /// <summary>
    /// Starts the global timer that fires connection status checks.
    /// </summary>
    public void StartGlobalTimer()
    {
      if (_globalTimer == null)
      {
        _globalTimer = new System.Timers.Timer {AutoReset = true};
        _globalTimer.Elapsed += UpdateMachinesAndInstancesConnectionTimeouts;
        _globalTimer.Interval = 1000;
      }

      if (_machinesList.Machines.Count(machine => !machine.IsLocal) + _mySQLInstancesList.Count(instance => instance.MonitorAndNotifyStatus) == 0)
      {
        _globalTimer.Stop();
      }
      else if (!_globalTimer.Enabled)
      {
        _globalTimer.Start();
      }
    }

    /// <summary>
    /// Delegate method that reports the asynchronous operation to refresh the services and instances statuses has completed.
    /// </summary>
    /// <param name="sender">Sender object.</param>
    /// <param name="e">Event arguments.</param>
    private void StatusRefreshWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
    {
      MySqlSourceTrace.WriteToLog(
        e.Cancelled ? "Notifier status refresh was cancelled." : "Notifier status refresh completed successfully.",
        SourceLevels.Information);
      _refreshStatusMenuItem.Text = Resources.RefreshStatusMenuText;
      _refreshStatusMenuItem.Image = Resources.RefreshStatus;
      StatusRefreshInProgress = false;
    }

    /// <summary>
    /// Delegate method that asynchronously refreshes the services and instances statuses.
    /// </summary>
    /// <param name="sender">Sender object.</param>
    /// <param name="e">Event arguments.</param>
    private void StatusRefreshWorkerDoWork(object sender, DoWorkEventArgs e)
    {
      BackgroundWorker worker = sender is BackgroundWorker ? sender as BackgroundWorker : null;

      if (worker != null && worker.CancellationPending)
      {
        e.Cancel = true;
        return;
      }

      // First refresh local machine.
      bool cancelled = _machinesList.LocalMachine.RefreshStatus(ref worker);
      if (cancelled)
      {
        e.Cancel = true;
        return;
      }

      // Refresh remote machines.
      foreach (var remoteMachine in _machinesList.Machines)
      {
        if (worker != null && worker.CancellationPending)
        {
          e.Cancel = true;
          return;
        }

        if (remoteMachine.IsLocal)
        {
          continue;
        }

        cancelled = remoteMachine.RefreshStatus(ref worker);
        if (cancelled)
        {
          e.Cancel = true;
          return;
        }
      }

      // Refresh MySQL Instances
      foreach (var instance in _mySQLInstancesList)
      {
        cancelled = instance.RefreshStatus(ref worker);
        if (cancelled)
        {
          e.Cancel = true;
          return;
        }
      }

      // Refresh Notifier's icon.
      if (Settings.Default.NotifyOfStatusChange)
      {
        RefreshNotifierIcon();
      }
    }

    /// <summary>
    /// Stops background connection activities like connection tests, etc.
    /// </summary>
    private void StopBackgroundActions()
    {
      // Stop global status refresh.
      if (StatusRefreshInProgress)
      {
        CancelAsynchronousStatusRefresh();
      }

      // Stop the global timer that fires connection tests for offline machines and MySQL instances.
      if (_globalTimer != null && _globalTimer.Enabled)
      {
        _globalTimer.Stop();
      }

      // Cancel any background machine connection tests.
      if (_machinesList.Machines != null)
      {
        foreach (Machine machine in _machinesList.Machines)
        {
          machine.CancelAsynchronousConnectionTest();
        }
      }

      // Stop MySQL Instances connection checks.
      foreach (MySqlInstance instance in _mySQLInstancesList)
      {
        instance.CancelAsynchronousStatusCheck();
      }

      // Stop the connections file watcher while users maintain services and instances.
      if (_connectionsFileWatcher != null)
      {
        _connectionsFileWatcher.EnableRaisingEvents = false;
      }
    }

    /// <summary>
    /// Event delegate method fired when the instance monitoring timer's interval elapses.
    /// </summary>
    /// <param name="sender">Sender object.</param>
    /// <param name="e">Event arguments.</param>
    private void UpdateMachinesAndInstancesConnectionTimeouts(object sender, System.Timers.ElapsedEventArgs e)
    {
      _machinesList.UpdateMachinesConnectionTimeouts();
      _mySQLInstancesList.UpdateInstancesConnectionTimeouts();
    }

    private void UpdateStaticMenuItems()
    {
      bool hasUpdates = (Settings.Default.UpdateCheck & (int)SoftwareUpdateStaus.HasUpdates) != 0;
      _hasUpdatesSeparator.Visible = hasUpdates;
      _installAvailablelUpdatesMenuItem.Visible = hasUpdates;
      _ignoreAvailableUpdateMenuItem.Visible = hasUpdates;
      _launchInstallerMenuItem.Enabled = MySqlInstaller.IsInstalled;
      _launchWorkbenchUtilitiesMenuItem.Visible = MySqlWorkbench.IsMySQLUtilitiesInstalled();
      _refreshStatusSeparator.Visible = CurrentServicesAndInstancesCount > 0;
      _refreshStatusMenuItem.Visible = CurrentServicesAndInstancesCount > 0;
      _actionsMenuItem.Visible = CurrentServicesAndInstancesCount > 0;
    }
  }

  public enum ServiceListChangeType
  {
    Add,
    AutoAdd,
    Remove
  }

  public enum SoftwareUpdateStaus : int
  {
    Checking = 1,
    HasUpdates = 2,
    Notified = 4
  }
}