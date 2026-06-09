using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Epic.OnlineServices;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Platform;
using Godot;
using FileAccess = Godot.FileAccess;

public partial class EOSManager : Node
{
    public static EOSManager Instance { get; private set; }

    private PlatformInterface _platformInterface;
    private LobbyInterface _lobbyInterface;
    private P2PInterface _p2pInterface;
    private ProductUserId _localProductUserId;

    private ulong _peerNotificationId;
    private EOSMultiplayerPeer _currentMultiplayerPeer;

    public override void _Ready()
    {
        LoadNativeEOS();
        var env = new Dictionary<string, string>();

        if (!FileAccess.FileExists("res://.env"))
        {
            GD.PrintErr($"[EnvLoader] Warning: .env file not found!");
            return;
        }

        using var file = FileAccess.Open("res://.env", FileAccess.ModeFlags.Read);
        while (!file.EofReached())
        {
            string line = file.GetLine().Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

            int index = line.IndexOf('=');
            if (index > 0)
            {
                string key = line.Substring(0, index).Trim();
                string value = line.Substring(index + 1).Trim();
                env[key] = value;
            }
        }

        if (!env.ContainsKey("EOS_PRODUCT_ID") || !env.ContainsKey("EOS_CLIENT_ID"))
        {
            GD.PrintErr("[EOS] Initialization aborted: Missing required .env keys.");
            return;
        }

        var initOptions = new InitializeOptions()
        {
            ProductName = "MyGodotGame",
            ProductVersion = "1.0.0"
        };

        var initResult = PlatformInterface.Initialize(ref initOptions);
        if (initResult != Result.Success)
        {
            GD.PrintErr($"[EOS] SDK Initialization failed: {initResult}");
            return;
        }

        var options = new Options()
        {
            ProductId = env["EOS_PRODUCT_ID"],
            SandboxId = env["EOS_SANDBOX_ID"],
            DeploymentId = env["EOS_DEPLOYMENT_ID"],
            ClientCredentials = new ClientCredentials()
            {
                ClientId = env["EOS_CLIENT_ID"],
                ClientSecret = env["EOS_CLIENT_SECRET"]
            },
            IsServer = false
        };

        _platformInterface = PlatformInterface.Create(ref options);
        if (_platformInterface == null)
        {
            GD.PrintErr("[EOS] Failed to create platform interface instance.");
            return;
        }

        _lobbyInterface = _platformInterface.GetLobbyInterface();
        _p2pInterface = _platformInterface.GetP2PInterface();

        GD.Print("[EOS] Successfully initialized using environment variables!");
        if (Instance != null)
            GD.PrintErr("[EOS] Warning: Multiple EOSManager instances detected. This may lead to unexpected behavior.");
        Instance = this;

        StartListeningForP2PRequests();
    }

    public void SetLocalUserId(ProductUserId puid)
    {
        _localProductUserId = puid;
    }

    public override void _Process(double delta)
    {
        _platformInterface?.Tick();
    }

    private void StartListeningForP2PRequests()
    {
        if (_p2pInterface == null) return;

        var addNotifyOptions = new AddNotifyPeerConnectionRequestOptions()
        {
            LocalUserId = _localProductUserId,
            SocketId = new SocketId() { SocketName = "GameTraffic" }
        };

        _peerNotificationId = _p2pInterface.AddNotifyPeerConnectionRequest(ref addNotifyOptions, null, (ref OnIncomingConnectionRequestInfo data) =>
        {
            var acceptOptions = new AcceptConnectionOptions()
            {
                LocalUserId = _localProductUserId,
                RemoteUserId = data.RemoteUserId,
                SocketId = data.SocketId
            };

            if (_p2pInterface.AcceptConnection(ref acceptOptions) == Result.Success)
            {
                GD.Print($"[EOS P2P] Connected seamlessly to remote user session: {data.RemoteUserId}");
                _currentMultiplayerPeer?.RegisterRemotePeer(data.RemoteUserId);
            }
        });
    }

    public void HostLobby(uint maxPlayers, string password = "")
    {
        if (_lobbyInterface == null || _localProductUserId == null) return;

        var createOptions = new CreateLobbyOptions()
        {
            LocalUserId = _localProductUserId,
            MaxLobbyMembers = maxPlayers,
            PermissionLevel = LobbyPermissionLevel.Publicadvertised,
            PresenceEnabled = true
        };

        _lobbyInterface.CreateLobby(ref createOptions, null, (ref CreateLobbyCallbackInfo data) =>
        {
            if (data.ResultCode != Result.Success)
            {
                GD.PrintErr($"[EOS Lobby] Hosting creation failed: {data.ResultCode}");
                return;
            }

            GD.Print($"[EOS Lobby] Hosted successfully! Lobby ID: {data.LobbyId}");

            if (!string.IsNullOrEmpty(password))
            {
                var modOptions = new UpdateLobbyModificationOptions() { LobbyId = data.LobbyId, LocalUserId = _localProductUserId };
                if (_lobbyInterface.UpdateLobbyModification(ref modOptions, out LobbyModification modHandle) == Result.Success)
                {
                    var attrOptions = new LobbyModificationAddAttributeOptions()
                    {
                        Attribute = new Epic.OnlineServices.Lobby.AttributeData()
                        {
                            Key = "LOBBY_PASSWORD",
                            Value = new AttributeDataValue() { AsUtf8 = password }
                        },
                        Visibility = LobbyAttributeVisibility.Public
                    };
                    modHandle.AddAttribute(ref attrOptions);

                    var updateOptions = new UpdateLobbyOptions() { LobbyModificationHandle = modHandle };
                    _lobbyInterface.UpdateLobby(ref updateOptions, null, (ref UpdateLobbyCallbackInfo updateData) => { });
                }
            }

            _currentMultiplayerPeer = new EOSMultiplayerPeer();
            _currentMultiplayerPeer.InitializeAsServer(_p2pInterface, _localProductUserId);
            GetTree().GetMultiplayer().MultiplayerPeer = _currentMultiplayerPeer;
        });
    }

    public void JoinLobby(string lobbyId, string enteredPassword = "")
    {
        if (_lobbyInterface == null || _localProductUserId == null) return;

        var searchOptions = new CreateLobbySearchOptions() { MaxResults = 1 };
        if (_lobbyInterface.CreateLobbySearch(ref searchOptions, out LobbySearch searchHandle) != Result.Success)
        {
            GD.PrintErr("[EOS Lobby] Failed to create lobby search handle.");
            return;
        }

        var setIdOptions = new LobbySearchSetLobbyIdOptions() { LobbyId = lobbyId };
        searchHandle.SetLobbyId(ref setIdOptions);

        var findOptions = new LobbySearchFindOptions() { LocalUserId = _localProductUserId };
        searchHandle.Find(ref findOptions, null, (ref LobbySearchFindCallbackInfo findData) =>
        {
            if (findData.ResultCode != Result.Success)
            {
                GD.PrintErr($"[EOS Lobby] Search failed: {findData.ResultCode}");
                return;
            }

            var countOptions = new LobbySearchGetSearchResultCountOptions();
            if (searchHandle.GetSearchResultCount(ref countOptions) == 0)
            {
                GD.PrintErr("[EOS Lobby] Target lobby could not be found.");
                return;
            }

            var copyResultOptions = new LobbySearchCopySearchResultByIndexOptions() { LobbyIndex = 0 };
            if (searchHandle.CopySearchResultByIndex(ref copyResultOptions, out LobbyDetails lobbyDetailsHandle) != Result.Success)
            {
                GD.PrintErr("[EOS Lobby] Failed to retrieve lobby details handle from search.");
                return;
            }

            var attrCountOptions = new LobbyDetailsGetAttributeCountOptions();
            uint attrCount = lobbyDetailsHandle.GetAttributeCount(ref attrCountOptions);
            for (uint i = 0; i < attrCount; i++)
            {
                var copyAttrOptions = new LobbyDetailsCopyAttributeByIndexOptions() { AttrIndex = i };
                if (lobbyDetailsHandle.CopyAttributeByIndex(ref copyAttrOptions, out Epic.OnlineServices.Lobby.Attribute? attr) == Result.Success)
                {
                    if (attr?.Data?.Key == "LOBBY_PASSWORD" && attr?.Data.Value.Value.AsUtf8 != enteredPassword)
                    {
                        GD.PrintErr("[EOS Lobby] Access Denied: Invalid password provided.");
                        lobbyDetailsHandle.Release();
                        return;
                    }
                }
            }

            var getOwnerOptions = new LobbyDetailsGetLobbyOwnerOptions();
            ProductUserId serverPuid = lobbyDetailsHandle.GetLobbyOwner(ref getOwnerOptions);

            if (serverPuid == null)
            {
                GD.PrintErr("[EOS Lobby] Failed to resolve lobby owner from details.");
                lobbyDetailsHandle.Release();
                return;
            }

            var joinOptions = new JoinLobbyOptions()
            {
                LocalUserId = _localProductUserId,
                LobbyDetailsHandle = lobbyDetailsHandle,
                PresenceEnabled = true
            };

            _lobbyInterface.JoinLobby(ref joinOptions, null, (ref JoinLobbyCallbackInfo joinData) =>
            {
                if (joinData.ResultCode != Result.Success)
                {
                    GD.PrintErr($"[EOS Lobby] Failed to join target lobby: {joinData.ResultCode}");
                    lobbyDetailsHandle.Release();
                    return;
                }

                GD.Print($"[EOS Lobby] Successfully inside lobby. Attaching custom Godot peer wrapper to host: {serverPuid}");

                _currentMultiplayerPeer = new EOSMultiplayerPeer();
                _currentMultiplayerPeer.InitializeAsClient(_p2pInterface, _localProductUserId, serverPuid);
                GetTree().GetMultiplayer().MultiplayerPeer = _currentMultiplayerPeer;

                lobbyDetailsHandle.Release();
            });
        });
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
        {
            if (_peerNotificationId != 0) _p2pInterface?.RemoveNotifyPeerConnectionRequest(_peerNotificationId);
            _currentMultiplayerPeer?.Close();

            _platformInterface?.Release();
            _platformInterface = null;
            PlatformInterface.Shutdown();
            GD.Print("[EOS] Cleanly shut down.");
        }
    }

    void LoadNativeEOS()
    {
        NativeLibrary.SetDllImportResolver(typeof(EOSManager).Assembly, (libraryName, assembly, searchPath) =>
        {
            if (libraryName.Contains("EOS"))
            {
                string fileName = string.Empty;
                if (OperatingSystem.IsWindows()) fileName = "EOSSDK-Win64-Shipping.dll";
                else if (OperatingSystem.IsLinux()) fileName = "libEOSSDK-Linux-Shipping.so";
                else if (OperatingSystem.IsAndroid())
                {
                    if (NativeLibrary.TryLoad("EOSSDK", out IntPtr androidHandle)) return androidHandle;
                    if (NativeLibrary.TryLoad("libEOSSDK.so", out IntPtr androidHandleLong)) return androidHandleLong;
                    return IntPtr.Zero;
                }

                if (!string.IsNullOrEmpty(fileName))
                {
                    string rootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                    if (File.Exists(rootPath) && NativeLibrary.TryLoad(rootPath, out IntPtr rootHandle)) return rootHandle;

                    string subFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EOS", "Bin", fileName);
                    if (File.Exists(subFolderPath) && NativeLibrary.TryLoad(subFolderPath, out IntPtr subFolderHandle)) return subFolderHandle;
                }
            }
            return IntPtr.Zero;
        });
    }
}
