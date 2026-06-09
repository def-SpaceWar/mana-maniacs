using Godot;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using System.Collections.Generic;

public partial class EOSMultiplayerPeer : MultiplayerPeerExtension
{
    private P2PInterface _p2pInterface;
    private ProductUserId _localProductUserId;
    private bool _isServer;
    private ConnectionStatus _connectionStatus = ConnectionStatus.Disconnected;

    private int _targetPeer;
    private int _transferChannel = 0;
    private TransferModeEnum _transferMode = TransferModeEnum.Reliable;

    // Translation maps between Godot Integer Peer IDs and EOS ProductUserIds
    private readonly Dictionary<int, ProductUserId> _idToPuid = [];
    private readonly Dictionary<ProductUserId, int> _puidToId = [];

    private struct InboundPacket
    {
        public byte[] Data;
        public int SenderId;
        public int Channel;
        public TransferModeEnum Mode;
    }

    private readonly Queue<InboundPacket> _incomingPackets = new();
    private InboundPacket? _currentPacket = null;

    public void InitializeAsServer(P2PInterface p2pInterface, ProductUserId localPuid)
    {
        _p2pInterface = p2pInterface;
        _localProductUserId = localPuid;
        _isServer = true;
        _connectionStatus = ConnectionStatus.Connected;
    }

    public void InitializeAsClient(P2PInterface p2pInterface, ProductUserId localPuid, ProductUserId serverPuid)
    {
        _p2pInterface = p2pInterface;
        _localProductUserId = localPuid;
        _isServer = false;
        _connectionStatus = ConnectionStatus.Connected;

        // Map the host explicitly to Peer ID 1
        _puidToId[serverPuid] = 1;
        _idToPuid[1] = serverPuid;

        EmitSignal(SignalName.PeerConnected, 1);
    }

    public int RegisterRemotePeer(ProductUserId puid)
    {
        if (_puidToId.TryGetValue(puid, out int existingId))
            return existingId;

        // Deterministically generate a stable, positive 31-bit integer (> 1) from the PUID string
        int generatedId = (puid.ToString().GetHashCode() & 0x7FFFFFFF) % 2147483645 + 2;

        _puidToId[puid] = generatedId;
        _idToPuid[generatedId] = puid;

        EmitSignal(SignalName.PeerConnected, generatedId);
        return generatedId;
    }

    public void UnregisterRemotePeer(ProductUserId puid)
    {
        if (_puidToId.TryGetValue(puid, out int id))
        {
            _puidToId.Remove(puid);
            _idToPuid.Remove(id);
            EmitSignal(SignalName.PeerDisconnected, id);
        }
    }

    public override int _GetUniqueId()
    {
        if (_isServer) return 1;
        return (_localProductUserId.ToString().GetHashCode() & 0x7FFFFFFF) % 2147483645 + 2;
    }

    public override bool _IsServer() => _isServer;
    public override ConnectionStatus _GetConnectionStatus() => _connectionStatus;

    public override void _Poll()
    {
        if (_p2pInterface == null || _localProductUserId == null) return;

        ProductUserId remotePuid = null;
        SocketId socketId = default;

        for (byte ch = 0; ch < 4; ch++)
        {
            var sizeOptions = new GetNextReceivedPacketSizeOptions()
            {
                LocalUserId = _localProductUserId,
                RequestedChannel = ch
            };

            while (_p2pInterface.GetNextReceivedPacketSize(ref sizeOptions, out uint nextPacketSize) == Result.Success)
            {
                byte[] packetBuffer = new byte[nextPacketSize];
                var receiveOptions = new ReceivePacketOptions()
                {
                    LocalUserId = _localProductUserId,
                    MaxDataSizeBytes = nextPacketSize,
                    RequestedChannel = ch
                };

                var result = _p2pInterface.ReceivePacket(
                    ref receiveOptions,
                    ref remotePuid,
                    ref socketId,
                    out byte outChannel,
                    packetBuffer,
                    out uint bytesRead
                );

                if (result == Result.Success && remotePuid != null)
                {
                    int senderId = RegisterRemotePeer(remotePuid);
                    _incomingPackets.Enqueue(new InboundPacket
                    {
                        Data = packetBuffer,
                        SenderId = senderId,
                        Channel = outChannel,
                        Mode = _transferMode
                    });
                }
            }
        }
    }

    public override Error _PutPacketScript(byte[] pBuffer)
    {
        if (_p2pInterface == null || _localProductUserId == null) return Error.Failed;

        var sendOptions = new SendPacketOptions()
        {
            LocalUserId = _localProductUserId,
            SocketId = new SocketId() { SocketName = "GameTraffic" },
            Channel = (byte)_transferChannel,
            Data = pBuffer,
            Reliability = ConvertReliability(_transferMode),
            AllowDelayedDelivery = true
        };

        // Target ID 0 means broadcast to all known peers
        if (_targetPeer == 0)
        {
            foreach (var pair in _idToPuid)
            {
                if (pair.Key == _GetUniqueId()) continue;
                sendOptions.RemoteUserId = pair.Value;
                _p2pInterface.SendPacket(ref sendOptions);
            }
            return Error.Ok;
        }

        if (_idToPuid.TryGetValue(_targetPeer, out var targetPuid))
        {
            sendOptions.RemoteUserId = targetPuid;
            return _p2pInterface.SendPacket(ref sendOptions) == Result.Success ? Error.Ok : Error.Failed;
        }

        return Error.InvalidParameter;
    }

    public override byte[] _GetPacketScript()
    {
        if (_incomingPackets.Count == 0) return [];
        _currentPacket = _incomingPackets.Dequeue();
        return _currentPacket.Value.Data;
    }

    public override int _GetAvailablePacketCount() => _incomingPackets.Count;
    public override int _GetPacketPeer() => _currentPacket?.SenderId ?? 0;
    public override int _GetPacketChannel() => _currentPacket?.Channel ?? 0;
    public override TransferModeEnum _GetPacketMode() => _currentPacket?.Mode ?? TransferModeEnum.Reliable;

    public override void _SetTransferChannel(int pChannel) => _transferChannel = pChannel;
    public override int _GetTransferChannel() => _transferChannel;
    public override void _SetTransferMode(TransferModeEnum pMode) => _transferMode = pMode;
    public override TransferModeEnum _GetTransferMode() => _transferMode;
    public override void _SetTargetPeer(int pPeer) => _targetPeer = pPeer;

    public override void _Close()
    {
        _incomingPackets.Clear();
        _idToPuid.Clear();
        _puidToId.Clear();
        _connectionStatus = ConnectionStatus.Disconnected;
    }

    public override void _DisconnectPeer(int pPeer, bool pForce) => UnregisterRemotePeer(_idToPuid[pPeer]);

    private static PacketReliability ConvertReliability(TransferModeEnum mode)
    {
        return mode switch
        {
            TransferModeEnum.Unreliable => PacketReliability.UnreliableUnordered,
            TransferModeEnum.UnreliableOrdered => PacketReliability.UnreliableUnordered,
            TransferModeEnum.Reliable => PacketReliability.ReliableOrdered,
            _ => PacketReliability.ReliableOrdered
        };
    }
}
