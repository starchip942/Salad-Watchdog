using System;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace GpuWatchdog
{
    /// <summary>Tray icon: GPU temperature on a coloured square (like the old watchdog), status, Open panel, Open log, Quit.</summary>
    sealed class Tray : ApplicationContext
    {
        readonly NotifyIcon icon = new NotifyIcon();
        readonly Control ui = new Control();
        readonly Agent agent; readonly Panel panel;
        readonly ToolStripMenuItem status = new ToolStripMenuItem { Enabled = false };
        readonly ToolStripMenuItem relay = new ToolStripMenuItem { Enabled = false };
        readonly ToolStripMenuItem salad = new ToolStripMenuItem { Enabled = false };
        readonly Timer timer = new Timer { Interval = 2000 };
        IntPtr iconHandle = IntPtr.Zero;
        string lastIconText = "";

        public Tray(Agent a, Panel p)
        {
            agent = a; panel = p;
            ui.CreateControl();
            var menu = new ContextMenuStrip();
            menu.Items.Add(status); menu.Items.Add(salad); menu.Items.Add(relay);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open panel", null, (s, e) => panel.Open()).Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add("Open log", null, (s, e) => { try { System.Diagnostics.Process.Start("notepad.exe", "\"" + Log.FilePath + "\""); } catch { } });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Quit());
            icon.ContextMenuStrip = menu;
            icon.DoubleClick += (s, e) => panel.Open();
            icon.Visible = true;
            timer.Tick += (s, e) => Refresh();
            timer.Start();
            Refresh();
        }

        public void Invoke(Action a) { try { if (ui.InvokeRequired) ui.BeginInvoke(a); else a(); } catch { } }

        public void Balloon(string title, string text) => Invoke(() => icon.ShowBalloonTip(8000, title, text, ToolTipIcon.Warning));

        void Refresh()
        {
            var r = agent.Last;
            var t = r.GpuTemp;
            var lim = Conf.S.SafetyCoreLimitC;
            var color = agent.Lock.Active ? Color.DarkRed : t == null ? Color.Gray : t >= lim ? Color.Red : t >= lim - 15 ? Color.DarkOrange : Color.ForestGreen;
            var text = agent.Lock.Active ? "!" : t == null ? "?" : ((int)Math.Round(t.Value)).ToString();
            SetIcon(text, color);
            status.Text = t == null ? "GPU temperature unavailable" : $"GPU {t:0}°C · limit {lim}°C";
            salad.Text = "Salad: " + (agent.Lock.Active ? "LOCKED OUT" : agent.SaladState.Replace('_', ' ')) + (agent.Hold && !agent.Lock.Active ? " (hold)" : "") + (agent.ActiveGames.Count > 0 ? " · " + agent.ActiveGames[0].Name : "");
            relay.Text = agent.Relay.Connected ? $"Website: connected · {agent.Relay.Viewers} watching" : "Website: not connected";
            var tip = $"GPU Watchdog · GPU {(t == null ? "?" : t.Value.ToString("0"))}°C · " + (agent.Relay.Connected ? "online" : "offline");
            icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        void SetIcon(string text, Color color)
        {
            var key = text + color.Name;
            if (key == lastIconText) return;
            lastIconText = key;
            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var font = new Font("Arial", text.Length > 2 ? 7 : 10, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.Clear(color);
                    g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                    g.DrawString(text, font, Brushes.White, new RectangleF(0, 1, 16, 16), fmt);
                }
                var h = bmp.GetHicon();
                icon.Icon = Icon.FromHandle(h);
                if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
                iconHandle = h;
            }
        }

        void Quit()
        {
            var ok = MessageBox.Show("Quit GPU Watchdog?\n\nRemote access stops and the GPU safety stop is switched off until you start it again.",
                "GPU Watchdog", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (ok != DialogResult.Yes) return;
            timer.Stop();
            icon.Visible = false;
            ExitThread();
        }

        public void ExitQuietly() { timer.Stop(); icon.Visible = false; ExitThread(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { icon.Dispose(); timer.Dispose(); ui.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
