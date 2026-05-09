using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

public class LaunchItem
{
    public string type { get; set; }
    public string name { get; set; }
    public string target { get; set; }
    public string args { get; set; }
    public bool enabled { get; set; }
    public string iconPath { get; set; }
}

public class WorkspaceProfile
{
    public string name { get; set; }
    public List<LaunchItem> items { get; set; }
}

public class LauncherSettings
{
    public bool closeAfterLaunch { get; set; }
    public bool minimizeAfterLaunch { get; set; }
    public bool startWithWindows { get; set; }
    public string language { get; set; }
}

public class LauncherConfig
{
    public string currentProfile { get; set; }
    public List<WorkspaceProfile> profiles { get; set; }
    public LauncherSettings settings { get; set; }
    public List<LaunchItem> items { get; set; }
    public object urls { get; set; }
    public object apps { get; set; }
}

public class TimeoutWebClient : WebClient
{
    protected override WebRequest GetWebRequest(Uri address)
    {
        WebRequest request = base.GetWebRequest(address);
        if (request != null)
        {
            request.Timeout = 5000;
            if (request is HttpWebRequest)
            {
                ((HttpWebRequest)request).ReadWriteTimeout = 5000;
                ((HttpWebRequest)request).AllowAutoRedirect = true;
            }
        }
        return request;
    }
}

public class ComboDropDownRightClickRouter : NativeWindow
{
    private const int WM_RBUTTONUP = 0x0205;
    private const int LB_ITEMFROMPOINT = 0x01A9;
    private readonly ComboBox combo;
    private readonly ContextMenuStrip menu;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMBOBOXINFO
    {
        public int cbSize;
        public RECT rcItem;
        public RECT rcButton;
        public int stateButton;
        public IntPtr hwndCombo;
        public IntPtr hwndItem;
        public IntPtr hwndList;
    }

    [DllImport("user32.dll")]
    private static extern bool GetComboBoxInfo(IntPtr hwndCombo, ref COMBOBOXINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public ComboDropDownRightClickRouter(ComboBox combo, ContextMenuStrip menu)
    {
        this.combo = combo;
        this.menu = menu;
        combo.DropDown += delegate { AttachList(); };
        combo.HandleDestroyed += delegate { ReleaseHandle(); };
    }

    private void AttachList()
    {
        COMBOBOXINFO info = new COMBOBOXINFO();
        info.cbSize = Marshal.SizeOf(typeof(COMBOBOXINFO));
        if (GetComboBoxInfo(combo.Handle, ref info) && info.hwndList != IntPtr.Zero && info.hwndList != Handle)
        {
            ReleaseHandle();
            AssignHandle(info.hwndList);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_RBUTTONUP)
        {
            int result = SendMessage(Handle, LB_ITEMFROMPOINT, IntPtr.Zero, m.LParam).ToInt32();
            int index = result & 0xFFFF;
            bool outside = (result >> 16) != 0;
            if (!outside && index >= 0 && index < combo.Items.Count)
            {
                combo.SelectedIndex = index;
            }
            combo.DroppedDown = false;
            menu.Show(combo, combo.PointToClient(Control.MousePosition));
            return;
        }
        base.WndProc(ref m);
    }
}

public static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
    private const int SW_RESTORE = 9;

    [STAThread]
    public static void Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch { }
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length > 0 && args[0] == "--render-check")
        {
            using (LauncherForm form = new LauncherForm())
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            {
                form.Show();
                Application.DoEvents();
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-self-check.png"), System.Drawing.Imaging.ImageFormat.Png);
                form.Close();
            }
            return;
        }

        bool createdNew;
        using (Mutex mutex = new Mutex(true, "WorkspaceLauncher.SingleInstance." + HashForMutex(AppDomain.CurrentDomain.BaseDirectory), out createdNew))
        {
            if (!createdNew)
            {
                BringExistingInstanceToFront();
                return;
            }

            Application.Run(new LauncherForm());
        }
    }

    private static void BringExistingInstanceToFront()
    {
        try
        {
            Process current = Process.GetCurrentProcess();
            string currentPath = "";
            try { currentPath = current.MainModule.FileName; } catch { }

            foreach (Process process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id == current.Id) continue;
                bool sameExe = false;
                try { sameExe = String.Equals(process.MainModule.FileName, currentPath, StringComparison.OrdinalIgnoreCase); } catch { }
                if (!sameExe) continue;

                IntPtr hwnd = process.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                return;
            }
        }
        catch { }
    }

    private static string HashForMutex(string value)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}

public class LauncherForm : Form
{
    private const int IconColumnX = 12;
    private const int NameColumnX = 48;
    private const int TypeColumnX = 322;
    private const int TargetColumnX = 424;

    private readonly string appDir = AppDomain.CurrentDomain.BaseDirectory;
    private readonly string configPath;
    private readonly string iconDir;
    private readonly ListBox itemList = new ListBox();
    private readonly ComboBox profileBox = new ComboBox();
    private readonly Button addButton = new Button();
    private readonly Button importButton = new Button();
    private readonly Button exportButton = new Button();
    private readonly Button clearButton = new Button();
    private readonly Button settingsButton = new Button();
    private readonly Button saveButton = new Button();
    private readonly Button launchButton = new Button();
    private readonly Label titleLabel = new Label();
    private readonly Label subtitleLabel = new Label();
    private readonly Label statusLabel = new Label();
    private readonly Label profileLabel = new Label();
    private readonly Label emptyLabel = new Label();
    private readonly Label nameHeaderLabel = new Label();
    private readonly Label typeHeaderLabel = new Label();
    private readonly Label targetHeaderLabel = new Label();
    private readonly ToolTip toolTip = new ToolTip();
    private readonly ContextMenuStrip menu = new ContextMenuStrip();
    private readonly ToolStripMenuItem editMenu = new ToolStripMenuItem();
    private readonly ToolStripMenuItem toggleMenu = new ToolStripMenuItem();
    private readonly ToolStripMenuItem moveUpMenu = new ToolStripMenuItem();
    private readonly ToolStripMenuItem moveDownMenu = new ToolStripMenuItem();
    private readonly ToolStripMenuItem detectMenu = new ToolStripMenuItem();
    private readonly ToolStripMenuItem copyTargetMenu = new ToolStripMenuItem();
    private readonly ToolStripMenuItem openFolderMenu = new ToolStripMenuItem();
    private readonly ToolStripMenuItem deleteMenu = new ToolStripMenuItem();
    private readonly ContextMenuStrip profileMenu = new ContextMenuStrip();
    private readonly ToolStripMenuItem newProfileMenu = new ToolStripMenuItem();
    private readonly ToolStripMenuItem renameProfileMenu = new ToolStripMenuItem();
    private readonly ToolStripMenuItem deleteProfileMenu = new ToolStripMenuItem();
    private ComboDropDownRightClickRouter profileDropDownRightClick;
    private readonly List<WorkspaceProfile> profiles = new List<WorkspaceProfile>();
    private LauncherSettings settings = new LauncherSettings();
    private bool suppressProfileChange = false;
    private string activeProfileName = "";
    private bool chinese = true;

    public LauncherForm()
    {
        configPath = Path.Combine(appDir, "workspace-launcher.config.json");
        iconDir = Path.Combine(appDir, "icons");
        Directory.CreateDirectory(iconDir);
        BuildUi();
        LoadConfig();
        ApplyLanguage();
        toolTip.SetToolTip(addButton, L("Add a website or app.", "\u6dfb\u52a0\u7f51\u9875\u6216\u8f6f\u4ef6\u3002"));
        toolTip.SetToolTip(saveButton, L("Writes the current list to workspace-launcher.config.json.", "\u628a\u5f53\u524d\u5217\u8868\u5199\u5165\u914d\u7f6e\u6587\u4ef6\uff0c\u4e0b\u6b21\u6253\u5f00\u4ecd\u4f1a\u4fdd\u7559\u3002"));
        RefreshStatus();
    }

    private string L(string en, string zh)
    {
        return chinese ? zh : en;
    }

    private void BuildUi()
    {
        Text = "Workspace Launcher";
        Size = new Size(1040, 680);
        MinimumSize = new Size(860, 540);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(246, 247, 251);
        Font = new Font("Microsoft YaHei UI", 9F);
        SetWindowIcon();

        titleLabel.Font = new Font(Font.FontFamily, 22F, FontStyle.Bold);
        titleLabel.ForeColor = Color.FromArgb(22, 28, 38);
        titleLabel.Location = new Point(36, 28);
        titleLabel.AutoSize = true;
        Controls.Add(titleLabel);

        subtitleLabel.ForeColor = Color.FromArgb(82, 92, 110);
        subtitleLabel.Location = new Point(39, 84);
        subtitleLabel.AutoSize = true;
        Controls.Add(subtitleLabel);

        profileLabel.ForeColor = Color.FromArgb(82, 92, 110);
        profileLabel.AutoSize = true;
        Controls.Add(profileLabel);

        profileBox.DropDownStyle = ComboBoxStyle.DropDownList;
        profileBox.Size = new Size(220, 28);
        profileBox.SelectedIndexChanged += delegate
        {
            if (suppressProfileChange) return;
            SwitchProfile(profileBox.SelectedItem as string);
        };
        Controls.Add(profileBox);

        itemList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        itemList.DrawMode = DrawMode.OwnerDrawFixed;
        itemList.ItemHeight = 42;
        itemList.BorderStyle = BorderStyle.None;
        itemList.BackColor = Color.White;
        itemList.HorizontalScrollbar = true;
        itemList.SelectionMode = SelectionMode.MultiExtended;
        itemList.DrawItem += DrawItem;
        itemList.DoubleClick += delegate { EditSelected(); };
        itemList.MouseDown += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            int index = itemList.IndexFromPoint(e.Location);
            if (index >= 0 && !itemList.SelectedIndices.Contains(index))
            {
                itemList.ClearSelected();
                itemList.SelectedIndex = index;
            }
        };
        Controls.Add(itemList);

        emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
        emptyLabel.ForeColor = Color.FromArgb(105, 114, 130);
        emptyLabel.BackColor = Color.White;
        emptyLabel.Visible = false;
        emptyLabel.Click += delegate { AddItem(); };
        Controls.Add(emptyLabel);

        editMenu.Click += delegate { EditSelected(); };
        toggleMenu.Click += delegate { ToggleSelectedEnabled(); };
        moveUpMenu.Click += delegate { MoveSelected(-1); };
        moveDownMenu.Click += delegate { MoveSelected(1); };
        detectMenu.Click += delegate { DetectSelected(); };
        copyTargetMenu.Click += delegate { CopySelectedTarget(); };
        openFolderMenu.Click += delegate { OpenSelectedFolder(); };
        deleteMenu.Click += delegate { DeleteSelected(); };
        menu.Items.AddRange(new ToolStripItem[] { editMenu, toggleMenu, new ToolStripSeparator(), moveUpMenu, moveDownMenu, new ToolStripSeparator(), detectMenu, copyTargetMenu, openFolderMenu, new ToolStripSeparator(), deleteMenu });
        menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
        {
            bool hasSelection = itemList.SelectedItems.Count > 0;
            bool single = itemList.SelectedItems.Count == 1;
            editMenu.Enabled = single;
            toggleMenu.Enabled = hasSelection;
            if (single)
            {
                LaunchItem selected = (LaunchItem)itemList.SelectedItem;
                toggleMenu.Text = selected.enabled ? L("Disable", "停用") : L("Enable", "启用");
            }
            else toggleMenu.Text = L("Enable / Disable", "启用 / 停用");
            moveUpMenu.Enabled = single && itemList.SelectedIndex > 0;
            moveDownMenu.Enabled = single && itemList.SelectedIndex >= 0 && itemList.SelectedIndex < itemList.Items.Count - 1;
            detectMenu.Enabled = hasSelection;
            copyTargetMenu.Enabled = single;
            openFolderMenu.Enabled = single;
            deleteMenu.Enabled = hasSelection;
        };
        itemList.ContextMenuStrip = menu;

        ConfigureHeader(nameHeaderLabel);
        ConfigureHeader(typeHeaderLabel);
        ConfigureHeader(targetHeaderLabel);
        Controls.Add(nameHeaderLabel);
        Controls.Add(typeHeaderLabel);
        Controls.Add(targetHeaderLabel);

        newProfileMenu.Click += delegate { NewProfile(); };
        renameProfileMenu.Click += delegate { RenameProfile(); };
        deleteProfileMenu.Click += delegate { DeleteProfile(); };
        profileMenu.Items.AddRange(new ToolStripItem[] { newProfileMenu, deleteProfileMenu, renameProfileMenu });
        profileBox.ContextMenuStrip = profileMenu;
        profileDropDownRightClick = new ComboDropDownRightClickRouter(profileBox, profileMenu);

        ConfigureButton(addButton, false);
        ConfigureButton(importButton, false);
        ConfigureButton(exportButton, false);
        ConfigureButton(clearButton, false);
        ConfigureButton(settingsButton, false);
        ConfigureButton(saveButton, false);
        ConfigureButton(launchButton, true);

        addButton.Click += delegate { AddItem(); };
        importButton.Click += delegate { ImportConfig(); };
        exportButton.Click += delegate { ExportConfig(); };
        clearButton.Click += delegate { ClearAll(); };
        settingsButton.Click += delegate { EditSettings(); };
        saveButton.Click += delegate { SaveConfig(); statusLabel.Text = L("Saved.", "\u5df2\u4fdd\u5b58\u3002"); };
        launchButton.Click += delegate { LaunchAll(); };

        Controls.Add(addButton);
        Controls.Add(importButton);
        Controls.Add(exportButton);
        Controls.Add(clearButton);
        Controls.Add(settingsButton);
        Controls.Add(saveButton);
        Controls.Add(launchButton);

        statusLabel.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        statusLabel.ForeColor = Color.FromArgb(82, 92, 110);
        statusLabel.AutoEllipsis = true;
        Controls.Add(statusLabel);

        Resize += delegate { LayoutControls(); };
        LayoutControls();
    }

    private void SetWindowIcon()
    {
        try
        {
            string iconPath = Path.Combine(appDir, "WorkspaceLauncher.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
                return;
            }

            using (Icon embedded = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
            {
                if (embedded != null) Icon = (Icon)embedded.Clone();
            }
        }
        catch { }
    }

    private void ConfigureButton(Button button, bool primary)
    {
        button.Size = primary ? new Size(150, 38) : new Size(96, 38);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(219, 224, 235);
        button.BackColor = primary ? Color.FromArgb(42, 105, 244) : Color.White;
        button.ForeColor = primary ? Color.White : Color.FromArgb(42, 49, 62);
        button.Cursor = Cursors.Hand;
    }

    private void ConfigureHeader(Label label)
    {
        label.ForeColor = Color.FromArgb(105, 114, 130);
        label.BackColor = Color.White;
        label.AutoSize = false;
        label.TextAlign = ContentAlignment.MiddleLeft;
    }

    private void ApplyLanguage()
    {
        titleLabel.Text = L("Workspace Launcher", "工作区启动器");
        subtitleLabel.Text = L("Manage apps and websites, then open the whole workspace with one click.", "管理常用软件和网页，一键打开完整工作区。");
        profileLabel.Text = L("Profile", "方案");
        addButton.Text = L("Add Item", "添加项目");
        importButton.Text = L("Import", "导入");
        exportButton.Text = L("Export", "导出");
        clearButton.Text = L("Clear All", "清空全部");
        settingsButton.Text = L("Settings", "设置");
        saveButton.Text = L("Save", "保存");
        launchButton.Text = L("Launch", "一键启动");
        editMenu.Text = L("Edit", "编辑");
        toggleMenu.Text = L("Enable / Disable", "启用 / 停用");
        moveUpMenu.Text = L("Move Up", "上移");
        moveDownMenu.Text = L("Move Down", "下移");
        detectMenu.Text = L("Detect", "识别");
        copyTargetMenu.Text = L("Copy Path / URL", "复制路径/网址");
        openFolderMenu.Text = L("Open Location", "打开所在位置");
        deleteMenu.Text = L("Delete", "删除");
        newProfileMenu.Text = L("New Profile", "新建方案");
        deleteProfileMenu.Text = L("Delete Profile", "删除方案");
        renameProfileMenu.Text = L("Rename Profile", "重命名方案");
        nameHeaderLabel.Text = L("Name", "名称");
        typeHeaderLabel.Text = L("Type", "类型");
        targetHeaderLabel.Text = L("Path / URL", "路径 / 网址");
        emptyLabel.Text = L("No items yet. Click Add Item to create one.", "还没有项目。点击“添加项目”开始。");
        toolTip.SetToolTip(addButton, L("Add a website or app.", "\u6dfb\u52a0\u7f51\u9875\u6216\u8f6f\u4ef6\u3002"));
        toolTip.SetToolTip(profileBox, L("Right-click to manage profiles.", "\u53f3\u952e\u7ba1\u7406\u65b9\u6848\u3002"));
        toolTip.SetToolTip(importButton, L("Import a saved launcher configuration.", "\u5bfc\u5165\u4e00\u4efd\u542f\u52a8\u5668\u914d\u7f6e\u3002"));
        toolTip.SetToolTip(exportButton, L("Export all profiles and settings.", "\u5bfc\u51fa\u6240\u6709\u65b9\u6848\u548c\u8bbe\u7f6e\u3002"));
        toolTip.SetToolTip(settingsButton, L("Open launcher settings.", "\u6253\u5f00\u542f\u52a8\u5668\u8bbe\u7f6e\u3002"));
        toolTip.SetToolTip(saveButton, L("Writes the current list to workspace-launcher.config.json.", "\u628a\u5f53\u524d\u5217\u8868\u5199\u5165\u914d\u7f6e\u6587\u4ef6\uff0c\u4e0b\u6b21\u6253\u5f00\u4ecd\u4f1a\u4fdd\u7559\u3002"));
        toolTip.SetToolTip(statusLabel, configPath);
        RefreshStatus();
    }

    private void LayoutControls()
    {
        int selectorLabelWidth = 70;
        int selectorBoxWidth = 220;
        int selectorLeft = ClientSize.Width - selectorLabelWidth - selectorBoxWidth - 36;
        profileLabel.Location = new Point(selectorLeft, 44);
        profileBox.Location = new Point(selectorLeft + selectorLabelWidth, 40);
        int listLeft = 28;
        int headerY = 132;
        int listTop = 156;
        nameHeaderLabel.Bounds = new Rectangle(listLeft + NameColumnX, headerY, 260, 22);
        typeHeaderLabel.Bounds = new Rectangle(listLeft + TypeColumnX, headerY, 92, 22);
        targetHeaderLabel.Bounds = new Rectangle(listLeft + TargetColumnX, headerY, ClientSize.Width - listLeft - TargetColumnX - 28, 22);
        itemList.Bounds = new Rectangle(listLeft, listTop, ClientSize.Width - 56, ClientSize.Height - 246);
        emptyLabel.Bounds = new Rectangle(itemList.Left + 20, itemList.Top + 20, itemList.Width - 40, Math.Min(160, itemList.Height - 40));
        int y = ClientSize.Height - 78;
        addButton.Location = new Point(28, y);
        importButton.Location = new Point(132, y);
        exportButton.Location = new Point(236, y);
        clearButton.Location = new Point(340, y);
        settingsButton.Location = new Point(444, y);
        launchButton.Location = new Point(ClientSize.Width - 178, y);
        saveButton.Location = new Point(launchButton.Left - 108, y);
        statusLabel.Location = new Point(28, ClientSize.Height - 34);
        statusLabel.Size = new Size(ClientSize.Width - 56, 24);
    }

    private void DrawItem(object sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= itemList.Items.Count) return;

        LaunchItem item = (LaunchItem)itemList.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        string validation = ValidateItem(item);
        Color back = selected ? Color.FromArgb(232, 238, 255) : (!item.enabled ? Color.FromArgb(248, 249, 252) : Color.White);
        using (SolidBrush backBrush = new SolidBrush(back))
        {
            e.Graphics.FillRectangle(backBrush, e.Bounds);
        }

        using (Image icon = LoadIconImage(item))
        {
            if (icon != null)
            {
                e.Graphics.DrawImage(icon, new Rectangle(e.Bounds.X + IconColumnX, e.Bounds.Y + 9, 24, 24));
            }
        }

        Color textColor = item.enabled ? Color.FromArgb(22, 28, 38) : Color.FromArgb(140, 148, 160);
        Color typeColor = String.IsNullOrWhiteSpace(validation) ? textColor : Color.FromArgb(196, 76, 62);
        string type = item.type == "app" ? L("App", "软件") : L("Web", "网页");
        if (!item.enabled) type += L(" off", " 停用");
        if (!String.IsNullOrWhiteSpace(validation)) type += " !";
        TextRenderer.DrawText(e.Graphics, item.name ?? "", Font, new Rectangle(e.Bounds.X + NameColumnX, e.Bounds.Y, 260, e.Bounds.Height), textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, type, Font, new Rectangle(e.Bounds.X + TypeColumnX, e.Bounds.Y, 92, e.Bounds.Height), typeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, item.target ?? "", Font, new Rectangle(e.Bounds.X + TargetColumnX, e.Bounds.Y, e.Bounds.Width - TargetColumnX - 10, e.Bounds.Height), textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        e.DrawFocusRectangle();
    }

    private void LoadConfig()
    {
        itemList.Items.Clear();
        profiles.Clear();
        try
        {
            if (!File.Exists(configPath))
            {
                profiles.Add(new WorkspaceProfile { name = DefaultProfileName(), items = new List<LaunchItem>() });
                settings = new LauncherSettings();
                settings.language = "zh";
                RefreshProfileBox(DefaultProfileName());
                LoadProfileItems(CurrentProfile());
                SaveConfig();
                return;
            }
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            LauncherConfig config = serializer.Deserialize<LauncherConfig>(File.ReadAllText(configPath, Encoding.UTF8));
            if (config == null) config = new LauncherConfig();
            settings = config.settings ?? new LauncherSettings();
            if (String.IsNullOrWhiteSpace(settings.language)) settings.language = "zh";
            chinese = !String.Equals(settings.language, "en", StringComparison.OrdinalIgnoreCase);

            if (config.profiles != null && config.profiles.Count > 0)
            {
                foreach (WorkspaceProfile profile in config.profiles)
                {
                    if (profile == null) continue;
                    string name = String.IsNullOrWhiteSpace(profile.name) ? DefaultProfileName() : profile.name.Trim();
                    profiles.Add(new WorkspaceProfile { name = UniqueProfileName(name), items = profile.items ?? new List<LaunchItem>() });
                }
            }
            else
            {
                profiles.Add(new WorkspaceProfile { name = DefaultProfileName(), items = config.items ?? new List<LaunchItem>() });
            }

            if (profiles.Count == 0) profiles.Add(new WorkspaceProfile { name = DefaultProfileName(), items = new List<LaunchItem>() });

            bool migrated = false;
            foreach (WorkspaceProfile profile in profiles)
            {
                List<LaunchItem> normalizedItems = new List<LaunchItem>();
                foreach (LaunchItem item in profile.items ?? new List<LaunchItem>())
                {
                    if (item == null || String.IsNullOrWhiteSpace(item.target)) continue;
                    LaunchItem normalized = Normalize(CloneItem(item));
                    if (normalized.type == "app" && (String.IsNullOrWhiteSpace(normalized.iconPath) || Path.GetExtension(normalized.iconPath).Equals(".ico", StringComparison.OrdinalIgnoreCase)))
                    {
                        DetectItem(normalized, false);
                        migrated = true;
                    }
                    normalizedItems.Add(normalized);
                }
                profile.items = normalizedItems;
            }

            string selected = !String.IsNullOrWhiteSpace(config.currentProfile) ? config.currentProfile : profiles[0].name;
            if (FindProfile(selected) == null) selected = profiles[0].name;
            RefreshProfileBox(selected);
            LoadProfileItems(CurrentProfile());
            if (migrated || config.profiles == null) SaveConfig();
        }
        catch (Exception ex)
        {
            MessageBox.Show(L("Could not read config: ", "读取配置失败：") + ex.Message);
        }
    }

    private void SaveConfig()
    {
        try
        {
            SaveCurrentListToProfile();
            LauncherConfig config = new LauncherConfig();
            config.currentProfile = CurrentProfileName();
            config.profiles = new List<WorkspaceProfile>();
            foreach (WorkspaceProfile profile in profiles)
            {
                config.profiles.Add(new WorkspaceProfile { name = profile.name, items = CloneItems(profile.items) });
            }
            config.settings = settings;
            config.items = CloneItems((CurrentProfile() != null ? CurrentProfile().items : new List<LaunchItem>()));
            config.urls = null;
            config.apps = null;
            JavaScriptSerializer serializer = new JavaScriptSerializer();

            string tempPath = configPath + ".tmp";
            File.WriteAllText(tempPath, PrettyJson(serializer.Serialize(config)), Encoding.UTF8);
            ReplaceFile(tempPath, configPath);

            CleanupIconCache();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(L("Could not save config: ", "保存配置失败：") + ex.Message);
        }
    }

    private void ReplaceFile(string tempPath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            File.Move(tempPath, destinationPath);
            return;
        }

        string backupPath = destinationPath + ".bak";
        try
        {
            File.Replace(tempPath, destinationPath, backupPath, true);
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
        catch
        {
            File.Copy(tempPath, destinationPath, true);
            File.Delete(tempPath);
        }
    }

    private string DefaultProfileName()
    {
        return "Default";
    }

    private string CurrentProfileName()
    {
        if (!String.IsNullOrWhiteSpace(activeProfileName)) return activeProfileName;
        return profileBox.SelectedItem as string ?? (profiles.Count > 0 ? profiles[0].name : DefaultProfileName());
    }

    private WorkspaceProfile CurrentProfile()
    {
        return FindProfile(CurrentProfileName()) ?? (profiles.Count > 0 ? profiles[0] : null);
    }

    private WorkspaceProfile FindProfile(string name)
    {
        foreach (WorkspaceProfile profile in profiles)
        {
            if (String.Equals(profile.name, name, StringComparison.OrdinalIgnoreCase)) return profile;
        }
        return null;
    }

    private string UniqueProfileName(string baseName)
    {
        if (String.IsNullOrWhiteSpace(baseName)) baseName = DefaultProfileName();
        string name = baseName.Trim();
        if (FindProfile(name) == null) return name;
        int index = 2;
        while (FindProfile(name + " " + index) != null) index++;
        return name + " " + index;
    }

    private void RefreshProfileBox(string selectedName)
    {
        suppressProfileChange = true;
        profileBox.Items.Clear();
        foreach (WorkspaceProfile profile in profiles) profileBox.Items.Add(profile.name);
        if (profileBox.Items.Count > 0)
        {
            int selectedIndex = 0;
            for (int i = 0; i < profileBox.Items.Count; i++)
            {
                if (String.Equals((string)profileBox.Items[i], selectedName, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    break;
                }
            }
            profileBox.SelectedIndex = selectedIndex;
        }
        suppressProfileChange = false;
    }

    private void SaveCurrentListToProfile()
    {
        WorkspaceProfile profile = FindProfile(activeProfileName) ?? CurrentProfile();
        if (profile == null) return;
        profile.items = new List<LaunchItem>();
        foreach (LaunchItem item in itemList.Items) profile.items.Add(CloneItem(item));
    }

    private void LoadProfileItems(WorkspaceProfile profile)
    {
        itemList.Items.Clear();
        if (profile != null && profile.items != null)
        {
            foreach (LaunchItem item in profile.items)
            {
                if (item != null && !String.IsNullOrWhiteSpace(item.target)) AddOrReplace(Normalize(CloneItem(item)));
            }
            activeProfileName = profile.name;
        }
        RefreshStatus();
    }

    private void SwitchProfile(string name)
    {
        if (String.IsNullOrWhiteSpace(name)) return;
        SaveCurrentListToProfile();
        WorkspaceProfile next = FindProfile(name);
        if (next == null) return;
        LoadProfileItems(next);
        SaveConfig();
    }

    private LaunchItem CloneItem(LaunchItem item)
    {
        if (item == null) return new LaunchItem();
        return new LaunchItem
        {
            type = item.type,
            name = item.name,
            target = item.target,
            args = item.args,
            enabled = item.enabled,
            iconPath = item.iconPath
        };
    }

    private List<LaunchItem> CloneItems(List<LaunchItem> items)
    {
        List<LaunchItem> clones = new List<LaunchItem>();
        if (items == null) return clones;
        foreach (LaunchItem item in items) clones.Add(CloneItem(item));
        return clones;
    }

    private string PrettyJson(string json)
    {
        StringBuilder builder = new StringBuilder();
        int indent = 0;
        bool quote = false;
        for (int i = 0; i < json.Length; i++)
        {
            char ch = json[i];
            if (ch == '"' && !IsEscaped(json, i)) quote = !quote;
            if (!quote && (ch == '{' || ch == '['))
            {
                builder.Append(ch).AppendLine();
                indent++;
                builder.Append(new string(' ', indent * 2));
            }
            else if (!quote && (ch == '}' || ch == ']'))
            {
                builder.AppendLine();
                indent--;
                builder.Append(new string(' ', indent * 2)).Append(ch);
            }
            else if (!quote && ch == ',')
            {
                builder.Append(ch).AppendLine().Append(new string(' ', indent * 2));
            }
            else if (!quote && ch == ':') builder.Append(": ");
            else builder.Append(ch);
        }
        return builder.ToString();
    }

    private bool IsEscaped(string text, int index)
    {
        int slashCount = 0;
        for (int i = index - 1; i >= 0 && text[i] == '\\'; i--) slashCount++;
        return slashCount % 2 == 1;
    }

    private LaunchItem Normalize(LaunchItem item)
    {
        if (String.IsNullOrWhiteSpace(item.type)) item.type = "url";
        item.type = item.type == "app" ? "app" : "url";
        if (item.type == "url") item.target = NormalizeUrl(item.target);
        else item.target = (item.target ?? "").Trim();
        if (String.IsNullOrWhiteSpace(item.name)) item.name = GuessName(item.type, item.target);
        if (item.args == null) item.args = "";
        if (item.type == "url") item.args = "";
        if (item.iconPath == null) item.iconPath = "";
        return item;
    }

    private void AddOrReplace(LaunchItem item)
    {
        string key = ItemKey(item);
        for (int i = 0; i < itemList.Items.Count; i++)
        {
            if (String.Equals(ItemKey((LaunchItem)itemList.Items[i]), key, StringComparison.OrdinalIgnoreCase))
            {
                itemList.Items[i] = item;
                return;
            }
        }
        itemList.Items.Add(item);
    }

    private void AddItem()
    {
        LaunchItem item = new LaunchItem { type = "url", name = "", target = "https://", args = "", enabled = true, iconPath = "" };
        if (EditDialog.Show(this, item, chinese, iconDir))
        {
            DetectItem(item, false);
            AddOrReplace(Normalize(item));
            SaveConfig();
            RefreshStatus();
        }
    }

    private void EditSelected()
    {
        if (itemList.SelectedItems.Count != 1) return;
        LaunchItem item = (LaunchItem)itemList.SelectedItem;
        if (EditDialog.Show(this, item, chinese, iconDir))
        {
            int index = itemList.SelectedIndex;
            DetectItem(item, false);
            itemList.Items[index] = Normalize(item);
            SaveConfig();
        }
    }

    private void DetectSelected()
    {
        foreach (LaunchItem item in itemList.SelectedItems)
        {
            DetectItem(item, true);
        }
        SaveConfig();
        itemList.Invalidate();
    }

    private void DeleteSelected()
    {
        while (itemList.SelectedIndices.Count > 0)
        {
            itemList.Items.RemoveAt(itemList.SelectedIndices[0]);
        }
        SaveConfig();
    }

    private void ClearAll()
    {
        if (itemList.Items.Count == 0) return;
        DialogResult result = MessageBox.Show(L("Clear all apps and websites?", "确定清空所有网页和软件吗？"), "Workspace Launcher", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != DialogResult.Yes) return;
        itemList.Items.Clear();
        SaveConfig();
    }

    private void ToggleSelectedEnabled()
    {
        foreach (LaunchItem item in itemList.SelectedItems)
        {
            item.enabled = !item.enabled;
        }
        SaveConfig();
        itemList.Invalidate();
    }

    private void MoveSelected(int direction)
    {
        if (itemList.SelectedItems.Count != 1) return;
        int index = itemList.SelectedIndex;
        int next = index + direction;
        if (index < 0 || next < 0 || next >= itemList.Items.Count) return;
        object item = itemList.Items[index];
        itemList.Items.RemoveAt(index);
        itemList.Items.Insert(next, item);
        itemList.SelectedIndex = next;
        SaveConfig();
    }

    private void CopySelectedTarget()
    {
        if (itemList.SelectedItems.Count != 1) return;
        LaunchItem item = (LaunchItem)itemList.SelectedItem;
        if (!String.IsNullOrWhiteSpace(item.target)) Clipboard.SetText(item.target);
    }

    private void OpenSelectedFolder()
    {
        if (itemList.SelectedItems.Count != 1) return;
        LaunchItem item = (LaunchItem)itemList.SelectedItem;
        try
        {
            if (item.type == "app")
            {
                string path = ResolveAppPath(item.target);
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                    return;
                }
            }

            string target = item.type == "url" ? NormalizeUrl(item.target) : item.target;
            if (!String.IsNullOrWhiteSpace(target)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Workspace Launcher");
        }
    }

    private void NewProfile()
    {
        string name = PromptDialog.Show(this, L("New Profile", "新建方案"), L("Profile name", "方案名称"), "", chinese);
        if (String.IsNullOrWhiteSpace(name)) return;
        SaveCurrentListToProfile();
        string unique = UniqueProfileName(name);
        profiles.Add(new WorkspaceProfile { name = unique, items = new List<LaunchItem>() });
        RefreshProfileBox(unique);
        LoadProfileItems(FindProfile(unique));
        SaveConfig();
    }

    private void RenameProfile()
    {
        WorkspaceProfile profile = CurrentProfile();
        if (profile == null) return;
        string name = PromptDialog.Show(this, L("Rename Profile", "重命名方案"), L("Profile name", "方案名称"), profile.name, chinese);
        if (String.IsNullOrWhiteSpace(name)) return;
        string oldName = profile.name;
        profile.name = oldName;
        string unique = name.Trim();
        foreach (WorkspaceProfile other in profiles)
        {
            if (!Object.ReferenceEquals(other, profile) && String.Equals(other.name, unique, StringComparison.OrdinalIgnoreCase))
            {
                unique = UniqueProfileName(unique);
                break;
            }
        }
        profile.name = unique;
        activeProfileName = unique;
        RefreshProfileBox(unique);
        SaveConfig();
    }

    private void DeleteProfile()
    {
        if (profiles.Count <= 1)
        {
            MessageBox.Show(L("Keep at least one profile.", "至少保留一个方案。"), "Workspace Launcher");
            return;
        }
        WorkspaceProfile profile = CurrentProfile();
        if (profile == null) return;
        DialogResult result = MessageBox.Show(L("Delete current profile?", "删除当前方案吗？"), "Workspace Launcher", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != DialogResult.Yes) return;
        int index = profiles.IndexOf(profile);
        profiles.Remove(profile);
        string selected = profiles[Math.Min(index, profiles.Count - 1)].name;
        RefreshProfileBox(selected);
        LoadProfileItems(FindProfile(selected));
        SaveConfig();
    }

    private void EditSettings()
    {
        if (SettingsDialog.Show(this, settings, chinese))
        {
            chinese = !String.Equals(settings.language, "en", StringComparison.OrdinalIgnoreCase);
            ApplyLanguage();
            ApplyStartupSetting();
            SaveConfig();
        }
    }

    private void ApplyStartupSetting()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key == null) return;
                if (settings.startWithWindows)
                {
                    key.SetValue("WorkspaceLauncher", "\"" + Application.ExecutablePath + "\"");
                }
                else
                {
                    key.DeleteValue("WorkspaceLauncher", false);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(L("Could not update startup setting: ", "无法更新开机自启设置：") + ex.Message, "Workspace Launcher");
        }
    }

    private void ImportConfig()
    {
        using (OpenFileDialog dialog = new OpenFileDialog())
        {
            dialog.Filter = "Workspace Launcher config (*.json)|*.json|All files (*.*)|*.*";
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                LauncherConfig imported = serializer.Deserialize<LauncherConfig>(File.ReadAllText(dialog.FileName, Encoding.UTF8));
                if (imported == null || ((imported.profiles == null || imported.profiles.Count == 0) && (imported.items == null || imported.items.Count == 0)))
                {
                    MessageBox.Show(L("This file is not a valid launcher config.", "这个文件不是有效的启动器配置。"), "Workspace Launcher");
                    return;
                }
                string backup = Path.Combine(appDir, "workspace-launcher.import-backup." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".json");
                if (File.Exists(configPath)) File.Copy(configPath, backup, true);
                File.Copy(dialog.FileName, configPath, true);
                LoadConfig();
                SaveConfig();
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("Could not import config: ", "导入配置失败：") + ex.Message, "Workspace Launcher");
            }
        }
    }

    private void ExportConfig()
    {
        SaveConfig();
        using (SaveFileDialog dialog = new SaveFileDialog())
        {
            dialog.Filter = "Workspace Launcher config (*.json)|*.json|All files (*.*)|*.*";
            dialog.FileName = "workspace-launcher.config.json";
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            File.Copy(configPath, dialog.FileName, true);
        }
    }

    private void LaunchAll()
    {
        SaveConfig();
        int count = 0;
        int skipped = 0;
        List<string> failures = new List<string>();
        foreach (LaunchItem item in itemList.Items)
        {
            if (!item.enabled)
            {
                skipped++;
                continue;
            }
            try
            {
                string validation = ValidateItem(item);
                if (!String.IsNullOrWhiteSpace(validation))
                {
                    failures.Add((item.name ?? item.target) + ": " + validation);
                    continue;
                }
                if (item.type == "app")
                {
                    Process.Start(new ProcessStartInfo(ResolveAppPath(item.target)) { UseShellExecute = true, Arguments = item.args ?? "" });
                }
                else
                {
                    Process.Start(new ProcessStartInfo(NormalizeUrl(item.target)) { UseShellExecute = true });
                }
                count++;
            }
            catch (Exception ex)
            {
                failures.Add((item.name ?? item.target) + ": " + ex.Message);
            }
        }
        statusLabel.Text = L("Launched ", "已启动 ") + count + L(" item(s).", " 个项目。") + (skipped > 0 ? L(" Skipped ", " 已跳过 ") + skipped + L(".", " 个。") : "") + (failures.Count > 0 ? L(" Failed ", " 失败 ") + failures.Count + L(".", " 个。") : "");
        if (failures.Count > 0)
        {
            int take = Math.Min(10, failures.Count);
            StringBuilder message = new StringBuilder();
            message.AppendLine(L("Some items could not be launched:", "有些项目无法启动："));
            for (int i = 0; i < take; i++) message.AppendLine("- " + failures[i]);
            if (failures.Count > take) message.AppendLine("...");
            MessageBox.Show(message.ToString(), "Workspace Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        if (count > 0 && failures.Count == 0)
        {
            if (settings.closeAfterLaunch) Close();
            else if (settings.minimizeAfterLaunch) WindowState = FormWindowState.Minimized;
        }
    }

    private void RefreshStatus()
    {
        if (statusLabel != null)
        {
            int enabled = 0;
            foreach (LaunchItem item in itemList.Items) if (item.enabled) enabled++;
            statusLabel.Text = CurrentProfileName() + " · " + L("Loaded ", "已加载 ") + itemList.Items.Count + L(" item(s), ", " 个项目，") + enabled + L(" enabled. Config: ", " 个已启用。配置：") + configPath;
            emptyLabel.Visible = itemList.Items.Count == 0;
            if (emptyLabel.Visible) emptyLabel.BringToFront();
        }
        itemList.Invalidate();
    }

    private string ValidateItem(LaunchItem item)
    {
        if (item == null) return L("Item is empty.", "项目为空。");
        if (String.IsNullOrWhiteSpace(item.target)) return L("Path or URL is empty.", "路径或网址为空。");
        if (item.type == "app")
        {
            string path = ResolveAppPath(item.target);
            if (File.Exists(path)) return "";
            if (LooksLikeShellCommand(item.target)) return "";
            return L("App path does not exist.", "软件路径不存在。");
        }

        Uri uri;
        string url = NormalizeUrl(item.target);
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return L("Website URL is invalid.", "网页地址格式不正确。");
        }
        return "";
    }

    private void DetectItem(LaunchItem item, bool replaceName)
    {
        if (item.type == "app")
        {
            string path = ResolveAppPath(item.target);
            if (File.Exists(path))
            {
                item.target = path;
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                if (replaceName || String.IsNullOrWhiteSpace(item.name))
                {
                    item.name = !String.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : Path.GetFileNameWithoutExtension(path);
                }
                try
                {
                    string savePath = SaveAppIcon(path);
                    if (!String.IsNullOrWhiteSpace(savePath)) item.iconPath = savePath;
                }
                catch { }
            }
            return;
        }

        item.target = NormalizeUrl(item.target);
        try
        {
            string html;
            using (WebClient client = NewWebClient())
            {
                html = client.DownloadString(item.target);
            }
            Match title = Regex.Match(html, "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (title.Success && (replaceName || String.IsNullOrWhiteSpace(item.name)))
            {
                item.name = HttpUtility.HtmlDecode(Regex.Replace(title.Groups[1].Value, "\\s+", " ").Trim());
            }
            string iconUrl = GetFaviconUrl(item.target, html);
            string iconPath = DownloadIcon(iconUrl, item.target);
            if (!String.IsNullOrWhiteSpace(iconPath)) item.iconPath = iconPath;
        }
        catch
        {
            if (String.IsNullOrWhiteSpace(item.name)) item.name = GuessName(item.type, item.target);
            try
            {
                string iconPath = DownloadIcon(GetFaviconUrl(item.target), item.target);
                if (!String.IsNullOrWhiteSpace(iconPath)) item.iconPath = iconPath;
            }
            catch { }
        }
    }

    private WebClient NewWebClient()
    {
        WebClient client = new TimeoutWebClient();
        client.Headers.Add("User-Agent", "Mozilla/5.0");
        client.Encoding = Encoding.UTF8;
        return client;
    }

    private string GetFaviconUrl(string target)
    {
        return GetFaviconUrl(target, "");
    }

    private string GetFaviconUrl(string target, string html)
    {
        Uri uri = new Uri(NormalizeUrl(target));
        string pageIcon = FindFaviconInHtml(uri, html);
        if (!String.IsNullOrWhiteSpace(pageIcon)) return pageIcon;
        string host = uri.Host.ToLowerInvariant();
        if (host == "cnki.net" || host.EndsWith(".cnki.net"))
        {
            return uri.Scheme + "://" + uri.Host + "/favicon.ico";
        }
        return uri.Scheme + "://" + uri.Host + "/favicon.ico";
    }

    private string FindFaviconInHtml(Uri pageUri, string html)
    {
        if (String.IsNullOrWhiteSpace(html)) return "";
        MatchCollection links = Regex.Matches(html, "<link[^>]+>", RegexOptions.IgnoreCase);
        foreach (Match link in links)
        {
            string tag = link.Value;
            if (tag.IndexOf("icon", StringComparison.OrdinalIgnoreCase) < 0) continue;
            Match href = Regex.Match(tag, "href\\s*=\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase);
            if (!href.Success) continue;
            try
            {
                Uri iconUri = new Uri(pageUri, HttpUtility.HtmlDecode(href.Groups[1].Value));
                return iconUri.ToString();
            }
            catch { }
        }
        return "";
    }

    private string DownloadIcon(string iconUrl, string target)
    {
        try
        {
            byte[] bytes;
            using (WebClient client = NewWebClient())
            {
                bytes = client.DownloadData(iconUrl);
            }
            string basePath = Path.Combine(iconDir, "web-" + Hash(target + iconUrl));
            if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0)
            {
                string pngPath = basePath + ".png";
                using (MemoryStream stream = new MemoryStream(bytes))
                using (Icon icon = new Icon(stream))
                using (Bitmap bitmap = icon.ToBitmap())
                {
                    bitmap.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
                }
                return pngPath;
            }

            string path = basePath + ".png";
            using (MemoryStream stream = new MemoryStream(bytes))
            using (Image downloaded = Image.FromStream(stream))
            {
                downloaded.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            using (Image img = Image.FromFile(path))
            {
                if (img.Width > 0) return path;
            }
        }
        catch { }
        try
        {
            Uri uri = new Uri(NormalizeUrl(target));
            string googleUrl = "https://www.google.com/s2/favicons?domain=" + Uri.EscapeDataString(uri.Host) + "&sz=64";
            if (!String.Equals(iconUrl, googleUrl, StringComparison.OrdinalIgnoreCase)) return DownloadIcon(googleUrl, target);
        }
        catch { }
        return "";
    }

    private Image LoadIconImage(LaunchItem item)
    {
        try
        {
            if (!String.IsNullOrWhiteSpace(item.iconPath) && File.Exists(item.iconPath))
            {
                if (Path.GetExtension(item.iconPath).Equals(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    using (Icon icon = new Icon(item.iconPath, 64, 64))
                    using (Bitmap bitmap = DrawIconToBitmap(icon, 64))
                    {
                        return FitIcon(bitmap);
                    }
                }
                using (Image img = Image.FromFile(item.iconPath)) return FitIcon(img);
            }
        }
        catch { }
        return FitIcon(item.type == "app" ? SystemIcons.Application.ToBitmap() : SystemIcons.Information.ToBitmap());
    }

    private string SaveAppIcon(string path)
    {
        try
        {
            using (Icon icon = Icon.ExtractAssociatedIcon(path))
            {
                if (icon == null) return "";
                string savePath = Path.Combine(iconDir, "app-" + Hash(path) + ".png");
                using (Bitmap bitmap = DrawIconToBitmap(icon, 64))
                {
                    bitmap.Save(savePath, System.Drawing.Imaging.ImageFormat.Png);
                }
                return savePath;
            }
        }
        catch { return ""; }
    }

    private Bitmap DrawIconToBitmap(Icon icon, int size)
    {
        Bitmap bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawIcon(icon, new Rectangle(0, 0, size, size));
        }
        return bitmap;
    }

    private Image FitIcon(Image source)
    {
        Bitmap bitmap = new Bitmap(24, 24);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, new Rectangle(2, 2, 20, 20));
        }
        return bitmap;
    }

    private void CleanupIconCache()
    {
        try
        {
            HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LaunchItem item in itemList.Items)
            {
                if (!String.IsNullOrWhiteSpace(item.iconPath) && File.Exists(item.iconPath)) keep.Add(Path.GetFullPath(item.iconPath));
            }
            foreach (WorkspaceProfile profile in profiles)
            {
                foreach (LaunchItem item in profile.items ?? new List<LaunchItem>())
                {
                    if (!String.IsNullOrWhiteSpace(item.iconPath) && File.Exists(item.iconPath)) keep.Add(Path.GetFullPath(item.iconPath));
                }
            }
            foreach (string file in Directory.GetFiles(iconDir))
            {
                if (!keep.Contains(Path.GetFullPath(file))) File.Delete(file);
            }
        }
        catch { }
    }

    private string ResolveAppPath(string target)
    {
        string path = Environment.ExpandEnvironmentVariables(target ?? "");
        path = path.Trim().Trim('"');
        if (File.Exists(path)) return path;
        if (!path.Contains("\\") && !path.Contains("/") && Path.GetExtension(path).Length > 0)
        {
            string windowsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path);
            if (File.Exists(windowsPath)) return windowsPath;
            string systemPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), path);
            if (File.Exists(systemPath)) return systemPath;
            string envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in envPath.Split(';'))
            {
                try
                {
                    if (String.IsNullOrWhiteSpace(dir)) continue;
                    string candidate = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim()), path);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        if (Directory.Exists(path))
        {
            string[] exes = Directory.GetFiles(path, "*.exe", SearchOption.TopDirectoryOnly);
            foreach (string exe in exes)
            {
                if (Path.GetFileNameWithoutExtension(exe).IndexOf(new DirectoryInfo(path).Name, StringComparison.OrdinalIgnoreCase) >= 0) return exe;
            }
            if (exes.Length > 0) return exes[0];
        }
        return path;
    }

    private bool LooksLikeShellCommand(string target)
    {
        if (String.IsNullOrWhiteSpace(target)) return false;
        string value = target.Trim();
        if (value.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.StartsWith("ms-", StringComparison.OrdinalIgnoreCase)) return true;
        if (!value.Contains("\\") && !value.Contains("/") && Path.GetExtension(value).Length > 0) return true;
        return false;
    }

    private string NormalizeUrl(string url)
    {
        if (String.IsNullOrWhiteSpace(url)) return "";
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "https://" + url;
        return url;
    }

    private string GuessName(string type, string target)
    {
        if (type == "app") return Path.GetFileNameWithoutExtension(target ?? "");
        try { return new Uri(NormalizeUrl(target)).Host; } catch { return target ?? ""; }
    }

    private string ItemKey(LaunchItem item)
    {
        if (item == null) return "";
        if (item.type == "app") return "app|" + ResolveAppPath(item.target).ToLowerInvariant();
        return "url|" + NormalizeUrl(item.target).TrimEnd('/').ToLowerInvariant();
    }

    private string Hash(string value)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}

public class EditDialog : Form
{
    private readonly ComboBox typeBox = new ComboBox();
    private readonly TextBox nameBox = new TextBox();
    private readonly TextBox targetBox = new TextBox();
    private readonly TextBox argsBox = new TextBox();
    private readonly CheckBox enabledBox = new CheckBox();
    private readonly Label targetLabel;
    private readonly Label argsHintLabel = new Label();
    private readonly Button browseButton = new Button();
    private readonly LaunchItem item;
    private readonly bool chinese;

    private EditDialog(LaunchItem item, bool chinese)
    {
        this.item = item;
        this.chinese = chinese;
        Text = L("Add / Edit Item", "\u6dfb\u52a0/\u7f16\u8f91\u9879\u76ee");
        Size = new Size(680, 390);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        AddLabel(L("Type", "\u7c7b\u578b"), 24, 30);
        AddLabel(L("Name", "\u540d\u79f0"), 24, 86);
        targetLabel = AddLabel(L("Path or URL", "\u8def\u5f84\u6216\u7f51\u5740"), 24, 142);
        AddLabel(L("Arguments", "\u542f\u52a8\u53c2\u6570"), 24, 198);

        typeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        typeBox.Items.Add(L("Website", "\u7f51\u9875"));
        typeBox.Items.Add(L("App", "\u8f6f\u4ef6"));
        typeBox.SetBounds(135, 26, 180, 28);
        typeBox.SelectedIndex = item.type == "app" ? 1 : 0;
        typeBox.SelectedIndexChanged += delegate { UpdateTypeUi(); };

        nameBox.SetBounds(135, 82, 480, 28);
        targetBox.SetBounds(135, 138, 390, 28);
        argsBox.SetBounds(135, 194, 480, 28);
        nameBox.Text = item.name ?? "";
        targetBox.Text = item.target ?? "";
        argsBox.Text = item.args ?? "";
        browseButton.Text = L("Browse", "\u6d4f\u89c8");
        browseButton.SetBounds(535, 138, 80, 28);
        browseButton.Click += delegate
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "Programs (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK) targetBox.Text = dialog.FileName;
            }
        };

        argsHintLabel.ForeColor = Color.FromArgb(105, 114, 130);
        argsHintLabel.AutoSize = false;
        argsHintLabel.SetBounds(135, 226, 480, 44);
        argsHintLabel.Text = L("Extra options passed to an app. Usually leave blank; websites ignore this field.", "\u4f20\u7ed9\u8f6f\u4ef6\u7684\u989d\u5916\u547d\u4ee4\u9009\u9879\uff0c\u4e00\u822c\u7559\u7a7a\uff1b\u7f51\u9875\u4e0d\u4f7f\u7528\u8fd9\u4e00\u9879\u3002");
        enabledBox.Text = L("Enabled in one-click launch", "\u5728\u4e00\u952e\u542f\u52a8\u4e2d\u542f\u7528");
        enabledBox.Checked = item.enabled;
        enabledBox.SetBounds(135, 266, 260, 24);

        Controls.Add(typeBox);
        Controls.Add(nameBox);
        Controls.Add(targetBox);
        Controls.Add(argsBox);
        Controls.Add(browseButton);
        Controls.Add(argsHintLabel);
        Controls.Add(enabledBox);

        Button ok = new Button { Text = L("Save", "\u4fdd\u5b58"), DialogResult = DialogResult.OK };
        Button cancel = new Button { Text = L("Cancel", "\u53d6\u6d88"), DialogResult = DialogResult.Cancel };
        ok.SetBounds(435, 292, 80, 32);
        cancel.SetBounds(535, 292, 80, 32);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        UpdateTypeUi();
    }

    private string L(string en, string zh)
    {
        return chinese ? zh : en;
    }

    private string SelectedType()
    {
        return typeBox.SelectedIndex == 1 ? "app" : "url";
    }

    private void UpdateTypeUi()
    {
        bool app = SelectedType() == "app";
        targetLabel.Text = app ? L("App Path", "\u8f6f\u4ef6\u8def\u5f84") : L("Website URL", "\u7f51\u9875\u5730\u5740");
        browseButton.Visible = app;
        argsBox.Enabled = app;
        if (!app) argsBox.Text = "";
        if (app && targetBox.Text == "https://") targetBox.Text = "";
        if (!app && String.IsNullOrWhiteSpace(targetBox.Text)) targetBox.Text = "https://";
    }

    private Label AddLabel(string text, int x, int y)
    {
        Label label = new Label { Text = text, AutoSize = true, Location = new Point(x, y + 4) };
        Controls.Add(label);
        return label;
    }

    public static bool Show(IWin32Window owner, LaunchItem item, bool chinese, string iconDir)
    {
        using (EditDialog dialog = new EditDialog(item, chinese))
        {
            if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
            item.type = dialog.SelectedType();
            item.name = dialog.nameBox.Text.Trim();
            item.target = dialog.targetBox.Text.Trim();
            item.args = item.type == "app" ? dialog.argsBox.Text.Trim() : "";
            item.enabled = dialog.enabledBox.Checked;
            return !String.IsNullOrWhiteSpace(item.target) && item.target.Trim() != "https://";
        }
    }
}

public class PromptDialog : Form
{
    private readonly TextBox valueBox = new TextBox();
    private readonly bool chinese;

    private PromptDialog(string title, string labelText, string value, bool chinese)
    {
        this.chinese = chinese;
        Text = title;
        Size = new Size(460, 180);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        Label label = new Label { Text = labelText, AutoSize = true, Location = new Point(24, 26) };
        valueBox.SetBounds(120, 22, 290, 28);
        valueBox.Text = value ?? "";
        Controls.Add(label);
        Controls.Add(valueBox);

        Button ok = new Button { Text = L("OK", "\u786e\u5b9a"), DialogResult = DialogResult.OK };
        Button cancel = new Button { Text = L("Cancel", "\u53d6\u6d88"), DialogResult = DialogResult.Cancel };
        ok.SetBounds(230, 86, 80, 32);
        cancel.SetBounds(330, 86, 80, 32);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private string L(string en, string zh)
    {
        return chinese ? zh : en;
    }

    public static string Show(IWin32Window owner, string title, string label, string value, bool chinese)
    {
        using (PromptDialog dialog = new PromptDialog(title, label, value, chinese))
        {
            if (dialog.ShowDialog(owner) != DialogResult.OK) return "";
            return dialog.valueBox.Text.Trim();
        }
    }
}

public class SettingsDialog : Form
{
    private readonly ComboBox languageBox = new ComboBox();
    private readonly CheckBox minimizeBox = new CheckBox();
    private readonly CheckBox closeBox = new CheckBox();
    private readonly CheckBox startupBox = new CheckBox();
    private readonly LauncherSettings settings;
    private readonly bool chinese;

    private SettingsDialog(LauncherSettings settings, bool chinese)
    {
        this.settings = settings;
        this.chinese = chinese;
        Text = L("Settings", "\u8bbe\u7f6e");
        Size = new Size(520, 310);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        Label languageLabel = new Label { Text = L("Language", "\u8bed\u8a00"), AutoSize = true, Location = new Point(28, 32) };
        languageBox.DropDownStyle = ComboBoxStyle.DropDownList;
        languageBox.Items.Add("\u4e2d\u6587");
        languageBox.Items.Add("English");
        languageBox.SetBounds(140, 28, 220, 28);
        languageBox.SelectedIndex = String.Equals(settings.language, "en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        minimizeBox.Text = L("Minimize Workspace Launcher after a successful launch", "\u4e00\u952e\u542f\u52a8\u6210\u529f\u540e\u6700\u5c0f\u5316\u542f\u52a8\u5668");
        closeBox.Text = L("Close Workspace Launcher after a successful launch", "\u4e00\u952e\u542f\u52a8\u6210\u529f\u540e\u5173\u95ed\u542f\u52a8\u5668");
        startupBox.Text = L("Start Workspace Launcher with Windows", "\u5f00\u673a\u81ea\u52a8\u542f\u52a8 Workspace Launcher");
        minimizeBox.SetBounds(28, 76, 430, 28);
        closeBox.SetBounds(28, 116, 430, 28);
        startupBox.SetBounds(28, 156, 430, 28);
        minimizeBox.Checked = settings.minimizeAfterLaunch;
        closeBox.Checked = settings.closeAfterLaunch;
        startupBox.Checked = settings.startWithWindows;
        minimizeBox.CheckedChanged += delegate { if (minimizeBox.Checked) closeBox.Checked = false; };
        closeBox.CheckedChanged += delegate { if (closeBox.Checked) minimizeBox.Checked = false; };
        Controls.Add(languageLabel);
        Controls.Add(languageBox);
        Controls.Add(minimizeBox);
        Controls.Add(closeBox);
        Controls.Add(startupBox);

        Button ok = new Button { Text = L("Save", "\u4fdd\u5b58"), DialogResult = DialogResult.OK };
        Button cancel = new Button { Text = L("Cancel", "\u53d6\u6d88"), DialogResult = DialogResult.Cancel };
        ok.SetBounds(300, 210, 80, 32);
        cancel.SetBounds(400, 210, 80, 32);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private string L(string en, string zh)
    {
        return chinese ? zh : en;
    }

    public static bool Show(IWin32Window owner, LauncherSettings settings, bool chinese)
    {
        if (settings == null) settings = new LauncherSettings();
        using (SettingsDialog dialog = new SettingsDialog(settings, chinese))
        {
            if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
            settings.minimizeAfterLaunch = dialog.minimizeBox.Checked;
            settings.closeAfterLaunch = dialog.closeBox.Checked;
            settings.startWithWindows = dialog.startupBox.Checked;
            settings.language = dialog.languageBox.SelectedIndex == 1 ? "en" : "zh";
            return true;
        }
    }
}
