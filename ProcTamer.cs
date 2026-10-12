using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ProcTamer
{
    class Rule
    {
        public bool Enabled;
        public string Name;
        public bool Eco;
        public bool Ifeo;
        public int Kbps;

        // 运行时状态，不写入配置
        public int Running;
        public string EcoState = "";
        public string IfeoState = "";
        public long RelayLast;
        public double RelayRate;
    }

    // 中转里一个进程名对应一份：限速值、令牌桶、连接数和累计上传量
    class RelayEntry
    {
        public volatile int Rate;   // 字节/秒，0 = 不限
        public int Conns;
        public int Accepted;
        public long Total;

        readonly object gate = new object();
        double tokens;
        long last;
        bool started;

        // 申请上传 want 字节。返回这次能发的字节数；返回 0 表示桶里不够，waitMs 是该等的毫秒数
        public int Take(int want, out int waitMs)
        {
            waitMs = 0;
            int rate = Rate;
            if (rate <= 0) return want;
            lock (gate)
            {
                long now = Stopwatch.GetTimestamp();
                double cap = Math.Max(rate / 4.0, 4096);
                if (!started) { tokens = cap; started = true; }
                else tokens = Math.Min(cap, tokens + (now - last) * (double)rate / Stopwatch.Frequency);
                last = now;
                // 攒够一小块再放行，免得把数据切成很多几字节的小包
                int need = Math.Min(want, Math.Max(512, rate / 50));
                if (tokens >= need)
                {
                    int n = (int)Math.Min(want, tokens);
                    tokens -= n;
                    return n;
                }
                waitMs = Math.Max(5, Math.Min(1000, (int)((need - tokens) * 1000 / rate) + 1));
                return 0;
            }
        }
    }

    // 本机限速中转：只在 127.0.0.1 上提供 SOCKS5，仅支持 TCP CONNECT。
    // 代理软件把指定进程的 TCP 连接转到这里，SOCKS5 用户名就是进程名；
    // 上传方向按该进程的限速值过令牌桶，下载方向不限。
    // TUN 模式下系统自带的按进程限速全部失效，所以改在这里限
    class Relay
    {
        public const int Port = 17877;
        const int HandshakeMs = 10000;
        const int ConnectMs = 10000;
        const int GraceMs = 60000;

        readonly Dictionary<string, RelayEntry> entries =
            new Dictionary<string, RelayEntry>(StringComparer.OrdinalIgnoreCase);
        TcpListener listener;
        volatile bool stopping;

        public string Error;   // 监听失败的原因，正常为 null

        public RelayEntry Entry(string name)
        {
            lock (entries)
            {
                RelayEntry e;
                if (!entries.TryGetValue(name, out e)) { e = new RelayEntry(); entries[name] = e; }
                return e;
            }
        }

        public void Start()
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.ExclusiveAddressUse = true;
                listener.Start(64);
            }
            catch (Exception ex)
            {
                listener = null;
                Error = ex.Message;
                return;
            }
            Task.Run(new Func<Task>(AcceptLoop));
        }

        public void Stop()
        {
            stopping = true;
            if (listener != null) { try { listener.Stop(); } catch { } }
        }

        async Task AcceptLoop()
        {
            while (!stopping)
            {
                TcpClient c = null;
                try { c = await listener.AcceptTcpClientAsync(); } catch { }
                if (c == null)
                {
                    if (stopping) return;
                    await Task.Delay(200);
                    continue;
                }
                Task t = Handle(c);
            }
        }

        async Task Handle(TcpClient client)
        {
            Socket remote = null;
            RelayEntry entry = null;
            bool ready = false;
            try
            {
                client.NoDelay = true;
                NetworkStream cs = client.GetStream();
                // 连上来却迟迟不完成握手的，到时间直接断开
                Task guard = Task.Delay(HandshakeMs).ContinueWith(delegate
                {
                    if (!Volatile.Read(ref ready)) { try { client.Close(); } catch { } }
                });

                byte[] b = new byte[600];
                // 协商：只接受用户名/密码方式，用户名用来传进程名，密码不检查
                await ReadExact(cs, b, 2);
                if (b[0] != 5) return;
                int n = b[1];
                await ReadExact(cs, b, n);
                if (Array.IndexOf(b, (byte)2, 0, n) < 0)
                {
                    await cs.WriteAsync(new byte[] { 5, 0xFF }, 0, 2);
                    return;
                }
                await cs.WriteAsync(new byte[] { 5, 2 }, 0, 2);
                await ReadExact(cs, b, 2);
                if (b[0] != 1) return;
                int ulen = b[1];
                await ReadExact(cs, b, ulen + 1);
                string user = Encoding.UTF8.GetString(b, 0, ulen).Trim();
                await ReadExact(cs, b, b[ulen]);
                await cs.WriteAsync(new byte[] { 1, 0 }, 0, 2);

                // 请求：VER CMD RSV ATYP 地址 端口
                await ReadExact(cs, b, 4);
                if (b[0] != 5) return;
                byte cmd = b[1], atyp = b[3];
                IPAddress[] addrs;
                if (atyp == 1 || atyp == 4)
                {
                    byte[] raw = new byte[atyp == 1 ? 4 : 16];
                    await ReadExact(cs, raw, raw.Length);
                    addrs = new IPAddress[] { new IPAddress(raw) };
                }
                else if (atyp == 3)
                {
                    await ReadExact(cs, b, 1);
                    int hlen = b[0];
                    await ReadExact(cs, b, hlen);
                    string host = Encoding.ASCII.GetString(b, 0, hlen);
                    IPAddress ip;
                    if (IPAddress.TryParse(host, out ip)) addrs = new IPAddress[] { ip };
                    else
                    {
                        addrs = null;
                        try { addrs = await Dns.GetHostAddressesAsync(host); } catch { }
                    }
                }
                else
                {
                    await Reply(cs, 8);
                    return;
                }
                await ReadExact(cs, b, 2);
                int port = (b[0] << 8) | b[1];
                Volatile.Write(ref ready, true);
                if (cmd != 1)
                {
                    await Reply(cs, 7);
                    return;
                }

                if (addrs != null)
                {
                    // 先试 IPv4，最多试 3 个地址
                    foreach (IPAddress a in addrs.OrderBy(delegate(IPAddress x)
                        { return x.AddressFamily == AddressFamily.InterNetwork ? 0 : 1; }).Take(3))
                    {
                        Socket s = new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        bool ok = false;
                        try
                        {
                            Task ct = Task.Factory.FromAsync(s.BeginConnect, s.EndConnect, a, port, null);
                            if (await Task.WhenAny(ct, Task.Delay(ConnectMs)) == ct) { await ct; ok = true; }
                            else Forget(ct);
                        }
                        catch { }
                        if (ok) { remote = s; break; }
                        s.Close();
                    }
                }
                if (remote == null)
                {
                    await Reply(cs, 4);
                    return;
                }
                remote.NoDelay = true;
                KeepAlive(remote);
                KeepAlive(client.Client);
                await Reply(cs, 0);

                entry = Entry(user);
                Interlocked.Increment(ref entry.Conns);
                Interlocked.Increment(ref entry.Accepted);
                NetworkStream rs = new NetworkStream(remote, false);
                Task up = Pump(cs, rs, remote, entry);
                Task down = Pump(rs, cs, client.Client, null);
                Task first = await Task.WhenAny(up, down);
                // 一个方向结束后，另一个方向最多再等一会儿，免得对端不关连接时一直挂着
                await Task.WhenAny(first == up ? down : up, Task.Delay(GraceMs));
            }
            catch { }
            finally
            {
                try { client.Close(); } catch { }
                if (remote != null) { try { remote.Close(); } catch { } }
                if (entry != null) Interlocked.Decrement(ref entry.Conns);
            }
        }

        // limit 不为 null 的是上传方向：过令牌桶并计入累计量
        static async Task Pump(NetworkStream src, NetworkStream dst, Socket dstSocket, RelayEntry limit)
        {
            byte[] buf = new byte[16384];
            try
            {
                for (;;)
                {
                    int n = await src.ReadAsync(buf, 0, buf.Length);
                    if (n <= 0) break;
                    int off = 0;
                    while (off < n)
                    {
                        int take = n - off;
                        if (limit != null)
                        {
                            int wait;
                            take = limit.Take(take, out wait);
                            if (take == 0) { await Task.Delay(wait); continue; }
                        }
                        await dst.WriteAsync(buf, off, take);
                        off += take;
                        if (limit != null) Interlocked.Add(ref limit.Total, take);
                    }
                }
                dstSocket.Shutdown(SocketShutdown.Send);
            }
            catch { }
        }

        static async Task ReadExact(NetworkStream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = await s.ReadAsync(buf, off, count - off);
                if (n <= 0) throw new EndOfStreamException();
                off += n;
            }
        }

        static Task Reply(NetworkStream s, byte code)
        {
            return s.WriteAsync(new byte[] { 5, code, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
        }

        static void Forget(Task t)
        {
            t.ContinueWith(delegate(Task x) { Exception ignored = x.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
        }

        // 60 秒无数据后开始探测，对端消失的连接能被及时清掉
        static void KeepAlive(Socket s)
        {
            try
            {
                byte[] v = new byte[12];
                BitConverter.GetBytes(1u).CopyTo(v, 0);
                BitConverter.GetBytes(60000u).CopyTo(v, 4);
                BitConverter.GetBytes(10000u).CopyTo(v, 8);
                s.IOControl(IOControlCode.KeepAliveValues, v, null);
            }
            catch { }
        }
    }

    static class Native
    {
        const uint PROCESS_SET_INFORMATION = 0x0200;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint IDLE_PRIORITY_CLASS = 0x40;
        const uint NORMAL_PRIORITY_CLASS = 0x20;
        const int ProcessPowerThrottling = 4;
        const int ProcessIoPriority = 33;

        [StructLayout(LayoutKind.Sequential)]
        struct POWER_THROTTLING
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetPriorityClass(IntPtr h, uint cls);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessInformation(IntPtr h, int cls, ref POWER_THROTTLING info, int size);
        [DllImport("ntdll.dll")]
        static extern int NtSetInformationProcess(IntPtr h, int cls, ref int info, int len);
        [DllImport("iphlpapi.dll")]
        static extern int GetBestInterface(uint destAddr, out uint index);

        // 外网流量如果先进虚拟网卡（代理的 TUN 模式），返回那块网卡的名字，否则返回 null。
        // QoS 限速由“QoS 数据包计划程序”执行，它不绑定这类网卡，所以按程序名限速会失效
        public static string TunnelAdapter()
        {
            try
            {
                uint index;
                if (GetBestInterface(0x08080808, out index) != 0) return null;
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    IPv4InterfaceProperties v4 = ni.GetIPProperties().GetIPv4Properties();
                    if (v4 == null || v4.Index != (int)index) continue;
                    switch (ni.NetworkInterfaceType)
                    {
                        case NetworkInterfaceType.Ethernet:
                        case NetworkInterfaceType.GigabitEthernet:
                        case NetworkInterfaceType.FastEthernetT:
                        case NetworkInterfaceType.FastEthernetFx:
                        case NetworkInterfaceType.Wireless80211:
                        case NetworkInterfaceType.Ppp:
                            return null;
                        default:
                            return ni.Name;
                    }
                }
            }
            catch { }
            return null;
        }

        // on=true：低优先级 + 效能模式(EcoQoS) + 极低 I/O 优先级；on=false：还原为系统默认
        public static bool Tame(int pid, bool on)
        {
            IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                bool ok = SetPriorityClass(h, on ? IDLE_PRIORITY_CLASS : NORMAL_PRIORITY_CLASS);
                POWER_THROTTLING s;
                s.Version = 1;
                s.ControlMask = on ? 1u : 0u;
                s.StateMask = on ? 1u : 0u;
                SetProcessInformation(h, ProcessPowerThrottling, ref s, Marshal.SizeOf(typeof(POWER_THROTTLING)));
                int io = on ? 0 : 2;
                NtSetInformationProcess(h, ProcessIoPriority, ref io, 4);
                return ok;
            }
            finally { CloseHandle(h); }
        }
    }

    class AddDialog : Form
    {
        ComboBox box;
        public string Result;

        public AddDialog(Font font)
        {
            Text = "添加进程";
            Font = font;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(380, 120);

            Label lb = new Label();
            lb.Text = "从运行中的进程里选，或直接输入程序名（如 xxx.exe）：";
            lb.SetBounds(12, 12, 356, 20);

            box = new ComboBox();
            box.SetBounds(12, 38, 356, 26);
            box.DropDownStyle = ComboBoxStyle.DropDown;
            box.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            box.AutoCompleteSource = AutoCompleteSource.ListItems;
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Process p in Process.GetProcesses())
            {
                try { names.Add(p.ProcessName + ".exe"); } catch { }
                p.Dispose();
            }
            box.Items.AddRange(names.OrderBy(delegate(string n) { return n; }, StringComparer.OrdinalIgnoreCase).ToArray());

            Button ok = new Button();
            ok.Text = "添加"; ok.SetBounds(196, 80, 82, 28);
            ok.Click += delegate
            {
                string t = box.Text.Trim();
                if (t.Length == 0) return;
                if (t.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    MessageBox.Show(this, "只填程序名，不要带路径。", Text);
                    return;
                }
                if (!t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) t += ".exe";
                Result = t;
                DialogResult = DialogResult.OK;
            };
            Button cancel = new Button();
            cancel.Text = "取消"; cancel.SetBounds(286, 80, 82, 28);
            cancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lb, box, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
        }
    }

    class MainForm : Form
    {
        const string AppName = "ProcTamer";
        const string QosPrefix = "ProcTamer_";
        const string IfeoRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        const int ColEnabled = 0, ColName = 1, ColEco = 2, ColIfeo = 3, ColKbps = 4, ColStatus = 5;

        readonly string configPath;
        readonly bool isAdmin;
        readonly string shotPath;
        bool startHidden;

        List<Rule> rules = new List<Rule>();
        Dictionary<int, string> tamed = new Dictionary<int, string>();
        HashSet<string> qosOn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool qosPending;
        volatile int qosGen;
        readonly object psLock = new object();
        readonly Relay relay = new Relay();
        readonly Stopwatch relayClock = Stopwatch.StartNew();

        bool paused;
        bool loading;
        bool reallyExit;

        DataGridView grid;
        ToolStripButton btnPause, btnAuto, btnElevate;
        ToolStripStatusLabel lblStatus;
        NotifyIcon tray;
        ToolStripMenuItem miPause;
        System.Windows.Forms.Timer timer;
        Icon icoOn, icoPaused, icoLimited;

        public MainForm(bool hidden, string shot, int shotDelay)
        {
            startHidden = hidden;
            shotPath = shot;
            configPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "rules.tsv");
            isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            if (isAdmin) { try { Process.EnterDebugMode(); } catch { } }

            icoOn = MakeIcon(Color.FromArgb(46, 160, 67));
            icoPaused = MakeIcon(Color.FromArgb(140, 140, 140));
            icoLimited = MakeIcon(Color.FromArgb(219, 139, 11));

            BuildUi();
            LoadConfig();
            FillGrid();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += delegate { Tick(); };
            timer.Start();

            // 后台线程要用 BeginInvoke 回到界面线程，窗口没显示（--tray）时也得先有句柄
            if (!IsHandleCreated) CreateHandle();
            relay.Start();
            ApplyAll(false);
            QueryAutostart();

            if (shotPath != null)
            {
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = shotDelay;
                t.Tick += delegate
                {
                    t.Stop();
                    using (Bitmap bmp = new Bitmap(Width, Height))
                    {
                        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                        bmp.Save(shotPath);
                    }
                    ExitApp();
                };
                t.Start();
            }
        }

        // ---------- 界面 ----------

        void BuildUi()
        {
            Text = AppName + (isAdmin ? "" : "（非管理员）");
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(860, 380);
            MinimumSize = new Size(700, 260);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = icoOn;

            ToolStrip bar = new ToolStrip();
            bar.GripStyle = ToolStripGripStyle.Hidden;
            bar.RenderMode = ToolStripRenderMode.System;
            bar.Padding = new Padding(6, 4, 6, 4);
            ToolStripButton add = TextButton("添加进程");
            add.Click += delegate { AddRule(); };
            ToolStripButton del = TextButton("删除");
            del.Click += delegate { DeleteRule(); };
            btnPause = TextButton("暂停全部");
            btnPause.Click += delegate { TogglePause(); };
            btnAuto = TextButton("开机自启");
            btnAuto.ToolTipText = "登录后以管理员权限静默启动到托盘";
            btnAuto.Click += delegate { ToggleAutostart(); };
            btnElevate = TextButton("以管理员身份重启");
            btnElevate.Visible = !isAdmin;
            btnElevate.Click += delegate { RestartElevated(); };
            bar.Items.AddRange(new ToolStripItem[] {
                add, del, new ToolStripSeparator(), btnPause, new ToolStripSeparator(), btnAuto, btnElevate });

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.BorderStyle = BorderStyle.None;
            grid.BackgroundColor = Color.White;
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = Color.FromArgb(232, 232, 232);
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(246, 246, 246);
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 235, 250);
            grid.DefaultCellStyle.SelectionForeColor = Color.Black;
            grid.ShowCellToolTips = true;

            float s;
            using (Graphics g = CreateGraphics()) s = g.DpiX / 96F;
            grid.RowTemplate.Height = (int)(28 * s);
            grid.ColumnHeadersHeight = (int)(30 * s);

            grid.Columns.Add(CheckCol("启用", 50, s, "勾上这条规则才生效"));
            DataGridViewTextBoxColumn name = new DataGridViewTextBoxColumn();
            name.HeaderText = "进程"; name.ReadOnly = true; name.Width = (int)(190 * s);
            grid.Columns.Add(name);
            grid.Columns.Add(CheckCol("效能模式", 76, s, "检测到进程运行时设为：低优先级 + 效能模式 + 极低 I/O 优先级"));
            grid.Columns.Add(CheckCol("注册表优先级", 96, s, "让系统在进程启动时直接给低优先级。用于会“拒绝访问”的反作弊进程，对下次启动的进程生效"));
            DataGridViewTextBoxColumn kb = new DataGridViewTextBoxColumn();
            kb.HeaderText = "限上传 KB/s"; kb.Width = (int)(92 * s);
            kb.ToolTipText = "0 = 不限速";
            kb.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Columns.Add(kb);
            DataGridViewTextBoxColumn st = new DataGridViewTextBoxColumn();
            st.HeaderText = "状态"; st.ReadOnly = true;
            st.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            st.DefaultCellStyle.ForeColor = Color.FromArgb(90, 90, 90);
            grid.Columns.Add(st);
            foreach (DataGridViewColumn c in grid.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;

            grid.CurrentCellDirtyStateChanged += delegate
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellValueChanged += OnCellChanged;
            grid.DataError += delegate(object o, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };

            StatusStrip status = new StatusStrip();
            status.SizingGrip = false;
            lblStatus = new ToolStripStatusLabel();
            lblStatus.Spring = true;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            status.Items.Add(lblStatus);

            Controls.Add(grid);
            Controls.Add(bar);
            Controls.Add(status);

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem miShow = new ToolStripMenuItem("显示主窗口");
            miShow.Click += delegate { ShowWindow(); };
            miPause = new ToolStripMenuItem("暂停全部");
            miPause.Click += delegate { TogglePause(); };
            ToolStripMenuItem miExit = new ToolStripMenuItem("退出并还原");
            miExit.Click += delegate { ExitApp(); };
            menu.Items.AddRange(new ToolStripItem[] { miShow, miPause, new ToolStripSeparator(), miExit });

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.Icon = icoOn;
            tray.Text = AppName;
            tray.Visible = shotPath == null;
            tray.DoubleClick += delegate { ShowWindow(); };
        }

        static ToolStripButton TextButton(string text)
        {
            ToolStripButton b = new ToolStripButton(text);
            b.DisplayStyle = ToolStripItemDisplayStyle.Text;
            b.Padding = new Padding(6, 2, 6, 2);
            return b;
        }

        static DataGridViewCheckBoxColumn CheckCol(string header, int width, float scale, string tip)
        {
            DataGridViewCheckBoxColumn c = new DataGridViewCheckBoxColumn();
            c.HeaderText = header;
            c.Width = (int)(width * scale);
            c.ToolTipText = tip;
            return c;
        }

        static Icon MakeIcon(Color c)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, 2, 2, 28, 28);
                    using (Pen p = new Pen(Color.White, 3F))
                    {
                        p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                        g.DrawLine(p, 10, 12, 16, 20);
                        g.DrawLine(p, 16, 20, 22, 12);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        // 带 --tray 启动时不显示窗口，只进托盘
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            Cleanup();
            base.OnFormClosing(e);
        }

        void ShowWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        void ExitApp()
        {
            reallyExit = true;
            Close();
        }

        // ---------- 配置 ----------

        void LoadConfig()
        {
            rules.Clear();
            if (File.Exists(configPath))
            {
                foreach (string line in File.ReadAllLines(configPath, Encoding.UTF8))
                {
                    string[] f = line.Split('\t');
                    if (f.Length < 5 || f[1].Trim().Length == 0) continue;
                    Rule r = new Rule();
                    r.Enabled = f[0] == "1";
                    r.Name = f[1].Trim();
                    r.Eco = f[2] == "1";
                    r.Ifeo = f[3] == "1";
                    int.TryParse(f[4], out r.Kbps);
                    rules.Add(r);
                }
                return;
            }
            // 首次运行的预设，全部默认不启用
            rules.Add(Preset("SGuard64.exe", true, true, 0));
            rules.Add(Preset("SGuardSvc64.exe", true, true, 0));
            rules.Add(Preset("完美世界竞技平台.exe", false, false, 100));
            rules.Add(Preset("pwautil64.exe", false, false, 100));
            rules.Add(Preset("WmgpOptimizer_x64.exe", false, false, 100));
            rules.Add(Preset("5EClient.exe", false, false, 100));
            rules.Add(Preset("Bucky64.exe", false, false, 100));
        }

        static Rule Preset(string name, bool eco, bool ifeo, int kbps)
        {
            Rule r = new Rule();
            r.Name = name; r.Eco = eco; r.Ifeo = ifeo; r.Kbps = kbps;
            return r;
        }

        void SaveConfig()
        {
            StringBuilder sb = new StringBuilder();
            foreach (Rule r in rules)
                sb.AppendLine(string.Join("\t", new string[] {
                    r.Enabled ? "1" : "0", r.Name, r.Eco ? "1" : "0", r.Ifeo ? "1" : "0", r.Kbps.ToString() }));
            try { File.WriteAllText(configPath, sb.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { lblStatus.Text = "配置保存失败：" + ex.Message; }
        }

        void FillGrid()
        {
            loading = true;
            grid.Rows.Clear();
            foreach (Rule r in rules)
            {
                int i = grid.Rows.Add(r.Enabled, r.Name, r.Eco, r.Ifeo, r.Kbps.ToString(), "");
                grid.Rows[i].Tag = r;
            }
            loading = false;
        }

        void OnCellChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (loading || e.RowIndex < 0) return;
            DataGridViewRow row = grid.Rows[e.RowIndex];
            Rule r = (Rule)row.Tag;
            r.Enabled = Convert.ToBoolean(row.Cells[ColEnabled].Value);
            r.Eco = Convert.ToBoolean(row.Cells[ColEco].Value);
            r.Ifeo = Convert.ToBoolean(row.Cells[ColIfeo].Value);
            int kb;
            int.TryParse(Convert.ToString(row.Cells[ColKbps].Value), out kb);
            r.Kbps = Math.Max(0, Math.Min(kb, 1000000));
            if (e.ColumnIndex == ColKbps)
            {
                loading = true;
                row.Cells[ColKbps].Value = r.Kbps.ToString();
                loading = false;
            }
            ApplyAll(true);
        }

        void AddRule()
        {
            using (AddDialog d = new AddDialog(Font))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                foreach (Rule x in rules)
                    if (string.Equals(x.Name, d.Result, StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show(this, "列表里已经有 " + d.Result + " 了。", AppName);
                        return;
                    }
                rules.Add(Preset(d.Result, true, false, 0));
            }
            FillGrid();
            ApplyAll(true);
        }

        void DeleteRule()
        {
            if (grid.CurrentRow == null) return;
            Rule r = (Rule)grid.CurrentRow.Tag;
            if (MessageBox.Show(this, "删除 " + r.Name + " 这条规则？", AppName,
                    MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            if (isAdmin) SetIfeo(r.Name, false);
            relay.Entry(r.Name).Rate = 0;
            rules.Remove(r);
            FillGrid();
            ApplyAll(true);
        }

        void TogglePause()
        {
            paused = !paused;
            ApplyAll(false);
        }

        // ---------- 应用规则 ----------

        bool Active(Rule r) { return r.Enabled && !paused; }

        void ApplyAll(bool save)
        {
            if (save) SaveConfig();
            SyncIfeo();
            SyncQos();
            SyncRelay();
            Tick();
        }

        // 规则停用或暂停时中转照常转发，只是不限速
        void SyncRelay()
        {
            foreach (Rule r in rules)
                relay.Entry(r.Name).Rate = Active(r) && r.Kbps > 0 ? r.Kbps * 1000 : 0;
        }

        void MeasureRelay()
        {
            double secs = relayClock.Elapsed.TotalSeconds;
            if (secs < 1) return;
            relayClock.Restart();
            foreach (Rule r in rules)
            {
                long total = Interlocked.Read(ref relay.Entry(r.Name).Total);
                r.RelayRate = (total - r.RelayLast) / secs;
                r.RelayLast = total;
            }
        }

        static string FormatSize(double bytes)
        {
            if (bytes < 1e6) return (bytes / 1e3).ToString("0.#") + " KB";
            if (bytes < 1e9) return (bytes / 1e6).ToString("0.##") + " MB";
            return (bytes / 1e9).ToString("0.##") + " GB";
        }

        void Tick()
        {
            MeasureRelay();
            HashSet<int> keep = new HashSet<int>();
            foreach (Rule r in rules)
            {
                Process[] ps = Process.GetProcessesByName(BaseName(r.Name));
                r.Running = ps.Length;
                bool act = Active(r) && r.Eco;
                int ok = 0, fail = 0;
                foreach (Process p in ps)
                {
                    if (act)
                    {
                        if (Native.Tame(p.Id, true)) { ok++; tamed[p.Id] = r.Name; keep.Add(p.Id); }
                        else fail++;
                    }
                    p.Dispose();
                }
                if (!act || ps.Length == 0) r.EcoState = "";
                else if (fail == 0) r.EcoState = "效能✔";
                else if (ok == 0) r.EcoState = "效能✘ 拒绝访问";
                else r.EcoState = "效能 部分✔";
            }
            Restore(keep);
            RefreshStatus();
        }

        // 把不再受规则管的进程恢复成普通优先级
        void Restore(HashSet<int> keep)
        {
            foreach (int pid in tamed.Keys.ToList())
            {
                if (keep != null && keep.Contains(pid)) continue;
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                        if (string.Equals(p.ProcessName, BaseName(tamed[pid]), StringComparison.OrdinalIgnoreCase))
                            Native.Tame(pid, false);
                }
                catch { }
                tamed.Remove(pid);
            }
        }

        static string BaseName(string exe)
        {
            return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe;
        }

        void SyncIfeo()
        {
            foreach (Rule r in rules)
            {
                bool want = Active(r) && r.Ifeo;
                if (!isAdmin) { r.IfeoState = want ? "注册表✘ 需管理员" : ""; continue; }
                try
                {
                    bool done = SetIfeo(r.Name, want);
                    r.IfeoState = !want ? "" : done ? "注册表✔" : "注册表✘ 已有其他设置";
                }
                catch (Exception ex) { r.IfeoState = want ? "注册表✘ " + ex.Message : ""; }
            }
        }

        // 只动带有本程序标记的 PerfOptions，别人写的不碰。返回 on 时是否写入成功
        static bool SetIfeo(string exe, bool on)
        {
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey root = hklm.OpenSubKey(IfeoRoot, true))
            {
                if (on)
                {
                    using (RegistryKey k = root.CreateSubKey(exe + @"\PerfOptions"))
                    {
                        if (k.ValueCount > 0 && k.GetValue(AppName) == null) return false;
                        k.SetValue("CpuPriorityClass", 1, RegistryValueKind.DWord);
                        k.SetValue("IoPriority", 0, RegistryValueKind.DWord);
                        k.SetValue(AppName, 1, RegistryValueKind.DWord);
                    }
                    return true;
                }
                bool empty;
                using (RegistryKey e = root.OpenSubKey(exe, true))
                {
                    if (e == null) return false;
                    using (RegistryKey k = e.OpenSubKey("PerfOptions"))
                        if (k == null || k.GetValue(AppName) == null) return false;
                    e.DeleteSubKey("PerfOptions");
                    empty = e.SubKeyCount == 0 && e.ValueCount == 0;
                }
                if (empty) root.DeleteSubKey(exe, false);
                return false;
            }
        }

        string BuildQosScript(Dictionary<string, string> map)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference='SilentlyContinue'; $ProgressPreference='SilentlyContinue'");
            sb.AppendLine("Get-NetQosPolicy -PolicyStore ActiveStore | Where-Object { $_.Name -like '" + QosPrefix +
                "*' } | ForEach-Object { Remove-NetQosPolicy -Name $_.Name -PolicyStore ActiveStore -Confirm:$false }");
            int i = 0;
            foreach (Rule r in rules)
            {
                if (map == null || !Active(r) || r.Kbps <= 0) continue;
                string name = QosPrefix + (i++);
                map[name] = r.Name;
                sb.AppendLine("New-NetQosPolicy -Name '" + name + "' -AppPathNameMatchCondition '" + r.Name.Replace("'", "''") +
                    "' -ThrottleRateActionBitsPerSecond " + ((long)r.Kbps * 8000) +
                    " -NetworkProfile All -PolicyStore ActiveStore | Out-Null");
            }
            sb.AppendLine("Get-NetQosPolicy -PolicyStore ActiveStore | Where-Object { $_.Name -like '" + QosPrefix +
                "*' } | ForEach-Object { $_.Name }");
            return sb.ToString();
        }

        void SyncQos()
        {
            if (!isAdmin) return;
            Dictionary<string, string> map = new Dictionary<string, string>();
            string script = BuildQosScript(map);
            int gen = ++qosGen;
            qosPending = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string output;
                lock (psLock)
                {
                    if (gen != qosGen) return;
                    output = RunPs(script);
                }
                HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string exe;
                    if (map.TryGetValue(line.Trim(), out exe)) set.Add(exe);
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (gen != qosGen) return;
                        qosOn = set;
                        qosPending = false;
                        RefreshStatus();
                    });
                }
                catch { }
            });
        }

        static string RunPs(string script)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(30000);
                    return o;
                }
            }
            catch { return ""; }
        }

        void RefreshStatus()
        {
            int activeCount = 0;
            bool limiting = false;
            string tunnel = Native.TunnelAdapter();
            loading = true;
            foreach (DataGridViewRow row in grid.Rows)
            {
                Rule r = (Rule)row.Tag;
                string text;
                if (!r.Enabled) text = "未启用";
                else if (paused) text = "已暂停";
                else
                {
                    activeCount++;
                    List<string> parts = new List<string>();
                    parts.Add(r.Running == 0 ? "未运行" : "运行中×" + r.Running);
                    if (r.EcoState.Length > 0) parts.Add(r.EcoState);
                    if (r.IfeoState.Length > 0) parts.Add(r.IfeoState);
                    RelayEntry re = relay.Entry(r.Name);
                    bool relayed = re.Accepted > 0;
                    if (r.Kbps > 0)
                    {
                        limiting = true;
                        if (tunnel != null)
                        {
                            if (relay.Error != null) parts.Add("限速✘ 中转未启动");
                            else if (!relayed) parts.Add("限速✘ 流量走 " + tunnel + "（TUN），等待中转连接");
                        }
                        else if (!isAdmin) { if (!relayed) parts.Add("限速✘ 需管理员"); }
                        else if (qosPending) parts.Add("限速 设置中…");
                        else parts.Add(qosOn.Contains(r.Name) ? "限速✔ " + r.Kbps + "KB/s" : "限速✘ 未生效");
                    }
                    if (relayed)
                        parts.Add("中转 " + re.Conns + " 连接  ↑ " + (r.RelayRate / 1e3).ToString("0.#") + " KB/s" +
                            (r.Kbps > 0 ? "（限 " + r.Kbps + "）" : "（不限）") + "  累计 " + FormatSize(Interlocked.Read(ref re.Total)));
                    text = string.Join("  ·  ", parts.ToArray());
                }
                if (!text.Equals(row.Cells[ColStatus].Value)) row.Cells[ColStatus].Value = text;
            }
            loading = false;

            string state = paused ? "已暂停" : activeCount + " 条规则生效中";
            btnPause.Text = paused ? "恢复全部" : "暂停全部";
            miPause.Text = btnPause.Text;
            if (limiting && relay.Error != null)
                lblStatus.Text = state + "    限速中转未启动（127.0.0.1:" + Relay.Port + " 监听失败：" + relay.Error + "）";
            else if (limiting && tunnel != null)
                lblStatus.Text = state + "    检测到代理 TUN 模式：限上传由本机中转 127.0.0.1:" + Relay.Port +
                    " 执行，需要代理软件把这些进程的 TCP 转过来";
            else
                lblStatus.Text = state + (isAdmin ? "" : "    当前不是管理员：注册表优先级和系统 QoS 限上传不可用（经中转的限上传不受影响），效能模式只对普通进程有效");
            Icon ico = paused ? icoPaused : (isAdmin ? icoOn : icoLimited);
            if (tray.Icon != ico) { tray.Icon = ico; Icon = ico; }
            tray.Text = AppName + " - " + state;
        }

        // 退出 = 全部还原：撤限速、恢复进程优先级、删掉本程序写的注册表项
        void Cleanup()
        {
            timer.Stop();
            tray.Visible = false;
            relay.Stop();
            Restore(null);
            if (!isAdmin) return;
            foreach (Rule r in rules) { try { SetIfeo(r.Name, false); } catch { } }
            qosGen++;
            lock (psLock) RunPs(BuildQosScript(null));
        }

        // ---------- 开机自启 / 提权 ----------

        void QueryAutostart()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool on = RunPs("if (Get-ScheduledTask -TaskName '" + AppName +
                    "' -ErrorAction SilentlyContinue) { 'yes' }").Contains("yes");
                try { BeginInvoke((MethodInvoker)delegate { btnAuto.Checked = on; }); } catch { }
            });
        }

        void ToggleAutostart()
        {
            if (!isAdmin)
            {
                MessageBox.Show(this, "设置开机自启需要管理员权限，请先点“以管理员身份重启”。", AppName);
                return;
            }
            string exe = Application.ExecutablePath.Replace("'", "''");
            string script = btnAuto.Checked
                ? "Unregister-ScheduledTask -TaskName '" + AppName + "' -Confirm:$false"
                : "$u=[Security.Principal.WindowsIdentity]::GetCurrent().Name\n" +
                  "$a=New-ScheduledTaskAction -Execute '" + exe + "' -Argument '--tray'\n" +
                  "$t=New-ScheduledTaskTrigger -AtLogOn -User $u\n" +
                  "$p=New-ScheduledTaskPrincipal -UserId $u -RunLevel Highest -LogonType Interactive\n" +
                  "$s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)\n" +
                  "Register-ScheduledTask -TaskName '" + AppName + "' -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null";
            btnAuto.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                RunPs(script);
                try { BeginInvoke((MethodInvoker)delegate { btnAuto.Enabled = true; QueryAutostart(); }); } catch { }
            });
        }

        void RestartElevated()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--wait");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
            }
            catch { return; } // 用户在 UAC 里点了否
            ExitApp();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool hidden = args.Contains("--tray");
            string shot = null;
            int si = Array.IndexOf(args, "--shot");
            if (si >= 0 && si + 1 < args.Length) shot = args[si + 1];
            int shotDelay = 1500;
            int di = Array.IndexOf(args, "--shot-after");
            if (di >= 0 && di + 1 < args.Length) int.TryParse(args[di + 1], out shotDelay);

            bool created;
            // 截图调试模式不占用单实例，免得和正在运行的正式实例冲突
            using (Mutex mutex = new Mutex(true, "ProcTamer_single_instance" + (shot != null ? "_shot" : ""), out created))
            {
                if (!created)
                {
                    bool got = false;
                    if (args.Contains("--wait"))
                    {
                        try { got = mutex.WaitOne(8000); }
                        catch (AbandonedMutexException) { got = true; }
                    }
                    if (!got)
                    {
                        MessageBox.Show("ProcTamer 已经在运行，请看任务栏右下角的托盘图标。", "ProcTamer");
                        return;
                    }
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(hidden, shot, shotDelay));
                mutex.ReleaseMutex();
            }
        }
    }
}
