using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;
using System.Xml;

internal static class Setup
{
    private const string Product = "HoL Round / Rec Duct Align | Revit 2023";
    private const string ClassName = "MEP_Align_Round_Duct_3D_ver1.RibbonApplication";
    private const string Dll = "HoL.DuctAlign.Revit2023.dll";
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", "2023");
    private static readonly string Folder = Path.Combine(Root, "HoL.RoundDuctAlign");
    private static readonly string Manifest = Path.Combine(Root, "HoL.RoundDuctAlign.addin");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "/verify")
        {
            try
            {
                byte[] bytes = Payload();
                // Validate the embedded DLL without installing or executing the Revit add-in.
                Assembly asm = Assembly.ReflectionOnlyLoad(bytes);
                bool version = asm.GetName().Version.ToString() == "1.6.0.0";
                bool api = asm.GetReferencedAssemblies().Any(a => a.Name == "RevitAPI" && a.Version.Major == 23);
                string[] resources = asm.GetManifestResourceNames();
                bool icons = resources.Contains("HoL.RoundAlign.RoundAlign16.png") && resources.Contains("HoL.RoundAlign.RoundAlign32.png") && resources.Contains("HoL.RoundAlign.RecAlign16.png") && resources.Contains("HoL.RoundAlign.RecAlign32.png");
                XmlDocument manifest = MakeManifest();
                if (!version || !api || !icons || manifest.SelectSingleNode("//FullClassName").InnerText != ClassName)
                    throw new Exception("Payload verification failed.");
                string hash;
                using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                File.WriteAllText(args[1], "PASS\r\nVersion=1.6.0.0\r\nRevitAPI=23\r\nEmbeddedIcons=Round16,Round32,Rec16,Rec32\r\nPayloadSHA256=" + hash + "\r\nNo installation or model changes performed.\r\n");
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(args[1], "FAIL: " + ex); return 1; }
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (Form form = new Form())
        {
            form.Text = Product; form.ClientSize = new Size(590, 350);
            form.StartPosition = FormStartPosition.CenterScreen; form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false; form.MinimizeBox = false;
            form.Font = new Font("Segoe UI", 10);
            form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Label title = new Label { Text = "ROUND + REC DUCT ALIGN", Font = new Font("Segoe UI", 19, FontStyle.Bold), Location = new Point(24, 22), AutoSize = true };
            Label intro = new Label { Text = "Phiên bản 1.6.0 • Autodesk Revit 2023 • Windows x64\n\nCài cho tài khoản Windows hiện tại, không cần quyền admin.\nĐóng Revit 2023 trước khi cài, cập nhật hoặc gỡ.\nSau khi cài: HoL Tools → CAD Align → Round Align / Rec Align.", Location = new Point(26, 72), Size = new Size(545, 115) };
            Label status = new Label { Text = "Sẵn sàng cài đặt.", Location = new Point(26, 191), Size = new Size(540, 60), ForeColor = Color.FromArgb(30, 91, 166) };
            Button install = new Button { Text = "Cài / cập nhật", Location = new Point(26, 275), Size = new Size(175, 40) };
            Button uninstall = new Button { Text = "Gỡ cài đặt", Location = new Point(216, 275), Size = new Size(160, 40) };
            Button close = new Button { Text = "Đóng", Location = new Point(394, 275), Size = new Size(170, 40) };
            install.Click += delegate { Run(status, Install, "Đã cài xong. Mở Revit 2023 → HoL Tools → Round Align / Rec Align."); };
            uninstall.Click += delegate
            {
                if (MessageBox.Show("Gỡ HoL Round + Rec Duct Align khỏi Revit 2023?", Product, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    Run(status, Uninstall, "Đã gỡ công cụ khỏi tài khoản hiện tại.");
            };
            close.Click += delegate { form.Close(); };
            form.Controls.AddRange(new Control[] { title, intro, status, install, uninstall, close });
            Application.Run(form);
        }
        return 0;
    }
    private static void Run(Label status, Action action, string success)
    {
        try { action(); status.ForeColor = Color.DarkGreen; status.Text = success; }
        catch (Exception ex) { status.ForeColor = Color.Firebrick; status.Text = ex.Message; MessageBox.Show(ex.Message, Product, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    private static byte[] Payload()
    {
        using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.Addin.dll"))
        using (MemoryStream buffer = new MemoryStream())
        {
            if (input == null) throw new Exception("Thiếu dữ liệu DLL trong bộ cài.");
            input.CopyTo(buffer); return buffer.ToArray();
        }
    }
    private static void CheckPathsAndRevit()
    {
        string fullRoot = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Folder).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(Manifest).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Đường dẫn cài đặt không hợp lệ.");
        foreach (Process process in Process.GetProcessesByName("Revit"))
        {
            try
            {
                string path = process.MainModule.FileName;
                if (FileVersionInfo.GetVersionInfo(path).FileMajorPart == 23 || path.IndexOf("Revit 2023", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new InvalidOperationException("Hãy đóng Revit 2023 rồi chạy lại bộ cài.");
            }
            catch (InvalidOperationException) { throw; }
            catch { throw new Exception("Không kiểm tra được phiên Revit đang mở. Hãy đóng Revit rồi thử lại."); }
            finally { process.Dispose(); }
        }
        if (File.Exists(Manifest))
        {
            XmlDocument previous = new XmlDocument(); previous.Load(Manifest);
            XmlNode entry = previous.SelectSingleNode("/RevitAddIns/AddIn/FullClassName");
            if (entry == null || entry.InnerText != ClassName) throw new Exception("Manifest hiện tại không thuộc công cụ này; không ghi đè.");
        }
    }
    private static XmlDocument MakeManifest()
    {
        XmlDocument xml = new XmlDocument();
        xml.LoadXml("<RevitAddIns><AddIn Type='Application'><Name>HoL Round + Rec Duct Align</Name><Assembly/><AddInId>4B095FD2-B2E9-43DF-BE97-62C95F99B6A1</AddInId><FullClassName/>" +
            "<VendorId>HOL1</VendorId><VendorDescription>HoL MEP Tools</VendorDescription></AddIn></RevitAddIns>");
        xml.SelectSingleNode("//Assembly").InnerText = Path.Combine(Folder, Dll);
        xml.SelectSingleNode("//FullClassName").InnerText = ClassName;
        return xml;
    }
    private static void Install()
    {
        CheckPathsAndRevit();
        object release = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", 0);
        if (Convert.ToInt32(release) < 528040) throw new Exception("Cần .NET Framework 4.8 hoặc mới hơn trước khi dùng công cụ.");
        Directory.CreateDirectory(Folder);
        string dllPath = Path.Combine(Folder, Dll);
        byte[] previousDll = File.Exists(dllPath) ? File.ReadAllBytes(dllPath) : null;
        byte[] previousManifest = File.Exists(Manifest) ? File.ReadAllBytes(Manifest) : null;
        try { File.WriteAllBytes(dllPath, Payload()); MakeManifest().Save(Manifest); }
        catch
        {
            if (previousDll == null) { if (File.Exists(dllPath)) File.Delete(dllPath); } else File.WriteAllBytes(dllPath, previousDll);
            if (previousManifest == null) { if (File.Exists(Manifest)) File.Delete(Manifest); } else File.WriteAllBytes(Manifest, previousManifest);
            throw;
        }
    }
    private static void Uninstall()
    {
        CheckPathsAndRevit();
        if (File.Exists(Manifest)) File.Delete(Manifest);
        string dllPath = Path.Combine(Folder, Dll);
        if (File.Exists(dllPath)) File.Delete(dllPath);
        if (Directory.Exists(Folder) && !Directory.EnumerateFileSystemEntries(Folder).Any()) Directory.Delete(Folder, false);
    }
}
