package main

import (
	"database/sql"
	"encoding/json"
	"path/filepath"
	"sync"
	"time"

	"github.com/rs/zerolog/log"
	_ "github.com/mattn/go-sqlite3"
)

type SQLiteStore struct {
	db      *sql.DB
	writeCh chan func()
	once    sync.Once
}

func NewSQLiteStore(dataDir string) (*SQLiteStore, error) {
	dbPath := filepath.Join(dataDir, "store.db")
	db, err := sql.Open("sqlite3", dbPath+"?_journal_mode=WAL&_busy_timeout=5000")
	if err != nil {
		return nil, err
	}
	db.SetMaxOpenConns(1)

	s := &SQLiteStore{
		db:      db,
		writeCh: make(chan func(), 512),
	}

	if err := s.createTables(); err != nil {
		db.Close()
		return nil, err
	}

	go s.writeLoop()
	return s, nil
}

func (s *SQLiteStore) createTables() error {
	_, err := s.db.Exec(`
		CREATE TABLE IF NOT EXISTS rooms (
			id TEXT PRIMARY KEY,
			title TEXT,
			avatar_url TEXT,
			type TEXT,
			account_id TEXT,
			network TEXT,
			last_activity INTEGER,
			unread_count INTEGER,
			is_pinned INTEGER,
			is_muted INTEGER,
			is_archived INTEGER,
			is_low_priority INTEGER,
			encrypted INTEGER,
			self_user_id TEXT,
			canonical_alias TEXT,
			topic TEXT,
			direct_user_id TEXT,
			timeline_end TEXT,
			space_id TEXT,
			priority TEXT,
			local_chat_id TEXT,
			join_rule TEXT
		);
		CREATE TABLE IF NOT EXISTS members (
			room_id TEXT,
			user_id TEXT,
			display_name TEXT,
			avatar_url TEXT,
			membership TEXT,
			PRIMARY KEY(room_id, user_id)
		);
		CREATE TABLE IF NOT EXISTS messages (
			id TEXT PRIMARY KEY,
			room_id TEXT,
			account_id TEXT,
			sender_id TEXT,
			sender_name TEXT,
			timestamp INTEGER,
			sort_key TEXT,
			type TEXT,
			text TEXT,
			is_sender INTEGER,
			event_id TEXT,
			linked_message_id TEXT,
			redacted INTEGER,
			attachments_json TEXT,
			reactions_json TEXT
		);
		CREATE TABLE IF NOT EXISTS accounts (
			account_id TEXT PRIMARY KEY,
			network TEXT,
			user_json TEXT
		);
		CREATE TABLE IF NOT EXISTS encrypted_files (
			mxc_uri TEXT PRIMARY KEY,
			file_json TEXT
		);
		CREATE INDEX IF NOT EXISTS idx_messages_room ON messages(room_id);
		CREATE INDEX IF NOT EXISTS idx_members_room ON members(room_id);
	`)
	return err
}

func (s *SQLiteStore) writeLoop() {
	for fn := range s.writeCh {
		fn()
	}
}

func (s *SQLiteStore) enqueue(fn func()) {
	select {
	case s.writeCh <- fn:
	default:
		go func() { s.writeCh <- fn }()
	}
}

func (s *SQLiteStore) Close() {
	s.once.Do(func() {
		close(s.writeCh)
		s.db.Close()
	})
}

func boolToInt(b bool) int {
	if b {
		return 1
	}
	return 0
}

func intToBool(i int) bool {
	return i != 0
}

func (s *SQLiteStore) SaveRoom(room *Room) {
	s.enqueue(func() {
		room.mu.RLock()
		defer room.mu.RUnlock()
		_, err := s.db.Exec(`INSERT OR REPLACE INTO rooms
			(id, title, avatar_url, type, account_id, network, last_activity,
			 unread_count, is_pinned, is_muted, is_archived, is_low_priority,
			 encrypted, self_user_id, canonical_alias, topic, direct_user_id,
			 timeline_end, space_id, priority, local_chat_id, join_rule)
			VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`,
			room.ID, room.Title, room.AvatarURL, room.Type, room.AccountID, room.Network,
			room.LastActivity.UnixMilli(), room.UnreadCount,
			boolToInt(room.IsPinned), boolToInt(room.IsMuted),
			boolToInt(room.IsArchived), boolToInt(room.IsLowPriority),
			boolToInt(room.Encrypted), room.SelfUserID, room.CanonicalAlias,
			room.Topic, room.DirectUserID, room.TimelineEnd, room.SpaceID,
			room.Priority, room.LocalChatID, room.JoinRule,
		)
		if err != nil {
			log.Warn().Err(err).Str("room", room.ID).Msg("Failed to save room to DB")
		}
	})
}

func (s *SQLiteStore) DeleteRoom(roomID string) {
	s.enqueue(func() {
		s.db.Exec("DELETE FROM rooms WHERE id = ?", roomID)
		s.db.Exec("DELETE FROM members WHERE room_id = ?", roomID)
	})
}

func (s *SQLiteStore) SaveMember(roomID string, member *Member) {
	s.enqueue(func() {
		_, err := s.db.Exec(`INSERT OR REPLACE INTO members
			(room_id, user_id, display_name, avatar_url, membership)
			VALUES (?,?,?,?,?)`,
			roomID, member.UserID, member.DisplayName, member.AvatarURL, member.Membership,
		)
		if err != nil {
			log.Warn().Err(err).Str("room", roomID).Str("user", member.UserID).Msg("Failed to save member to DB")
		}
	})
}

func (s *SQLiteStore) SaveMessage(msg *Message) {
	s.enqueue(func() {
		var attachJSON, reactJSON string
		if len(msg.Attachments) > 0 {
			if b, err := json.Marshal(msg.Attachments); err == nil {
				attachJSON = string(b)
			}
		}
		if len(msg.Reactions) > 0 {
			if b, err := json.Marshal(msg.Reactions); err == nil {
				reactJSON = string(b)
			}
		}
		_, err := s.db.Exec(`INSERT OR REPLACE INTO messages
			(id, room_id, account_id, sender_id, sender_name, timestamp,
			 sort_key, type, text, is_sender, event_id, linked_message_id,
			 redacted, attachments_json, reactions_json)
			VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`,
			msg.ID, msg.ChatID, msg.AccountID, msg.SenderID, msg.SenderName,
			msg.Timestamp.UnixMilli(), msg.SortKey, msg.Type, msg.Text,
			boolToInt(msg.IsSender), msg.EventID, msg.LinkedMessageID,
			boolToInt(msg.Redacted), attachJSON, reactJSON,
		)
		if err != nil {
			log.Warn().Err(err).Str("msg", msg.ID).Msg("Failed to save message to DB")
		}
	})
}

func (s *SQLiteStore) SaveAccount(acct *Account) {
	s.enqueue(func() {
		var userJSON string
		if acct.User != nil {
			if b, err := json.Marshal(acct.User); err == nil {
				userJSON = string(b)
			}
		}
		_, err := s.db.Exec(`INSERT OR REPLACE INTO accounts (account_id, network, user_json) VALUES (?,?,?)`,
			acct.AccountID, acct.Network, userJSON,
		)
		if err != nil {
			log.Warn().Err(err).Str("account", acct.AccountID).Msg("Failed to save account to DB")
		}
	})
}

func (s *SQLiteStore) LoadAll(store *Store) error {
	start := time.Now()

	roomCount, err := s.loadRooms(store)
	if err != nil {
		return err
	}
	memberCount, err := s.loadMembers(store)
	if err != nil {
		return err
	}
	acctCount, err := s.loadAccounts(store)
	if err != nil {
		return err
	}

	s.reconcileTimestamps(store)

	log.Info().
		Int("rooms", roomCount).
		Int("members", memberCount).
		Int("accounts", acctCount).
		Dur("elapsed", time.Since(start)).
		Msg("Loaded store from SQLite")
	return nil
}

func (s *SQLiteStore) reconcileTimestamps(store *Store) {
	rows, err := s.db.Query("SELECT room_id, MAX(timestamp) FROM messages GROUP BY room_id")
	if err != nil {
		return
	}
	defer rows.Close()

	fixed := 0
	for rows.Next() {
		var roomID string
		var maxTS int64
		if rows.Scan(&roomID, &maxTS) != nil {
			continue
		}
		room := store.GetRoom(roomID)
		if room == nil {
			continue
		}
		msgTime := time.UnixMilli(maxTS)
		if msgTime.After(room.LastActivity) {
			room.LastActivity = msgTime
			store.SetRoom(room)
			fixed++
		}
	}
	if fixed > 0 {
		log.Info().Int("fixed", fixed).Msg("Reconciled room timestamps from messages")
	}
}

func (s *SQLiteStore) loadRooms(store *Store) (int, error) {
	rows, err := s.db.Query(`SELECT id, title, avatar_url, type, account_id, network,
		last_activity, unread_count, is_pinned, is_muted, is_archived, is_low_priority,
		encrypted, self_user_id, canonical_alias, topic, direct_user_id,
		timeline_end, space_id, priority, local_chat_id, join_rule FROM rooms`)
	if err != nil {
		return 0, err
	}
	defer rows.Close()

	count := 0
	for rows.Next() {
		var r Room
		var lastAct int64
		var isPinned, isMuted, isArchived, isLowPri, encrypted int
		var title, avatarURL, typ, accountID, network, selfUserID sql.NullString
		var canonAlias, topic, directUserID, timelineEnd, spaceID, priority, localChatID, joinRule sql.NullString

		err := rows.Scan(&r.ID, &title, &avatarURL, &typ, &accountID, &network,
			&lastAct, &r.UnreadCount, &isPinned, &isMuted, &isArchived, &isLowPri,
			&encrypted, &selfUserID, &canonAlias, &topic, &directUserID,
			&timelineEnd, &spaceID, &priority, &localChatID, &joinRule)
		if err != nil {
			continue
		}

		r.Title = title.String
		r.AvatarURL = avatarURL.String
		r.Type = typ.String
		r.AccountID = accountID.String
		r.Network = network.String
		r.LastActivity = time.UnixMilli(lastAct)
		r.IsPinned = intToBool(isPinned)
		r.IsMuted = intToBool(isMuted)
		r.IsArchived = intToBool(isArchived)
		r.IsLowPriority = intToBool(isLowPri)
		r.Encrypted = intToBool(encrypted)
		r.SelfUserID = selfUserID.String
		r.CanonicalAlias = canonAlias.String
		r.Topic = topic.String
		r.DirectUserID = directUserID.String
		r.TimelineEnd = timelineEnd.String
		r.SpaceID = spaceID.String
		r.Priority = priority.String
		r.LocalChatID = localChatID.String
		r.JoinRule = joinRule.String
		r.Members = make(map[string]*Member)

		store.mu.Lock()
		store.rooms[r.ID] = &r
		store.mu.Unlock()
		count++
	}
	return count, nil
}

func (s *SQLiteStore) loadMembers(store *Store) (int, error) {
	rows, err := s.db.Query("SELECT room_id, user_id, display_name, avatar_url, membership FROM members")
	if err != nil {
		return 0, err
	}
	defer rows.Close()

	count := 0
	for rows.Next() {
		var roomID string
		var m Member
		var displayName, avatarURL, membership sql.NullString
		err := rows.Scan(&roomID, &m.UserID, &displayName, &avatarURL, &membership)
		if err != nil {
			continue
		}
		m.DisplayName = displayName.String
		m.AvatarURL = avatarURL.String
		m.Membership = membership.String

		store.mu.RLock()
		room, ok := store.rooms[roomID]
		store.mu.RUnlock()
		if ok {
			room.mu.Lock()
			if room.Members == nil {
				room.Members = make(map[string]*Member)
			}
			room.Members[m.UserID] = &m
			room.MemberCount = countJoinedMembers(room.Members)
			room.mu.Unlock()
		}
		count++
	}
	return count, nil
}

func (s *SQLiteStore) loadAccounts(store *Store) (int, error) {
	rows, err := s.db.Query("SELECT account_id, network, user_json FROM accounts")
	if err != nil {
		return 0, err
	}
	defer rows.Close()

	count := 0
	for rows.Next() {
		var acct Account
		var userJSON sql.NullString
		err := rows.Scan(&acct.AccountID, &acct.Network, &userJSON)
		if err != nil {
			continue
		}
		if userJSON.String != "" {
			var u AccountUser
			if json.Unmarshal([]byte(userJSON.String), &u) == nil {
				acct.User = &u
			}
		}
		store.mu.Lock()
		store.accounts[acct.AccountID] = &acct
		store.mu.Unlock()
		count++
	}
	return count, nil
}

func (s *SQLiteStore) GetMessages(roomID string, limit int) []*Message {
	if limit <= 0 {
		limit = 50
	}
	rows, err := s.db.Query(
		"SELECT id, room_id, account_id, sender_id, sender_name, timestamp, sort_key, type, text, is_sender, event_id, linked_message_id, redacted, attachments_json, reactions_json FROM messages WHERE room_id = ? ORDER BY timestamp DESC LIMIT ?",
		roomID, limit)
	if err != nil {
		return nil
	}
	defer rows.Close()

	var msgs []*Message
	for rows.Next() {
		var m Message
		var ts int64
		var isSender, redacted int
		var attachJSON, reactJSON sql.NullString
		var senderName, sortKey, linkedMsgID, eventID sql.NullString
		err := rows.Scan(&m.ID, &m.ChatID, &m.AccountID, &m.SenderID, &senderName,
			&ts, &sortKey, &m.Type, &m.Text, &isSender, &eventID, &linkedMsgID,
			&redacted, &attachJSON, &reactJSON)
		if err != nil {
			continue
		}
		m.SenderName = senderName.String
		m.SortKey = sortKey.String
		m.LinkedMessageID = linkedMsgID.String
		m.EventID = eventID.String
		m.Timestamp = time.UnixMilli(ts)
		m.IsSender = intToBool(isSender)
		m.Redacted = intToBool(redacted)

		if attachJSON.String != "" {
			json.Unmarshal([]byte(attachJSON.String), &m.Attachments)
		}
		if reactJSON.String != "" {
			json.Unmarshal([]byte(reactJSON.String), &m.Reactions)
		}
		msgs = append(msgs, &m)
	}

	for i, j := 0, len(msgs)-1; i < j; i, j = i+1, j-1 {
		msgs[i], msgs[j] = msgs[j], msgs[i]
	}
	return msgs
}

func (s *SQLiteStore) GetMessagesBefore(roomID string, beforeTimestamp int64, limit int) []*Message {
	if limit <= 0 {
		limit = 25
	}
	rows, err := s.db.Query(
		"SELECT id, room_id, account_id, sender_id, sender_name, timestamp, sort_key, type, text, is_sender, event_id, linked_message_id, redacted, attachments_json, reactions_json FROM messages WHERE room_id = ? AND timestamp < ? ORDER BY timestamp DESC LIMIT ?",
		roomID, beforeTimestamp, limit)
	if err != nil {
		return nil
	}
	defer rows.Close()

	var msgs []*Message
	for rows.Next() {
		var m Message
		var ts int64
		var isSender, redacted int
		var senderName, sortKey, linkedMsgID, eventID, attachJSON, reactJSON sql.NullString
		err := rows.Scan(&m.ID, &m.ChatID, &m.AccountID, &m.SenderID, &senderName, &ts, &sortKey, &m.Type, &m.Text, &isSender, &eventID, &linkedMsgID, &redacted, &attachJSON, &reactJSON)
		if err != nil {
			continue
		}
		m.SenderName = senderName.String
		m.SortKey = sortKey.String
		m.LinkedMessageID = linkedMsgID.String
		m.EventID = eventID.String
		m.Timestamp = time.UnixMilli(ts)
		m.IsSender = intToBool(isSender)
		m.Redacted = intToBool(redacted)
		if attachJSON.String != "" {
			json.Unmarshal([]byte(attachJSON.String), &m.Attachments)
		}
		if reactJSON.String != "" {
			json.Unmarshal([]byte(reactJSON.String), &m.Reactions)
		}
		msgs = append(msgs, &m)
	}
	for i, j := 0, len(msgs)-1; i < j; i, j = i+1, j-1 {
		msgs[i], msgs[j] = msgs[j], msgs[i]
	}
	return msgs
}

func (s *SQLiteStore) GetEncryptedFileJSON(mxcURI string) string {
	if s.db == nil {
		return ""
	}

	// Fast lookup from dedicated table
	var fileJSON sql.NullString
	err := s.db.QueryRow("SELECT file_json FROM encrypted_files WHERE mxc_uri = ?", mxcURI).Scan(&fileJSON)
	if err == nil && fileJSON.Valid && fileJSON.String != "" {
		return fileJSON.String
	}

	// Fallback: search through message attachments
	rows, err := s.db.Query("SELECT attachments_json FROM messages WHERE attachments_json LIKE ? LIMIT 50", "%"+mxcURI+"%")
	if err != nil {
		return ""
	}
	defer rows.Close()

	for rows.Next() {
		var attachJSON sql.NullString
		if err := rows.Scan(&attachJSON); err != nil || !attachJSON.Valid {
			continue
		}
		var attachments []Attachment
		if err := json.Unmarshal([]byte(attachJSON.String), &attachments); err != nil {
			continue
		}
		for _, att := range attachments {
			if att.SrcURL == mxcURI && att.EncryptedFileJSON != "" {
				// Cache it for next time
				s.SaveEncryptedFile(mxcURI, att.EncryptedFileJSON)
				return att.EncryptedFileJSON
			}
		}
	}
	return ""
}

func (s *SQLiteStore) SaveEncryptedFile(mxcURI, fileJSON string) {
	if s.db == nil || mxcURI == "" || fileJSON == "" {
		return
	}
	s.writeCh <- func() {
		s.db.Exec("INSERT OR REPLACE INTO encrypted_files (mxc_uri, file_json) VALUES (?, ?)", mxcURI, fileJSON)
	}
}
