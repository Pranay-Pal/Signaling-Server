using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Android; // For permissions

#region --- JSON Message Classes ---

// Base classes to help with JSON deserialization
[Serializable]
public abstract class BaseMessage { public string type; }

// --- Client-to-Server Messages ---
[Serializable]
public class CreateRoomMessage : BaseMessage { public CreateRoomMessage() { type = "create"; } }
[Serializable]
public class JoinRoomMessage : BaseMessage { public string roomId; public JoinRoomMessage(string id) { type = "join"; roomId = id; } }
[Serializable]
public class SignalMessage : BaseMessage { public string targetId; public string payload; public SignalMessage(string target, string load) { type = "signal"; targetId = target; payload = load; } }

// --- Server-to-Client Messages ---
[Serializable]
public class RoomJoinedMessage : BaseMessage { public string roomId; public string myId; }
[Serializable]
public class InitiateCallMessage : BaseMessage { public string peerId; }
[Serializable]
public class SignalRelayMessage : BaseMessage { public string senderId; public string payload; }
[Serializable]
public class ErrorMessage : BaseMessage { public string message; }

// --- WebRTC Payload (inner JSON for serialization) ---
// We wrap the SDP/Candidate in a simple class to give it a 'type'
// This helps the receiver deserialize the correct object.
[Serializable]
public class BaseWebRTCPayload { public string type; }
[Serializable]
public class IceCandidatePayload : BaseWebRTCPayload { public RTCIceCandidate candidate; public IceCandidatePayload(RTCIceCandidate c) { type = "candidate"; candidate = c; } }
[Serializable]
public class SessionDescriptionPayload : BaseWebRTCPayload { public RTCSessionDescription sdp; public SessionDescriptionPayload(RTCSessionDescription s) { type = s.type == RTCSdpType.Offer ? "offer" : "answer"; sdp = s; } }

#endregion

public class WebRTCManager : MonoBehaviour
{
    [Header("Signaling Server")]
    // IMPORTANT: Use wss:// for Render deployment
    public string signalingUrl = "wss://your-app-name.onrender.com";

    [Header("WebRTC Video")]
    public RawImage localVideoView; // Keep this to see our own camera

    private string _myId;
    private string _roomId;
    private ClientWebSocket _ws;
    private CancellationTokenSource _cts;

    private WebCamTexture _webCam;
    private VideoStreamTrack _localVideoTrack;
    private AudioStreamTrack _localAudioTrack;
    
    // Key: The peer's unique ID. Value: The connection to that peer.
    private Dictionary<string, RTCPeerConnection> _peerConnections = new Dictionary<string, RTCPeerConnection>();
    // Key: The peer's unique ID. Value: The data channel to that peer.
    private Dictionary<string, RTCDataChannel> _dataChannels = new Dictionary<string, RTCDataChannel>();
    
    // Thread-safe queue for messages from WebSocket thread to Unity main thread
    private ConcurrentQueue<string> _messageQueue = new ConcurrentQueue<string>();
    
    #region --- Unity Lifecycle ---

    void Start()
    {
        // 1. Ask for permissions (Camera/Mic)
        #if UNITY_ANDROID
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera)) Permission.RequestUserPermission(Permission.Camera);
        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone)) Permission.RequestUserPermission(Permission.Microphone);
        #endif

        // 2. Start WebRTC's internal update loop
        StartCoroutine(WebRTC.Update());

        // 3. Start local camera & mic
        StartCoroutine(StartMedia());

        // 4. Connect to signaling server
        ConnectToSignaling();
    }

    void Update()
    {
        // Process messages from the WebSocket thread
        while (_messageQueue.TryDequeue(out string message))
        {
            HandleSignalingMessage(message);
        }
    }

    async void OnDestroy()
    {
        // Clean up everything
        if (_webCam != null) _webCam.Stop();
        _localVideoTrack?.Dispose();
        _localAudioTrack?.Dispose();

        foreach (var dc in _dataChannels.Values)
        {
            dc.Close();
            dc.Dispose();
        }
        _dataChannels.Clear();

        foreach (var pc in _peerConnections.Values)
        {
            pc.Close();
            pc.Dispose();
        }
        _peerConnections.Clear();

        if (_cts != null) _cts.Cancel();
        if (_ws != null && _ws.State == WebSocketState.Open)
        {
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client shutting down", CancellationToken.None);
        }
        _ws?.Dispose();
    }

    #endregion

    #region --- Public Control Methods ---

    public void CreateRoom()
    {
        SendMessage(new CreateRoomMessage());
        Debug.Log("Attempting to create room...");
    }

    public void JoinRoom(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            Debug.LogError("Room ID is null or empty");
            return;
        }
        SendMessage(new JoinRoomMessage(id));
        Debug.Log($"Attempting to join room {id}...");
    }

    public void BroadcastData(string message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        foreach (var dc in _dataChannels.Values)
        {
            if (dc.ReadyState == RTCDataChannelState.Open)
            {
                dc.Send(bytes);
            }
        }
    }

    public void SendDataToPeer(string peerId, string message)
    {
        if (_dataChannels.TryGetValue(peerId, out RTCDataChannel dc))
        {
            if (dc.ReadyState == RTCDataChannelState.Open)
            {
                dc.Send(Encoding.UTF8.GetBytes(message));
            }
            else
            {
                Debug.LogWarning($"Data channel with {peerId} is not open (state: {dc.ReadyState})");
            }
        }
        else
        {
            Debug.LogWarning($"No data channel found for peer {peerId}");
        }
    }

    #endregion

    #region --- WebRTC & Media ---

    private IEnumerator StartMedia()
    {
        // --- Start Video ---
        WebCamDevice[] devices = WebCamTexture.devices;
        if (devices.Length == 0)
        {
            Debug.LogError("No camera found");
            yield break;
        }
        _webCam = new WebCamTexture(devices[0].name, 1280, 720, 30);
        _webCam.Play();
        localVideoView.texture = _webCam; // Show local view
        yield return new WaitUntil(() => _webCam.width > 16);
        _localVideoTrack = new VideoStreamTrack(_webCam);

        // --- Start Audio ---
        // This creates a track that captures from the default microphone
        _localAudioTrack = new AudioStreamTrack();
        Debug.Log("Local video and audio tracks initialized.");
    }

    private RTCPeerConnection CreatePeerConnection(string peerId)
    {
        // 1. Define STUN/TURN servers
        var config = new RTCConfiguration
        {
            iceServers = new[]
            {
                new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } }
                // Add your TURN server here if needed for Render deployment
            }
        };

        // 2. Create the connection
        var pc = new RTCPeerConnection(ref config);

        // 3. Handle ICE Candidates: Send them to the target peer
        pc.OnIceCandidate = candidate =>
        {
            var payload = JsonUtility.ToJson(new IceCandidatePayload(candidate));
            SendMessage(new SignalMessage(peerId, payload));
        };

        // 4. Handle incoming media tracks
        pc.OnTrack = e =>
        {
            if (e.Track is VideoTrack track)
            {
                Debug.Log($"Received video track from {peerId}");
                // You can get the texture from this track and assign it to a RawImage
                // track.OnVideoReceived += tex => { /* e.g. remoteVideoView.texture = tex; */ };
            }
            if (e.Track is AudioTrack atrack)
            {
                Debug.Log($"Received audio track from {peerId}");
                // Audio should play automatically
            }
        };

        // 5. Handle incoming data channels
        pc.OnDataChannel = dc =>
        {
            Debug.Log($"Received data channel from {peerId}");
            _dataChannels[peerId] = dc;
            RegisterDataChannelCallbacks(dc, peerId);
        };

        // 6. Add our local tracks so they can see/hear us
        if (_localVideoTrack != null) pc.AddTrack(_localVideoTrack);
        if (_localAudioTrack != null) pc.AddTrack(_localAudioTrack);
        
        return pc;
    }

    private void RegisterDataChannelCallbacks(RTCDataChannel dc, string peerId)
    {
        dc.OnOpen = () => Debug.Log($"Data channel with {peerId} OPENED");
        dc.OnClose = () => Debug.Log($"Data channel with {peerId} CLOSED");
        dc.OnMessage = bytes =>
        {
            string msg = Encoding.UTF8.GetString(bytes);
            Debug.Log($"Message from {peerId}: {msg}");
        };
    }

    // Called by "Existing Clients" when a new peer joins
    private IEnumerator CreateAndSendOffer(string peerId)
    {
        Debug.Log($"Creating offer for new peer: {peerId}");

        // 1. Create a new PC for this specific peer
        RTCPeerConnection pc = CreatePeerConnection(peerId);
        _peerConnections[peerId] = pc;

        // 2. Create Data Channel (as the offerer, we initiate this)
        RTCDataChannel dc = pc.CreateDataChannel("data");
        _dataChannels[peerId] = dc;
        RegisterDataChannelCallbacks(dc, peerId);

        // 3. Create the offer
        var offerOp = pc.CreateOffer();
        yield return offerOp;

        if (offerOp.IsError) { Debug.LogError($"Error creating offer for {peerId}: {offerOp.Error.message}"); yield break; }

        // 4. Set local description
        var desc = offerOp.Desc;
        var localDescOp = pc.SetLocalDescription(ref desc);
        yield return localDescOp;

        // 5. Send the offer to the target peer
        var payload = JsonUtility.ToJson(new SessionDescriptionPayload(desc));
        SendMessage(new SignalMessage(peerId, payload));
    }

    // Called by "New Joiner" when they receive an offer
    private IEnumerator HandleOffer(string senderId, RTCSessionDescription sdp)
    {
        Debug.Log($"Handling offer from peer: {senderId}");
        
        // 1. Create a PC for the peer who sent the offer
        RTCPeerConnection pc = CreatePeerConnection(senderId);
        _peerConnections[senderId] = pc;

        // 2. Set the remote description (the offer)
        var remoteDescOp = pc.SetRemoteDescription(ref sdp);
        yield return remoteDescOp;

        // 3. Create an answer
        var answerOp = pc.CreateAnswer();
        yield return answerOp;

        if (answerOp.IsError) { Debug.LogError($"Error creating answer for {senderId}: {answerOp.Error.message}"); yield break; }

        // 4. Set local description
        var desc = answerOp.Desc;
        var localDescOp = pc.SetLocalDescription(ref desc);
        yield return localDescOp;

        // 5. Send the answer back to the sender
        var payload = JsonUtility.ToJson(new SessionDescriptionPayload(desc));
        SendMessage(new SignalMessage(senderId, payload));
    }

    // Called by "Existing Client" when they get an answer
    private IEnumerator HandleAnswer(string senderId, RTCSessionDescription sdp)
    {
        Debug.Log($"Handling answer from peer: {senderId}");

        if (_peerConnections.TryGetValue(senderId, out RTCPeerConnection pc))
        {
            var remoteDescOp = pc.SetRemoteDescription(ref sdp);
            yield return remoteDescOp;
        }
    }

    // Called by everyone
    private void HandleCandidate(string senderId, RTCIceCandidate candidate)
    {
        if (_peerConnections.TryGetValue(senderId, out RTCPeerConnection pc))
        {
            pc.AddIceCandidate(candidate);
        }
    }

    #endregion

    #region --- WebSocket Signaling ---

    private async void ConnectToSignaling()
    {
        _ws = new ClientWebSocket();
        _cts = new CancellationTokenSource();
        try
        {
            await _ws.ConnectAsync(new Uri(signalingUrl), _cts.Token);
            Debug.Log("Connected to Signaling Server");
            StartReceivingMessages();
        }
        catch (Exception e)
        {
            Debug.LogError($"WebSocket Connection Error: {e.Message}");
        }
    }

    private async void StartReceivingMessages()
    {
        var buffer = new byte[1024 * 4];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _messageQueue.Enqueue(message); // Pass to main thread
                }
            }
        }
        catch (OperationCanceledException)
        {
            Debug.Log("WebSocket connection closed normally.");
        }
        catch (Exception e)
        {
            Debug.LogError($"WebSocket Receive Error: {e.Message}");
        }
    }

    private void HandleSignalingMessage(string json)
    {
        // First, deserialize just to find the type
        BaseMessage baseMsg = JsonUtility.FromJson<BaseMessage>(json);

        switch (baseMsg.type)
        {
            case "room_joined":
                var joinedMsg = JsonUtility.FromJson<RoomJoinedMessage>(json);
                _myId = joinedMsg.myId;
                _roomId = joinedMsg.roomId;
                Debug.Log($"Joined room {_roomId} as {_myId}");
                break;

            case "room_created":
                var createdMsg = JsonUtility.FromJson<RoomJoinedMessage>(json);
                _myId = createdMsg.myId;
                _roomId = createdMsg.roomId;
                Debug.Log($"Created room {_roomId} as {_myId}");
                break;
            
            case "initiate_call":
                // Server telling us (an existing client) to call a new peer
                var initMsg = JsonUtility.FromJson<InitiateCallMessage>(json);
                StartCoroutine(CreateAndSendOffer(initMsg.peerId));
                break;

            case "signal":
                // An Offer, Answer, or Candidate from another peer
                var relayMsg = JsonUtility.FromJson<SignalRelayMessage>(json);
                BaseWebRTCPayload innerPayload = JsonUtility.FromJson<BaseWebRTCPayload>(relayMsg.payload);
                
                if (innerPayload.type == "offer")
                {
                    var sdpPayload = JsonUtility.FromJson<SessionDescriptionPayload>(relayMsg.payload);
                    StartCoroutine(HandleOffer(relayMsg.senderId, sdpPayload.sdp));
                }
                else if (innerPayload.type == "answer")
                {
                    var sdpPayload = JsonUtility.FromJson<SessionDescriptionPayload>(relayMsg.payload);
                    StartCoroutine(HandleAnswer(relayMsg.senderId, sdpPayload.sdp));
                }
                else if (innerPayload.type == "candidate")
                {
                    var candPayload = JsonUtility.FromJson<IceCandidatePayload>(relayMsg.payload);
                    HandleCandidate(relayMsg.senderId, candPayload.candidate);
                }
                break;

            case "error":
                var errMsg = JsonUtility.FromJson<ErrorMessage>(json);
                Debug.LogError($"Error from server: {errMsg.message}");
                break;
        }
    }

    private async void SendMessage(BaseMessage message)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
        {
            Debug.LogError("WebSocket is not connected.");
            return;
        }
        
        string json = JsonUtility.ToJson(message);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
    }

    #endregion
}