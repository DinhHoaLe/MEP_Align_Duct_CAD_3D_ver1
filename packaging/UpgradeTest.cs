using System;
using System.IO;
using System.Linq;
using System.Xml;

internal static class UpgradeTest
{
    private static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(root);
        string dll = Path.Combine(root, "addin.dll"), manifest = Path.Combine(root, "addin.addin");
        byte[] old = { 1, 2, 3 }, next = File.ReadAllBytes(args[1]);
        var xml = new XmlDocument();
        xml.LoadXml("<RevitAddIns><AddIn Type='Application'><Name>HoL CAD Align</Name></AddIn></RevitAddIns>");
        Setup.WriteInstallation(dll, manifest, old, xml);
        Setup.WriteInstallation(dll, manifest, next, xml);
        if (!File.ReadAllBytes(dll).SequenceEqual(next)) throw new Exception("Upgrade failed");
        var installed = new XmlDocument(); installed.Load(manifest);
        if (installed.SelectNodes("/RevitAddIns/AddIn").Count != 1) throw new Exception("Duplicate registration");
        bool failed = false;
        try { Setup.WriteInstallation(dll, Path.Combine(root, "missing", "addin.addin"), old, xml); }
        catch (DirectoryNotFoundException) { failed = true; }
        if (!failed || !File.ReadAllBytes(dll).SequenceEqual(next)) throw new Exception("Rollback failed");
        Console.WriteLine("PASS: fresh write, in-place DLL update, single registration, DLL restored after manifest write failure.");
        return 0;
    }
}
