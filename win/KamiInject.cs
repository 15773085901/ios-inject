using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2)
        {
            return Cli.Run(args);
        }

        IntPtr h = GetConsoleWindow();
        if (h != IntPtr.Zero) ShowWindow(h, 0);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }
}

static class Injector
{
    public static void Run(string inputIpa, string outputIpa, string htmlPath, Action<string> log)
    {
        if (string.IsNullOrEmpty(inputIpa) || !File.Exists(inputIpa))
            throw new Exception("找不到输入 IPA：" + inputIpa);
        if (string.IsNullOrEmpty(outputIpa))
            throw new Exception("请选择输出路径");

        string fullOut = Path.GetFullPath(outputIpa);
        string outDir = Path.GetDirectoryName(fullOut);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        if (File.Exists(fullOut)) File.Delete(fullOut);

        string work = Path.Combine(Path.GetTempPath(), "KamiInject_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string zsign = ResolveTool("zsign.exe", work, log);
            string dylib = ResolveTool("libkami.dylib", work, log);

            string html = htmlPath;
            if (string.IsNullOrEmpty(html) || !File.Exists(html))
            {
                log("未指定自定义 HTML，使用内置模板");
                html = ResolveTool("index.html", work, log);
            }
            else
            {
                log("使用自定义 HTML：" + html);
            }

            string workIpa = Path.Combine(work, "input.ipa");
            log("复制 IPA 到临时目录 ...");
            File.Copy(inputIpa, workIpa, true);

            log("注入 HTML 模板 ...");
            string appPrefix = InjectHtml(workIpa, html, log);
            log("App 目录：" + appPrefix);

            log("调用 zsign 注入 dylib 并 adhoc 签名 ...");
            string args = "-a -l \"" + dylib + "\" -o \"" + fullOut + "\" \"" + workIpa + "\"";
            RunProcess(zsign, args, work, log);

            if (!File.Exists(fullOut)) throw new Exception("zsign 未生成输出文件");
            log("完成：" + fullOut);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    static string InjectHtml(string ipaPath, string htmlPath, Action<string> log)
    {
        using (ZipArchive zip = ZipFile.Open(ipaPath, ZipArchiveMode.Update))
        {
            string appPrefix = FindAppPrefix(zip);
            if (appPrefix == null) throw new Exception("不是有效的 IPA：在 Payload 下找不到 .app");

            string kamiEntry = appPrefix + "kami/index.html";
            ZipArchiveEntry old = zip.GetEntry(kamiEntry);
            if (old != null) old.Delete();

            ZipArchiveEntry entry = zip.CreateEntry(kamiEntry, CompressionLevel.Optimal);
            using (Stream es = entry.Open())
            using (FileStream fs = new FileStream(htmlPath, FileMode.Open, FileAccess.Read))
            {
                fs.CopyTo(es);
            }

            List<ZipArchiveEntry> dels = new List<ZipArchiveEntry>();
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                if (e.FullName.StartsWith(appPrefix + "_CodeSignature/", StringComparison.Ordinal))
                    dels.Add(e);
            }
            foreach (ZipArchiveEntry e in dels) e.Delete();
            if (dels.Count > 0) log("已移除旧签名文件 " + dels.Count + " 个");

            return appPrefix;
        }
    }

    static string FindAppPrefix(ZipArchive zip)
    {
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            if (e.FullName.StartsWith("Payload/", StringComparison.Ordinal) && e.FullName.EndsWith(".app/", StringComparison.Ordinal))
                return e.FullName;
        }
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            string[] parts = e.FullName.Split('/');
            if (parts.Length >= 3 && parts[0] == "Payload" && parts[1].EndsWith(".app", StringComparison.Ordinal))
                return "Payload/" + parts[1] + "/";
        }
        return null;
    }

    static string ResolveTool(string name, string work, Action<string> log)
    {
        string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string external = Path.Combine(exeDir, name);
        if (File.Exists(external))
        {
            log("使用外部文件：" + external);
            return external;
        }

        string dest = Path.Combine(work, name);
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (s == null) throw new Exception("缺少内置资源：" + name);
            using (FileStream fs = new FileStream(dest, FileMode.Create, FileAccess.Write))
            {
                s.CopyTo(fs);
            }
        }
        return dest;
    }

    static void RunProcess(string file, string args, string cwd, Action<string> log)
    {
        ProcessStartInfo psi = new ProcessStartInfo(file, args);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = cwd;

        using (Process p = new Process())
        {
            p.StartInfo = psi;
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) log(e.Data); };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) log(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception("zsign 退出码：" + p.ExitCode);
        }
    }
}

static class Cli
{
    public static int Run(string[] a)
    {
        try
        {
            string html = a.Length >= 3 ? a[2] : null;
            Injector.Run(a[0], a[1], html, Console.WriteLine);
            Console.WriteLine("OK");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
    }
}

class MainForm : Form
{
    TextBox txtIn;
    TextBox txtOut;
    TextBox txtHtml;
    TextBox logBox;
    Button btnRun;
    Button btnIn;
    Button btnOut;
    Button btnHtml;
    Label status;

    public MainForm()
    {
        Text = "卡密注入工具（本地 adhoc 版）";
        ClientSize = new Size(780, 590);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        Label l1 = new Label();
        l1.Text = "输入 IPA";
        l1.SetBounds(16, 18, 80, 22);

        txtIn = new TextBox();
        txtIn.SetBounds(100, 16, 570, 24);
        txtIn.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        btnIn = new Button();
        btnIn.Text = "选择...";
        btnIn.SetBounds(680, 14, 84, 28);
        btnIn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnIn.Click += delegate { ChooseIn(); };

        Label l2 = new Label();
        l2.Text = "输出 IPA";
        l2.SetBounds(16, 54, 80, 22);

        txtOut = new TextBox();
        txtOut.SetBounds(100, 52, 570, 24);
        txtOut.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        btnOut = new Button();
        btnOut.Text = "另存为...";
        btnOut.SetBounds(680, 50, 84, 28);
        btnOut.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnOut.Click += delegate { ChooseOut(); };

        Label l3 = new Label();
        l3.Text = "HTML(可选)";
        l3.SetBounds(16, 90, 80, 22);

        txtHtml = new TextBox();
        txtHtml.SetBounds(100, 88, 570, 24);
        txtHtml.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        btnHtml = new Button();
        btnHtml.Text = "选择...";
        btnHtml.SetBounds(680, 86, 84, 28);
        btnHtml.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnHtml.Click += delegate { ChooseHtml(); };

        btnRun = new Button();
        btnRun.Text = "开始注入（adhoc 签名）";
        btnRun.SetBounds(100, 126, 220, 38);
        btnRun.Click += delegate { StartInject(); };

        status = new Label();
        status.Text = "输出 IPA 可直接用 TrollStore 安装。";
        status.SetBounds(340, 136, 424, 22);
        status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        logBox = new TextBox();
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.SetBounds(16, 174, 748, 400);
        logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        logBox.BackColor = Color.White;

        Controls.Add(l1); Controls.Add(txtIn); Controls.Add(btnIn);
        Controls.Add(l2); Controls.Add(txtOut); Controls.Add(btnOut);
        Controls.Add(l3); Controls.Add(txtHtml); Controls.Add(btnHtml);
        Controls.Add(btnRun); Controls.Add(status); Controls.Add(logBox);
    }

    void ChooseIn()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Filter = "IPA 文件|*.ipa|所有文件|*.*";
            if (d.ShowDialog(this) == DialogResult.OK)
            {
                txtIn.Text = d.FileName;
                if (string.IsNullOrEmpty(txtOut.Text))
                {
                    string dir = Path.GetDirectoryName(d.FileName);
                    string name = Path.GetFileNameWithoutExtension(d.FileName);
                    txtOut.Text = Path.Combine(dir, name + "-injected.ipa");
                }
            }
        }
    }

    void ChooseOut()
    {
        using (SaveFileDialog d = new SaveFileDialog())
        {
            d.Filter = "IPA 文件|*.ipa";
            d.FileName = string.IsNullOrEmpty(txtOut.Text) ? "injected.ipa" : Path.GetFileName(txtOut.Text);
            if (d.ShowDialog(this) == DialogResult.OK) txtOut.Text = d.FileName;
        }
    }

    void ChooseHtml()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Filter = "HTML 文件|*.html;*.htm|所有文件|*.*";
            if (d.ShowDialog(this) == DialogResult.OK) txtHtml.Text = d.FileName;
        }
    }

    void Log(string s)
    {
        if (logBox.InvokeRequired)
        {
            logBox.BeginInvoke(new Action<string>(Log), new object[] { s });
            return;
        }
        logBox.AppendText(s + "\r\n");
    }

    void StartInject()
    {
        if (string.IsNullOrEmpty(txtIn.Text) || !File.Exists(txtIn.Text))
        {
            MessageBox.Show(this, "请选择输入 IPA", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrEmpty(txtOut.Text))
        {
            MessageBox.Show(this, "请选择输出路径", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string input = txtIn.Text.Trim();
        string output = txtOut.Text.Trim();
        string html = txtHtml.Text.Trim();

        btnRun.Enabled = false;
        logBox.Clear();
        Log("开始处理：" + input);

        Thread t = new Thread(delegate()
        {
            Exception err = null;
            try { Injector.Run(input, output, html, Log); }
            catch (Exception ex) { err = ex; }
            Done(err, output);
        });
        t.IsBackground = true;
        t.Start();
    }

    void Done(Exception err, string output)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<Exception, string>(Done), new object[] { err, output });
            return;
        }
        btnRun.Enabled = true;
        if (err != null)
        {
            Log("失败：" + err.Message);
            MessageBox.Show(this, err.Message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else
        {
            MessageBox.Show(this, "注入完成：\n" + output, "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
