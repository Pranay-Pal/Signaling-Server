const WebSocket = require('ws');
const { v4: uuidv4 } = require('uuid'); // Install via: npm install uuid

const PORT = process.env.PORT || 8080;
const wss = new WebSocket.Server({ port: PORT });

// State: Map<roomId, { clients: Set<WebSocket>, timer: Timeout }>
const rooms = new Map();

const ROOM_TIMEOUT_MS = 5 * 60 * 1000; // 5 Minutes

console.log(`Signaling Server running on port ${PORT}`);

wss.on('connection', (ws) => {
    // Assign a temporary ID to this socket for tracking
    ws.id = uuidv4(); 
    ws.currentRoom = null;
    console.log(`Client connected: ${ws.id}`);

    ws.on('message', (message) => {
        try {
            const data = JSON.parse(message);
            handleMessage(ws, data);
        } catch (e) {
            console.error("Invalid JSON:", e);
        }
    });

    ws.on('close', () => {
        // We don't handle complex disconnection logic, 
        // but we must remove the socket from the array to avoid errors.
        if (ws.currentRoom && rooms.has(ws.currentRoom)) {
            const room = rooms.get(ws.currentRoom);
            room.clients.delete(ws);
            // Note: We do NOT destroy the room on disconnect, 
            // we wait for the 5-min timer to do that.
        }
    });
});

function handleMessage(ws, data) {
    const type = data.type;

    switch (type) {
        case 'create':
            handleCreate(ws);
            break;

        case 'join':
            handleJoin(ws, data.roomId);
            break;

        case 'signal':
            handleSignal(ws, data);
            break;
    }
}

function handleCreate(ws) {
    const roomId = Math.floor(1000 + Math.random() * 9000).toString(); // Simple 4-digit ID
    
    createOrUpdateRoom(roomId, ws);

    ws.send(JSON.stringify({
        type: 'room_created',
        roomId: roomId,
        myId: ws.id // Client needs their own ID to identify themselves in the mesh
    }));
    
    console.log(`Room ${roomId} created by ${ws.id}`);
}

function handleJoin(ws, roomId) {
    if (!rooms.has(roomId)) {
        ws.send(JSON.stringify({ type: 'error', message: 'Room not found' }));
        return;
    }

    // Add user and EXTEND timer
    createOrUpdateRoom(roomId, ws);

    ws.send(JSON.stringify({
        type: 'room_joined',
        roomId: roomId,
        myId: ws.id
    }));

    console.log(`Client ${ws.id} joined room ${roomId}. Timer extended.`);
}

function createOrUpdateRoom(roomId, ws) {
    let room = rooms.get(roomId);

    // If room doesn't exist, init it
    if (!room) {
        room = { clients: new Set(), timer: null };
        rooms.set(roomId, room);
    }

    // Add client to room
    room.clients.add(ws);
    ws.currentRoom = roomId;

    // RESET TIMER: Clear old one, set new 5 min expiry
    if (room.timer) clearTimeout(room.timer);

    room.timer = setTimeout(() => {
        console.log(`Room ${roomId} expired (5 mins). destroying.`);
        // Optional: Notify clients the party is over? 
        // Or just silently close.
        if(rooms.has(roomId)) {
            rooms.get(roomId).clients.forEach(client => {
                if (client.readyState === WebSocket.OPEN) client.close();
            });
            rooms.delete(roomId);
        }
    }, ROOM_TIMEOUT_MS);
}

function handleSignal(ws, data) {
    // data.payload is the WebRTC data
    // data.roomId is the target
    
    const roomId = ws.currentRoom;
    if (!roomId || !rooms.has(roomId)) return;

    const room = rooms.get(roomId);

    // Broadcast to EVERYONE in the room EXCEPT the sender
    room.clients.forEach(client => {
        if (client !== ws && client.readyState === WebSocket.OPEN) {
            client.send(JSON.stringify({
                type: 'signal',
                senderId: ws.id, // CRITICAL for >2 users
                payload: data.payload
            }));
        }
    });
}