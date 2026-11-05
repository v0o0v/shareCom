using System.Diagnostics;
using Microsoft.Win32;
using ShareCom.App.Clipboard;
using ShareCom.App.Infra;
using ShareCom.App.Input;
using ShareCom.App.Native;
using ShareCom.App.UI;
using ShareCom.Core.Clipboard;
using ShareCom.Core.Files;
using ShareCom.Core.Identity;
using ShareCom.Core.Input;
using ShareCom.Core.Layout;
using ShareCom.Core.Net;
using ShareCom.Core.Protocol;
using ShareCom.Core.Update;
using Message = ShareCom.Core.Protocol.Message;

namespace ShareCom.App;

/// <summary>시작 방식. 로그인 자동 시작인지, 업데이트 직후인지, 업데이트를 되돌린 뒤인지.</summary>
internal sealed record StartupOptions(bool AtLogon = false, string? UpdatedEvent = null, string? UpdatedVersion = null, string? FailedVersion = null);

/// <summary>트레이에 상주하며 통신(Node)·입력 라우팅(InputRouter)·입력 훅·입력 재현을 잇는다.</summary>
internal sealed class TrayApp : ApplicationContext, IInputSink, IClipboardTransport, IFileTransport
{
    private readonly AppStorage _storage;
    private readonly JsonPeerStore _store;
    private readonly DeviceIdentity _identity;
    private readonly Node _node;
    private readonly InputRouter _router;
    private readonly InputInjector _injector = new();
    private readonly InputHooks _hooks;
    private readonly NotifyIcon _tray;
    private readonly Control _ui;
    private readonly ClipboardSync _clipboard;
    private readonly ClipboardWatcher _clipboardWatcher;
    private readonly FileTransferService _files;
    private readonly TrustSync _trust;
    private readonly LayoutSync _layout;
    private readonly UpdateService _updates;
    private LayoutEditorForm? _layoutEditor;
    private Guid? _controlling;
    /// <summary>마지막으로 조작을 시작·끝낸 시각. 끝난 뒤 5분은 업데이트하지 않는다.</summary>
    private DateTime _lastControlUtc = DateTime.UtcNow;
    /// <summary>알림 풍선을 누르면 열 주소(업데이트 알림일 때만).</summary>
    private Uri? _balloonUrl;
    /// <summary>설정 창에서 [업데이트]를 고른 업데이트. 창이 닫힌 뒤 적용한다.</summary>
    private ReadyUpdate? _manualUpdate;

    /// <summary>앱을 끈 뒤 Program 이 넘겨줄 업데이트. null 이면 그냥 끈다.</summary>
    public PendingUpdate? PendingUpdate { get; private set; }

    public static Version CurrentVersion => typeof(TrayApp).Assembly.GetName().Version ?? new Version(0, 0);

    public TrayApp(StartupOptions? startup = null)
    {
        startup ??= new StartupOptions();
        _ui = new Control();
        _ui.CreateControl(); // UI 스레드로 넘기는 BeginInvoke 용

        _storage = new AppStorage();
        _identity = _storage.LoadOrCreateIdentity();
        _store = new JsonPeerStore(_storage);
        Log.Info($"시작: {_identity.Name} ({_identity.Id}), 지문 {_identity.Thumbprint[..16]}…, 관리자={SystemSetup.IsAdministrator()}");

        _router = new InputRouter(this)
        {
            EdgeLocked = _storage.Settings.EdgeLocked,
            Hotkeys = _storage.Settings.Hotkeys ?? HotkeySettings.Default,
        };
        _router.EdgeLockChanged += locked =>
        {
            _storage.Settings.EdgeLocked = locked;
            SaveSettings();
            Notify(locked ? $"가장자리 넘기기를 잠갔습니다. ({HotkeySettings.LockKeyName(_router.Hotkeys.LockKey)} 로 풀기)" : "가장자리 넘기기 잠금을 풀었습니다.");
        };
        _router.SetSelf(_identity.Id);
        _router.SetLocalScreens(LocalMonitors(), Win32.PrimaryScalePercent());

        _node = new Node(_identity, _store, new UdpDiscovery(), new NodeOptions { EncryptionEnabled = _storage.Settings.EncryptionEnabled });
        _trust = new TrustSync(_node, _store);
        _trust.Log += text => Notify(text);

        _layout = new LayoutSync(_identity.Id, _node, id => _store.Get(id)?.Side, id => _store.Revoked.ContainsKey(id),
            _storage.Settings.Layout, _storage.Settings.RememberedLayout);
        _router.SetLayout(_layout.Doc.Entries);
        _layout.Changed += OnLayoutChanged;
        _layout.SetSelf(_identity.Name, LocalScreen());
        _store.Changed += () => _layout.Refresh();

        _node.Log += Log.Info;
        _node.PeerConnected += OnPeerConnected;
        _node.PeerDisconnected += OnPeerDisconnected;
        _node.MessageReceived += OnMessage;
        _node.PairingConfirmer = ConfirmPairingAsync;
        _node.PairingCompleted += OnPairingCompleted;
        _node.BlobReceived += OnBlob;

        _files = new FileTransferService(this);
        _files.Log += Log.Info;
        _files.SendRequested += OnIncomingFiles;

        _clipboard = new ClipboardSync(this) { Enabled = _storage.Settings.ClipboardEnabled };
        _clipboardWatcher = new ClipboardWatcher();
        _clipboardWatcher.Changed += OnLocalClipboardChanged;

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.Idle,
            Text = "ShareCom",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip { Font = UiFonts.Normal },
        };
        _tray.ContextMenuStrip.Opening += (_, e) => { BuildMenu(_tray.ContextMenuStrip); e.Cancel = false; };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowContextMenu();
        };
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_balloonUrl is { } url) OpenUrl(url);
            _balloonUrl = null;
        };

        _updates = new UpdateService(CurrentVersion,
            () => _storage.Settings.AutoUpdate,
            () => _storage.Settings.SkippedUpdateVersion,
            UpdateConditionsNow);
        _updates.ApplyRequested += ready => _ui.BeginInvoke(() => ApplyUpdate(ready, manual: false));
        _node.NewerPeerSeen += v => _updates.OnNewerPeer(v);

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _hooks = new InputHooks(_router);
        _hooks.Start();
        try
        {
            _node.Start();
        }
        catch (Exception ex)
        {
            Log.Error("통신 시작 실패", ex);
            MessageBox.Show($"통신을 시작하지 못했습니다. 다른 프로그램이 포트 47821 을 쓰고 있을 수 있습니다.\n\n{ex.Message}",
                "ShareCom", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        Task.Run(SystemSetup.EnsureFirewallRule);
        UpdateTrayState();
        if (startup.UpdatedVersion is { } updated)
        {
            Log.Info($"업데이트 직후 시작: {updated}");
            Notify($"ShareCom 을 {updated} 으로 업데이트했습니다. 눌러서 바뀐 점을 보세요.",
                new Uri($"https://github.com/{UpdateChecker.Repository}/releases/tag/v{updated}"));
        }
        else if (startup.FailedVersion is { } failed)
            Notify($"새 버전 {failed} 이 제대로 시작하지 않아 이전 버전으로 되돌렸습니다. 이 버전은 건너뜁니다.");
        else if (!startup.AtLogon)
            Notify(_store.GetAll().Count == 0
                ? "실행 중입니다. 트레이 아이콘을 눌러 다른 PC와 짝을 맺으세요."
                : "실행 중입니다. 짝 맺은 PC에 연결합니다.");
        else
            Log.Info("로그인 자동 시작으로 실행했습니다.");
        _ui.BeginInvoke(CheckAutoStart);
        _updates.Start();
        if (startup.UpdatedEvent is { } ev)
        {
            // 메시지 루프가 돌기 시작했을 때 이전 프로세스에 "잘 켜졌다"고 알린다.
            _ui.BeginInvoke(() =>
            {
                UpdateHandoff.SignalStarted(ev);
                var exe = Environment.ProcessPath;
                if (exe is not null)
                    Task.Run(() =>
                    {
                        if (!UpdateInstaller.Cleanup(exe)) Log.Info("이전 버전 파일을 지우지 못했습니다. 다음 업데이트 때 지웁니다.");
                    });
            });
        }
    }

    // ───────────── 업데이트 ─────────────

    private UpdateConditions UpdateConditionsNow() => new(
        Controlling: _controlling is not null,
        Controlled: _router.ControlledBy is not null,
        Transferring: _files.IsBusy,
        SinceLastInput: Win32.SinceLastInput(),
        SinceControlEnded: DateTime.UtcNow - _lastControlUtc);

    /// <summary>UI 스레드. 받은 exe 로 바꾸고 앱을 끈다. 끈 뒤에 Program 이 새 exe 를 띄운다.</summary>
    private void ApplyUpdate(ReadyUpdate ready, bool manual)
    {
        if (PendingUpdate is not null) return;
        var reason = UpdatePolicy.BlockReason(UpdateConditionsNow(), manual);
        var exe = Environment.ProcessPath;
        if (reason is null && exe is null) reason = "실행 파일 경로를 알 수 없습니다.";
        if (reason is null && !manual && _settingsForm is { IsDisposed: false }) reason = "설정 창이 열려 있습니다.";
        if (reason is not null)
        {
            Log.Info($"업데이트 {ready.VersionText} 을 지금 적용하지 않습니다: {reason}");
            if (manual) Notify($"업데이트를 미뤘습니다: {reason}");
            _updates.RetryLater();
            return;
        }
        try
        {
            UpdateInstaller.Swap(exe!, ready.File, ready.Candidate.Payload);
        }
        catch (Exception ex)
        {
            Log.Error($"업데이트 {ready.VersionText} 파일 바꾸기 실패", ex);
            _updates.ApplyFailed(ex.Message);
            Notify($"업데이트 {ready.VersionText} 을 적용하지 못했습니다: {ex.Message}");
            return;
        }
        Log.Info($"업데이트 {ready.VersionText} 적용: 파일을 바꿨습니다. 앱을 끄고 새 버전을 띄웁니다.");
        PendingUpdate = new PendingUpdate(exe!, ready.VersionText, ready.Candidate.PageUrl);
        ExitThread();
    }

    /// <summary>설정 창의 [지금 확인].</summary>
    private async Task<string> CheckUpdateFromSettings()
    {
        ReadyUpdate? ready;
        try { ready = await _updates.CheckAsync(manual: true); }
        catch (Exception ex) { return "확인하지 못했습니다: " + ex.Message; }
        if (ready is null) return _updates.Status;
        var answer = MessageBox.Show(_settingsForm,
            $"새 버전 {ready.VersionText} 이 있습니다(지금 {UpdateVersion.Display(CurrentVersion)}).\n\n" +
            "지금 업데이트할까요? ShareCom 이 잠깐 꺼졌다가 다시 켜지고, 그동안 다른 PC와 연결이 끊깁니다.",
            "ShareCom 업데이트", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return _updates.Status;
        _manualUpdate = ready;
        _settingsForm?.AcceptAndClose();
        return "업데이트하는 중…";
    }

    private static void OpenUrl(Uri url)
    {
        try { Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("주소를 열지 못했습니다", ex); }
    }

    /// <summary>
    /// 첫 실행이면 자동 시작을 켤지 한 번 묻는다. 이미 켜 두었는데 exe 를 옮겼으면 새 경로로 다시 등록한다.
    /// </summary>
    private void CheckAutoStart()
    {
        if (!SystemSetup.IsAdministrator()) return; // 개발용(NoAdmin) 실행은 등록할 수 없다
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var settings = _storage.Settings;
        if (!settings.AutoStartAsked)
        {
            settings.AutoStartAsked = true;
            SaveSettings();
            var answer = MessageBox.Show(
                "Windows 에 로그인할 때 ShareCom 을 자동으로 켤까요?\n" +
                "켜 두면 PC를 다시 켜도 따로 실행하지 않아도 다른 PC와 다시 연결됩니다.\n\n" +
                "나중에 트레이 메뉴 [설정…]에서 바꿀 수 있습니다.",
                "ShareCom 자동 시작", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes) SetAutoStart(true, showErrors: true);
            return;
        }
        if (!string.Equals(settings.AutoStartPath, exe, StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(() =>
            {
                if (!AutoStart.IsRegistered()) return;
                Log.Info($"exe 위치가 바뀌어 자동 시작을 다시 등록합니다: {settings.AutoStartPath} → {exe}");
                if (AutoStart.Register(exe) is null)
                {
                    settings.AutoStartPath = exe;
                    SaveSettings();
                }
            });
        }
    }

    /// <summary>자동 시작을 켜거나 끈다. 실패하면 false.</summary>
    private bool SetAutoStart(bool enabled, bool showErrors)
    {
        var exe = Environment.ProcessPath;
        string? error = enabled
            ? (exe is null ? "실행 파일 경로를 알 수 없습니다." : AutoStart.Register(exe))
            : AutoStart.Unregister();
        if (error is null)
        {
            _storage.Settings.AutoStartPath = enabled ? exe : null;
            SaveSettings();
            return true;
        }
        if (showErrors) MessageBox.Show(error, "ShareCom 자동 시작", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    // ───────────── IInputSink ─────────────

    void IInputSink.Send(Guid peer, Message message) => _node.Send(peer, message);
    void IInputSink.SetLocalCursor(int x, int y) => Win32.SetCursorPos(x, y);
    void IInputSink.HideLocalCursor() => CursorHider.Hide();
    void IInputSink.ShowLocalCursor() => CursorHider.Show();
    void IInputSink.ReleaseInjectedInput() => _injector.ReleaseAll();

    void IInputSink.ControllingChanged(Guid? peer)
    {
        _controlling = peer;
        _lastControlUtc = DateTime.UtcNow;
        _ui.BeginInvoke(UpdateTrayState);
    }

    // ───────────── 클립보드 ─────────────

    IReadOnlyList<Guid> IClipboardTransport.ConnectedPeers => _node.ConnectedPeers;

    void IClipboardTransport.SendToPeer(Guid peer, byte[] payload) => _node.SendBlob(peer, BlobKind.Clipboard, payload);

    void IClipboardTransport.WriteLocal(Guid fromPeer, ClipboardContent content) => _ui.BeginInvoke(() =>
    {
        _clipboardWatcher.MarkSelfWrite();
        if (content.Get(ClipboardFormats.Files) is { } files)
        {
            // 파일은 목록만 클립보드에 올리고, 탐색기에서 붙여넣을 때 원본 PC에서 받아온다.
            var offer = FileOffer.Decode(files.Data);
            var peerName = _store.Get(fromPeer)?.Name ?? "PC";
            OleClipboard.Set(new VirtualFileDataObject(offer,
                index => _files.OpenRemote(fromPeer, offer.Id, index, offer.Entries[index].Size), peerName));
            Log.Info($"클립보드 받음: '{peerName}' 의 파일 {offer.FileCount}개({FormatBytes(offer.TotalBytes)}) - 붙여넣을 때 받습니다");
            return;
        }
        ClipboardIo.Write(content);
        Log.Info($"클립보드 받음: {string.Join(", ", content.Items.Select(i => i.Format))}, {FormatBytes(content.TotalBytes)}");
    });

    /// <summary>UI 스레드에서 불린다. 읽기만 여기서 하고 보내기는 다른 스레드에서 한다.</summary>
    private void OnLocalClipboardChanged()
    {
        if (!_clipboard.Enabled || _node.ConnectedPeers.Count == 0) return;
        if (TryReadFileDrop() is { } paths)
        {
            // 탐색기에서 파일을 복사했다: 목록(제안)만 만들어 보낸다. 폴더 펼치기는 다른 스레드에서.
            Task.Run(() =>
            {
                try
                {
                    var offer = _files.RegisterOffer(paths);
                    if (offer.Entries.Count == 0) return;
                    _clipboard.OnLocalChanged(new ClipboardContent([new ClipboardItem(ClipboardFormats.Files, offer.Encode())]));
                    Log.Info($"파일 복사 알림 보냄: {offer.FileCount}개, {FormatBytes(offer.TotalBytes)}");
                }
                catch (Exception ex) { Log.Error("파일 목록 만들기 실패", ex); }
            });
            return;
        }
        var content = ClipboardIo.Read();
        if (content is null) return;
        Task.Run(() =>
        {
            try
            {
                int before = _clipboard.SentCount;
                _clipboard.OnLocalChanged(content);
                if (_clipboard.SentCount != before)
                    Log.Info($"클립보드 보냄: {string.Join(", ", content.Items.Select(i => i.Format))}, {FormatBytes(content.TotalBytes)}");
            }
            catch (Exception ex) { Log.Error("클립보드 보내기 실패", ex); }
        });
    }

    private static string[]? TryReadFileDrop()
    {
        try
        {
            if (!System.Windows.Forms.Clipboard.ContainsFileDropList()) return null;
            var list = System.Windows.Forms.Clipboard.GetFileDropList();
            return list.Count == 0 ? null : list.Cast<string>().ToArray();
        }
        catch { return null; }
    }

    private void OnBlob(Guid peer, BlobKind kind, byte[] data)
    {
        if (kind == BlobKind.FileSendRequest)
        {
            _files.OnBlob(peer, kind, data);
            return;
        }
        if (kind != BlobKind.Clipboard) return;
        // 받는 루프(마우스·키보드도 같이 받는다)를 붙잡지 않게 다른 스레드에서 푼다.
        Task.Run(() =>
        {
            try { _clipboard.OnRemote(peer, data); }
            catch (Exception ex) { Log.Error("받은 클립보드를 풀지 못했습니다", ex); }
        });
    }

    private static string FormatBytes(long n) => Sizes.Format(n);

    private static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
    }

    // ───────────── 파일 ─────────────

    bool IFileTransport.Send(Guid peer, Message message) => _node.Send(peer, message);
    bool IFileTransport.SendBulk(Guid peer, Message message) => _node.SendBulk(peer, message);
    bool IFileTransport.SendBlob(Guid peer, BlobKind kind, byte[] data) => _node.SendBlob(peer, kind, data);

    /// <summary>트레이 메뉴에서 고른 파일·폴더를 peer 로 보낸다. 상대가 [받기]를 누르면 상대가 끌어간다.</summary>
    private void SendFiles(Guid peer, string[] paths)
    {
        var name = _store.Get(peer)?.Name ?? "PC";
        Notify($"'{name}' 에 받을지 묻는 중입니다…");
        Task.Run(async () =>
        {
            try
            {
                var (accepted, offer) = await _files.RequestSendAsync(peer, paths);
                if (!accepted)
                {
                    Notify($"'{name}' 쪽에서 받기를 거절했습니다.");
                    return;
                }
                Notify($"'{name}' 로 보내는 중: 파일 {offer.FileCount}개, {FormatBytes(offer.TotalBytes)}");
                var (ok, error) = await _files.WaitResultAsync(offer.Id);
                Notify(ok ? $"'{name}' 로 다 보냈습니다." : $"'{name}' 로 보내지 못했습니다: {error}");
            }
            catch (Exception ex)
            {
                Log.Error("파일 보내기 실패", ex);
                Notify($"파일을 보내지 못했습니다: {ex.Message}");
            }
        });
    }

    private void OnIncomingFiles(Guid peer, FileOffer offer)
    {
        var name = _store.Get(peer)?.Name ?? "PC";
        var folder = KnownFolders.ShareComDownloads();
        _ui.BeginInvoke(() =>
        {
            var ask = new IncomingFilesForm(name, offer, folder);
            ask.FormClosed += (_, _) =>
            {
                _files.Respond(peer, offer.Id, ask.Accepted);
                if (!ask.Accepted) return;
                var progress = new TransferProgressForm($"'{name}' 에서 받는 중");
                progress.Show();
                var reporter = new Progress<FileProgress>(progress.Report); // UI 스레드에서 만들었으므로 UI 스레드로 넘어온다
                Task.Run(async () =>
                {
                    try
                    {
                        await _files.ReceiveToFolderAsync(peer, offer, folder, reporter, progress.Token);
                        Notify($"'{name}' 에서 파일 {offer.FileCount}개를 받았습니다. 트레이 메뉴 [받은 파일 폴더 열기]로 볼 수 있습니다.");
                    }
                    catch (OperationCanceledException) { Notify("파일 받기를 취소했습니다."); }
                    catch (Exception ex)
                    {
                        Log.Error("파일 받기 실패", ex);
                        Notify($"파일을 받지 못했습니다: {ex.Message}");
                    }
                    finally { _ui.BeginInvoke(progress.Finish); }
                });
            };
            ask.Show();
            ask.Activate();
        });
    }

    private ToolStripMenuItem BuildSendMenu()
    {
        var send = new ToolStripMenuItem("파일 보내기");
        var peers = _node.ConnectedPeers.Select(id => _store.Get(id)).OfType<PeerRecord>().OrderBy(p => p.Name).ToList();
        if (peers.Count == 0) send.DropDownItems.Add(new ToolStripMenuItem("(연결된 PC가 없습니다)") { Enabled = false });
        foreach (var p in peers)
        {
            var item = new ToolStripMenuItem(p.Name);
            var files = new ToolStripMenuItem("파일 고르기…");
            files.Click += (_, _) =>
            {
                using var dlg = new OpenFileDialog { Multiselect = true, Title = $"'{p.Name}' 로 보낼 파일" };
                if (dlg.ShowDialog() == DialogResult.OK) SendFiles(p.Id, dlg.FileNames);
            };
            var folder = new ToolStripMenuItem("폴더 고르기…");
            folder.Click += (_, _) =>
            {
                using var dlg = new FolderBrowserDialog { Description = $"'{p.Name}' 로 보낼 폴더", UseDescriptionForTitle = true, Multiselect = true };
                if (dlg.ShowDialog() == DialogResult.OK) SendFiles(p.Id, dlg.SelectedPaths);
            };
            item.DropDownItems.AddRange([files, folder]);
            send.DropDownItems.Add(item);
        }
        return send;
    }

    // ───────────── 통신 이벤트 ─────────────

    private void OnPeerConnected(Guid peer)
    {
        _router.SetPeerConnected(peer, true);
        _layout.OnConnected(peer);
        var name = _store.Get(peer)?.Name ?? "PC";
        Notify($"'{name}' 와 연결됐습니다{(_node.IsEncrypted(peer) ? "" : " (암호화 꺼짐)")}.");
        _ui.BeginInvoke(UpdateTrayState);
    }

    private void OnPeerDisconnected(Guid peer)
    {
        _router.SetPeerConnected(peer, false);
        _files.OnPeerDisconnected(peer);
        var name = _store.Get(peer)?.Name;
        if (name is not null) Notify($"'{name}' 와 연결이 끊겼습니다. 다시 연결을 시도합니다.");
        _ui.BeginInvoke(UpdateTrayState);
    }

    private void OnMessage(Guid peer, Message message)
    {
        if (_files.OnMessage(peer, message)) return;
        switch (message)
        {
            case LayoutMessage:
                _layout.OnMessage(peer, message);
                break;
            case EnterControlMessage e:
                _lastControlUtc = DateTime.UtcNow;
                _router.BeginControlledBy(peer);
                _injector.MoveTo(e.X, e.Y);
                break;
            case LeaveControlMessage:
                _lastControlUtc = DateTime.UtcNow;
                _router.EndControlledBy(peer);
                _injector.ReleaseAll();
                break;
            case TakeOverMessage:
                _router.OnTakeOver(peer);
                break;
            case MouseMoveMessage m when _router.ControlledBy == peer:
                _injector.MoveTo(m.X, m.Y);
                break;
            case MouseButtonMessage b when _router.ControlledBy == peer:
                _injector.Button(b.Button, b.Down);
                break;
            case MouseWheelMessage w when _router.ControlledBy == peer:
                _injector.Wheel(w.Delta, w.Horizontal);
                break;
            case KeyMessage k when _router.ControlledBy == peer || !k.Down:
                // 떼기는 조작이 끝난 뒤에 도착해도 재현한다(눌린 채 남지 않게).
                _injector.Key(k);
                break;
        }
    }

    private Task<bool> ConfirmPairingAsync(PairingPrompt prompt, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.BeginInvoke(() =>
        {
            var form = new PairingForm(prompt);
            form.FormClosed += (_, _) => tcs.TrySetResult(form.Accepted);
            ct.Register(() => _ui.BeginInvoke(() => { if (!form.IsDisposed) form.Close(); }));
            form.Show();
            form.Activate();
        });
        return tcs.Task;
    }

    private void OnPairingCompleted(PairingResult r)
    {
        Log.Info($"짝 맺기 결과: {r.Outcome} {r.PeerName} {r.Error}");
        Notify(r.Outcome switch
        {
            PairingOutcome.Paired => $"'{r.PeerName}' 와 짝을 맺었습니다. 곧 연결됩니다.",
            PairingOutcome.RejectedByPeer => $"'{r.PeerName}' 쪽에서 짝 맺기를 취소했습니다.",
            PairingOutcome.RejectedByMe => "짝 맺기를 취소했습니다.",
            _ => $"짝 맺기에 실패했습니다: {r.Error}",
        });
    }

    // ───────────── 시스템 이벤트 ─────────────

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        var screen = LocalScreen();
        _router.SetLocalScreens(screen.Monitors, screen.ScalePercent);
        _layout.SetSelf(_identity.Name, screen);
    }

    /// <summary>배치가 바뀌었다(내가 고쳤거나 다른 PC에서 받았다).</summary>
    private readonly object _layoutApplyGate = new();

    private void OnLayoutChanged(LayoutDoc _)
    {
        // 알림은 잠금 밖에서 오므로 두 스레드의 알림 순서가 뒤바뀔 수 있다. 늘 최신 판을 다시 읽어 적용한다.
        LayoutDoc doc;
        lock (_layoutApplyGate)
        {
            doc = _layout.Doc;
            _router.SetLayout(doc.Entries);
            _storage.Settings.Layout = doc;
            _storage.Settings.RememberedLayout = _layout.Remembered.ToList();
            SaveSettings();
        }
        _ui.BeginInvoke(() => _layoutEditor?.ShowRemoteChange(_layout.Doc));
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteConnect)
        {
            Log.Info($"화면 잠금/세션 전환({e.Reason}): 내 PC로 돌아옵니다.");
            _router.ReturnHome();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _router.ReturnHome();
        if (e.Mode == PowerModes.Resume)
        {
            Log.Info("절전에서 깨어남: 연결을 다시 확인합니다.");
            foreach (var peer in _node.ConnectedPeers) _node.Disconnect(peer); // 죽었을 연결을 바로 새로 건다
        }
    }

    // ───────────── 트레이 메뉴 ─────────────

    private void ShowContextMenu()
    {
        // NotifyIcon 의 감춰진 ShowContextMenu 를 불러 왼쪽 클릭에도 메뉴를 띄운다.
        typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(_tray, null);
    }

    private void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem($"ShareCom · 내 PC: {_identity.Name}") { Enabled = false });
        var nowText = _controlling is { } c ? $"지금: '{_store.Get(c)?.Name}' 조작 중" : "지금: 내 PC 사용 중";
        menu.Items.Add(new ToolStripMenuItem(nowText) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("짝 맺은 PC") { Enabled = false });
        var peers = _store.GetAll().OrderBy(p => p.Name).ToList();
        if (peers.Count == 0) menu.Items.Add(new ToolStripMenuItem("   (아직 없음)") { Enabled = false });
        foreach (var p in peers)
        {
            var state = _node.IsConnected(p.Id) ? (_node.IsEncrypted(p.Id) ? "연결됨" : "연결됨, 암호화 꺼짐") : "연결 안 됨";
            var item = new ToolStripMenuItem($"   {p.Name} — {state}");
            var unpair = new ToolStripMenuItem("짝 풀기…");
            unpair.Click += (_, _) => Unpair(p);
            item.DropDownItems.Add(unpair);
            menu.Items.Add(item);
        }

        var layout = new ToolStripMenuItem("화면 배치…");
        layout.Click += (_, _) => OpenLayoutEditor();
        menu.Items.Add(layout);

        var pairMenu = new ToolStripMenuItem("새 PC와 짝 맺기");
        var candidates = _node.Discovered.Where(d => _store.Get(d.Id) is null).ToList();
        if (candidates.Count == 0)
            pairMenu.DropDownItems.Add(new ToolStripMenuItem("(같은 와이파이에서 찾는 중… 상대 PC에서도 ShareCom 을 켜세요)") { Enabled = false });
        foreach (var d in candidates)
        {
            var item = new ToolStripMenuItem($"{d.Name} ({d.Endpoint.Address})");
            item.Click += (_, _) => StartPairing(d.Endpoint);
            pairMenu.DropDownItems.Add(item);
        }
        pairMenu.DropDownItems.Add(new ToolStripSeparator());
        var byAddress = new ToolStripMenuItem("IP 주소로 직접 짝 맺기…");
        byAddress.Click += (_, _) =>
        {
            using var f = new ConnectByAddressForm(_node.ListeningPort);
            if (f.ShowDialog() == DialogResult.OK && f.Endpoint is { } ep) StartPairing(ep);
        };
        pairMenu.DropDownItems.Add(byAddress);
        menu.Items.Add(pairMenu);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(BuildSendMenu());
        var received = new ToolStripMenuItem("받은 파일 폴더 열기");
        received.Click += (_, _) => OpenFolder(KnownFolders.ShareComDownloads());
        menu.Items.Add(received);
        menu.Items.Add(new ToolStripSeparator());

        var hotkeys = _router.Hotkeys;
        var edgeText = hotkeys.LockKey == 0 ? "가장자리 넘기기 잠금" : $"가장자리 넘기기 잠금 ({HotkeySettings.LockKeyName(hotkeys.LockKey)})";
        var edge = new ToolStripMenuItem(edgeText) { Checked = _router.EdgeLocked };
        edge.Click += (_, _) =>
        {
            _router.EdgeLocked = !_router.EdgeLocked;
            _storage.Settings.EdgeLocked = _router.EdgeLocked;
            SaveSettings();
        };
        menu.Items.Add(edge);

        var clipboard = new ToolStripMenuItem("클립보드 공유") { Checked = _clipboard.Enabled };
        clipboard.Click += (_, _) =>
        {
            _clipboard.Enabled = !_clipboard.Enabled;
            _storage.Settings.ClipboardEnabled = _clipboard.Enabled;
            SaveSettings();
        };
        menu.Items.Add(clipboard);

        var help = new ToolStripMenuItem("단축키 보기");
        help.Click += (_, _) => MessageBox.Show(HotkeyHelp(_router.Hotkeys), "ShareCom 단축키", MessageBoxButtons.OK, MessageBoxIcon.Information);
        menu.Items.Add(help);

        var settings = new ToolStripMenuItem("설정…");
        settings.Click += (_, _) => OpenSettings();
        menu.Items.Add(settings);

        var logs = new ToolStripMenuItem("기록 폴더 열기");
        logs.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_storage.Directory}\"") { UseShellExecute = true });
        menu.Items.Add(logs);

        menu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem("종료");
        exit.Click += (_, _) => ExitThread();
        menu.Items.Add(exit);
    }

    private void StartPairing(System.Net.IPEndPoint endpoint)
    {
        Notify($"{endpoint.Address} 에 짝 맺기를 요청합니다. 두 PC 화면에 코드가 뜹니다.");
        _ = Task.Run(() => _node.PairAsync(endpoint));
    }

    private void OpenLayoutEditor()
    {
        if (_layoutEditor is { IsDisposed: false })
        {
            _layoutEditor.Activate();
            return;
        }
        _layoutEditor = new LayoutEditorForm(_layout.Doc, _identity.Id, _node.IsConnected, positions => _layout.ApplyPositions(positions));
        _layoutEditor.FormClosed += (_, _) => _layoutEditor = null;
        _layoutEditor.Show();
        _layoutEditor.Activate();
    }

    private void Unpair(PeerRecord p)
    {
        var answer = MessageBox.Show($"'{p.Name}' 와 짝을 풀까요?\n이 PC와 짝인 다른 PC들에서도 함께 풀립니다. 다시 쓰려면 짝 맺기를 새로 해야 합니다.",
            "ShareCom", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.OK) _trust.Unpair(p.Id);
    }

    private static string HotkeyHelp(HotkeySettings h)
    {
        var lines = new List<string>
        {
            h.LockKey == 0
                ? "가장자리 넘기기 잠금 : (단축키 쓰지 않음, 트레이 메뉴에서)"
                : $"{HotkeySettings.LockKeyName(h.LockKey)} : 가장자리 넘기기 잠금 켜기/끄기",
        };
        if (h.JumpModifiers == HotkeyModifiers.None)
            lines.Add("PC 바로 이동 : (쓰지 않음)");
        else
        {
            var m = HotkeySettings.ModifiersName(h.JumpModifiers);
            lines.Add($"{m}+1 : 내 PC로");
            lines.Add($"{m}+2~4 : 다른 PC로 바로 이동 (화면 배치에서 왼쪽 PC부터 차례로 번호)");
        }
        lines.Add("Ctrl+Alt+Esc : 긴급 복귀 (무조건 내 PC로)");
        lines.Add("");
        lines.Add("단축키는 트레이 메뉴 [설정…]에서 바꿀 수 있습니다.");
        lines.Add("다른 PC를 조작하는 중에 그 PC의 마우스·키보드를 쓰면 그 PC가 주인을 되찾습니다.");
        lines.Add("Ctrl+Alt+Del 과 UAC 창·잠금 화면은 해당 PC에서 직접 조작해야 합니다.");
        return string.Join("\n", lines);
    }

    private SettingsForm? _settingsForm;

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        var values = new SettingsValues(
            AutoStart.IsRegistered(),
            _clipboard.Enabled,
            _node.EncryptionEnabled,
            _router.EdgeLocked,
            _router.Hotkeys,
            _storage.Settings.AutoUpdate);
        var version = UpdateVersion.Display(CurrentVersion);
        var info = new SettingsInfo(_identity.Name, _identity.Thumbprint[..16] + "…", version, SystemSetup.IsAdministrator(), _updates.Status);
        using var form = new SettingsForm(values, info,
            () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_storage.Directory}\"") { UseShellExecute = true }),
            CheckUpdateFromSettings);
        _settingsForm = form;
        try
        {
            if (form.ShowDialog() == DialogResult.OK && form.Result is { } next) ApplySettings(values, next);
        }
        finally
        {
            _settingsForm = null;
        }
        if (_manualUpdate is { } manual)
        {
            _manualUpdate = null;
            ApplyUpdate(manual, manual: true);
        }
    }

    private void ApplySettings(SettingsValues before, SettingsValues next)
    {
        if (next.AutoStart != before.AutoStart) SetAutoStart(next.AutoStart, showErrors: true);
        if (next.ClipboardEnabled != before.ClipboardEnabled)
        {
            _clipboard.Enabled = next.ClipboardEnabled;
            _storage.Settings.ClipboardEnabled = next.ClipboardEnabled;
        }
        if (next.EdgeLocked != before.EdgeLocked)
        {
            _router.EdgeLocked = next.EdgeLocked;
            _storage.Settings.EdgeLocked = next.EdgeLocked;
        }
        if (next.Hotkeys != before.Hotkeys)
        {
            _router.Hotkeys = next.Hotkeys;
            _storage.Settings.Hotkeys = _router.Hotkeys;
            Log.Info($"단축키 변경: 잠금 {HotkeySettings.LockKeyName(_router.Hotkeys.LockKey)}, 바로 이동 {HotkeySettings.ModifiersName(_router.Hotkeys.JumpModifiers)}");
        }
        if (next.AutoUpdate != before.AutoUpdate)
        {
            _storage.Settings.AutoUpdate = next.AutoUpdate;
            Log.Info($"자동 업데이트: {(next.AutoUpdate ? "켬" : "끔")}");
        }
        SaveSettings();
        if (next.EncryptionEnabled != before.EncryptionEnabled) SetEncryption(next.EncryptionEnabled);
    }

    private void SetEncryption(bool next)
    {
        if (next == _node.EncryptionEnabled) return;
        if (!next)
        {
            var answer = MessageBox.Show(
                "암호화를 끄면 같은 와이파이의 다른 기기가 키 입력(비밀번호 포함)과 클립보드를 엿볼 수 있고,\n" +
                "중간에 끼어들어 가짜 키 입력·마우스 조작을 넣어 이 PC를 관리자 권한으로 조작할 수도 있습니다.\n\n" +
                "믿을 수 있는 집 망에서 문제를 찾을 때만 잠깐 끄세요.\n" +
                "상대 PC도 암호화를 꺼야 실제로 평문으로 연결됩니다. 끌까요?",
                "ShareCom", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (answer != DialogResult.OK) return;
        }
        _node.EncryptionEnabled = next;
        _storage.Settings.EncryptionEnabled = next;
        SaveSettings();
        foreach (var peer in _node.ConnectedPeers) _node.Disconnect(peer); // 새 설정으로 다시 연결
    }

    private void UpdateTrayState()
    {
        if (_controlling is { } c)
        {
            _tray.Icon = TrayIcons.Controlling;
            _tray.Text = Trim($"ShareCom - '{_store.Get(c)?.Name}' 조작 중");
        }
        else
        {
            var connected = _node.ConnectedPeers.Count;
            _tray.Icon = connected > 0 ? TrayIcons.Connected : TrayIcons.Idle;
            _tray.Text = Trim(connected > 0 ? $"ShareCom - {connected}대 연결됨" : "ShareCom - 연결된 PC 없음");
        }
    }

    private static string Trim(string s) => s.Length <= 63 ? s : s[..63];

    private void Notify(string text, Uri? link = null)
    {
        Log.Info(text);
        _ui.BeginInvoke(() =>
        {
            _balloonUrl = link;
            _tray.ShowBalloonTip(link is null ? 3000 : 10000, "ShareCom", text, ToolTipIcon.None);
        });
    }

    private void SaveSettings()
    {
        try { _storage.Save(); } catch (Exception ex) { Log.Error("설정 저장 실패", ex); }
    }

    private static ScreenRect[] LocalMonitors() =>
        Screen.AllScreens.OrderByDescending(s => s.Primary)
              .Select(s => new ScreenRect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height))
              .ToArray();

    private static PcScreen LocalScreen() => new(LocalMonitors(), Win32.PrimaryScalePercent());

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _updates.Dispose();
            _router.ReturnHome();
            _hooks.Dispose();
            _clipboardWatcher.Dispose();
            OleClipboard.ReleaseIfOwned();
            _injector.ReleaseAll();
            CursorHider.RestoreNow();
            try { _node.DisposeAsync().AsTask().Wait(2000); } catch { }
            _tray.Visible = false;
            _tray.Dispose();
            _ui.Dispose();
            Log.Info("종료");
        }
        base.Dispose(disposing);
    }
}
