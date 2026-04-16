package main

import (
	"sort"
	"strings"
	"sync"
	"time"
)

type Store struct {
	mu       sync.RWMutex
	rooms    map[string]*Room
	accounts map[string]*Account
	db       *SQLiteStore
}

type Room struct {
	mu           sync.RWMutex
	ID           string
	LocalChatID  string
	AccountID    string
	Network      string
	Title        string
	AvatarURL    string
	Type         string
	LastActivity time.Time
	UnreadCount  int
	IsPinned     bool
	IsMuted      bool
	IsArchived   bool
	IsLowPriority bool
	Priority     string
	Tags         []string
	SpaceID      string
	Members      map[string]*Member
	MemberCount  int
	Preview      *Message
	LastReadKey  string
	Encrypted    bool
	Timeline     []*Message
	TimelineEnd  string

	SelfUserID     string
	CanonicalAlias string
	Topic          string
	JoinRule       string
	DirectUserID   string
	ReadReceipts   map[string]*ReadReceipt
}

func (r *Room) GetMembersSnapshot() []*Member {
	r.mu.RLock()
	defer r.mu.RUnlock()
	members := make([]*Member, 0, len(r.Members))
	for _, m := range r.Members {
		members = append(members, m)
	}
	return members
}

func (r *Room) SetMember(userID string, m *Member) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.Members == nil {
		r.Members = make(map[string]*Member)
	}
	r.Members[userID] = m
}

type Member struct {
	UserID      string
	DisplayName string
	AvatarURL   string
	Membership  string
}

type Message struct {
	ID              string
	ChatID          string
	AccountID       string
	SenderID        string
	SenderName      string
	Timestamp       time.Time
	SortKey         string
	Type            string
	Text            string
	IsSender        bool
	IsUnread        bool
	LinkedMessageID string
	Attachments     []*Attachment
	Reactions       []*Reaction
	EventID         string
	Redacted        bool
	IsEdited        bool
	EditedAt        time.Time
	Mentions        []MentionInfo
	LinkPreview     *LinkPreview
}

type LinkPreview struct {
	URL         string `json:"url"`
	Title       string `json:"title,omitempty"`
	Description string `json:"description,omitempty"`
	ImageMXC    string `json:"imageMxc,omitempty"`
	ImageWidth  int    `json:"imageWidth,omitempty"`
	ImageHeight int    `json:"imageHeight,omitempty"`
}

type ReadReceipt struct {
	UserID    string    `json:"userId"`
	EventID   string    `json:"eventId"`
	Timestamp time.Time `json:"timestamp"`
}

type MentionInfo struct {
	UserID      string `json:"userId"`
	DisplayName string `json:"displayName"`
}

type Attachment struct {
	ID         string
	Type       string
	SrcURL     string
	MimeType   string
	FileName   string
	FileSize   int64
	IsGif      bool
	IsSticker  bool
	IsVoiceNote bool
	Duration   float64
	Width      int
	Height     int
	EncryptedFileJSON string
}

type Reaction struct {
	ID            string
	ReactionKey   string
	ParticipantID string
	IsEmoji       bool
	ImgURL        string
}

type Account struct {
	AccountID string
	Network   string
	User      *AccountUser
}

type AccountUser struct {
	ID          string
	Username    string
	FullName    string
	Email       string
	PhoneNumber string
	ImgURL      string
	DisplayText string
	IsSelf      bool
}

func NewStore() *Store {
	return &Store{
		rooms:    make(map[string]*Room),
		accounts: make(map[string]*Account),
	}
}

func (s *Store) GetRoom(roomID string) *Room {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.rooms[roomID]
}

func (s *Store) SetRoom(room *Room) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.rooms[room.ID] = room
	if s.db != nil {
		s.db.SaveRoom(room)
	}
}

func (s *Store) DeleteRoom(roomID string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.rooms, roomID)
	if s.db != nil {
		s.db.DeleteRoom(roomID)
	}
}

func (s *Store) GetRoomsFiltered(accountID string, isPinned, isMuted, isArchived *bool, isLowPriority *bool) []*Room {
	s.mu.RLock()
	defer s.mu.RUnlock()

	rooms := make([]*Room, 0)
	for _, r := range s.rooms {
		if accountID != "" && r.AccountID != accountID {
			continue
		}
		if isPinned != nil && r.IsPinned != *isPinned {
			continue
		}
		if isMuted != nil && r.IsMuted != *isMuted {
			continue
		}
		if isArchived != nil && r.IsArchived != *isArchived {
			continue
		}
		if isLowPriority != nil && r.IsLowPriority != *isLowPriority {
			continue
		}
		rooms = append(rooms, r)
	}
	sortRooms(rooms)
	return rooms
}

func (s *Store) SearchRooms(query string) []*Room {
	s.mu.RLock()
	defer s.mu.RUnlock()

	query = strings.ToLower(query)
	rooms := make([]*Room, 0)
	for _, r := range s.rooms {
		if strings.Contains(strings.ToLower(r.Title), query) ||
			strings.Contains(strings.ToLower(r.Network), query) {
			rooms = append(rooms, r)
		}
	}
	sortRooms(rooms)
	return rooms
}

func sortRooms(rooms []*Room) {
	sort.Slice(rooms, func(i, j int) bool {
		if rooms[i].IsPinned != rooms[j].IsPinned {
			return rooms[i].IsPinned
		}
		iZero := rooms[i].LastActivity.IsZero()
		jZero := rooms[j].LastActivity.IsZero()
		if iZero != jZero {
			return !iZero
		}
		return rooms[i].LastActivity.After(rooms[j].LastActivity)
	})
}

func (s *Store) ResolveRootSpaces() int {
	s.mu.Lock()
	defer s.mu.Unlock()

	changed := 0
	for _, room := range s.rooms {
		if room.SpaceID == "" {
			continue
		}
		root := room.SpaceID
		visited := map[string]bool{room.ID: true}
		for {
			if visited[root] {
				break
			}
			visited[root] = true
			parent, ok := s.rooms[root]
			if !ok || parent.SpaceID == "" {
				break
			}
			root = parent.SpaceID
		}
		if root != room.SpaceID {
			room.SpaceID = root
			if s.db != nil {
				s.db.SaveRoom(room)
			}
			changed++
		}
	}
	return changed
}

func (s *Store) SetAccount(acct *Account) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.accounts[acct.AccountID] = acct
	if s.db != nil {
		s.db.SaveAccount(acct)
	}
}

func (s *Store) GetAccounts() []*Account {
	s.mu.RLock()
	defer s.mu.RUnlock()
	accts := make([]*Account, 0, len(s.accounts))
	for _, a := range s.accounts {
		accts = append(accts, a)
	}
	return accts
}

func (s *Store) EnsureRoom(roomID string) *Room {
	s.mu.Lock()
	defer s.mu.Unlock()
	if r, ok := s.rooms[roomID]; ok {
		return r
	}
	r := &Room{
		ID:      roomID,
		Members: make(map[string]*Member),
		Type:    "single",
	}
	s.rooms[roomID] = r
	return r
}

func (s *Store) RoomCount() int {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return len(s.rooms)
}

func (s *Store) InitDB(dataDir string) error {
	db, err := NewSQLiteStore(dataDir)
	if err != nil {
		return err
	}
	s.db = db
	return s.db.LoadAll(s)
}

func (s *Store) CloseDB() {
	if s.db != nil {
		s.db.Close()
	}
}

func (s *Store) SaveMessage(msg *Message) {
	if s.db != nil {
		s.db.SaveMessage(msg)
	}
}

func (s *Store) SaveMember(roomID string, member *Member) {
	if s.db != nil {
		s.db.SaveMember(roomID, member)
	}
}

func (s *Store) GetMessagesFromDB(roomID string, limit int) []*Message {
	if s.db != nil {
		return s.db.GetMessages(roomID, limit)
	}
	return nil
}

func (s *Store) GetMessagesBeforeFromDB(roomID string, beforeTimestamp int64, limit int) []*Message {
	if s.db != nil {
		return s.db.GetMessagesBefore(roomID, beforeTimestamp, limit)
	}
	return nil
}

func (s *Store) GetEncryptedFileJSON(mxcURI string) string {
	if s.db != nil {
		return s.db.GetEncryptedFileJSON(mxcURI)
	}
	return ""
}

func (s *Store) GetTimestampBySortKey(roomID, sortKey string) int64 {
	if s.db != nil {
		return s.db.GetTimestampBySortKey(roomID, sortKey)
	}
	return 0
}

func (s *Store) SaveEncryptedFile(mxcURI, fileJSON string) {
	if s.db != nil {
		s.db.SaveEncryptedFile(mxcURI, fileJSON)
	}
}
