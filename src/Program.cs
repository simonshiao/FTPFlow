using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32;

namespace FtpFlow
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                Application app = new Application();
                AppController controller = new AppController(args);
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                {
                    MessageBox.Show(e.Exception.Message, "FTP Flow", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true;
                };
                app.Run(controller.Window);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-error.log"), ex.ToString(), Encoding.UTF8);
                MessageBox.Show("程式無法啟動：\n" + ex.Message, "FTP Flow", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.ExitCode = 1;
            }
        }
    }

    public sealed class AppController
    {
        private sealed class SavedProfile
        {
            public string Id { get; set; }
            public ConnectionSettings Connection { get; set; }
            public string Name { get { return Connection == null || string.IsNullOrWhiteSpace(Connection.User) ? "尚未設定帳號" : Connection.User; } }
            public string ServerLabel { get { return Connection == null || string.IsNullOrWhiteSpace(Connection.Host) ? "尚未設定伺服器" : Connection.Host + ":" + Connection.Port; } }

            public SavedProfile()
            {
                Id = Guid.NewGuid().ToString("N"); Connection = new ConnectionSettings();
            }
        }

        public Window Window { get; private set; }
        private FtpClient client;
        private CancellationTokenSource cancellation;
        private readonly List<RemoteItem> items = new List<RemoteItem>();
        private bool busy;
        private bool closeRequested;
        private readonly string basePath = AppDomain.CurrentDomain.BaseDirectory;
        private readonly string[] args;
        private string savedPassword = "";
        private int operationVersion;
        private GridView detailView;
        private ItemsPanelTemplate detailPanel;
        private Style detailItemStyle;
        private readonly List<SavedProfile> profiles = new List<SavedProfile>();
        private SavedProfile selectedProfile;
        private string apiToken = "";
        private int apiPort = 17821;
        private HttpListener apiListener;
        private Task apiLoop;
        private readonly JavaScriptSerializer apiJson = new JavaScriptSerializer();
        private bool loadingProfiles;

        private T Find<T>(string name) where T : class { return Window.FindName(name) as T; }
        private TextBox Text(string name) { return Find<TextBox>(name); }
        private Button Button(string name) { return Find<Button>(name); }
        private TextBlock Label(string name) { return Find<TextBlock>(name); }
        private ListView Files { get { return Find<ListView>("FileList"); } }
        private bool Connected { get { return client != null && client.Connected; } }

        public AppController(string[] arguments)
        {
            args = arguments;
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) Window = (Window)XamlReader.Load(resource);
            LoadSettings();
            Files.ItemsSource = items;
            detailView = Files.View as GridView;
            detailPanel = Files.ItemsPanel;
            detailItemStyle = Files.ItemContainerStyle;
            SetViewMode(LoadTilePreference(), false);
            Button("ManageAccountsInSettingsButton").Click += delegate { ManageProfiles(); };
            Button("AddProfileButton").Click += delegate { AddProfile(); };
            Button("SettingsGearButton").Click += delegate
            {
                Expander settings = Find<Expander>("ConnectionSettingsExpander");
                settings.IsExpanded = !settings.IsExpanded;
                Button("SettingsGearButton").ToolTip = settings.IsExpanded ? "收合連線與 API 設定" : "展開連線與 API 設定";
                if (settings.IsExpanded) Window.Dispatcher.BeginInvoke(new Action(delegate { Find<ScrollViewer>("SettingsScroll").ScrollToTop(); }));
            };
            Find<ComboBox>("ProfileBox").SelectionChanged += delegate
            {
                if (loadingProfiles || busy) return;
                SavedProfile next = Find<ComboBox>("ProfileBox").SelectedItem as SavedProfile;
                if (next == null || next == selectedProfile) return;
                StoreCurrentProfile();
                SaveProfiles();
                if (Connected) Disconnect();
                selectedProfile = next;
                ApplyProfile(next);
                UpdateProfileSummary();
            };
            Button("ApiToggleButton").Click += delegate { ToggleApiServer(); };
            Button("ApiCopyButton").Click += delegate { CopyApiAccess(); };
            Button("ConnectButton").Click += async delegate { await Connect(); };
            Button("DisconnectButton").Click += delegate { Disconnect(); };
            Button("GoButton").Click += async delegate { await Navigate(Text("PathBox").Text); };
            Button("RootButton").Click += async delegate { await Navigate("/"); };
            Button("UpButton").Click += async delegate { if (Connected) await Navigate(FtpClient.Normalize(client.CurrentPath + "/..")); };
            Button("RefreshButton").Click += async delegate { if (Connected) await Navigate(client.CurrentPath); };
            Button("UploadButton").Click += async delegate { await Upload(); };
            Button("NewFolderButton").Click += async delegate { await NewFolder(); };
            Button("ListViewButton").Click += delegate { SetViewMode(false, true); };
            Button("TileViewButton").Click += delegate { SetViewMode(true, true); };
            Button("DownloadButton").Click += async delegate { await Download(false); };
            Button("DownloadAllButton").Click += async delegate { await Download(true); };
            Button("DeleteButton").Click += async delegate { await Delete(); };
            Button("CancelButton").Click += delegate { if (cancellation != null) { cancellation.Cancel(); Label("TaskLabel").Text = "正在取消並中斷連線…"; } };
            Text("PathBox").KeyDown += async delegate(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await Navigate(Text("PathBox").Text); } };
            Text("SearchBox").TextChanged += delegate { UpdateList(); };
            Files.SelectionChanged += delegate { UpdateActions(); };
            Files.MouseDoubleClick += async delegate(object s, MouseButtonEventArgs e)
            {
                DependencyObject source = e.OriginalSource as DependencyObject;
                ListViewItem container = source == null ? null : ItemsControl.ContainerFromElement(Files, source) as ListViewItem;
                if (container == null || busy || !Connected) return;
                RemoteItem item = container.DataContext as RemoteItem;
                if (item == null) return;
                if (item.IsDirectory) await Navigate(item.FullPath); else await Download(false);
            };
            Window.PreviewKeyDown += async delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.F5 && Connected && !busy) { e.Handled = true; await Navigate(client.CurrentPath); }
                else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Text("SearchBox").Focus(); Text("SearchBox").SelectAll(); }
                else if (e.SystemKey == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt && Connected && !busy) { e.Handled = true; await Navigate(FtpClient.Normalize(client.CurrentPath + "/..")); }
            };
            Window.Closing += delegate(object s, CancelEventArgs e)
            {
                if (busy) { e.Cancel = true; closeRequested = true; if (cancellation != null) cancellation.Cancel(); }
                else { StopApiServer(false); if (client != null) client.Dispose(); }
            };
            Window.SizeChanged += delegate { SizeColumns(); };
            Window.Loaded += async delegate
            {
                SizeColumns();
                StartApiServer();
                SaveProfiles();
                if (args.Contains("--ui-smoke")) await Smoke();
                else
                {
                    if (!string.IsNullOrWhiteSpace(Text("HostBox").Text)) await Connect(true);
                    else Label("StatusLabel").Text = "請選擇或新增 FTP 帳號。";
                    if (args.Contains("--auto-connect-test")) FinishAutoConnectTest();
                }
            };
            UpdateActions();
        }

        private void SizeColumns()
        {
            GridView view = Files.View as GridView;
            if (view != null && Files.ActualWidth > 350) view.Columns[0].Width = Math.Max(210, Files.ActualWidth - 355);
        }

        private string ViewPreferencePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FTPFlow", "view-mode.txt");
        }

        private bool LoadTilePreference()
        {
            if (args.Contains("--ui-smoke") || args.Contains("--auto-connect-test")) return false;
            try { string path = ViewPreferencePath(); return File.Exists(path) && File.ReadAllText(path).Trim() == "tiles"; }
            catch { return false; }
        }

        private void SetViewMode(bool tiles, bool save)
        {
            List<object> selected = Files.SelectedItems.Cast<object>().ToList();
            Files.View = null;
            Files.ItemTemplate = tiles ? (DataTemplate)Window.Resources["TileItemTemplate"] : null;
            Files.ItemsPanel = tiles ? (ItemsPanelTemplate)Window.Resources["TileItemsPanel"] : detailPanel;
            Files.ItemContainerStyle = tiles ? (Style)Window.Resources["TileItemStyle"] : detailItemStyle;
            Files.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, tiles ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
            if (!tiles) Files.View = detailView;
            foreach (object item in selected) if (Files.Items.Contains(item) && !Files.SelectedItems.Contains(item)) Files.SelectedItems.Add(item);
            Button("ListViewButton").Background = new SolidColorBrush(tiles ? Colors.White : Color.FromRgb(232, 242, 255));
            Button("TileViewButton").Background = new SolidColorBrush(tiles ? Color.FromRgb(232, 242, 255) : Colors.White);
            Button("ListViewButton").Foreground = new SolidColorBrush(tiles ? Color.FromRgb(48, 48, 56) : Color.FromRgb(0, 93, 186));
            Button("TileViewButton").Foreground = new SolidColorBrush(tiles ? Color.FromRgb(0, 93, 186) : Color.FromRgb(48, 48, 56));
            SizeColumns();
            if (save)
            {
                try { string path = ViewPreferencePath(); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, tiles ? "tiles" : "list", Encoding.UTF8); }
                catch { Label("StatusLabel").Text = "顯示方式已切換，但無法儲存偏好設定。"; }
            }
        }

        private string SettingsPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FTPFlow", "settings.xml");
        }

        private static byte[] SecretEntropy()
        {
            return Encoding.UTF8.GetBytes("FTPFlow local account secrets v1");
        }

        private static string ProtectSecret(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), SecretEntropy(), DataProtectionScope.CurrentUser));
        }

        private static string UnprotectSecret(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (!value.StartsWith("dpapi:", StringComparison.Ordinal)) return value;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value.Substring(6)), SecretEntropy(), DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }

        private ConnectionSettings ParseConnection(XElement element)
        {
            return new ConnectionSettings
            {
                Host = (string)element.Element("Host") ?? "",
                Port = ParsePort((string)element.Element("Port")),
                User = (string)element.Element("User") ?? "",
                Password = UnprotectSecret((string)element.Element("Password") ?? ""),
                InitialPath = (string)element.Element("InitialPath") ?? "/",
                EncodingName = (string)element.Element("Encoding") ?? "UTF-8"
            };
        }

        private static int ParsePort(string value) { int port; return int.TryParse(value, out port) && port >= 1 && port <= 65535 ? port : 21; }

        private void LoadSettings()
        {
            try
            {
                string file = SettingsPath();
                XElement root = File.Exists(file) ? XElement.Load(file) : null;
                bool migrated = false;
                if (root == null)
                {
                    string legacy = Path.Combine(basePath, "settings.xml");
                    if (File.Exists(legacy))
                    {
                        root = XElement.Load(legacy);
                        SavedProfile profile = new SavedProfile { Connection = ParseConnection(root) };
                        profiles.Add(profile); selectedProfile = profile; migrated = true;
                    }
                }
                if (root != null && root.Name == "FtpFlowProfiles")
                {
                    foreach (XElement element in root.Element("Profiles") == null ? Enumerable.Empty<XElement>() : root.Element("Profiles").Elements("Profile"))
                    {
                        SavedProfile profile = new SavedProfile();
                        profile.Id = (string)element.Attribute("id") ?? profile.Id;
                        profile.Connection = ParseConnection(element);
                        profiles.Add(profile);
                    }
                    string selectedId = (string)root.Attribute("selected") ?? "";
                    selectedProfile = profiles.FirstOrDefault(x => x.Id == selectedId);
                    apiPort = root.Element("ApiPort") == null ? 17821 : ParsePort((string)root.Element("ApiPort"));
                    apiToken = UnprotectSecret((string)root.Element("ApiToken") ?? "");
                }
                if (profiles.Count == 0) profiles.Add(new SavedProfile());
                if (selectedProfile == null) selectedProfile = profiles[0];
                Find<ComboBox>("ProfileBox").DisplayMemberPath = "Name";
                Find<ComboBox>("ProfileBox").ItemsSource = profiles;
                Find<ComboBox>("ProfileBox").SelectedItem = selectedProfile;
                Text("ApiPortBox").Text = apiPort.ToString();
                Text("ApiTokenBox").Text = apiToken;
                Text("ApiEndpointBox").Text = "http://127.0.0.1:" + apiPort + "/api/v1";
                ApplyProfile(selectedProfile);
                UpdateProfileSummary();
                if (migrated) SaveProfiles();
            }
            catch { Label("StatusLabel").Text = "帳號設定無法讀取，請使用帳號管理重新設定。"; }
        }

        private ConnectionSettings ReadSettings()
        {
            string host = Text("HostBox").Text.Trim(); int port;
            if (host.Length == 0) { Text("HostBox").Focus(); throw new ArgumentException("請輸入伺服器名稱或 IP。"); }
            if (host.Contains("://") || host.Contains("/") || host.Any(char.IsWhiteSpace)) throw new ArgumentException("伺服器請只填主機名稱或 IP；連接埠填在右側欄位。");
            if (!int.TryParse(Text("PortBox").Text, out port) || port < 1 || port > 65535) { Text("PortBox").Focus(); throw new ArgumentException("連接埠必須介於 1 到 65535。"); }
            if (Text("UserBox").Text.Length == 0) { Text("UserBox").Focus(); throw new ArgumentException("請輸入 FTP 帳號。"); }
            return new ConnectionSettings { Host = host, Port = port, User = Text("UserBox").Text, Password = Text("PassBox").Text, InitialPath = Text("InitialBox").Text, EncodingName = ((ComboBoxItem)Find<ComboBox>("EncodingBox").SelectedItem).Content.ToString() };
        }

        private void SaveSettings(ConnectionSettings settings)
        {
            if (args.Contains("--ui-smoke")) { savedPassword = settings.Password; return; }
            selectedProfile.Connection = settings;
            StoreCurrentProfile();
            SaveProfiles();
            savedPassword = settings.Password;
        }

        private void StoreCurrentProfile()
        {
            if (selectedProfile == null || Text("HostBox") == null) return;
            int port;
            selectedProfile.Connection = new ConnectionSettings
            {
                Host = Text("HostBox").Text.Trim(),
                Port = int.TryParse(Text("PortBox").Text, out port) ? port : 21,
                User = Text("UserBox").Text,
                Password = Text("PassBox").Text,
                InitialPath = Text("InitialBox").Text,
                EncodingName = ((ComboBoxItem)Find<ComboBox>("EncodingBox").SelectedItem).Content.ToString()
            };
        }

        private void ApplyProfile(SavedProfile profile)
        {
            if (profile == null) return;
            ConnectionSettings settings = profile.Connection ?? new ConnectionSettings();
            loadingProfiles = true;
            Text("HostBox").Text = settings.Host ?? "";
            Text("PortBox").Text = settings.Port.ToString();
            Text("UserBox").Text = settings.User ?? "";
            Text("PassBox").Text = settings.Password ?? "";
            savedPassword = settings.Password ?? "";
            Text("InitialBox").Text = string.IsNullOrWhiteSpace(settings.InitialPath) ? "/" : settings.InitialPath;
            Text("PathBox").Text = Text("InitialBox").Text;
            string enc = settings.EncodingName ?? "UTF-8";
            Find<ComboBox>("EncodingBox").SelectedIndex = enc == "GBK" ? 1 : enc == "Big5" ? 2 : 0;
            Find<ComboBox>("ProfileBox").SelectedItem = profile;
            loadingProfiles = false;
        }

        private void UpdateProfileSummary()
        {
            SavedProfile profile = selectedProfile;
            ConnectionSettings settings = profile == null ? null : profile.Connection;
            string host = settings == null || string.IsNullOrWhiteSpace(settings.Host) ? "尚未設定伺服器" : settings.Host + ":" + settings.Port;
            string path = settings == null || string.IsNullOrWhiteSpace(settings.InitialPath) ? "/" : settings.InitialPath;
            Find<TextBlock>("ProfileSummaryLabel").Text = profile == null ? "請新增 FTP 帳號" : host + "  ·  " + path;
        }

        private XElement ProfileElement(SavedProfile profile)
        {
            ConnectionSettings s = profile.Connection ?? new ConnectionSettings();
            return new XElement("Profile", new XAttribute("id", profile.Id),
                new XElement("Host", s.Host ?? ""), new XElement("Port", s.Port), new XElement("User", s.User ?? ""),
                new XElement("Password", ProtectSecret(s.Password)), new XElement("InitialPath", s.InitialPath ?? "/"), new XElement("Encoding", s.EncodingName ?? "UTF-8"));
        }

        private void SaveProfiles()
        {
            if (args.Contains("--ui-smoke")) return;
            StoreCurrentProfile();
            XElement root = new XElement("FtpFlowProfiles", new XAttribute("version", "3"), new XAttribute("selected", selectedProfile == null ? "" : selectedProfile.Id),
                new XElement("Profiles", profiles.Select(ProfileElement)), new XElement("ApiPort", apiPort), new XElement("ApiToken", ProtectSecret(apiToken)));
            string file = SettingsPath(); Directory.CreateDirectory(Path.GetDirectoryName(file)); root.Save(file);
        }

        private void EnsureApiToken()
        {
            if (!string.IsNullOrEmpty(apiToken)) return;
            byte[] bytes = new byte[32];
            using (RNGCryptoServiceProvider random = new RNGCryptoServiceProvider()) random.GetBytes(bytes);
            apiToken = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            Text("ApiTokenBox").Text = apiToken;
        }

        private void CopyApiAccess()
        {
            EnsureApiToken();
            try
            {
                Clipboard.SetText("API: http://127.0.0.1:" + apiPort + "/api/v1\r\nAuthorization: Bearer " + apiToken);
                Label("ApiStatusLabel").Text = "本機 API 位址與權杖已複製。";
            }
            catch { Label("ApiStatusLabel").Text = "無法寫入剪貼簿，請從欄位複製。"; }
        }

        private void ToggleApiServer()
        {
            if (apiListener != null && apiListener.IsListening) { StopApiServer(true); return; }
            int port;
            if (!int.TryParse(Text("ApiPortBox").Text, out port) || port < 1024 || port > 65535)
            {
                Label("ApiStatusLabel").Text = "連接埠請填 1024 到 65535。"; return;
            }
            apiPort = port;
            Text("ApiEndpointBox").Text = "http://127.0.0.1:" + apiPort + "/api/v1";
            EnsureApiToken(); SaveProfiles(); StartApiServer();
        }

        private void StartApiServer()
        {
            if (args.Contains("--ui-smoke") || apiListener != null && apiListener.IsListening) return;
            EnsureApiToken();
            int port;
            if (!int.TryParse(Text("ApiPortBox").Text, out port) || port < 1024 || port > 65535) port = apiPort;
            apiPort = port;
            Text("ApiPortBox").Text = apiPort.ToString();
            Text("ApiEndpointBox").Text = "http://127.0.0.1:" + apiPort + "/api/v1";
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + apiPort + "/");
            try
            {
                listener.Start(); apiListener = listener;
                Label("ApiStatusLabel").Text = "本機控制 API 運作中 · 127.0.0.1:" + apiPort;
                Button("ApiToggleButton").Content = "停止 API";
                apiLoop = Task.Run(async delegate
                {
                    while (listener.IsListening)
                    {
                        HttpListenerContext context;
                        try { context = await listener.GetContextAsync(); }
                        catch { break; }
                        Task requestTask = Task.Run(async delegate { await HandleApiRequest(context); });
                    }
                });
                SaveProfiles();
            }
            catch (Exception ex)
            {
                listener.Close(); apiListener = null;
                Label("ApiStatusLabel").Text = "API 啟動失敗：" + ex.Message;
                Button("ApiToggleButton").Content = "啟動 API";
            }
        }

        private void StopApiServer(bool updateStatus)
        {
            HttpListener listener = apiListener; apiListener = null;
            if (listener != null) { try { listener.Stop(); listener.Close(); } catch { } }
            if (updateStatus && Label("ApiStatusLabel") != null)
            {
                Label("ApiStatusLabel").Text = "本機控制 API 已停止。";
                Button("ApiToggleButton").Content = "啟動 API";
            }
        }

        private static bool ConstantTimeEquals(string left, string right)
        {
            byte[] a = Encoding.UTF8.GetBytes(left ?? ""), b = Encoding.UTF8.GetBytes(right ?? "");
            int diff = a.Length ^ b.Length; int count = Math.Max(a.Length, b.Length);
            for (int i = 0; i < count; i++) diff |= (i < a.Length ? a[i] : 0) ^ (i < b.Length ? b[i] : 0);
            return diff == 0;
        }

        private async Task HandleApiRequest(HttpListenerContext context)
        {
            int status = 200; object result;
            try
            {
                if (context.Request.Headers["Origin"] != null || !IPAddress.IsLoopback(context.Request.RemoteEndPoint.Address)) throw new ApiException(403, "只允許本機程式呼叫此 API。");
                string authorization = context.Request.Headers["Authorization"] ?? "";
                if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || !ConstantTimeEquals(authorization.Substring(7), apiToken)) throw new ApiException(401, "請提供正確的 Bearer 權杖。");
                if (context.Request.ContentLength64 > 1024 * 1024) throw new ApiException(413, "請求內容超過 1 MB 限制。");
                Dictionary<string, object> body = null;
                if (context.Request.HasEntityBody)
                {
                    using (StreamReader reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
                    {
                        string json = await reader.ReadToEndAsync();
                        if (!string.IsNullOrWhiteSpace(json)) body = apiJson.Deserialize<Dictionary<string, object>>(json);
                    }
                }
                result = await ExecuteApiOnUi(context.Request.HttpMethod, context.Request.Url.AbsolutePath, body);
            }
            catch (ApiException ex) { status = ex.StatusCode; result = new { ok = false, error = ex.Message }; }
            catch (Exception ex) { status = 400; result = new { ok = false, error = ex.Message }; }
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(apiJson.Serialize(status >= 400 ? result : new { ok = true, result = result }));
                context.Response.StatusCode = status; context.Response.ContentType = "application/json; charset=utf-8"; context.Response.ContentLength64 = bytes.Length;
                using (Stream output = context.Response.OutputStream) await output.WriteAsync(bytes, 0, bytes.Length);
            }
            catch { try { context.Response.Abort(); } catch { } }
        }

        private Task<object> ExecuteApiOnUi(string method, string path, Dictionary<string, object> body)
        {
            TaskCompletionSource<object> source = new TaskCompletionSource<object>();
            Window.Dispatcher.BeginInvoke(new Action(async delegate
            {
                try { source.SetResult(await ExecuteApi(method, path, body)); }
                catch (Exception ex) { source.SetException(ex); }
            }));
            return source.Task;
        }

        private static string ApiValue(Dictionary<string, object> body, string key)
        {
            object value;
            return body != null && body.TryGetValue(key, out value) && value != null ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : "";
        }

        private static bool ApiBoolean(Dictionary<string, object> body, string key)
        {
            object value; return body != null && body.TryGetValue(key, out value) && value != null && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private async Task<object> ExecuteApi(string method, string path, Dictionary<string, object> body)
        {
            if (method == "GET" && path == "/api/v1/status")
                return new { connected = Connected, busy = busy, profile = selectedProfile == null ? "" : selectedProfile.Name, host = Connected ? selectedProfile.Connection.Host : "", currentPath = Connected ? client.CurrentPath : "", apiPort = apiPort };
            if (method == "GET" && path == "/api/v1/files")
            {
                if (!Connected) throw new ApiException(409, "FTP 尚未連線。");
                return items.Select(x => new { name = x.Name, path = x.FullPath, type = x.IsDirectory ? "directory" : x.IsLink ? "link" : "file", size = x.Size, modified = x.Modified }).ToArray();
            }
            if (busy) throw new ApiException(409, "目前有傳輸工作進行中，請稍後重試。");
            if (!Connected) throw new ApiException(409, "FTP 尚未連線。");
            if (method == "POST" && path == "/api/v1/navigate")
            {
                string target = ApiValue(body, "path"); if (target.Length == 0) throw new ApiException(400, "path 為必填欄位。");
                await ApiWork("AI API：切換目錄…", delegate { client.ChangeDirectory(target); return null; }, true);
                return new { path = client.CurrentPath };
            }
            if (method == "POST" && path == "/api/v1/folders")
            {
                string name = ApiValue(body, "name");
                try { FtpClient.GuardName(name); } catch { throw new ApiException(400, "資料夾名稱不正確。"); }
                if (items.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ApiException(409, "目前目錄已有同名項目。");
                await ApiWork("AI API：新增資料夾…", delegate { client.CreateDirectory(name); return null; }, true);
                return new { name = name, path = FtpClient.Join(client.CurrentPath, name) };
            }
            if (method == "POST" && path == "/api/v1/upload")
            {
                string localPath = ApiValue(body, "localPath"), remoteName = ApiValue(body, "remoteName");
                if (!File.Exists(localPath)) throw new ApiException(400, "localPath 找不到本機檔案。");
                if (remoteName.Length == 0) remoteName = Path.GetFileName(localPath);
                try { FtpClient.GuardName(remoteName); } catch { throw new ApiException(400, "remoteName 不正確。"); }
                RemoteItem existing = items.FirstOrDefault(x => x.Name.Equals(remoteName, StringComparison.OrdinalIgnoreCase));
                if (existing != null && (!ApiBoolean(body, "overwrite") || existing.IsDirectory || existing.IsLink)) throw new ApiException(409, "遠端已存在同名項目；僅一般檔案可用 overwrite=true 覆寫。");
                string destination = FtpClient.Join(client.CurrentPath, remoteName);
                await ApiWork("AI API：上傳 " + remoteName + "…", delegate { client.Upload(localPath, destination, null); return null; }, true);
                return new { name = remoteName, path = destination };
            }
            if (method == "POST" && path == "/api/v1/download")
            {
                string name = ApiValue(body, "name"), folder = ApiValue(body, "destination");
                if (string.IsNullOrWhiteSpace(folder)) throw new ApiException(400, "destination 本機資料夾為必填欄位。");
                RemoteItem item = items.FirstOrDefault(x => x.Name.Equals(name, StringComparison.Ordinal));
                if (item == null || item.IsDirectory || item.IsLink) throw new ApiException(404, "找不到可下載的檔案。");
                string saved = await ApiWork("AI API：下載 " + item.Name + "…", delegate { return client.Download(item, folder, null); }, false) as string;
                return new { name = item.Name, localPath = saved };
            }
            if ((method == "POST" && path == "/api/v1/delete") || (method == "DELETE" && path == "/api/v1/files"))
            {
                if (!ApiBoolean(body, "confirm")) throw new ApiException(400, "刪除必須明確傳入 confirm=true。");
                string name = ApiValue(body, "name"); RemoteItem item = items.FirstOrDefault(x => x.Name.Equals(name, StringComparison.Ordinal));
                if (item == null) throw new ApiException(404, "找不到目前目錄中的項目。");
                await ApiWork("AI API：刪除 " + item.Name + "…", delegate { client.Delete(item, null, 0); return null; }, true);
                return new { deleted = name };
            }
            throw new ApiException(404, "找不到 API 路徑。");
        }

        private async Task<object> ApiWork(string title, Func<object> operation, bool refresh)
        {
            if (busy) throw new ApiException(409, "目前有其他工作進行中。");
            busy = true; cancellation = new CancellationTokenSource();
            if (client != null) client.UseCancellation(cancellation.Token);
            Label("TaskLabel").Text = title; Label("StatusLabel").Text = title; UpdateActions();
            try
            {
                object result = await Task.Run(operation);
                if (refresh) items.Clear();
                if (refresh) items.AddRange(await Task.Run(delegate { return client.ListDirectory(); }));
                Label("TaskLabel").Text = "AI 控制完成"; Label("StatusLabel").Text = "AI 控制完成 · " + DateTime.Now.ToString("HH:mm:ss");
                return result;
            }
            catch (Exception ex)
            {
                Disconnect(); Label("TaskLabel").Text = Friendly(ex); Label("StatusLabel").Text = Friendly(ex);
                throw new ApiException(502, Friendly(ex));
            }
            finally
            {
                busy = false; if (client != null) client.UseCancellation(CancellationToken.None);
                cancellation.Dispose(); cancellation = null; UpdateList(); UpdateActions();
                if (closeRequested) Window.Close();
            }
        }

        private sealed class ApiException : Exception
        {
            public int StatusCode;
            public ApiException(int statusCode, string message) : base(message) { StatusCode = statusCode; }
        }

        private TextBox AddTextField(Panel panel, string label, string value, double height)
        {
            panel.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(99, 99, 109)), FontSize = 12, Margin = new Thickness(0, 11, 0, 6) });
            TextBox box = new TextBox { Text = value ?? "", Height = height, Padding = new Thickness(11, 8, 11, 8), Background = new SolidColorBrush(Color.FromRgb(246, 247, 250)), BorderBrush = new SolidColorBrush(Color.FromRgb(226, 227, 232)), BorderThickness = new Thickness(1) };
            panel.Children.Add(box); return box;
        }

        private ConnectionSettings AskForConnection(ConnectionSettings current)
        {
            if (current == null) current = new ConnectionSettings();
            Window dialog = new Window { Title = "新增 FTP 帳號", Owner = Window, Width = 470, Height = 610, MinWidth = 440, MinHeight = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(245, 245, 247)), FontFamily = Window.FontFamily };
            Grid root = new Grid { Margin = new Thickness(22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            StackPanel heading = new StackPanel { Margin = new Thickness(2, 0, 0, 13) };
            heading.Children.Add(new TextBlock { Text = "新增 FTP 帳號", FontSize = 21, FontWeight = FontWeights.SemiBold });
            heading.Children.Add(new TextBlock { Text = "使用者名稱會作為帳號名稱，密碼只需輸入一次。", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 130)), Margin = new Thickness(0, 5, 0, 0) });
            Grid.SetRow(heading, 0); root.Children.Add(heading);
            StackPanel form = new StackPanel();
            TextBox host = AddTextField(form, "伺服器", current.Host, 38);
            TextBox port = AddTextField(form, "連接埠", current.Port.ToString(), 38);
            TextBox user = AddTextField(form, "使用者名稱", "", 38);
            TextBox password = AddTextField(form, "密碼", "", 38);
            TextBox initial = AddTextField(form, "起始目錄", string.IsNullOrWhiteSpace(current.InitialPath) ? "/" : current.InitialPath, 38);
            form.Children.Add(new TextBlock { Text = "檔名編碼", Foreground = new SolidColorBrush(Color.FromRgb(99, 99, 109)), FontSize = 12, Margin = new Thickness(0, 11, 0, 6) });
            ComboBox encoding = new ComboBox { Height = 38, Padding = new Thickness(9, 6, 9, 6), ItemsSource = new string[] { "UTF-8", "GBK", "Big5" }, SelectedItem = current.EncodingName };
            if (encoding.SelectedItem == null) encoding.SelectedIndex = 0;
            form.Children.Add(encoding);
            ScrollViewer scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1); root.Children.Add(scroll);
            StackPanel footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            Button cancel = new Button { Content = "取消", MinWidth = 82, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            Button save = new Button { Content = "儲存帳號", MinWidth = 105, IsDefault = true, Background = new SolidColorBrush(Color.FromRgb(0, 122, 255)), Foreground = Brushes.White };
            footer.Children.Add(cancel); footer.Children.Add(save); Grid.SetRow(footer, 2); root.Children.Add(footer); dialog.Content = root;
            ConnectionSettings result = null;
            save.Click += delegate
            {
                int parsedPort;
                if (string.IsNullOrWhiteSpace(host.Text) || host.Text.Contains("://") || host.Text.Contains("/") || host.Text.Any(char.IsWhiteSpace)) { MessageBox.Show(dialog, "請輸入不含 ftp:// 的伺服器名稱或 IP。", "伺服器格式不正確", MessageBoxButton.OK, MessageBoxImage.Information); host.Focus(); return; }
                if (!int.TryParse(port.Text, out parsedPort) || parsedPort < 1 || parsedPort > 65535) { MessageBox.Show(dialog, "連接埠請輸入 1 到 65535。", "連接埠不正確", MessageBoxButton.OK, MessageBoxImage.Information); port.Focus(); return; }
                if (string.IsNullOrWhiteSpace(user.Text)) { MessageBox.Show(dialog, "請輸入 FTP 使用者名稱。", "需要帳號", MessageBoxButton.OK, MessageBoxImage.Information); user.Focus(); return; }
                result = new ConnectionSettings { Host = host.Text.Trim(), Port = parsedPort, User = user.Text.Trim(), Password = password.Text, InitialPath = string.IsNullOrWhiteSpace(initial.Text) ? "/" : initial.Text.Trim(), EncodingName = encoding.SelectedItem == null ? "UTF-8" : encoding.SelectedItem.ToString() };
                dialog.DialogResult = true; dialog.Close();
            };
            dialog.Loaded += delegate { if (string.IsNullOrWhiteSpace(host.Text)) host.Focus(); else user.Focus(); };
            return dialog.ShowDialog() == true ? result : null;
        }

        private SavedProfile AddOrUpdateProfile(ConnectionSettings settings)
        {
            SavedProfile existing = profiles.FirstOrDefault(x => x.Connection != null && string.Equals(x.Connection.Host, settings.Host, StringComparison.OrdinalIgnoreCase) && x.Connection.Port == settings.Port && string.Equals(x.Connection.User, settings.User, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { existing.Connection = settings; return existing; }
            existing = profiles.FirstOrDefault(x => x.Connection != null && string.IsNullOrWhiteSpace(x.Connection.Host) && string.IsNullOrWhiteSpace(x.Connection.User));
            if (existing != null) { existing.Connection = settings; return existing; }
            SavedProfile added = new SavedProfile { Connection = settings }; profiles.Add(added); return added;
        }

        private ConnectionSettings NewAccountDefaults()
        {
            ConnectionSettings current = selectedProfile == null ? new ConnectionSettings() : selectedProfile.Connection;
            return new ConnectionSettings
            {
                Host = current == null ? "" : current.Host,
                Port = current == null ? 21 : current.Port,
                InitialPath = "/",
                EncodingName = current == null ? "UTF-8" : current.EncodingName
            };
        }

        private void SelectProfile(SavedProfile profile)
        {
            if (profile == null) return;
            if (Connected && profile != selectedProfile) Disconnect();
            selectedProfile = profile;
            ComboBox selector = Find<ComboBox>("ProfileBox");
            loadingProfiles = true; selector.Items.Refresh(); selector.SelectedItem = profile; loadingProfiles = false;
            ApplyProfile(profile); UpdateProfileSummary(); SaveProfiles();
        }

        private void AddProfile()
        {
            if (busy) return;
            StoreCurrentProfile();
            ConnectionSettings entered = AskForConnection(NewAccountDefaults());
            if (entered == null) return;
            SavedProfile profile = AddOrUpdateProfile(entered);
            SelectProfile(profile);
        }

        private void ManageProfiles()
        {
            if (busy) return;
            StoreCurrentProfile();
            Window dialog = new Window { Title = "FTP 帳號", Owner = Window, Width = 540, Height = 430, MinWidth = 480, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(245, 245, 247)), FontFamily = Window.FontFamily };
            Grid root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            StackPanel heading = new StackPanel { Margin = new Thickness(3, 0, 0, 12) };
            heading.Children.Add(new TextBlock { Text = "選擇 FTP 帳號", FontSize = 21, FontWeight = FontWeights.SemiBold });
            heading.Children.Add(new TextBlock { Text = "帳號名稱就是使用者名稱；選取後會帶入已保存的密碼。", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 130)), Margin = new Thickness(0, 5, 0, 0) });
            root.Children.Add(heading);
            ListBox list = new ListBox { ItemsSource = profiles, SelectedItem = selectedProfile, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(2) };
            list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            DataTemplate card = new DataTemplate(typeof(SavedProfile));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.BackgroundProperty, Brushes.White); border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(229, 229, 235))); border.SetValue(Border.BorderThicknessProperty, new Thickness(1)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12)); border.SetValue(Border.PaddingProperty, new Thickness(15, 12, 15, 12)); border.SetValue(Border.MarginProperty, new Thickness(1, 3, 1, 3));
            FrameworkElementFactory stack = new FrameworkElementFactory(typeof(StackPanel));
            FrameworkElementFactory title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new Binding("Name")); title.SetValue(TextBlock.FontSizeProperty, 14.0); title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); stack.AppendChild(title);
            FrameworkElementFactory details = new FrameworkElementFactory(typeof(TextBlock)); details.SetBinding(TextBlock.TextProperty, new Binding("ServerLabel")); details.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(120, 120, 130))); details.SetValue(TextBlock.FontSizeProperty, 11.0); details.SetValue(TextBlock.MarginProperty, new Thickness(0, 5, 0, 0)); stack.AppendChild(details);
            border.AppendChild(stack); card.VisualTree = border; list.ItemTemplate = card;
            list.MouseDoubleClick += delegate { if (list.SelectedItem is SavedProfile) { SelectProfile((SavedProfile)list.SelectedItem); dialog.Close(); } };
            Grid.SetRow(list, 1); root.Children.Add(list);
            StackPanel footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button add = new Button { Content = "＋ 新增帳號", Margin = new Thickness(0, 0, 7, 0), Padding = new Thickness(11, 7, 11, 7) };
            Button delete = new Button { Content = "移除帳號", Margin = new Thickness(0, 0, 7, 0), Padding = new Thickness(11, 7, 11, 7), Foreground = new SolidColorBrush(Color.FromRgb(220, 67, 67)) };
            Button use = new Button { Content = "使用此帳號", Padding = new Thickness(13, 7, 13, 7), Background = new SolidColorBrush(Color.FromRgb(0, 122, 255)), Foreground = Brushes.White, IsDefault = true };
            footer.Children.Add(add); footer.Children.Add(delete); footer.Children.Add(use); Grid.SetRow(footer, 2); root.Children.Add(footer); dialog.Content = root;
            add.Click += delegate
            {
                ConnectionSettings entered = AskForConnection(NewAccountDefaults()); if (entered == null) return;
                SavedProfile added = AddOrUpdateProfile(entered); list.Items.Refresh(); list.SelectedItem = added; SelectProfile(added);
            };
            delete.Click += delegate
            {
                SavedProfile chosen = list.SelectedItem as SavedProfile; if (chosen == null || string.IsNullOrWhiteSpace(chosen.Connection.User)) return;
                if (MessageBox.Show(dialog, "移除此電腦保存的「" + chosen.Name + "」帳號與密碼？", "移除 FTP 帳號", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                bool wasSelected = chosen == selectedProfile; profiles.Remove(chosen);
                if (profiles.Count == 0) profiles.Add(new SavedProfile());
                list.Items.Refresh(); list.SelectedItem = profiles[0];
                if (wasSelected) SelectProfile((SavedProfile)list.SelectedItem); else SaveProfiles();
            };
            use.Click += delegate { SavedProfile chosen = list.SelectedItem as SavedProfile; if (chosen != null) SelectProfile(chosen); dialog.Close(); };
            dialog.ShowDialog();
        }

        private async Task Connect(bool automatic = false)
        {
            if (busy) return;
            ConnectionSettings settings;
            try
            {
                settings = ReadSettings();
                if (automatic) savedPassword = settings.Password;
                else SaveSettings(settings);
            }
            catch (Exception ex) { ShowError(ex); return; }
            Disconnect();
            await Run("正在連線到 " + settings.Host + "…", delegate
            {
                client = new FtpClient(); client.Connect(settings, cancellation.Token);
                return client.ListDirectory();
            }, "已連線 · " + settings.Host);
        }

        private async Task Navigate(string path)
        {
            if (!Connected || busy) return;
            await Run("正在讀取資料夾…", delegate { client.ChangeDirectory(path); return client.ListDirectory(); }, "目錄已更新");
        }

        private void Disconnect()
        {
            if (client != null) { client.Dispose(); client = null; }
            items.Clear(); UpdateList();
            Label("ConnectionLabel").Text = "尚未連線";
            Label("ConnectionHint").Text = "設定將在連線時儲存於本機。";
            Find<System.Windows.Shapes.Ellipse>("ConnectionDot").Fill = new SolidColorBrush(Color.FromRgb(174, 174, 183));
            Label("StatusLabel").Text = "連線已中斷"; UpdateActions();
        }

        private async Task Run(string title, Func<List<RemoteItem>> work, string success)
        {
            if (busy) return;
            busy = true; cancellation = new CancellationTokenSource();
            if (client != null) client.UseCancellation(cancellation.Token);
            int version = ++operationVersion;
            Label("TaskLabel").Text = title; Label("StatusLabel").Text = title; Label("TransferLabel").Text = "";
            ProgressBar bar = Find<ProgressBar>("ProgressBar"); bar.IsIndeterminate = true; bar.Value = 0;
            Button("CancelButton").Visibility = Visibility.Visible; UpdateActions();
            try
            {
                List<RemoteItem> result = await Task.Run(work);
                cancellation.Token.ThrowIfCancellationRequested();
                if (result != null) { items.Clear(); items.AddRange(result); Text("SearchBox").Clear(); }
                if (Connected)
                {
                    Text("PathBox").Text = client.CurrentPath;
                    Label("ConnectionLabel").Text = "已連線"; Label("ConnectionHint").Text = "修改左側欄位後，按連線重新套用。";
                    Find<System.Windows.Shapes.Ellipse>("ConnectionDot").Fill = new SolidColorBrush(Color.FromRgb(48, 180, 111));
                }
                Label("TaskLabel").Text = success; Label("StatusLabel").Text = success + " · " + DateTime.Now.ToString("HH:mm:ss");
                bar.Value = 100; Log(success);
            }
            catch (Exception ex)
            {
                bool cancelled = cancellation.IsCancellationRequested;
                Disconnect();
                string message = cancelled ? "已取消操作，請重新連線；已完成的遠端變更會保留。" : Friendly(ex);
                Label("TaskLabel").Text = message; Label("StatusLabel").Text = message; Label("TransferLabel").Text = "";
                bar.Value = 0; Log(message);
                if (!cancelled && !closeRequested && !args.Contains("--auto-connect-test")) MessageBox.Show(Window, message + "\n\n連線已中斷。請確認設定與網路後重新連線。", "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                operationVersion = version + 1;
                busy = false; bar.IsIndeterminate = false; Button("CancelButton").Visibility = Visibility.Collapsed;
                if (client != null) client.UseCancellation(CancellationToken.None);
                cancellation.Dispose(); cancellation = null; UpdateList(); UpdateActions();
                if (closeRequested) Window.Close();
            }
        }

        private IProgress<TransferProgress> Progress(string verb, int index, int count, int version)
        {
            return new UiProgress<TransferProgress>(Window.Dispatcher, delegate(TransferProgress p)
            {
                if (!busy || operationVersion != version) return;
                Label("TaskLabel").Text = string.Format("{0} ({1}/{2}) · {3}", verb, index, count, p.Name);
                ProgressBar bar = Find<ProgressBar>("ProgressBar"); bar.IsIndeterminate = p.Total < 0;
                if (p.Total >= 0) bar.Value = 100.0 * ((index - 1) + (p.Total == 0 ? 1 : Math.Min(1.0, (double)p.Bytes / p.Total))) / count;
                Label("TransferLabel").Text = RemoteItem.FormatSize(p.Bytes) + (p.Total >= 0 ? " / " + RemoteItem.FormatSize(p.Total) : "");
            });
        }

        private async Task Download(bool all)
        {
            if (busy || !Connected) return;
            List<RemoteItem> selected = (all ? items : Files.SelectedItems.Cast<RemoteItem>()).Where(x => !x.IsDirectory && !x.IsLink).ToList();
            if (selected.Count == 0) { MessageBox.Show(Window, "請選取要下載的檔案；資料夾與連結不包含在下載範圍。", "下載檔案"); return; }
            string folder;
            using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "選擇下載位置（同名檔案會自動加編號，保留原檔）"; dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog(new System.Windows.Forms.NativeWindowOwner(new System.Windows.Interop.WindowInteropHelper(Window).Handle)) != System.Windows.Forms.DialogResult.OK) return;
                folder = dialog.SelectedPath;
            }
            await Run("準備下載 " + selected.Count + " 個檔案…", delegate
            {
                for (int i = 0; i < selected.Count; i++) client.Download(selected[i], folder, Progress("下載中", i + 1, selected.Count, operationVersion));
                return null;
            }, "下載完成 · " + selected.Count + " 個檔案 · " + folder);
        }

        private async Task NewFolder()
        {
            if (busy || !Connected) return;
            Window dialog = new Window { Owner = Window, Title = "新增資料夾", Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            StackPanel panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = "請輸入新資料夾名稱：", Margin = new Thickness(0, 0, 0, 9) });
            TextBox input = new TextBox { MinWidth = 350, Height = 32, Padding = new Thickness(6) };
            panel.Children.Add(input);
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            Button cancel = new Button { Content = "取消", MinWidth = 72, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            Button confirm = new Button { Content = "建立", MinWidth = 72, IsDefault = true };
            confirm.Click += delegate { dialog.DialogResult = true; };
            actions.Children.Add(cancel); actions.Children.Add(confirm); panel.Children.Add(actions); dialog.Content = panel;
            dialog.Loaded += delegate { input.Focus(); };
            if (dialog.ShowDialog() != true) return;
            await CreateFolder(input.Text.Trim());
        }

        private async Task CreateFolder(string name)
        {
            if (busy || !Connected) return;
            try { FtpClient.GuardName(name); }
            catch { MessageBox.Show(Window, "請輸入資料夾名稱，不能包含斜線或換行。", "名稱不正確", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (items.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(Window, "目前資料夾已有同名項目，請使用其他名稱。", "無法新增", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await Run("正在新增資料夾 · " + name + "…", delegate { client.CreateDirectory(name); return client.ListDirectory(); }, "資料夾已建立 · " + name);
        }

        private async Task Upload()
        {
            if (busy || !Connected) return;
            OpenFileDialog dialog = new OpenFileDialog { Title = "選擇要上傳的檔案", Multiselect = true };
            if (dialog.ShowDialog(Window) != true) return;
            List<string> paths = new List<string>();
            foreach (string file in dialog.FileNames)
            {
                string name = Path.GetFileName(file); FtpClient.GuardName(name);
                RemoteItem existing = items.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (existing.IsDirectory || existing.IsLink) { MessageBox.Show(Window, "遠端已有同名資料夾或連結：" + name + "\n請先重新命名本機檔案。", "無法上傳"); continue; }
                    if (MessageBox.Show(Window, "遠端已有「" + name + "」。\n要覆寫這個檔案嗎？", "確認覆寫", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) continue;
                }
                paths.Add(file);
            }
            if (paths.Count == 0) return;
            string destination = client.CurrentPath;
            await Run("準備上傳 " + paths.Count + " 個檔案…", delegate
            {
                for (int i = 0; i < paths.Count; i++) client.Upload(paths[i], FtpClient.Join(destination, Path.GetFileName(paths[i])), Progress("上傳中", i + 1, paths.Count, operationVersion));
                return client.ListDirectory();
            }, "上傳完成 · " + paths.Count + " 個檔案");
        }

        private async Task Delete()
        {
            if (busy || !Connected || Files.SelectedItems.Count == 0) return;
            List<RemoteItem> selected = Files.SelectedItems.Cast<RemoteItem>().ToList();
            string names = string.Join("\n", selected.Take(8).Select(x => "• " + x.Name).ToArray()) + (selected.Count > 8 ? "\n…" : "");
            string message = "即將從 " + client.CurrentPath + " 刪除 " + selected.Count + " 個項目：\n\n" + names + "\n\n資料夾內的所有內容也會刪除，無法復原。";
            if (MessageBox.Show(Window, message, "確認永久刪除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            IProgress<string> status = new Progress<string>(delegate(string name) { if (busy) Label("TaskLabel").Text = "正在刪除 · " + name; });
            await Run("正在刪除選取項目…", delegate
            {
                foreach (RemoteItem item in selected) client.Delete(item, name => status.Report(name), 0);
                return client.ListDirectory();
            }, "刪除完成 · " + selected.Count + " 個項目");
        }

        private void UpdateList()
        {
            if (Files.ItemsSource == null) return;
            string query = Text("SearchBox").Text.Trim();
            ICollectionView view = CollectionViewSource.GetDefaultView(Files.ItemsSource);
            view.Filter = delegate(object value) { return ((RemoteItem)value).Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0; }; view.Refresh();
            Find<TextBlock>("SearchPlaceholder").Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            int shown = view.Cast<object>().Count();
            Label("CountLabel").Text = Connected ? items.Count(x => x.IsDirectory) + " 個資料夾 · " + items.Count(x => !x.IsDirectory) + " 個檔案" + (query.Length > 0 ? " · 符合 " + shown + " 項" : "") : "連線後即可瀏覽資料夾與檔案";
            Find<StackPanel>("EmptyPanel").Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
            Label("EmptyTitle").Text = !Connected ? "你的檔案，準備就緒" : query.Length > 0 ? "沒有符合的檔案" : "這裡還沒有檔案";
            Label("EmptyHint").Text = !Connected ? "選擇左側帳號，或按「＋」新增帳號，再按連線。" : query.Length > 0 ? "換個關鍵字，或清除搜尋條件再試一次。" : "按「上傳檔案」，把第一個檔案放到這裡。";
            SizeColumns(); UpdateActions();
        }

        private void UpdateActions()
        {
            bool ready = Connected && !busy;
            foreach (string name in new string[] { "RootButton", "UpButton", "GoButton", "RefreshButton", "UploadButton", "NewFolderButton", "DownloadAllButton" }) Button(name).IsEnabled = ready;
            Button("ListViewButton").IsEnabled = !busy; Button("TileViewButton").IsEnabled = !busy;
            Button("DownloadAllButton").IsEnabled = ready && items.Any(x => !x.IsDirectory && !x.IsLink);
            Button("DownloadButton").IsEnabled = ready && Files.SelectedItems.Cast<RemoteItem>().Any(x => !x.IsDirectory && !x.IsLink);
            Button("DeleteButton").IsEnabled = ready && Files.SelectedItems.Count > 0;
            Button("ConnectButton").IsEnabled = !busy; Button("DisconnectButton").IsEnabled = ready;
            Find<ComboBox>("ProfileBox").IsEnabled = !busy; Button("ManageAccountsInSettingsButton").IsEnabled = !busy;
            Button("AddProfileButton").IsEnabled = !busy;
            foreach (string name in new string[] { "HostBox", "PortBox", "UserBox", "PassBox", "InitialBox" }) Text(name).IsEnabled = !busy;
            Find<ComboBox>("EncodingBox").IsEnabled = !busy; Text("PathBox").IsEnabled = ready; Files.IsEnabled = !busy;
            Label("SelectionLabel").Text = Files.SelectedItems.Count > 0 ? "已選取 " + Files.SelectedItems.Count + " 項" : "可多選檔案 · 雙擊資料夾進入";
        }

        private string Friendly(Exception ex)
        {
            string message = ex.Message;
            if (!string.IsNullOrEmpty(savedPassword)) message = message.Replace(savedPassword, "[密碼]");
            if (ex is System.Net.Sockets.SocketException) return "網路連線失敗：" + message;
            if (ex is DecoderFallbackException) return "檔名編碼不相容，請改選 GBK 或 Big5 後重新連線。";
            return message;
        }
        private void ShowError(Exception ex) { if (!args.Contains("--auto-connect-test")) MessageBox.Show(Window, Friendly(ex), "請檢查連線設定", MessageBoxButton.OK, MessageBoxImage.Information); }

        private void FinishAutoConnectTest()
        {
            bool expectFailure = args.Contains("--expect-auto-failure");
            bool ok = expectFailure
                ? !Connected && Button("ConnectButton").IsEnabled && Text("HostBox").IsEnabled
                : Connected && client.CurrentPath == "/simon" && Files.Items.Count == 3 && Button("UploadButton").IsEnabled;
            string output = Path.Combine(basePath, "verification");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "auto-connect.txt"), ok ? "PASS: startup connection state matches the expected result" : "FAIL: unexpected startup connection state", Encoding.UTF8);
            Environment.ExitCode = ok ? 0 : 1;
            Window.Close();
        }
        private void Log(string message)
        {
            try
            {
                string logPath = Path.Combine(basePath, "logs"); Directory.CreateDirectory(logPath);
                File.AppendAllText(Path.Combine(logPath, DateTime.Now.ToString("yyyy-MM-dd") + ".log"), DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private async Task Smoke()
        {
            string output = Path.Combine(basePath, "verification"); Directory.CreateDirectory(output);
            List<string> checks = new List<string>();
            try
            {
                XElement preset;
                using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("DefaultSettings.xml"))
                    preset = XElement.Load(source);
                if (string.IsNullOrEmpty((string)preset.Element("Host")) || string.IsNullOrEmpty((string)preset.Element("User")) || string.IsNullOrEmpty((string)preset.Element("Password")))
                    throw new Exception("Original connection presets are missing from the executable");
                checks.Add("PASS: original host, account and password are embedded in the executable");
                if (args.Contains("--expect-embedded-presets"))
                {
                    if (Text("HostBox").Text != (string)preset.Element("Host") || Text("UserBox").Text != (string)preset.Element("User") ||
                        Text("PassBox").Text != (string)preset.Element("Password") || Text("InitialBox").Text != (string)preset.Element("InitialPath"))
                        throw new Exception("Standalone executable did not prefill the original connection data");
                    checks.Add("PASS: original login and path prefill without a working external settings file");
                }
                if (args.Contains("--expect-custom-presets"))
                {
                    if (Text("HostBox").Text != "custom-host.invalid" || Text("UserBox").Text != "custom-user" ||
                        Text("PassBox").Text != "custom-password" || Text("InitialBox").Text != "/custom")
                        throw new Exception("Saved editable connection settings did not override the original defaults");
                    checks.Add("PASS: saved editable login and path override the embedded presets");
                }
                if (Text("PassBox") == null || Text("PassBox").IsReadOnly) throw new Exception("Password must be a visible editable TextBox");
                Text("HostBox").Text = "127.0.0.1"; Text("UserBox").Text = "demo-user"; Text("PassBox").Text = "editable-password";
                Text("InitialBox").Text = "/design"; Text("PathBox").Text = "/design";
                checks.Add("PASS: editable host, account and visible password TextBoxes");
                if (Button("UploadButton").IsEnabled || Button("DeleteButton").IsEnabled) throw new Exception("Offline actions must be disabled");
                checks.Add("PASS: offline transfer and delete disabled");
                await Task.Delay(200); Snapshot(Path.Combine(output, "ui-offline.png"));
                // UI fixture only; never used during normal startup and never connects to the preset server.
                items.Add(new RemoteItem { Name = "設計素材", FullPath = "/design/設計素材", IsDirectory = true, Size = -1, Modified = "2026/10/23 09:20" });
                items.Add(new RemoteItem { Name = "設備點檢紀錄.xlsx", FullPath = "/design/設備點檢紀錄.xlsx", Size = 248832, Modified = "2026/10/23 09:35" });
                items.Add(new RemoteItem { Name = "產品照片.png", FullPath = "/design/產品照片.png", Size = 2478120, Modified = "2026/10/22 16:12" });
                items.Add(new RemoteItem { Name = "使用說明.pdf", FullPath = "/design/使用說明.pdf", Size = 856402, Modified = "2026/10/21 10:08" });
                UpdateList(); Files.SelectedIndex = 1;
                SetViewMode(true, false);
                await Task.Delay(150); Window.UpdateLayout();
                FrameworkElement firstTile = Files.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
                FrameworkElement secondTile = Files.ItemContainerGenerator.ContainerFromIndex(1) as FrameworkElement;
                if (Files.View != null || firstTile == null || secondTile == null) throw new Exception("Tile view did not create its items");
                Point firstPosition = firstTile.TranslatePoint(new Point(0, 0), Files);
                Point secondPosition = secondTile.TranslatePoint(new Point(0, 0), Files);
                if (secondPosition.X <= firstPosition.X || Math.Abs(secondPosition.Y - firstPosition.Y) > 2 || Files.SelectedItems.Count != 1) throw new Exception("Tiles did not arrange horizontally or preserve selection");
                checks.Add("PASS: horizontal icon view arranges items side by side and preserves selection");
                Snapshot(Path.Combine(output, "ui-tiles.png"));
                SetViewMode(false, false);
                if (Files.View != detailView || Files.Items.Count != 4 || Files.SelectedItems.Count != 1) throw new Exception("Switching back to list view failed");
                checks.Add("PASS: list view restores columns, items and selection");
                Text("SearchBox").Text = "點檢";
                if (Files.Items.Count != 1) throw new Exception("Search filtering failed");
                checks.Add("PASS: Chinese search filters the actual ListView");
                Text("SearchBox").Clear(); if (Files.Items.Count != 4) throw new Exception("Clear search failed");
                checks.Add("PASS: clear search restores all four fixture items");
                Label("ConnectionLabel").Text = "介面預覽"; Label("CountLabel").Text = "1 個資料夾 · 3 個檔案 · 預覽資料";
                Label("ConnectionHint").Text = "畫面驗證資料，未連線至伺服器。";
                Label("StatusLabel").Text = "UI 預覽 · 此畫面使用測試資料";
                await Task.Delay(150); Snapshot(Path.Combine(output, "ui-files.png"));
                Window.Width = Window.MinWidth; Window.Height = Window.MinHeight;
                await Task.Delay(150); Snapshot(Path.Combine(output, "ui-minimum.png"));
                if (Find<ScrollViewer>("SettingsScroll").ScrollableHeight > 1) throw new Exception("Settings overflow at minimum window size");
                checks.Add("PASS: all connection fields fit at minimum size without scrolling");
                checks.Add("PASS: rendered default and minimum-size window screenshots");
                int testPortIndex = Array.IndexOf(args, "--test-port");
                if (testPortIndex >= 0)
                {
                    Text("HostBox").Text = "127.0.0.1"; Text("PortBox").Text = args[testPortIndex + 1];
                    Text("UserBox").Text = "ui-edited-user"; Text("PassBox").Text = "ui-edited-password"; Text("InitialBox").Text = "/simon";
                    await Connect();
                    if (!Connected || Files.Items.Count != 3 || !Button("UploadButton").IsEnabled) throw new Exception("UI connect/list/action state failed");
                    checks.Add("PASS: live UI connection reads edited settings and lists loopback FTP files");
                    if (!Button("NewFolderButton").IsEnabled) throw new Exception("New folder button did not enable after connection");
                    bool submittedFolderDialog = false;
                    DispatcherTimer folderDialogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                    folderDialogTimer.Tick += delegate
                    {
                        Window prompt = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w != Window && w.Title == "新增資料夾");
                        if (prompt == null) return;
                        StackPanel panel = (StackPanel)prompt.Content;
                        TextBox nameInput = panel.Children.OfType<TextBox>().First();
                        Button create = panel.Children.OfType<StackPanel>().First().Children.OfType<Button>().First(b => (string)b.Content == "建立");
                        nameInput.Text = "新增測試資料夾";
                        submittedFolderDialog = true;
                        folderDialogTimer.Stop();
                        create.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    };
                    folderDialogTimer.Start();
                    Button("NewFolderButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    for (int attempt = 0; attempt < 100 && (busy || !items.Any(x => x.Name == "新增測試資料夾")); attempt++) await Task.Delay(50);
                    folderDialogTimer.Stop();
                    RemoteItem createdFolder = items.FirstOrDefault(x => x.Name == "新增測試資料夾" && x.IsDirectory);
                    if (!submittedFolderDialog || !Connected || createdFolder == null) throw new Exception("UI folder dialog, creation or refresh failed");
                    checks.Add("PASS: New Folder button and dialog create a Chinese-named folder and refresh the listing");
                    await Run("清理測試資料夾…", delegate { client.Delete(createdFolder, null, 0); return client.ListDirectory(); }, "測試資料夾已移除");
                    Files.SelectedItem = items.First(x => !x.IsDirectory);
                    if (!Button("DownloadButton").IsEnabled || !Button("DeleteButton").IsEnabled) throw new Exception("Connected selection actions failed");
                    checks.Add("PASS: download and delete enabled for connected file selection");
                    await Navigate("/simon/中文資料夾");
                    if (Text("PathBox").Text != "/simon/中文資料夾" || Files.Items.Count != 1) throw new Exception("UI navigation failed");
                    checks.Add("PASS: UI navigation updates path and listing from real FTP replies");
                    await Navigate("/simon");
                    RemoteItem transferItem = items.First(x => !x.IsDirectory && x.Size > 0);
                    string transferFolder = Path.Combine(output, "loopback-download");
                    string downloaded = null;
                    await Run("UI 傳輸測試…", delegate { downloaded = client.Download(transferItem, transferFolder, Progress("下載中", 1, 1, operationVersion)); return null; }, "UI 傳輸測試完成");
                    if (!Connected || downloaded == null || !File.Exists(downloaded) || Find<ProgressBar>("ProgressBar").Value != 100) throw new Exception("UI transfer/progress failed");
                    checks.Add("PASS: background FTP download dispatches progress to UI and completes at 100 percent");
                    await Run("UI 上傳測試…", delegate { client.Upload(downloaded, "/simon/ui-upload-test.txt", Progress("上傳中", 1, 1, operationVersion)); return client.ListDirectory(); }, "UI 上傳測試完成");
                    RemoteItem uploaded = items.FirstOrDefault(x => x.Name == "ui-upload-test.txt");
                    if (!Connected || uploaded == null) throw new Exception("UI upload/refresh failed");
                    await Run("UI 刪除測試…", delegate { client.Delete(uploaded, null, 0); return client.ListDirectory(); }, "UI 刪除測試完成");
                    if (!Connected || items.Any(x => x.Name == "ui-upload-test.txt")) throw new Exception("UI delete/refresh failed");
                    checks.Add("PASS: UI worker upload and delete refresh the real loopback listing");
                    File.Delete(downloaded);
                    await Task.Delay(100); Snapshot(Path.Combine(output, "ui-connected.png"));
                    Disconnect();
                    if (Button("UploadButton").IsEnabled || Files.Items.Count != 0) throw new Exception("Disconnect state failed");
                    checks.Add("PASS: disconnect clears listing and disables transfer actions");
                }
                File.WriteAllLines(Path.Combine(output, "ui-smoke.txt"), checks, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                checks.Add("FAIL: " + ex); File.WriteAllLines(Path.Combine(output, "ui-smoke.txt"), checks, Encoding.UTF8); Environment.ExitCode = 1;
            }
            Window.Close();
        }

        private void Snapshot(string path)
        {
            Window.UpdateLayout();
            FrameworkElement visual = Window.Content as FrameworkElement;
            RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual); PngBitmapEncoder png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(path)) png.Save(stream);
        }
    }

    internal sealed class UiProgress<T> : IProgress<T>
    {
        private readonly Dispatcher dispatcher;
        private readonly Action<T> callback;
        public UiProgress(Dispatcher dispatcher, Action<T> callback) { this.dispatcher = dispatcher; this.callback = callback; }
        public void Report(T value) { dispatcher.BeginInvoke(new Action(delegate { callback(value); })); }
    }
}

namespace System.Windows.Forms
{
    internal sealed class NativeWindowOwner : IWin32Window
    {
        public IntPtr Handle { get; private set; }
        public NativeWindowOwner(IntPtr handle) { Handle = handle; }
    }
}
