package main

import (
	"encoding/json"
	"net/http"
	"sync"

	"github.com/gorilla/websocket"
	"github.com/rs/zerolog/log"
)

var upgrader = websocket.Upgrader{
	CheckOrigin: func(r *http.Request) bool { return true },
}

type WSHub struct {
	mu      sync.RWMutex
	clients map[*WSClient]bool
}

type WSClient struct {
	conn   *websocket.Conn
	send   chan []byte
	hub    *WSHub
	chatIDs map[string]bool
}

func NewWSHub() *WSHub {
	return &WSHub{
		clients: make(map[*WSClient]bool),
	}
}

func (h *WSHub) Broadcast(evt WSEvent) {
	data, err := json.Marshal(evt)
	if err != nil {
		log.Error().Err(err).Msg("Failed to marshal WebSocket event")
		return
	}

	h.mu.RLock()
	defer h.mu.RUnlock()

	for client := range h.clients {
		select {
		case client.send <- data:
		default:
			log.Warn().Msg("WebSocket client buffer full, dropping event")
		}
	}
}

func (h *WSHub) HandleWebSocket(w http.ResponseWriter, r *http.Request) {
	conn, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		log.Error().Err(err).Msg("WebSocket upgrade failed")
		return
	}

	client := &WSClient{
		conn:    conn,
		send:    make(chan []byte, 256),
		hub:     h,
		chatIDs: make(map[string]bool),
	}

	h.mu.Lock()
	h.clients[client] = true
	h.mu.Unlock()

	log.Info().Msg("WebSocket client connected")

	go client.writePump()
	go client.readPump()
}

func (c *WSClient) readPump() {
	defer func() {
		c.hub.mu.Lock()
		delete(c.hub.clients, c)
		c.hub.mu.Unlock()
		c.conn.Close()
		log.Info().Msg("WebSocket client disconnected")
	}()

	for {
		_, message, err := c.conn.ReadMessage()
		if err != nil {
			if websocket.IsUnexpectedCloseError(err, websocket.CloseGoingAway, websocket.CloseNormalClosure) {
				log.Error().Err(err).Msg("WebSocket read error")
			}
			return
		}

		var sub WSSubscription
		if err := json.Unmarshal(message, &sub); err == nil && sub.Type == "subscriptions.set" {
			c.chatIDs = make(map[string]bool)
			for _, chatID := range sub.ChatIDs {
				c.chatIDs[chatID] = true
			}
			log.Debug().
				Int("chatCount", len(sub.ChatIDs)).
				Msg("WebSocket subscription updated")
		}
	}
}

func (c *WSClient) writePump() {
	defer c.conn.Close()

	for message := range c.send {
		if err := c.conn.WriteMessage(websocket.TextMessage, message); err != nil {
			log.Error().Err(err).Msg("WebSocket write error")
			return
		}
	}
}
