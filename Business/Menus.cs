﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security;
using System.Windows.Forms;
using SystemTrayMenu.DataClasses;
using SystemTrayMenu.Handler;
using SystemTrayMenu.Helper;
using SystemTrayMenu.Utilities;
using Menu = SystemTrayMenu.UserInterface.Menu;
using Timer = System.Windows.Forms.Timer;

namespace SystemTrayMenu.Business
{
    internal class Menus : IDisposable
    {
        internal event EventHandler<bool> LoadStarted;
        internal event EventHandlerEmpty LoadStopped;
        private enum OpenCloseState { Default, Opening, Closing };
        private OpenCloseState openCloseState = OpenCloseState.Default;
        private readonly Menu[] menus = new Menu[MenuDefines.MenusMax];
        private readonly BackgroundWorker worker = new BackgroundWorker();

        private readonly KeyboardInput keyboardInput;
        private readonly Timer timerStillActiveCheck = new Timer();
        private readonly WaitLeave waitLeave = new WaitLeave(MenuDefines.TimeUntilClose);
        private DateTime deactivatedTime = DateTime.MinValue;

        private IEnumerable<Menu> AsEnumerable => menus.Where(m => m != null && !m.IsDisposed);
        private List<Menu> AsList => AsEnumerable.ToList();

        public Menus()
        {
            worker.WorkerSupportsCancellation = true;
            worker.DoWork += Load;
            void Load(object sender, DoWorkEventArgs e)
            {
                e.Result = GetData((BackgroundWorker)sender, Config.Path, 0);
            }

            worker.RunWorkerCompleted += LoadCompleted;
            void LoadCompleted(object sender, RunWorkerCompletedEventArgs e)
            {
                keyboardInput.ResetSelectedByKey();
                LoadStopped();
                MenuData menuData = (MenuData)e.Result;
                if (menuData.Validity == MenuDataValidity.Valid)
                {
                    DisposeMenu(menus[0]);
                    menus[0] = Create(menuData, Path.GetFileName(Config.Path));
                    AsEnumerable.ToList().ForEach(m => { m.ShowWithFade(); });
                }
            }

            keyboardInput = new KeyboardInput(menus);
            keyboardInput.RegisterHotKey();
            keyboardInput.HotKeyPressed += KeyboardInput_HotKeyPressed;
            void KeyboardInput_HotKeyPressed()
            {
                SwitchOpenClose(false);
            }

            keyboardInput.ClosePressed += MenusFadeOut;
            keyboardInput.RowDeselected += CheckMenuOpenerStop;
            keyboardInput.RowSelected += KeyboardInputRowSelected;
            void KeyboardInputRowSelected(DataGridView dgv, int rowIndex)
            {
                FadeInIfNeeded();
                CheckMenuOpenerStart(dgv, rowIndex);
            }

            waitLeave.LeaveTriggered += LeaveTriggered;
            void LeaveTriggered()
            {
                FadeHalfOrOutIfNeeded();
            }
        }

        internal void SwitchOpenClose(bool byClick)
        {
            if (byClick && (DateTime.Now - deactivatedTime).TotalMilliseconds < 200)
            {
                //By click on notifyicon the menu gets deactivated and closed
            }
            else if (string.IsNullOrEmpty(Config.Path))
            {
                //Case when Folder Dialog open
            }
            else if (openCloseState == OpenCloseState.Opening ||
                menus[0].Visible && openCloseState == OpenCloseState.Default)
            {
                openCloseState = OpenCloseState.Closing;
                MenusFadeOut();
                StopWorker();
                if (!AsEnumerable.Any(m => m.Visible))
                {
                    openCloseState = OpenCloseState.Default;
                }
            }
            else
            {
                openCloseState = OpenCloseState.Opening;
                LoadStarted(this, true);
                StartWorker();
            }
            deactivatedTime = DateTime.MinValue;
        }

        public void Dispose()
        {
            worker.Dispose();
            keyboardInput.Dispose();
            timerStillActiveCheck.Dispose();
            waitLeave.Dispose();
            IconReader.Dispose();
            DisposeMenu(menus[0]);
        }

        internal void DisposeMenu(Menu menuToDispose)
        {
            if (menuToDispose != null)
            {
                DataGridView dgv = menuToDispose.GetDataGridView();
                foreach (DataGridViewRow row in dgv.Rows)
                {
                    RowData rowData = (RowData)row.Cells[2].Value;
                    rowData.Dispose();
                    DisposeMenu(rowData.SubMenu);
                }
                dgv.ClearSelection();
                menuToDispose.Dispose();
            }
        }

        internal static MenuData GetData(BackgroundWorker worker, string path, int level)
        {
            MenuData menuData = new MenuData
            {
                RowDatas = new List<RowData>(),
                Validity = MenuDataValidity.Invalid,
                Level = level
            };
            if (!worker.CancellationPending)
            {
                string[] directories = Array.Empty<string>();

                try
                {
                    directories = Directory.GetDirectories(path);
                    Array.Sort(directories, new WindowsExplorerSort());
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log.Warn($"path:'{path}'", ex);
                    menuData.Validity = MenuDataValidity.NoAccess;
                }
                catch (IOException ex)
                {
                    Log.Warn($"path:'{path}'", ex);
                }

                foreach (string directory in directories)
                {
                    if (worker != null && worker.CancellationPending)
                    {
                        break;
                    }

                    bool hiddenEntry = false;
                    if (FolderOptions.IsHidden(directory, ref hiddenEntry))
                    {
                        continue;
                    }

                    RowData rowData = ReadRowData(directory, false);
                    rowData.ContainsMenu = true;
                    rowData.HiddenEntry = hiddenEntry;
                    string resolvedLnkPath = string.Empty;
                    rowData.ReadIcon(true, ref resolvedLnkPath);
                    menuData.RowDatas.Add(rowData);
                }
            }

            if (!worker.CancellationPending)
            {
                string[] files = Array.Empty<string>();

                try
                {
                    files = Directory.GetFiles(path);
                    Array.Sort(files, new WindowsExplorerSort());
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log.Warn($"path:'{path}'", ex);
                    menuData.Validity = MenuDataValidity.NoAccess;
                }
                catch (IOException ex)
                {
                    Log.Warn($"path:'{path}'", ex);
                }

                foreach (string file in files)
                {
                    if (worker != null && worker.CancellationPending)
                    {
                        break;
                    }

                    bool hiddenEntry = false;
                    if (FolderOptions.IsHidden(file, ref hiddenEntry))
                    {
                        continue;
                    }

                    RowData rowData = ReadRowData(file, false);
                    string resolvedLnkPath = string.Empty;
                    if (rowData.ReadIcon(false, ref resolvedLnkPath))
                    {
                        rowData = ReadRowData(resolvedLnkPath, true, rowData);
                        rowData.ContainsMenu = true;
                        rowData.HiddenEntry = hiddenEntry;
                    }

                    menuData.RowDatas.Add(rowData);
                }
            }

            if (!worker.CancellationPending)
            {
                if (menuData.Validity == MenuDataValidity.Invalid)
                {
                    menuData.Validity = MenuDataValidity.Valid;
                }
            }

            return menuData;
        }

        internal void MainPreload()
        {
            menus[0] = Create(GetData(worker, Config.Path, 0),
                Path.GetFileName(Config.Path));
            menus[0].AdjustSizeAndLocation();
            DisposeMenu(menus[0]);
        }

        internal void StartWorker()
        {
            if (worker.IsBusy)
            {
                LoadStopped();
            }
            else
            {
                worker.RunWorkerAsync();
            }
        }

        internal void StopWorker()
        {
            if (worker.IsBusy)
            {
                worker.CancelAsync();
            }
        }

        private static RowData ReadRowData(string fileName,
            bool isResolvedLnk, RowData rowData = null)
        {
            if (rowData == null)
            {
                rowData = new RowData();
            }
            rowData.IsResolvedLnk = isResolvedLnk;

            try
            {
                rowData.FileInfo = new FileInfo(fileName);
                rowData.TargetFilePath = rowData.FileInfo.FullName;
                if (!isResolvedLnk)
                {
                    rowData.SetText(rowData.FileInfo.Name);
                    rowData.TargetFilePathOrig = rowData.FileInfo.FullName;
                }
            }
            catch (Exception ex)
            {
                if (ex is SecurityException ||
                    ex is ArgumentException ||
                    ex is UnauthorizedAccessException ||
                    ex is PathTooLongException ||
                    ex is NotSupportedException)
                {
                    Log.Warn($"fileName:'{fileName}'", ex);
                }
                else
                {
                    throw;
                }
            }

            return rowData;
        }


        private Menu Create(MenuData menuData, string title = null)
        {
            Menu menu = new Menu();

            if (title != null)
            {
                if (string.IsNullOrEmpty(title))
                {
                    title = Path.GetPathRoot(Config.Path);
                }

                menu.SetTitle(title);
                menu.UserClickedOpenFolder += OpenFolder;
                void OpenFolder()
                {
                    Log.ProcessStart("explorer.exe", Config.Path);
                }
            }

            menu.Level = menuData.Level;
            menu.MouseWheel += AdjustMenusSizeAndLocation;
            menu.MouseLeave += waitLeave.Start;
            menu.MouseEnter += waitLeave.Stop;
            menu.KeyPress += keyboardInput.KeyPress;
            menu.CmdKeyProcessed += keyboardInput.CmdKeyProcessed;
            menu.SearchTextChanging += keyboardInput.SearchTextChanging;
            menu.SearchTextChanged += Menu_SearchTextChanged;
            void Menu_SearchTextChanged(object sender, EventArgs e)
            {
                keyboardInput.SearchTextChanged(sender, e);
                AdjustMenusSizeAndLocation();
            }
            menu.Deactivate += Deactivate;
            void Deactivate(object sender, EventArgs e)
            {
                FadeHalfOrOutIfNeeded();
                if (!IsActive())
                {
                    deactivatedTime = DateTime.Now;
                }
            }

            menu.Activated += Activated;
            void Activated(object sender, EventArgs e)
            {
                if (IsActive() &&
                    menus[0].IsUsable)
                {
                    menus[0].SetTitleColorActive();
                    AsList.ForEach(m => m.ShowWithFade());
                }

                CheckIfWindowsStartStoleFocusNoDeactivateInRareCase();
                void CheckIfWindowsStartStoleFocusNoDeactivateInRareCase()
                {
                    timerStillActiveCheck.Interval = 1000;
                    timerStillActiveCheck.Tick += StillActiveTick;
                    void StillActiveTick(object senderTimer, EventArgs eTimer)
                    {
                        if (!waitLeave.IsRunning)
                        {
                            FadeHalfOrOutIfNeeded();
                            if (!IsActive())
                            {
                                timerStillActiveCheck.Stop();
                            }
                        }
                    }
                    timerStillActiveCheck.Start();
                }
            }

            menu.VisibleChanged += MenuVisibleChanged;
            AddItemsToMenu(menuData.RowDatas, menu);
            DataGridView dgv = menu.GetDataGridView();
            dgv.CellMouseEnter += Dgv_CellMouseEnter;
            dgv.CellMouseLeave += Dgv_CellMouseLeave;
            dgv.MouseDown += Dgv_MouseDown;
            dgv.MouseDoubleClick += Dgv_MouseDoubleClick;
            dgv.SelectionChanged += Dgv_SelectionChanged;

            return menu;
        }

        private void MenuVisibleChanged(object sender, EventArgs e)
        {
            Menu menu = (Menu)sender;
            if (menu.IsUsable)
            {
                AdjustMenusSizeAndLocation();
                if (menu.Level == 0)
                {
                    menus[0].AdjustSizeAndLocation();
                }
            }
            if (!menu.Visible)
            {
                DisposeMenu(menu);
            }
            if (!AsEnumerable.Any(m => m.Visible))
            {
                openCloseState = OpenCloseState.Default;
            }
        }

        private void AddItemsToMenu(List<RowData> data, Menu menu)
        {
            DataGridView dgv = menu.GetDataGridView();
            DataTable dataTable = new DataTable();
            dataTable.Columns.Add(dgv.Columns[0].Name, typeof(Icon));
            dataTable.Columns.Add(dgv.Columns[1].Name, typeof(string));
            dataTable.Columns.Add("data", typeof(RowData));
            foreach (RowData rowData in data)
            {
                CreateMenuRow(rowData, menu, dataTable);
            }
            dgv.DataSource = dataTable;
        }

        private void Dgv_CellMouseEnter(object sender, DataGridViewCellEventArgs e)
        {
            if (menus[0].IsUsable)
            {
                if (keyboardInput.InUse)
                {
                    CheckMenuOpenerStop(keyboardInput.iMenuKey,
                        keyboardInput.iRowKey);
                    keyboardInput.ClearIsSelectedByKey();
                    keyboardInput.InUse = false;
                }

                DataGridView dgv = (DataGridView)sender;
                keyboardInput.Select(dgv, e.RowIndex);
                CheckMenuOpenerStart(dgv, e.RowIndex);
            }
        }

        private void CheckMenuOpenerStart(DataGridView dgv, int rowIndex)
        {
            if (rowIndex > -1 &&
                dgv.Rows.Count > rowIndex)
            {
                RowData trigger = (RowData)dgv.Rows[rowIndex].Cells[2].Value;
                trigger.IsSelected = true;
                dgv.Rows[rowIndex].Selected = true;
                Menu menuFromTrigger = (Menu)dgv.FindForm();
                Menu menuTriggered = trigger.SubMenu;
                int level = menuFromTrigger.Level + 1;

                if (trigger.ContainsMenu &&
                    level < MenuDefines.MenusMax &&
                    menus[0].IsUsable &&
                    (menus[level] == null ||
                    menus[level] != menuTriggered))
                {
                    trigger.StopLoadMenuAndStartWaitToOpenIt();
                    trigger.StartMenuOpener();

                    if (trigger.Reading.IsBusy)
                    {
                        trigger.RestartLoading = true;
                    }
                    else
                    {
                        LoadStarted(this, false);
                        trigger.Reading.RunWorkerAsync(level);
                    }
                }
            }
        }

        private void CheckMenuOpenerStop(int menuIndex, int rowIndex, DataGridView dgv = null)
        {
            Menu menu = menus[menuIndex];
            if (menu != null &&
                rowIndex > -1)
            {
                if (dgv == null)
                {
                    dgv = menu.GetDataGridView();
                }
                if (dgv.Rows.Count > rowIndex)
                {
                    RowData trigger = (RowData)dgv.Rows[rowIndex].Cells[2].Value;
                    if (trigger.Reading.IsBusy)
                    {
                        if (!trigger.IsContextMenuOpen)
                        {
                            trigger.IsSelected = false;
                            dgv.Rows[rowIndex].Selected = false;
                        }
                        trigger.Reading.CancelAsync();
                    }
                    else if (trigger.ContainsMenu && !trigger.IsLoading)
                    {
                        trigger.IsSelected = true;
                        dgv.Rows[rowIndex].Selected = true;
                    }
                    else
                    {
                        if (!trigger.IsContextMenuOpen)
                        {
                            trigger.IsSelected = false;
                            dgv.Rows[rowIndex].Selected = false;
                        }
                    }
                    if (trigger.IsLoading)
                    {
                        trigger.StopLoadMenuAndStartWaitToOpenIt();
                        trigger.IsLoading = false;
                    }
                }
            }
        }

        private void Dgv_CellMouseLeave(object sender, DataGridViewCellEventArgs e)
        {
            if (!keyboardInput.InUse)
            {
                DataGridView dgv = (DataGridView)sender;
                Menu menu = (Menu)dgv.FindForm();
                CheckMenuOpenerStop(menu.Level, e.RowIndex, dgv);
            }
        }

        private void Dgv_MouseDown(object sender, MouseEventArgs e)
        {
            DataGridView dgv = (DataGridView)sender;
            DataGridView.HitTestInfo hitTestInfo;
            hitTestInfo = dgv.HitTest(e.X, e.Y);
            if (hitTestInfo.RowIndex > -1 &&
                dgv.Rows.Count > hitTestInfo.RowIndex)
            {
                RowData trigger = (RowData)dgv.Rows[hitTestInfo.RowIndex].Cells[2].Value;
                trigger.MouseDown(dgv, e);
            }
        }

        private void Dgv_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            DataGridView dgv = (DataGridView)sender;
            DataGridView.HitTestInfo hitTestInfo;
            hitTestInfo = dgv.HitTest(e.X, e.Y);
            if (hitTestInfo.RowIndex > -1 &&
                dgv.Rows.Count > hitTestInfo.RowIndex)
            {
                RowData trigger = (RowData)dgv.Rows[hitTestInfo.RowIndex].Cells[2].Value;
                trigger.DoubleClick(e);
            }
        }

        private void Dgv_SelectionChanged(object sender, EventArgs e)
        {
            DataGridView dgv = (DataGridView)sender;
            foreach (DataGridViewRow row in dgv.Rows)
            {
                RowData rowData = (RowData)row.Cells[2].Value;

                if (rowData == null)
                {
#warning evalute the case again, should we prevent it somewhere else?
                }
                else if (!menus[0].IsUsable)
                {
                    row.DefaultCellStyle.SelectionBackColor = Color.White;
                }
                else if (rowData.IsSelectedByKeyboard)
                {
                    row.DefaultCellStyle.SelectionBackColor =
                        MenuDefines.ColorSelectedItem;
                    row.Selected = true;
                }
                else if (rowData.IsSelected)
                {
                    row.DefaultCellStyle.SelectionBackColor =
                        MenuDefines.ColorOpenFolder;
                    row.Selected = true;
                }
                else
                {
                    rowData.IsSelected = false;
                    row.Selected = false;
                }
            }
        }

        private void CreateMenuRow(RowData rowData, Menu menu, DataTable dataTable)
        {
            rowData.SetData(rowData, dataTable);
            rowData.OpenMenu += OpenSubMenu;
            rowData.Reading.WorkerSupportsCancellation = true;
            rowData.Reading.DoWork += ReadMenu_DoWork;
            void ReadMenu_DoWork(object senderDoWork,
                DoWorkEventArgs eDoWork)
            {
                int level = (int)eDoWork.Argument;
                BackgroundWorker worker = (BackgroundWorker)senderDoWork;
                eDoWork.Result = Business.Menus.GetData(worker, rowData.TargetFilePath, level);
            }

            rowData.Reading.RunWorkerCompleted += ReadMenu_RunWorkerCompleted;
            void ReadMenu_RunWorkerCompleted(object senderCompleted,
                RunWorkerCompletedEventArgs e)
            {
                MenuData menuData = (MenuData)e.Result;
                if (rowData.RestartLoading)
                {
                    rowData.RestartLoading = false;
                    rowData.Reading.RunWorkerAsync(menuData.Level);
                }
                else
                {
                    LoadStopped();
                    if (menuData.Validity != MenuDataValidity.Invalid)
                    {
                        menu = Create(menuData);
                        if (menuData.RowDatas.Count > 0)
                        {
                            menu.SetTypeSub();
                        }
                        else if (menuData.Validity == MenuDataValidity.NoAccess)
                        {
                            menu.SetTypeNoAccess();
                        }
                        else
                        {
                            menu.SetTypeEmpty();
                        }
                        menu.Tag = rowData;
                        rowData.SubMenu = menu;
                        rowData.MenuLoaded();
                    }
                }
            }
        }

        private void OpenSubMenu(object sender, RowData trigger)
        {
            Menu menuTriggered = trigger.SubMenu;
            Menu menuFromTrigger = menus[menuTriggered.Level - 1];

            for (int level = menuTriggered.Level;
                level < MenuDefines.MenusMax; level++)
            {
                if (menus[level] != null)
                {
                    Menu menuToClose = menus[level];
                    RowData oldTrigger = (RowData)menuToClose.Tag;
                    DataGridView dgv = menuFromTrigger.GetDataGridView();
                    foreach (DataGridViewRow row in dgv.Rows)
                    {
                        RowData rowData = (RowData)row.Cells[2].Value;
                        rowData.IsSelected = false;
                    }
                    trigger.IsSelected = true;
                    dgv.ClearSelection();
                    dgv.Rows[trigger.RowIndex].Selected = true;
                    menuToClose.HideWithFade();
                    menuToClose.VisibleChanged += MenuVisibleChanged;
                    menus[level] = null;
                }
            }

            DisposeMenu(menus[menuTriggered.Level]);
            menus[menuTriggered.Level] = menuTriggered;
            AdjustMenusSizeAndLocation();
            menus[menuTriggered.Level].ShowWithFadeOrTransparent(IsActive());
        }

        private void FadeInIfNeeded()
        {
            if (menus[0].IsUsable)
            {
                bool active = IsActive();
                AsList.ForEach(menu => menu.ShowWithFadeOrTransparent(active));
            }
        }

        internal void FadeHalfOrOutIfNeeded()
        {
            if (menus[0].IsUsable)
            {
                if (!(IsActive()))
                {
                    Point position = Control.MousePosition;
                    if (AsList.Any(m => m.IsMouseOn(position)))
                    {
                        if (!keyboardInput.InUse)
                        {
                            AsList.ForEach(menu => menu.ShowTransparent());
                        }
                    }
                    else
                    {
                        MenusFadeOut();
                    }
                }
            }
        }

        private bool IsActive()
        {
            return Form.ActiveForm is Menu;
        }

        private void MenusFadeOut()
        {
            openCloseState = OpenCloseState.Closing;
            AsList.ForEach(menu =>
            {
                if (menu.Level > 0)
                {
                    menus[menu.Level] = null;
                }
                menu.HideWithFade();
            });
        }

        private void AdjustMenusSizeAndLocation()
        {
            Menu menuPredecessor = menus[0];
            int widthPredecessors = -1; // -1 padding
            bool directionToRight = false;

            menus[0].AdjustSizeAndLocation();

            foreach (Menu menu in AsEnumerable.Where(m => m.Level > 0))
            {
                int newWith = (menu.Width -
                    menu.Padding.Horizontal + menuPredecessor.Width);
                if (directionToRight)
                {
                    if (widthPredecessors - menu.Width <=
                        -menu.Padding.Horizontal)
                    {
                        directionToRight = false;
                    }
                    else
                    {
                        widthPredecessors -= newWith;
                    }
                }
                else if (Statics.ScreenWidth <
                    widthPredecessors + menuPredecessor.Width + menu.Width)
                {
                    directionToRight = true;
                    widthPredecessors -= newWith;
                }

                menu.AdjustSizeAndLocation(menuPredecessor, directionToRight);
                widthPredecessors += menu.Width - menu.Padding.Left;
                menuPredecessor = menu;
            }
        }
    }
}
