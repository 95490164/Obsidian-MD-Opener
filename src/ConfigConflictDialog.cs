using System;
using System.Drawing;
using System.Windows.Forms;

namespace ObsidianMdOpener
{
    internal enum ConfigConflictChoice
    {
        UseIncoming,
        UseCentral
    }

    internal sealed class ConfigConflict
    {
        public string RelativePath { get; set; }
        public string IncomingVault { get; set; }
        public string CentralVault { get; set; }
        public DateTime IncomingModifiedLocal { get; set; }
        public DateTime CentralModifiedLocal { get; set; }
    }

    internal static class ConfigConflictDialog
    {
        public static ConfigConflictChoice Show(ConfigConflict conflict)
        {
            Application.EnableVisualStyles();
            using (Form form = new Form())
            using (Label title = new Label())
            using (TextBox details = new TextBox())
            using (Button incoming = new Button())
            using (Button central = new Button())
            {
                form.Text = "Obsidian 配置冲突";
                form.StartPosition = FormStartPosition.CenterScreen;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ShowInTaskbar = true;
                form.ClientSize = new Size(620, 330);

                title.Text = "检测到同一份配置在两个仓库中都被修改";
                title.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
                title.AutoSize = false;
                title.Location = new Point(22, 20);
                title.Size = new Size(570, 28);

                details.ReadOnly = true;
                details.Multiline = true;
                details.ScrollBars = ScrollBars.Vertical;
                details.BackColor = SystemColors.Window;
                details.Location = new Point(22, 58);
                details.Size = new Size(576, 190);
                details.Text =
                    "配置文件：" + conflict.RelativePath + Environment.NewLine +
                    Environment.NewLine +
                    "这次修改来自：" + conflict.IncomingVault + Environment.NewLine +
                    "修改时间：" + FormatTime(conflict.IncomingModifiedLocal) + Environment.NewLine +
                    Environment.NewLine +
                    "当前中央版本来自：" + conflict.CentralVault + Environment.NewLine +
                    "修改时间：" + FormatTime(conflict.CentralModifiedLocal) + Environment.NewLine +
                    Environment.NewLine +
                    "请选择其中一个作为后续同步版本。另一个版本不会自动合并。";

                incoming.Text = "使用这次修改";
                incoming.Location = new Point(310, 270);
                incoming.Size = new Size(138, 38);
                incoming.DialogResult = DialogResult.Yes;

                central.Text = "保留中央版本";
                central.Location = new Point(460, 270);
                central.Size = new Size(138, 38);
                central.DialogResult = DialogResult.No;

                form.Controls.Add(title);
                form.Controls.Add(details);
                form.Controls.Add(incoming);
                form.Controls.Add(central);
                form.AcceptButton = incoming;
                form.CancelButton = central;

                return form.ShowDialog() == DialogResult.Yes
                    ? ConfigConflictChoice.UseIncoming
                    : ConfigConflictChoice.UseCentral;
            }
        }

        private static string FormatTime(DateTime value)
        {
            if (value == DateTime.MinValue)
            {
                return "未知";
            }
            return value.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
