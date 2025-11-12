# Signaling Server

A minimal WebSocket-based signaling server for WebRTC mesh calls. This repository contains a small Node.js WebSocket server that manages short-lived rooms and forwards WebRTC signaling messages between peers.

This README documents the server's behavior, message formats, example flows, testing scripts, deployment notes, and internals.

## Table of contents
- Overview
- Quickstart
- Message formats (inputs & outputs)
- Example flows
- Test clients & scripts
- Deployment & runtime notes
- Server internals
- Caveats, security & next steps

## Overview

The server accepts WebSocket connections and implements a small room-based signaling transport for WebRTC. Rooms are identified by 4-digit numeric IDs. Clients can create a room, join a room, and exchange `signal` messages which the server relays to other participants in the same room.

Key features:
- Simple room lifecycle with automatic expiry after inactivity (default 5 minutes)
- Per-socket UUID that clients receive as `myId` to identify the sender
- Forwards `signal` messages to every other participant in the room, attaching `senderId`

Files of interest:
- `index.js` — main server
- `WebRTCManager.cs` — example Unity client (keeps a reference for integration)
- `package.json`, `Procfile` — metadata and deployment helpers

## Quickstart

Prerequisites: Node 18+ (or a Node version compatible with the dependencies)

Install dependencies and run the server locally:

```bash
npm install
npm start
```

The server listens on `process.env.PORT` or `8080` by default.

## Message formats (inputs & outputs)

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

## Example flows

1) Create and join
- Client A connects and sends `{ "type": "create" }`. Server replies with `{ "type": "room_created", "roomId": "4321", "myId": "..." }`.
- Client B connects and sends `{ "type": "join", "roomId": "4321" }`. Server replies with `{ "type": "room_joined", "roomId": "4321", "myId": "..." }`.

2) Signaling exchange
- After joining, peers exchange `signal` messages. When B sends a `signal`, the server forwards it to A with `senderId` set to B's UUID. Clients must use `senderId` to map incoming signals to peer connections.

## Test clients & scripts

Use `wscat` for quick manual testing:

```bash
npm i -g wscat
wscat -c ws://localhost:8080
```

From one `wscat` session: `{"type":"create"}`
From another session: `{"type":"join","roomId":"<roomId from first>"}`

Node test script (two simulated clients)

Create `test-two-clients.js` with the following content and run it while the server is running:

```javascript
const WebSocket = require('ws');

const url = 'ws://localhost:8080';

function makeClient(name) {
	const ws = new WebSocket(url);
	ws.on('open', () => console.log(`${name} open`));
	ws.on('message', (m) => console.log(`${name} recv:`, m.toString()));
	ws.on('close', () => console.log(`${name} close`));
	return ws;
}

(async () => {
	const a = makeClient('A');
	const b = makeClient('B');

	await new Promise(r => setTimeout(r, 200));

	a.send(JSON.stringify({ type: 'create' }));

	a.on('message', (msg) => {
		const data = JSON.parse(msg);
		if (data.type === 'room_created') {
			const roomId = data.roomId;
			console.log('Room created:', roomId);
			b.send(JSON.stringify({ type: 'join', roomId }));
			setTimeout(() => {
				b.send(JSON.stringify({ type: 'signal', payload: JSON.stringify({ type: 'dummy', text: 'hello from B' }) }));
			}, 200);
		}
	});
})();
```

## Deployment & runtime notes

- The server uses `process.env.PORT` or `8080`.
- A `Procfile` is included for platforms that honor it (Heroku/Render compatible).
- The server does not implement TLS termination — deploy behind a reverse proxy or load balancer that provides TLS, or enable TLS at the WebSocket server when adding certs.

## Server internals

- `index.js` — the server code. Key functions:
	- `handleMessage(ws, data)` — routes messages by `type`.
	- `handleCreate(ws)` — generates a 4-digit `roomId`, adds the creator to the room, and replies `room_created`.
	- `handleJoin(ws, roomId)` — joins an existing room and replies `room_joined` or an error if missing.
	- `createOrUpdateRoom(roomId, ws)` — ensures a room object exists, adds the socket, sets `ws.currentRoom`, and resets the expiry timer.
	- `handleSignal(ws, data)` — forwards `signal` messages to all other clients in the room with `senderId` added.

Room state shape:

```js
// Map<roomId, { clients: Set<WebSocket>, timer: Timeout }>
```

Room expiry: `ROOM_TIMEOUT_MS` (default 5 minutes). When a room expires, the server closes any connected sockets in that room and deletes the room.


# Signaling-Server