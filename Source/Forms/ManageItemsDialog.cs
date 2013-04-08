﻿//
// Copyright (c) 2013, Oracle and/or its affiliates. All rights reserved.
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
//

using System;
using System.Windows.Forms;
using MySql.Notifier.Properties;
using System.Drawing;

namespace MySql.Notifier
{
  public partial class ManageItemsDialog : BaseForm
  {
    private MySQLServicesList serviceList;
    public static string addServiceName;
    private MySQLService selectedService;

    public ManageItemsDialog(MySQLServicesList serviceList)
    {
      this.serviceList = serviceList;
      InitializeComponent();
      RefreshList();
    }

    private void RefreshList()
    {
      lstMonitoredServices.Items.Clear();
      foreach (MySQLService service in serviceList.Services)
      {
        ListViewItem itemList = new ListViewItem(service.DisplayName, 0);
        itemList.Tag = service;
        itemList.SubItems.Add(service.WinServiceType.ToString());
        itemList.SubItems.Add(service.Status.ToString());
        lstMonitoredServices.Items.Add(itemList);
      }
      if (lstMonitoredServices.Items.Count > 0)
        lstMonitoredServices.Items[0].Selected = true;
      else
      {
        btnDelete.Enabled = false;
        chkUpdateTrayIcon.Enabled = false;
        notifyOnStatusChange.Enabled = false;
      }
      Cursor.Current = Cursors.Arrow;
    }

    private void btnAdd_Click(object sender, EventArgs e)
    {
      Point screenPoint = btnAdd.PointToScreen(new Point(btnAdd.Left, btnAdd.Bottom));
      if (screenPoint.Y + AddButtonContextMenuStrip.Size.Height > Screen.PrimaryScreen.WorkingArea.Height)
      {
        AddButtonContextMenuStrip.Show(btnAdd, new Point(0, -AddButtonContextMenuStrip.Size.Height));
      }
      else
      {
        AddButtonContextMenuStrip.Show(btnAdd, new Point(0, btnAdd.Height));
      }
    }

    /// <summary>
    /// Deletes selected item from monitored items list
    /// </summary>
    /// <param name="sender">Sender Object</param>
    /// <param name="e">Event Arguments</param>
    private void btnDelete_Click(object sender, EventArgs e)
    {
      if (selectedService == null) return;

      serviceList.RemoveService(selectedService.ServiceName);
      RefreshList();
    }

    private void lstMonitoredServices_SelectedIndexChanged(object sender, EventArgs e)
    {
      btnDelete.Enabled = lstMonitoredServices.SelectedItems.Count > 0;
      notifyOnStatusChange.Enabled = btnDelete.Enabled;
      chkUpdateTrayIcon.Enabled = btnDelete.Enabled;

      selectedService = lstMonitoredServices.SelectedItems.Count > 0 ? lstMonitoredServices.SelectedItems[0].Tag as MySQLService : null;
      notifyOnStatusChange.Checked = selectedService != null && selectedService.NotifyOnStatusChange;
      chkUpdateTrayIcon.Checked = selectedService != null && selectedService.UpdateTrayIconOnStatusChange;
    }

    private void notifyOnStatusChange_CheckedChanged(object sender, EventArgs e)
    {
      if (selectedService == null) return;
      selectedService.NotifyOnStatusChange = notifyOnStatusChange.Checked;
    }

    private void chkUpdateTrayIcon_CheckedChanged(object sender, EventArgs e)
    {
      if (selectedService == null) return;
      selectedService.UpdateTrayIconOnStatusChange = chkUpdateTrayIcon.Checked;
    }

    private void btnClose_Click(object sender, EventArgs e)
    {
      Settings.Default.Save();
    }

    private void serviceToolStripMenuItem_Click(object sender, EventArgs e)
    {
      Cursor.Current = Cursors.WaitCursor;
      AddServiceDialog dialog = new AddServiceDialog();
      if (dialog.ShowDialog() == DialogResult.Cancel) return;

      foreach (MySQLService service in dialog.ServicesToAdd)
      {
        if (serviceList.Contains(service))
        {
          using (var errorDialog = new MessageDialog("Warning", "Selected Service is already in the Monitor List", false))
          {
            errorDialog.ShowDialog(this);
          }
        }
        else
        {
          serviceList.AddService(service);
        }
      }

      RefreshList();
    }

    private void mySQLInstanceToolStripMenuItem_Click(object sender, EventArgs e)
    {
      using (var monitorInstancesDialog = new MonitorMySQLServerInstancesDialog())
      {
        monitorInstancesDialog.ShowDialog();
      }
    }
  }
}