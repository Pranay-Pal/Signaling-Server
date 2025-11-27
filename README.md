
All messages are JSON strings sent over WebSocket.

Client -> Server

1) Create a room

Request:
```json
{ "type": "create" }
```

Response (Server -> client who created):
```json
{ "type": "room_created", "roomId": "1234", "myId": "<client-uuid>" }
```

2) Join a room

Request:
```json
{ "type": "join", "roomId": "1234" }
```

Responses:
- Success:
```json
{ "type": "room_joined", "roomId": "1234", "myId": "<client-uuid>" }
```
- Error (if room not found):
```json
{ "type": "error", "message": "Room not found" }
```

3) Signal relay (offer/answer/ICE)

Request:
```json
{
	"type": "signal",
	"payload": "<JSON-string-or-object>"
}
```

Notes:
- `payload` can be a JSON object or a string containing the inner WebRTC data (SDP or ICE candidate). The server treats it as opaque and simply forwards it.
- The server determines the target room using the socket's `currentRoom` property (set after create/join).

Server -> Other clients in the same room (forwarded signal):
```json
{
	"type": "signal",
	"senderId": "<uuid-of-sender>",
	"payload": "<original-payload>"
}
```