using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using NativeWebSocket;
using Unity.WebRTC;


public class WebRTCManager : MonoBehaviour
{
    [Header("Signaling")]
    public string ServerUrl = "ws://localhost:8080";

    // WebSocket
    private WebSocket webSocket;

    // WebRTC
    private RTCPeerConnection pc;
    private RTCDataChannel dataChannel;
    private bool isInitiator = false;
    private string roomId = null;
    private string myId = null;

    // Simple DTOs for JSON (Unity's JsonUtility works with fields)
    [Serializable]
    private class SignalPayload
    {
        public string sdp;
        public string sdpType; // 'offer' or 'answer'
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex = -1;
    }

    [Serializable]
    private class ClientMessage
    {
        public string type;
        public string roomId;
        public SignalPayload payload;
    }

    [Serializable]
    private class ServerMessage
    {
        public string type;
        public string roomId;
        public string myId;
        public string senderId;
        public SignalPayload payload;
        public string message;
    }

    void Start()
    {
        // Initialize WebRTC runtime
        WebRTC.Initialize(WebRTCSettings.EncoderType.Software);

        // Setup websocket
        webSocket = new WebSocket(ServerUrl);

        webSocket.OnOpen += () => Debug.Log("WebSocket: Open");
        webSocket.OnError += (e) => Debug.LogError("WebSocket Error: " + e);
        webSocket.OnClose += (e) => Debug.Log("WebSocket: Closed with code " + e);

        webSocket.OnMessage += (bytes) =>
        {
            var msg = Encoding.UTF8.GetString(bytes);
            Debug.Log("WebSocket: Message received - " + msg);
            HandleServerMessage(msg);
        };

        ConnectWebSocket();
    }

    void Update()
    {
        webSocket?.DispatchMessageQueue();
    }

    private async void ConnectWebSocket()
    {
        await webSocket.Connect();
    }

    public async void SendRaw(string json)
    {
        if (webSocket.State == WebSocketState.Open)
        {
            await webSocket.SendText(json);
        }
        else
        {
            Debug.LogWarning("WebSocket is not open. Unable to send message.");
        }
    }

    // Public helpers to start / join rooms
    public void CreateRoom()
    {
        var msg = new ClientMessage { type = "create" };
        SendRaw(JsonUtility.ToJson(msg));
        isInitiator = true; // creator will act as initiator
    }

    public void JoinRoom(string id)
    {
        var msg = new ClientMessage { type = "join", roomId = id };
        SendRaw(JsonUtility.ToJson(msg));
        isInitiator = false;
    }

    private void HandleServerMessage(string json)
    {
        var msg = JsonUtility.FromJson<ServerMessage>(json);
        if (msg == null || string.IsNullOrEmpty(msg.type)) return;

        switch (msg.type)
        {
            case "room_created":
            case "room_joined":
                roomId = msg.roomId;
                myId = msg.myId;
                Debug.Log($"Joined room {roomId} as {myId}. Initiator={isInitiator}");
                // Create peer connection now
                CreatePeerConnection();
                if (isInitiator)
                {
                    // Create data channel and send offer
                    CreateDataChannel("data");
                    StartCoroutine(CreateOffer());
                }
                break;

            case "signal":
                if (msg.payload != null)
                {
                    HandleSignalMessage(msg);
                }
                break;

            case "error":
                Debug.LogError("Signaling error: " + msg.message);
                break;
        }
    }

    private void HandleSignalMessage(ServerMessage msg)
    {
        var p = msg.payload;
        if (p == null) return;

        // SDP
        if (!string.IsNullOrEmpty(p.sdp))
        {
            var sdpType = p.sdpType?.ToLower();
            if (sdpType == "offer")
            {
                // Remote offer -> set remote and create answer
                StartCoroutine(OnReceivedOffer(p.sdp));
            }
            else if (sdpType == "answer")
            {
                // Remote answer -> set remote
                StartCoroutine(SetRemoteDescription(p.sdp, RTCSdpType.Answer));
            }
        }
        else if (!string.IsNullOrEmpty(p.candidate))
        {
            // ICE candidate
            try
            {
                var init = new RTCIceCandidateInit { candidate = p.candidate, sdpMid = p.sdpMid, sdpMLineIndex = p.sdpMLineIndex };
                var ice = new RTCIceCandidate(init);
                pc?.AddIceCandidate(ice);
                Debug.Log("Added remote ICE candidate");
            }
            catch (Exception ex)
            {
                Debug.LogError("Failed to add ICE candidate: " + ex);
            }
        }
    }

    private void CreatePeerConnection()
    {
        if (pc != null) return;

        var config = GetDefaultConfig();
        pc = new RTCPeerConnection(ref config);

        pc.OnIceCandidate = candidate =>
        {
            if (candidate == null) return;
            var payload = new SignalPayload
            {
                candidate = candidate.Candidate,
                sdpMid = candidate.SdpMid,
                sdpMLineIndex = candidate.SdpMLineIndex ?? -1
            };
            var msg = new ClientMessage { type = "signal", roomId = roomId, payload = payload };
            SendRaw(JsonUtility.ToJson(msg));
            Debug.Log("Sent ICE candidate to signaling server");
        };

        pc.OnIceConnectionChange = state => Debug.Log("ICE connection state: " + state);

        pc.OnDataChannel = channel =>
        {
            Debug.Log("Data channel received: " + channel.Label);
            dataChannel = channel;
            dataChannel.OnMessage = bytes =>
            {
                var str = Encoding.UTF8.GetString(bytes);
                Debug.Log("DataChannel message (remote): " + str);
            };
            dataChannel.OnOpen = () => Debug.Log("DataChannel open (remote)");
            dataChannel.OnClose = () => Debug.Log("DataChannel closed (remote)");
        };
    }

    private void CreateDataChannel(string label)
    {
        if (pc == null) CreatePeerConnection();
        if (dataChannel != null) return;

        var options = new RTCDataChannelInit { ordered = true };
        dataChannel = pc.CreateDataChannel(label, options);
        dataChannel.OnMessage = bytes =>
        {
            var s = Encoding.UTF8.GetString(bytes);
            Debug.Log("DataChannel message (local): " + s);
        };
        dataChannel.OnOpen = () => Debug.Log("DataChannel open (local)");
        dataChannel.OnClose = () => Debug.Log("DataChannel closed (local)");
    }

    private IEnumerator CreateOffer()
    {
        var op = pc.CreateOffer();
        yield return op;
        if (op.IsError)
        {
            Debug.LogError("CreateOffer failed: " + op.Error.message);
            yield break;
        }

        var desc = op.Desc;
        var opSet = pc.SetLocalDescription(ref desc);
        yield return opSet;
        if (opSet.IsError)
        {
            Debug.LogError("SetLocalDescription (offer) failed: " + opSet.Error.message);
            yield break;
        }

        // Send offer via signaling
        var payload = new SignalPayload { sdp = desc.sdp, sdpType = desc.type.ToString().ToLower() };
        var msg = new ClientMessage { type = "signal", roomId = roomId, payload = payload };
        SendRaw(JsonUtility.ToJson(msg));
        Debug.Log("Sent SDP offer to signaling server");
    }

    private IEnumerator OnReceivedOffer(string sdp)
    {
        // Ensure peer exists
        if (pc == null) CreatePeerConnection();

        var desc = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = sdp };
        var opRemote = pc.SetRemoteDescription(ref desc);
        yield return opRemote;
        if (opRemote.IsError)
        {
            Debug.LogError("SetRemoteDescription (offer) failed: " + opRemote.Error.message);
            yield break;
        }

        // Create answer
        var opAnswer = pc.CreateAnswer();
        yield return opAnswer;
        if (opAnswer.IsError)
        {
            Debug.LogError("CreateAnswer failed: " + opAnswer.Error.message);
            yield break;
        }

        var answerDesc = opAnswer.Desc;
        var opLocal = pc.SetLocalDescription(ref answerDesc);
        yield return opLocal;
        if (opLocal.IsError)
        {
            Debug.LogError("SetLocalDescription (answer) failed: " + opLocal.Error.message);
            yield break;
        }

        var payload = new SignalPayload { sdp = answerDesc.sdp, sdpType = answerDesc.type.ToString().ToLower() };
        var msg = new ClientMessage { type = "signal", roomId = roomId, payload = payload };
        SendRaw(JsonUtility.ToJson(msg));
        Debug.Log("Sent SDP answer to signaling server");
    }

    private IEnumerator SetRemoteDescription(string sdp, RTCSdpType type)
    {
        var desc = new RTCSessionDescription { type = type, sdp = sdp };
        var op = pc.SetRemoteDescription(ref desc);
        yield return op;
        if (op.IsError)
        {
            Debug.LogError("SetRemoteDescription failed: " + op.Error.message);
        }
        else
        {
            Debug.Log("Remote description set successfully");
        }
    }

    private RTCConfiguration GetDefaultConfig()
    {
        var config = new RTCConfiguration
        {
            iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } }
        };
        return config;
    }

    // Helper to send arbitrary data channel messages
    public void SendDataChannelMessage(string text)
    {
        if (dataChannel != null && dataChannel.ReadyState == RTCDataChannelState.Open)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            dataChannel.Send(bytes);
        }
        else
        {
            Debug.LogWarning("DataChannel not open. Cannot send message.");
        }
    }

    private void OnDestroy()
    {
        // Close and dispose PC
        if (dataChannel != null)
        {
            dataChannel.Close();
            dataChannel.Dispose();
            dataChannel = null;
        }
        if (pc != null)
        {
            pc.Close();
            pc.Dispose();
            pc = null;
        }

        // Dispose WebRTC runtime
        WebRTC.Dispose();

        // Close websocket
        try
        {
            webSocket?.Close();
        }
        catch (Exception e)
        {
            Debug.LogWarning("Error closing websocket: " + e.Message);
        }
    }
}
