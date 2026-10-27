using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FtpFlow
{
    public sealed class ConnectionSettings
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string User { get; set; }
        public string Password { get; set; }
        public string InitialPath { get; set; }
        public string EncodingName { get; set; }

        public ConnectionSettings()
        {
            Host = ""; Port = 21; User = ""; Password = ""; InitialPath = "/simon"; EncodingName = "UTF-8";
        }
    }

    public sealed class RemoteItem
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsDirectory { get; set; }
        public bool IsLink { get; set; }
        public long Size { get; set; }
        public string Modified { get; set; }
        public string Icon { get { return IsDirectory ? "\uE8B7" : "\uE8A5"; } }
        public string Kind { get { return IsLink ? "連結" : IsDirectory ? "資料夾" : "檔案"; } }
        public string SizeText { get { return IsDirectory ? "—" : Size < 0 ? "未知" : FormatSize(Size); } }
        public static string FormatSize(long size)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double n = size; int i = 0;
            while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
            return n.ToString(i == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[i];
        }
    }

    public sealed class TransferProgress
    {
        public string Name;
        public long Bytes;
        public long Total;
    }

    public sealed class FtpReply
    {
        public int Code;
        public string Message;
    }

    public sealed class FtpClient : IDisposable
    {
        private TcpClient control;
        private TcpClient data;
        private StreamReader reader;
        private StreamWriter writer;
        private CancellationTokenRegistration cancellation;
        private Encoding encoding;
        private CancellationToken token;
        private bool disposed;
        public string CurrentPath { get; private set; }
        public bool Connected { get { return !disposed && control != null && control.Connected; } }

        public static string Normalize(string path)
        {
            Guard(path);
            List<string> parts = new List<string>();
            foreach (string p in path.Replace('\\', '/').Split('/'))
            {
                if (p == "" || p == ".") continue;
                if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else parts.Add(p);
            }
            return "/" + string.Join("/", parts.ToArray());
        }

        public static string Join(string parent, string name)
        {
            GuardName(name);
            return Normalize(parent.TrimEnd('/') + "/" + name);
        }

        public static void GuardName(string name)
        {
            Guard(name);
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." || name.Contains("/") || name.Contains("\\"))
                throw new IOException("伺服器傳回無效的檔名，已停止這項操作。");
        }

        private static void Guard(string text)
        {
            if (text == null || text.IndexOfAny(new char[] { '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("欄位不能包含換行或控制字元。");
        }

        private static void Open(TcpClient client, string host, int port, CancellationToken ct)
        {
            IAsyncResult result = client.BeginConnect(host, port, null, null);
            using (WaitHandle handle = result.AsyncWaitHandle)
            {
                int selected = WaitHandle.WaitAny(new WaitHandle[] { handle, ct.WaitHandle }, 12000);
                if (selected == 1) { client.Close(); ct.ThrowIfCancellationRequested(); }
                if (selected == WaitHandle.WaitTimeout) { client.Close(); throw new IOException("連線逾時，請檢查伺服器、連接埠與網路。"); }
                client.EndConnect(result);
            }
            client.ReceiveTimeout = 20000;
            client.SendTimeout = 20000;
        }

        public void UseCancellation(CancellationToken ct)
        {
            cancellation.Dispose();
            token = ct;
            cancellation = ct.Register(delegate { CloseSockets(); });
        }

        public void Connect(ConnectionSettings settings, CancellationToken ct)
        {
            Guard(settings.Host); Guard(settings.User); Guard(settings.Password);
            encoding = settings.EncodingName == "GBK" ? Encoding.GetEncoding(936) :
                settings.EncodingName == "Big5" ? Encoding.GetEncoding(950) : new UTF8Encoding(false, true);
            disposed = false;
            control = new TcpClient();
            UseCancellation(ct);
            Open(control, settings.Host.Trim(), settings.Port, ct);
            NetworkStream stream = control.GetStream();
            reader = new StreamReader(stream, encoding, false, 4096, true);
            writer = new StreamWriter(stream, encoding, 4096, true);
            writer.NewLine = "\r\n"; writer.AutoFlush = true;
            FtpReply welcome = ReadReply();
            if (welcome.Code == 120) welcome = ReadReply();
            Require(welcome, 220);
            FtpReply user = Command("USER " + settings.User);
            if (user.Code == 331) Require(Command("PASS " + settings.Password), 230);
            else Require(user, 230);
            if (settings.EncodingName == "UTF-8") Command("OPTS UTF8 ON");
            Require(Command("TYPE I"), 200);
            ChangeDirectory(string.IsNullOrWhiteSpace(settings.InitialPath) ? "/" : settings.InitialPath);
        }

        private FtpReply ReadReply()
        {
            token.ThrowIfCancellationRequested();
            string first = reader.ReadLine();
            if (first == null) throw new IOException("伺服器已中斷連線，請重新連線。");
            int code;
            if (first.Length < 3 || !int.TryParse(first.Substring(0, 3), out code)) throw new IOException("FTP 回覆格式不正確。");
            StringBuilder text = new StringBuilder(first);
            if (first.Length > 3 && first[3] == '-')
            {
                for (int i = 0; i < 1000; i++)
                {
                    string line = reader.ReadLine();
                    if (line == null) throw new IOException("FTP 回覆未完成。");
                    text.AppendLine().Append(line);
                    if (line.StartsWith(code.ToString(CultureInfo.InvariantCulture) + " ")) return new FtpReply { Code = code, Message = text.ToString() };
                }
                throw new IOException("FTP 回覆過長。");
            }
            return new FtpReply { Code = code, Message = text.ToString() };
        }

        private FtpReply Command(string command)
        {
            token.ThrowIfCancellationRequested(); Guard(command);
            writer.WriteLine(command);
            return ReadReply();
        }

        private static void Require(FtpReply reply, params int[] accepted)
        {
            if (!accepted.Contains(reply.Code)) throw new IOException("FTP " + reply.Message);
        }

        public void ChangeDirectory(string path)
        {
            string normalized = Normalize(path.StartsWith("/") ? path : CurrentPath + "/" + path);
            Require(Command("CWD " + normalized), 250);
            FtpReply pwd = Command("PWD"); Require(pwd, 257);
            Match m = Regex.Match(pwd.Message, "\"((?:[^\"]|\"\")*)\"");
            CurrentPath = m.Success ? Normalize(m.Groups[1].Value.Replace("\"\"", "\"")) : normalized;
        }

        public void CreateDirectory(string name)
        {
            string path = Join(CurrentPath, name);
            Require(Command("MKD " + path), 257, 250);
        }

        private TcpClient Passive()
        {
            FtpReply r = Command("EPSV"); int port;
            if (r.Code == 229)
            {
                Match m = Regex.Match(r.Message, @"\((.)(?:\1){2}(\d+)\1\)");
                if (!m.Success) throw new IOException("EPSV 回覆格式不正確。");
                port = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                r = Command("PASV"); Require(r, 227);
                Match m = Regex.Match(r.Message, @"(\d+),(\d+),(\d+),(\d+),(\d+),(\d+)");
                if (!m.Success) throw new IOException("PASV 回覆格式不正確。");
                int hi = int.Parse(m.Groups[5].Value), lo = int.Parse(m.Groups[6].Value);
                if (hi > 255 || lo > 255) throw new IOException("PASV 連接埠不正確。");
                port = hi * 256 + lo;
            }
            if (port < 1 || port > 65535) throw new IOException("資料連接埠不正確。");
            data = new TcpClient();
            // Use the control peer, including servers behind NAT; never trust a foreign PASV address.
            Open(data, ((IPEndPoint)control.Client.RemoteEndPoint).Address.ToString(), port, token);
            return data;
        }

        private List<string> ReadDataLines(string command, bool optional)
        {
            List<string> lines = new List<string>();
            using (TcpClient socket = Passive())
            {
                FtpReply start = Command(command);
                if (optional && new int[] { 500, 501, 502, 504, 550 }.Contains(start.Code)) return null;
                Require(start, 125, 150);
                using (StreamReader listing = new StreamReader(socket.GetStream(), encoding, false))
                {
                    string line;
                    while ((line = listing.ReadLine()) != null) { token.ThrowIfCancellationRequested(); lines.Add(line); }
                }
            }
            data = null;
            Require(ReadReply(), 226, 250);
            return lines;
        }

        public List<RemoteItem> ListDirectory()
        {
            List<RemoteItem> items = new List<RemoteItem>();
            List<string> lines = ReadDataLines("MLSD", true);
            if (lines != null)
            {
                foreach (string line in lines)
                {
                    int space = line.IndexOf(' '); if (space < 0) continue;
                    string facts = line.Substring(0, space), name = line.Substring(space + 1);
                    Dictionary<string, string> info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string f in facts.Split(';')) { int eq = f.IndexOf('='); if (eq > 0) info[f.Substring(0, eq)] = f.Substring(eq + 1); }
                    string type; info.TryGetValue("type", out type);
                    if (type == "cdir" || type == "pdir" || name == "." || name == "..") continue;
                    GuardName(name);
                    long size = -1; string s; if (info.TryGetValue("size", out s)) long.TryParse(s, out size);
                    string modified = "—";
                    if (info.TryGetValue("modify", out s)) { DateTime date; if (DateTime.TryParseExact(s.Split('.')[0], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date)) modified = date.ToLocalTime().ToString("yyyy/MM/dd HH:mm"); }
                    items.Add(new RemoteItem { Name = name, FullPath = Join(CurrentPath, name), IsDirectory = string.Equals(type, "dir", StringComparison.OrdinalIgnoreCase), IsLink = type != null && type.IndexOf("slink", StringComparison.OrdinalIgnoreCase) >= 0, Size = size, Modified = modified });
                }
            }
            else
            {
                lines = ReadDataLines("LIST", true);
                bool parsed = lines != null;
                if (lines != null) foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("total ")) continue;
                    RemoteItem item = ParseListLine(line);
                    if (item == null) { parsed = false; break; }
                    if (item.Name == "." || item.Name == "..") continue;
                    GuardName(item.Name); item.FullPath = Join(CurrentPath, item.Name); items.Add(item);
                }
                if (!parsed)
                {
                    items.Clear();
                    lines = ReadDataLines("NLST", false);
                    foreach (string raw in lines)
                    {
                        string name = raw;
                        if (name.StartsWith(CurrentPath.TrimEnd('/') + "/", StringComparison.Ordinal)) name = name.Substring(CurrentPath.TrimEnd('/').Length + 1);
                        if (name == "." || name == "..") continue;
                        GuardName(name); string path = Join(CurrentPath, name); long size = -1; bool dir = false;
                        FtpReply sz = Command("SIZE " + path);
                        if (sz.Code == 213) long.TryParse(sz.Message.Substring(3).Trim(), out size);
                        else
                        {
                            FtpReply cd = Command("CWD " + path);
                            if (cd.Code == 250) { dir = true; Require(Command("CWD " + CurrentPath), 250); }
                        }
                        items.Add(new RemoteItem { Name = name, FullPath = path, Size = size, IsDirectory = dir, Modified = "—" });
                    }
                }
            }
            return items.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static RemoteItem ParseListLine(string line)
        {
            Match unix = Regex.Match(line, @"^(?<kind>[dl-])\S*\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+(?<date>\S+\s+\d+\s+[\d:]+)\s(?<name>.+)$");
            if (unix.Success)
            {
                string name = unix.Groups["name"].Value; bool link = unix.Groups["kind"].Value == "l";
                if (link && name.Contains(" -> ")) name = name.Substring(0, name.IndexOf(" -> ", StringComparison.Ordinal));
                return new RemoteItem { Name = name, IsDirectory = unix.Groups["kind"].Value == "d", IsLink = link, Size = long.Parse(unix.Groups["size"].Value), Modified = unix.Groups["date"].Value };
            }
            Match dos = Regex.Match(line, @"^\s*(?<date>\d{2}[-/]\d{2}[-/]\d{2,4}\s+\d{1,2}:\d{2}\s*[AP]M)\s+(?<size><DIR>|\d+)\s+(?<name>.+)$", RegexOptions.IgnoreCase);
            if (dos.Success) return new RemoteItem { Name = dos.Groups["name"].Value, IsDirectory = dos.Groups["size"].Value.Equals("<DIR>", StringComparison.OrdinalIgnoreCase), Size = dos.Groups["size"].Value.StartsWith("<") ? -1 : long.Parse(dos.Groups["size"].Value), Modified = dos.Groups["date"].Value };
            return null;
        }

        public static string SafeLocalName(string name)
        {
            StringBuilder safe = new StringBuilder();
            foreach (char c in name) safe.Append(char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            string result = safe.ToString().TrimEnd('.', ' ');
            if (result.Length == 0) result = "未命名";
            string stem = result.Split('.')[0];
            if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) result = "_" + result;
            if (result.Length > 160) result = result.Substring(0, 130) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + Path.GetExtension(result).Substring(0, Math.Min(Path.GetExtension(result).Length, 16));
            return result;
        }

        public string Download(RemoteItem item, string folder, IProgress<TransferProgress> progress)
        {
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, SafeLocalName(item.Name));
            string basename = Path.GetFileNameWithoutExtension(target), extension = Path.GetExtension(target);
            for (int i = 2; File.Exists(target) || Directory.Exists(target); i++) target = Path.Combine(folder, basename + " (" + i + ")" + extension);
            string partial = Path.Combine(folder, ".ftpflow-" + Guid.NewGuid().ToString("N") + ".part");
            try
            {
                using (TcpClient socket = Passive())
                {
                    Require(Command("RETR " + item.FullPath), 125, 150);
                    using (FileStream output = new FileStream(partial, FileMode.CreateNew)) Copy(socket.GetStream(), output, item.Name, item.Size, progress);
                }
                data = null; Require(ReadReply(), 226, 250); token.ThrowIfCancellationRequested();
                File.Move(partial, target); return target;
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }

        public void Upload(string localPath, string remotePath, IProgress<TransferProgress> progress)
        {
            Guard(remotePath);
            using (FileStream input = File.OpenRead(localPath))
            using (TcpClient socket = Passive())
            {
                Require(Command("STOR " + remotePath), 125, 150);
                Copy(input, socket.GetStream(), Path.GetFileName(localPath), input.Length, progress);
            }
            data = null; Require(ReadReply(), 226, 250);
        }

        private void Copy(Stream input, Stream output, string name, long total, IProgress<TransferProgress> progress)
        {
            byte[] buffer = new byte[65536]; long bytes = 0; int read; DateTime last = DateTime.MinValue;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); bytes += read;
                if (progress != null && (DateTime.UtcNow - last).TotalMilliseconds >= 100) { progress.Report(new TransferProgress { Name = name, Bytes = bytes, Total = total }); last = DateTime.UtcNow; }
            }
            output.Flush();
            if (total >= 0 && bytes != total) throw new IOException("檔案傳輸長度不符，這次傳輸未完成。");
            if (progress != null) progress.Report(new TransferProgress { Name = name, Bytes = bytes, Total = total });
        }

        public void Delete(RemoteItem item, Action<string> status, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 64) throw new IOException("資料夾層數超過限制，已停止刪除。");
            string path = Normalize(item.FullPath);
            if (path == "/") throw new IOException("不能刪除根目錄。");
            if (status != null) status(item.Name);
            if (!item.IsDirectory || item.IsLink) { Require(Command("DELE " + path), 250); return; }
            string original = CurrentPath;
            ChangeDirectory(path);
            List<RemoteItem> children = ListDirectory();
            foreach (RemoteItem child in children) Delete(child, status, depth + 1);
            ChangeDirectory(original);
            Require(Command("RMD " + path), 250);
        }

        private void CloseSockets()
        {
            try { if (data != null) data.Close(); } catch { }
            try { if (control != null) control.Close(); } catch { }
        }
        public void Dispose() { disposed = true; cancellation.Dispose(); CloseSockets(); }
    }
}
