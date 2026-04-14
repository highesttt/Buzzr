package main

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"runtime"
	"strconv"
	"strings"
	"time"

	"github.com/rs/zerolog/log"
	"maunium.net/go/mautrix/event"
)

var Version = "0.1.0"

type Server struct {
	port   int
	mc     *MatrixClient
	store  *Store
	wsHub  *WSHub
	cfg    *Config
	server *http.Server
}

func NewServer(port int, mc *MatrixClient, store *Store, wsHub *WSHub, cfg *Config) *Server {
	return &Server{
		port:  port,
		mc:    mc,
		store: store,
		wsHub: wsHub,
		cfg:   cfg,
	}
}

func (s *Server) baseURL() string {
	return fmt.Sprintf("http://localhost:%d", s.port)
}

func (s *Server) Start() error {
	mux := http.NewServeMux()

	mux.HandleFunc("POST /v1/auth/login", s.handleAuthLogin)
	mux.HandleFunc("POST /v1/auth/verify", s.handleAuthVerify)
	mux.HandleFunc("POST /v1/auth/logout", s.handleAuthLogout)
	mux.HandleFunc("GET /v1/auth/status", s.handleAuthStatus)
	mux.HandleFunc("POST /v1/auth/verify-device", s.withAuth(s.handleStartVerification))
	mux.HandleFunc("GET /v1/auth/verify-device/status", s.withAuth(s.handleVerificationStatus))
	mux.HandleFunc("POST /v1/auth/verify-device/confirm", s.withAuth(s.handleConfirmVerification))
	mux.HandleFunc("GET /v1/info", s.handleInfo)
	mux.HandleFunc("GET /v1/accounts", s.withAuth(s.handleGetAccounts))
	mux.HandleFunc("GET /v1/chats", s.withAuth(s.handleGetChats))
	mux.HandleFunc("POST /v1/chats", s.withAuth(s.handleCreateChat))
	mux.HandleFunc("GET /v1/chats/search", s.withAuth(s.handleSearchChats))
	mux.HandleFunc("GET /v1/chats/{chatID}", s.withAuth(s.handleGetChat))
	mux.HandleFunc("POST /v1/chats/{chatID}/archive", s.withAuth(s.handleArchiveChat))
	mux.HandleFunc("POST /v1/chats/{chatID}/pin", s.withAuth(s.handlePinChat))
	mux.HandleFunc("POST /v1/chats/{chatID}/unpin", s.withAuth(s.handleUnpinChat))
	mux.HandleFunc("POST /v1/chats/{chatID}/mute", s.withAuth(s.handleMuteChat))
	mux.HandleFunc("POST /v1/chats/{chatID}/unmute", s.withAuth(s.handleUnmuteChat))
	mux.HandleFunc("POST /v1/chats/{chatID}/markread", s.withAuth(s.handleMarkRead))
	mux.HandleFunc("GET /v1/chats/{chatID}/messages", s.withAuth(s.handleGetMessages))
	mux.HandleFunc("POST /v1/chats/{chatID}/messages", s.withAuth(s.handleSendMessage))
	mux.HandleFunc("PUT /v1/chats/{chatID}/messages/{messageID}", s.withAuth(s.handleEditMessage))
	mux.HandleFunc("DELETE /v1/chats/{chatID}/messages/{messageID}", s.withAuth(s.handleDeleteMessage))
	mux.HandleFunc("POST /v1/chats/{chatID}/messages/{messageID}/reactions", s.withAuth(s.handleAddReaction))
	mux.HandleFunc("DELETE /v1/chats/{chatID}/messages/{messageID}/reactions", s.withAuth(s.handleRemoveReaction))
	mux.HandleFunc("GET /v1/search", s.withAuth(s.handleUnifiedSearch))
	mux.HandleFunc("GET /v1/messages/search", s.withAuth(s.handleSearchMessages))
	mux.HandleFunc("POST /v1/assets/upload", s.withAuth(s.handleUploadAsset))
	mux.HandleFunc("POST /v1/assets/upload/base64", s.withAuth(s.handleUploadBase64))
	mux.HandleFunc("POST /v1/assets/download", s.withAuth(s.handleDownloadAsset))
	mux.HandleFunc("GET /v1/assets/serve", s.handleServeAsset)
	mux.HandleFunc("GET /v1/accounts/{accountID}/contacts", s.withAuth(s.handleSearchContacts))
	mux.HandleFunc("GET /v1/accounts/{accountID}/contacts/list", s.withAuth(s.handleListContacts))
	mux.HandleFunc("GET /v1/ws", s.handleWebSocket)
	mux.HandleFunc("POST /v1/focus", s.withAuth(s.handleFocus))

	s.server = &http.Server{
		Addr:    fmt.Sprintf(":%d", s.port),
		Handler: s.corsMiddleware(s.logMiddleware(mux)),
	}

	lc := net.ListenConfig{
		Control: setReuseAddr,
	}
	ln, err := lc.Listen(context.Background(), "tcp", s.server.Addr)
	if err != nil {
		return fmt.Errorf("listen on port %d: %w", s.port, err)
	}

	log.Info().Int("port", s.port).Msg("HTTP server starting")
	return s.server.Serve(ln)
}

func (s *Server) Stop() {
	if s.server != nil {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		s.server.Shutdown(ctx)
	}
}

func (s *Server) corsMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS")
		w.Header().Set("Access-Control-Allow-Headers", "Authorization, Content-Type")
		if r.Method == "OPTIONS" {
			w.WriteHeader(http.StatusNoContent)
			return
		}
		next.ServeHTTP(w, r)
	})
}

func (s *Server) logMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		next.ServeHTTP(w, r)
		log.Debug().
			Str("method", r.Method).
			Str("path", r.URL.Path).
			Dur("duration", time.Since(start)).
			Msg("HTTP request")
	})
}

func (s *Server) withAuth(handler http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		token := extractBearerToken(r)
		expectedToken := s.cfg.GetLocalAPIToken()

		if token == "" || token != expectedToken {
			writeError(w, http.StatusUnauthorized, "UNAUTHORIZED", "Missing or invalid access token")
			return
		}
		handler(w, r)
	}
}

func extractBearerToken(r *http.Request) string {
	auth := r.Header.Get("Authorization")
	if strings.HasPrefix(auth, "Bearer ") {
		return strings.TrimPrefix(auth, "Bearer ")
	}
	return ""
}

func writeJSON(w http.ResponseWriter, status int, data interface{}) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(data)
}

func writeError(w http.ResponseWriter, status int, code, message string) {
	writeJSON(w, status, APIErrorResponse{Code: code, Message: message})
}

func readJSON(r *http.Request, v interface{}) error {
	defer r.Body.Close()
	return json.NewDecoder(r.Body).Decode(v)
}

func (s *Server) handleInfo(w http.ResponseWriter, r *http.Request) {
	osName := runtime.GOOS
	if osName == "windows" {
		osName = "win32"
	}

	info := APIInfo{
		App: APIAppInfo{
			Name:     "Buzzr",
			Version:  Version,
			BundleID: "dev.highest.buzzr.sidecar",
		},
		Platform: APIPlatformInfo{
			OS:   osName,
			Arch: runtime.GOARCH,
		},
		Server: APIServerInfo{
			Status:       "running",
			BaseURL:      s.baseURL(),
			Port:         s.port,
			Hostname:     "localhost",
			RemoteAccess: false,
			MCPEnabled:   false,
		},
		Endpoints: APIEndpoints{
			WsEvents: fmt.Sprintf("ws://localhost:%d/v1/ws", s.port),
		},
	}
	writeJSON(w, http.StatusOK, info)
}

func (s *Server) handleAuthLogin(w http.ResponseWriter, r *http.Request) {
	var req APILoginRequest
	if err := readJSON(r, &req); err != nil || req.Email == "" {
		writeError(w, http.StatusBadRequest, "INVALID_REQUEST", "email is required")
		return
	}

	if err := s.mc.StartLogin(r.Context(), req.Email); err != nil {
		writeError(w, http.StatusInternalServerError, "LOGIN_FAILED", err.Error())
		return
	}

	writeJSON(w, http.StatusOK, APILoginResponse{
		Status:  "code_sent",
		Message: "Check your email for a verification code",
	})
}

func (s *Server) handleAuthVerify(w http.ResponseWriter, r *http.Request) {
	var req APIVerifyRequest
	if err := readJSON(r, &req); err != nil || req.Email == "" || req.Code == "" {
		writeError(w, http.StatusBadRequest, "INVALID_REQUEST", "email and code are required")
		return
	}

	localToken, err := s.mc.CompleteLogin(r.Context(), req.Email, req.Code)
	if err != nil {
		writeError(w, http.StatusUnauthorized, "AUTH_FAILED", err.Error())
		return
	}

	writeJSON(w, http.StatusOK, APIVerifyResponse{
		Status:        "authenticated",
		UserID:        s.cfg.Session.UserID,
		AccessToken:   localToken,
		HomeserverURL: s.cfg.Session.HomeserverURL,
	})
}

func (s *Server) handleAuthLogout(w http.ResponseWriter, r *http.Request) {
	s.mc.Stop()
	s.cfg.ClearSession()
	writeJSON(w, http.StatusOK, map[string]bool{"success": true})
}

func (s *Server) handleAuthStatus(w http.ResponseWriter, r *http.Request) {
	s.mc.mu.RLock()
	displayName := s.mc.displayName
	s.mc.mu.RUnlock()

	writeJSON(w, http.StatusOK, map[string]interface{}{
		"loggedIn":     s.mc.IsLoggedIn(),
		"userID":       s.cfg.Session.UserID,
		"homeserver":   s.cfg.Session.HomeserverURL,
		"roomCount":    s.store.RoomCount(),
		"accountCount": len(s.store.GetAccounts()),
		"displayName":  displayName,
	})
}

func (s *Server) handleStartVerification(w http.ResponseWriter, r *http.Request) {
	txnID, err := s.mc.StartDeviceVerification(r.Context())
	if err != nil {
		writeError(w, http.StatusInternalServerError, "VERIFICATION_FAILED", err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]interface{}{
		"status": "started",
		"txnID":  txnID,
	})
}

func (s *Server) handleVerificationStatus(w http.ResponseWriter, r *http.Request) {
	status := s.mc.GetVerificationStatus()
	writeJSON(w, http.StatusOK, status)
}

func (s *Server) handleConfirmVerification(w http.ResponseWriter, r *http.Request) {
	if err := s.mc.ConfirmDeviceVerification(r.Context()); err != nil {
		writeError(w, http.StatusInternalServerError, "CONFIRM_FAILED", err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]interface{}{
		"status": "confirmed",
	})
}

func (s *Server) handleGetAccounts(w http.ResponseWriter, r *http.Request) {
	accounts := s.store.GetAccounts()
	result := make([]APIAccount, len(accounts))
	base := s.baseURL()
	for i, a := range accounts {
		apiAcct := AccountToAPIAccount(a)
		if strings.HasPrefix(apiAcct.User.ImgURL, "mxc://") {
			apiAcct.User.ImgURL = fmt.Sprintf("%s/v1/assets/serve?uri=%s", base, url.QueryEscape(apiAcct.User.ImgURL))
		}
		if a.AccountID == "hungryserv" && apiAcct.User.ImgURL == "" && a.User != nil {
			rooms := s.store.GetRoomsFiltered("", nil, nil, nil, nil)
			for _, room := range rooms {
				for _, m := range room.GetMembersSnapshot() {
					if m.UserID == a.User.ID && m.AvatarURL != "" {
						apiAcct.User.ImgURL = fmt.Sprintf("%s/v1/assets/serve?uri=%s", base, url.QueryEscape(m.AvatarURL))
						break
					}
				}
				if apiAcct.User.ImgURL != "" {
					break
				}
			}
		}
		result[i] = apiAcct
	}
	writeJSON(w, http.StatusOK, result)
}

func (s *Server) handleGetChats(w http.ResponseWriter, r *http.Request) {
	query := r.URL.Query()
	cursor := query.Get("cursor")
	accountID := query.Get("accountIDs")
	if accountID == "" {
		accountID = query.Get("accountID")
	}
	inbox := query.Get("inbox")

	var isLowPriority *bool
	var isArchived *bool
	if inbox == "low-priority" {
		t := true
		isLowPriority = &t
	} else if inbox == "archive" {
		t := true
		isArchived = &t
	}

	rooms := s.store.GetRoomsFiltered(accountID, nil, nil, isArchived, isLowPriority)

	pageSize := 50
	if ls := query.Get("limit"); ls != "" {
		if lv, err := strconv.Atoi(ls); err == nil && lv > 0 {
			pageSize = lv
		}
	}

	startIdx := 0
	if cursor != "" {
		if idx, err := strconv.Atoi(cursor); err == nil && idx > 0 {
			startIdx = idx
		}
	}
	if startIdx > len(rooms) {
		startIdx = len(rooms)
	}
	endIdx := startIdx + pageSize
	if endIdx > len(rooms) {
		endIdx = len(rooms)
	}

	page := rooms[startIdx:endIdx]
	hasMore := endIdx < len(rooms)

	chats := make([]APIChat, len(page))
	for i, room := range page {
		chats[i] = RoomToAPIChat(room, s.baseURL())
	}

	var oldestCur, newestCur string
	if hasMore {
		oldestCur = strconv.Itoa(endIdx)
	}
	if startIdx > 0 {
		newestCur = strconv.Itoa(startIdx)
	}

	writeJSON(w, http.StatusOK, APIChatsResponse{
		Items:        chats,
		HasMore:      hasMore,
		OldestCursor: oldestCur,
		NewestCursor: newestCur,
	})
}

func (s *Server) handleGetChat(w http.ResponseWriter, r *http.Request) {
	chatID := r.PathValue("chatID")
	room := s.store.GetRoom(chatID)
	if room == nil {
		writeError(w, http.StatusNotFound, "not_found", "Chat not found")
		return
	}
	writeJSON(w, http.StatusOK, RoomToAPIChat(room, s.baseURL()))
}

func (s *Server) handleCreateChat(w http.ResponseWriter, r *http.Request) {
	var req APICreateChatRequest
	if err := readJSON(r, &req); err != nil {
		writeError(w, http.StatusBadRequest, "INVALID_REQUEST", err.Error())
		return
	}

	isDirect := req.Type == "single" || req.Type == ""
	roomID, err := s.mc.CreateRoom(r.Context(), req.Title, isDirect, req.ParticipantIDs)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "UNKNOWN_ERROR", err.Error())
		return
	}

	if req.MessageText != "" {
		s.mc.SendMessage(r.Context(), roomID, req.MessageText, "")
	}

	writeJSON(w, http.StatusOK, APICreateChatResponse{
		ChatID: roomID,
		Status: "created",
	})
}

func (s *Server) handleSearchChats(w http.ResponseWriter, r *http.Request) {
	query := r.URL.Query().Get("q")
	if query == "" {
		query = r.URL.Query().Get("query")
	}

	rooms := s.store.SearchRooms(query)

	chats := make([]APIChat, len(rooms))
	for i, room := range rooms {
		chats[i] = RoomToAPIChat(room, s.baseURL())
	}

	writeJSON(w, http.StatusOK, APIChatsResponse{
		Items:   chats,
		HasMore: false,
	})
}

func (s *Server) handleArchiveChat(w http.ResponseWriter, r *http.Request) {
	chatID := r.PathValue("chatID")
	var req APIArchiveRequest
	readJSON(r, &req)

	archived := true
	if r.ContentLength > 0 {
		archived = req.Archived
	}

	var err error
	if archived {
		err = s.mc.SetRoomTag(r.Context(), chatID, "m.lowpriority")
	} else {
		err = s.mc.RemoveRoomTag(r.Context(), chatID, "m.lowpriority")
	}

	if err != nil {
		writeError(w, http.StatusInternalServerError, "UNKNOWN_ERROR", err.Error())
		return
	}

	room := s.store.GetRoom(chatID)
	if room != nil {
		room.IsArchived = archived
		s.store.SetRoom(room)
	}

	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handlePinChat(w http.ResponseWriter, r *http.Request) {
	chatID := r.PathValue("chatID")
	if err := s.mc.SetRoomTag(r.Context(), chatID, "m.favourite"); err != nil {
		writeError(w, http.StatusInternalServerError, "UNKNOWN_ERROR", err.Error())
		return
	}

	room := s.store.GetRoom(chatID)
	if room != nil {
		room.IsPinned = true
		s.store.SetRoom(room)
	}

	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleUnpinChat(w http.ResponseWriter, r *http.Request) {
	chatID := r.PathValue("chatID")
	if err := s.mc.RemoveRoomTag(r.Context(), chatID, "m.favourite"); err != nil {
		writeError(w, http.StatusInternalServerError, "UNKNOWN_ERROR", err.Error())
		return
	}

	room := s.store.GetRoom(chatID)
	if room != nil {
		room.IsPinned = false
		s.store.SetRoom(room)
	}

	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleMuteChat(w http.ResponseWriter, r *http.Request) {
	chatID := r.PathValue("chatID")
	room := s.store.GetRoom(chatID)
	if room != nil {
		room.IsMuted = true
		s.store.SetRoom(room)
	}
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleUnmuteChat(w http.ResponseWriter, r *http.Request) {
	chatID := r.PathValue("chatID")
	room := s.store.GetRoom(chatID)
	if room != nil {
		room.IsMuted = false
		s.store.SetRoom(room)
	}
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleMarkRead(w http.ResponseWriter, r *http.Request) {
	chatID := r.PathValue("chatID")
	var req struct {
		EventID string `json:"eventId"`
	}
	readJSON(r, &req)
	var err error
	if req.EventID != "" {
		err = s.mc.MarkRead(r.Context(), chatID, req.EventID)
	} else {
		err = s.mc.MarkRead(r.Context(), chatID)
	}
	if err != nil {
		writeError(w, http.StatusInternalServerError, "UNKNOWN_ERROR", err.Error())
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleGetMessages(w http.ResponseWriter, r *http.Request) {
	chatID := r.PathValue("chatID")
	cursor := r.URL.Query().Get("cursor")
	dirStr := r.URL.Query().Get("direction")

	direction := 'b'
	if dirStr == "after" {
		direction = 'f'
	}

	limit := 50
	if ls := r.URL.Query().Get("limit"); ls != "" {
		if lv, err := strconv.Atoi(ls); err == nil && lv > 0 && lv <= 200 {
			limit = lv
		}
	}
	messages, hasMore, endToken, err := s.mc.GetMessages(r.Context(), chatID, limit, cursor, direction)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "UNKNOWN_ERROR", err.Error())
		return
	}

	apiMessages := make([]APIMessage, len(messages))
	for i, msg := range messages {
		apiMessages[i] = MessageToAPIMessage(msg, s.baseURL())
	}

	writeJSON(w, http.StatusOK, APIMessagesResponse{
		Items:        apiMessages,
		HasMore:      hasMore,
		OldestCursor: endToken,
	})
}

func (s *Server) handleSendMessage(w http.ResponseWriter, r *http.Request) {
	chatID := r.PathValue("chatID")

	var req APISendMessageRequest
	if err := readJSON(r, &req); err != nil {
		writeError(w, http.StatusBadRequest, "INVALID_REQUEST", err.Error())
		return
	}

	var eventID string
	var err error

	if req.Attachment != nil && req.Attachment.UploadID != "" {
		mxcURI, ok := s.mc.GetUploadMXC(req.Attachment.UploadID)
		if !ok {
			writeError(w, http.StatusBadRequest, "INVALID_UPLOAD", "Upload ID not found")
			return
		}

		msgType := determineMsgType(req.Attachment.MimeType)

		text := req.Text
		if text == "" {
			text = req.Attachment.FileName
		}
		_ = mxcURI
		_ = msgType

		eventID, err = s.mc.SendMessage(r.Context(), chatID, text, req.ReplyToMessageID)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "UNKNOWN_ERROR", err.Error())
			return
		}
	} else {
		eventID, err = s.mc.SendMessage(r.Context(), chatID, req.Text, req.ReplyToMessageID)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "UNKNOWN_ERROR", err.Error())
			return
		}
	}

	writeJSON(w, http.StatusOK, APISendMessageResponse{
		ChatID:           chatID,
		PendingMessageID: eventID,
	})
}

func (s *Server) handleEditMessage(w http.ResponseWriter, r *http.Request) {
	chatID := r.PathValue("chatID")
	messageID := r.PathValue("messageID")

	var req APIEditMessageRequest
	if err := readJSON(r, &req); err != nil || req.Text == "" {
		writeError(w, http.StatusBadRequest, "INVALID_REQUEST", "text is required")
		return
	}

	if err := s.mc.EditMessage(r.Context(), chatID, messageID, req.Text); err != nil {
		writeError(w, http.StatusInternalServerError, "UNKNOWN_ERROR", err.Error())
		return
	}

	writeJSON(w, http.StatusOK, APIEditMessageResponse{
		ChatID:    chatID,
		MessageID: messageID,
		Success:   true,
	})
}

func (s *Server) handleDeleteMessage(w http.ResponseWriter, r *http.Request) {
	chatID := r.PathValue("chatID")
	messageID := r.PathValue("messageID")

	if err := s.mc.RedactMessage(r.Context(), chatID, messageID); err != nil {
		writeError(w, http.StatusInternalServerError, "UNKNOWN_ERROR", err.Error())
		return
	}

	writeJSON(w, http.StatusOK, map[string]interface{}{
		"success":   true,
		"chatID":    chatID,
		"messageID": messageID,
	})
}

func (s *Server) handleAddReaction(w http.ResponseWriter, r *http.Request) {
	chatID := r.PathValue("chatID")
	messageID := r.PathValue("messageID")

	var req APIAddReactionRequest
	if err := readJSON(r, &req); err != nil || req.ReactionKey == "" {
		writeError(w, http.StatusBadRequest, "INVALID_REQUEST", "reactionKey is required")
		return
	}

	if err := s.mc.SendReaction(r.Context(), chatID, messageID, req.ReactionKey); err != nil {
		writeError(w, http.StatusInternalServerError, "UNKNOWN_ERROR", err.Error())
		return
	}

	writeJSON(w, http.StatusOK, APIAddReactionResponse{
		Success:       true,
		ChatID:        chatID,
		MessageID:     messageID,
		ReactionKey:   req.ReactionKey,
		TransactionID: req.TransactionID,
	})
}

func (s *Server) handleRemoveReaction(w http.ResponseWriter, r *http.Request) {
	chatID := r.PathValue("chatID")
	messageID := r.PathValue("messageID")
	reactionKey := r.URL.Query().Get("reactionKey")

	if err := s.mc.RemoveReaction(r.Context(), chatID, messageID, reactionKey); err != nil {
		writeError(w, http.StatusInternalServerError, "UNKNOWN_ERROR", err.Error())
		return
	}

	writeJSON(w, http.StatusOK, APIRemoveReactionResponse{
		Success:     true,
		ChatID:      chatID,
		MessageID:   messageID,
		ReactionKey: reactionKey,
	})
}

func (s *Server) handleUnifiedSearch(w http.ResponseWriter, r *http.Request) {
	query := r.URL.Query().Get("q")
	if query == "" {
		query = r.URL.Query().Get("query")
	}

	rooms := s.store.SearchRooms(query)
	chats := make([]APIChat, 0)
	for _, room := range rooms {
		chats = append(chats, RoomToAPIChat(room, s.baseURL()))
	}

	writeJSON(w, http.StatusOK, map[string]interface{}{
		"results": map[string]interface{}{
			"chats":     chats,
			"in_groups": []interface{}{},
			"messages": map[string]interface{}{
				"items":        []interface{}{},
				"chats":        map[string]interface{}{},
				"hasMore":      false,
				"oldestCursor": nil,
				"newestCursor": nil,
			},
		},
	})
}

func (s *Server) handleSearchMessages(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]interface{}{
		"items":        []interface{}{},
		"chats":        map[string]interface{}{},
		"hasMore":      false,
		"oldestCursor": nil,
		"newestCursor": nil,
	})
}

func (s *Server) handleUploadAsset(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseMultipartForm(500 << 20); err != nil {
		writeError(w, http.StatusBadRequest, "INVALID_REQUEST", "Failed to parse upload")
		return
	}

	file, header, err := r.FormFile("file")
	if err != nil {
		writeError(w, http.StatusBadRequest, "INVALID_REQUEST", "No file in upload")
		return
	}
	defer file.Close()

	data, err := io.ReadAll(file)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "UNKNOWN_ERROR", "Failed to read file")
		return
	}

	fileName := header.Filename
	if fn := r.FormValue("fileName"); fn != "" {
		fileName = fn
	}
	mimeType := header.Header.Get("Content-Type")
	if mt := r.FormValue("mimeType"); mt != "" {
		mimeType = mt
	}
	if mimeType == "" {
		mimeType = "application/octet-stream"
	}

	uploadID, err := s.mc.UploadMedia(r.Context(), data, fileName, mimeType)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "UNKNOWN_ERROR", err.Error())
		return
	}

	writeJSON(w, http.StatusOK, APIUploadResponse{
		UploadID: uploadID,
		FileName: fileName,
		MimeType: mimeType,
		FileSize: int64(len(data)),
	})
}

func (s *Server) handleUploadBase64(w http.ResponseWriter, r *http.Request) {
	var req struct {
		Content  string `json:"content"`
		FileName string `json:"fileName"`
		MimeType string `json:"mimeType"`
	}
	if err := readJSON(r, &req); err != nil {
		writeError(w, http.StatusBadRequest, "INVALID_REQUEST", err.Error())
		return
	}

	if req.Content == "" {
		writeError(w, http.StatusBadRequest, "INVALID_REQUEST", "No content provided")
		return
	}

	data, err := base64.StdEncoding.DecodeString(req.Content)
	if err != nil {
		// Try with padding stripped (common in URLs)
		data, err = base64.RawStdEncoding.DecodeString(req.Content)
		if err != nil {
			writeError(w, http.StatusBadRequest, "INVALID_REQUEST", "Invalid base64: "+err.Error())
			return
		}
	}

	fileName := req.FileName
	if fileName == "" {
		fileName = "upload"
	}
	mimeType := req.MimeType
	if mimeType == "" {
		mimeType = "application/octet-stream"
	}

	uploadID, err := s.mc.UploadMedia(r.Context(), data, fileName, mimeType)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "UPLOAD_FAILED", err.Error())
		return
	}

	writeJSON(w, http.StatusOK, APIUploadResponse{
		UploadID: uploadID,
	})
}

func (s *Server) handleDownloadAsset(w http.ResponseWriter, r *http.Request) {
	var req APIDownloadRequest
	if err := readJSON(r, &req); err != nil || req.URL == "" {
		writeError(w, http.StatusBadRequest, "INVALID_REQUEST", "url is required")
		return
	}

	writeJSON(w, http.StatusOK, APIDownloadResponse{
		Error: "Download to disk not yet implemented. Use /v1/assets/serve instead",
	})
}

func (s *Server) handleServeAsset(w http.ResponseWriter, r *http.Request) {
	uri := r.URL.Query().Get("uri")
	if uri == "" {
		uri = r.URL.Query().Get("url")
	}
	if uri == "" {
		writeError(w, http.StatusBadRequest, "INVALID_REQUEST", "uri parameter required")
		return
	}

	encFileJSON := s.store.GetEncryptedFileJSON(uri)

	data, contentType, err := s.mc.DownloadMedia(r.Context(), uri)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "UNKNOWN_ERROR", err.Error())
		return
	}

	if encFileJSON != "" {
		var encFile event.EncryptedFileInfo
		if jsonErr := json.Unmarshal([]byte(encFileJSON), &encFile); jsonErr == nil {
			if decErr := encFile.DecryptInPlace(data); decErr != nil {
				log.Warn().Err(decErr).Str("uri", uri).Msg("Failed to decrypt media")
			} else {
				log.Debug().Str("uri", uri[:min(len(uri), 60)]).Int("bytes", len(data)).Msg("Decrypted media successfully")
			}
		}
	} else {
		log.Debug().Str("uri", uri[:min(len(uri), 60)]).Int("bytes", len(data)).Msg("No encryption info found, serving raw")
	}

	if contentType != "" {
		w.Header().Set("Content-Type", contentType)
	}
	w.Header().Set("Content-Length", strconv.Itoa(len(data)))
	w.Header().Set("Cache-Control", "public, max-age=86400")
	w.WriteHeader(http.StatusOK)
	w.Write(data)
}

func (s *Server) handleSearchContacts(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]interface{}{
		"items": []interface{}{},
	})
}

func (s *Server) handleListContacts(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]interface{}{
		"items":        []interface{}{},
		"hasMore":      false,
		"oldestCursor": nil,
		"newestCursor": nil,
	})
}

func (s *Server) handleWebSocket(w http.ResponseWriter, r *http.Request) {
	token := extractBearerToken(r)
	if token == "" {
		token = r.URL.Query().Get("token")
	}
	expectedToken := s.cfg.GetLocalAPIToken()
	if token != expectedToken {
		writeError(w, http.StatusUnauthorized, "UNAUTHORIZED", "Invalid token")
		return
	}

	s.wsHub.HandleWebSocket(w, r)
}

func (s *Server) handleFocus(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]bool{"success": true})
}

func determineMsgType(mimeType string) string {
	if strings.HasPrefix(mimeType, "image/") {
		return "IMAGE"
	}
	if strings.HasPrefix(mimeType, "video/") {
		return "VIDEO"
	}
	if strings.HasPrefix(mimeType, "audio/") {
		return "AUDIO"
	}
	return "FILE"
}
