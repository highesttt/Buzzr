package main

import (
	"fmt"
	"net/url"
	"strings"
	"time"
)


type APIInfo struct {
	App       APIAppInfo       `json:"app"`
	Platform  APIPlatformInfo  `json:"platform"`
	Server    APIServerInfo    `json:"server"`
	Endpoints APIEndpoints     `json:"endpoints"`
}

type APIAppInfo struct {
	Name     string `json:"name"`
	Version  string `json:"version"`
	BundleID string `json:"bundle_id"`
}

type APIPlatformInfo struct {
	OS      string `json:"os"`
	Arch    string `json:"arch"`
	Release string `json:"release,omitempty"`
}

type APIServerInfo struct {
	Status       string `json:"status"`
	BaseURL      string `json:"base_url"`
	Port         int    `json:"port"`
	Hostname     string `json:"hostname"`
	RemoteAccess bool   `json:"remote_access"`
	MCPEnabled   bool   `json:"mcp_enabled"`
}

type APIEndpoints struct {
	WsEvents string `json:"ws_events"`
}


type APIAccount struct {
	AccountID string  `json:"accountID"`
	Network   string  `json:"network,omitempty"`
	User      APIUser `json:"user"`
}


type APIUser struct {
	ID            string `json:"id"`
	Username      string `json:"username,omitempty"`
	FullName      string `json:"fullName,omitempty"`
	Email         string `json:"email,omitempty"`
	PhoneNumber   string `json:"phoneNumber,omitempty"`
	ImgURL        string `json:"imgURL,omitempty"`
	DisplayText   string `json:"displayText,omitempty"`
	CannotMessage bool   `json:"cannotMessage,omitempty"`
	IsSelf        bool   `json:"isSelf,omitempty"`
}


type APIChat struct {
	ID            string             `json:"id"`
	LocalChatID   string             `json:"localChatID,omitempty"`
	AccountID     string             `json:"accountID"`
	Title         string             `json:"title"`
	AvatarURL     string             `json:"avatarURL,omitempty"`
	Type          string             `json:"type"`
	Participants  APIPaginatedUsers   `json:"participants"`
	LastActivity  string             `json:"lastActivity,omitempty"`
	UnreadCount   int                `json:"unreadCount"`
	IsArchived    bool               `json:"isArchived,omitempty"`
	IsMuted       bool               `json:"isMuted,omitempty"`
	IsPinned      bool               `json:"isPinned,omitempty"`
	IsLowPriority bool               `json:"isLowPriority,omitempty"`
	Priority      string             `json:"priority,omitempty"`
	Tags          []string           `json:"tags,omitempty"`
	SpaceID       string             `json:"spaceID,omitempty"`
	Preview       *APIMessage        `json:"preview,omitempty"`
}

type APIPaginatedUsers struct {
	Items   []APIUser `json:"items"`
	HasMore bool      `json:"hasMore"`
	Total   int       `json:"total"`
}

type APIChatsResponse struct {
	Items        []APIChat `json:"items"`
	HasMore      bool      `json:"hasMore"`
	OldestCursor string    `json:"oldestCursor"`
	NewestCursor string    `json:"newestCursor"`
}


type APIMessage struct {
	ID              string           `json:"id"`
	ChatID          string           `json:"chatID"`
	AccountID       string           `json:"accountID"`
	SenderID        string           `json:"senderID"`
	SenderName      string           `json:"senderName,omitempty"`
	Timestamp       string           `json:"timestamp"`
	SortKey         string           `json:"sortKey"`
	Type            string           `json:"type,omitempty"`
	Text            string           `json:"text,omitempty"`
	IsSender        bool             `json:"isSender,omitempty"`
	IsUnread        bool             `json:"isUnread,omitempty"`
	LinkedMessageID string           `json:"linkedMessageID,omitempty"`
	Attachments     []APIAttachment  `json:"attachments,omitempty"`
	Reactions       []APIReaction    `json:"reactions,omitempty"`
	Mentions        []APIMention     `json:"mentions,omitempty"`
	IsEdited        bool             `json:"isEdited,omitempty"`
	EditedAt        string           `json:"editedAt,omitempty"`
	LinkPreview     *APILinkPreview  `json:"linkPreview,omitempty"`
}

type APILinkPreview struct {
	URL         string `json:"url"`
	Title       string `json:"title,omitempty"`
	Description string `json:"description,omitempty"`
	ImageURL    string `json:"imageUrl,omitempty"`
	ImageWidth  int    `json:"imageWidth,omitempty"`
	ImageHeight int    `json:"imageHeight,omitempty"`
}

type APIMention struct {
	UserID      string `json:"userId"`
	DisplayName string `json:"displayName"`
}

type APIAttachment struct {
	ID         string          `json:"id,omitempty"`
	Type       string          `json:"type"`
	SrcURL     string          `json:"srcURL,omitempty"`
	MimeType   string          `json:"mimeType,omitempty"`
	FileName   string          `json:"fileName,omitempty"`
	FileSize   int64           `json:"fileSize,omitempty"`
	IsGif      bool            `json:"isGif,omitempty"`
	IsSticker  bool            `json:"isSticker,omitempty"`
	IsVoiceNote bool           `json:"isVoiceNote,omitempty"`
	Duration   float64         `json:"duration,omitempty"`
	PosterImg  string          `json:"posterImg,omitempty"`
	Size       *APIAttachSize  `json:"size,omitempty"`
}

type APIAttachSize struct {
	Width  int `json:"width"`
	Height int `json:"height"`
}

type APIReaction struct {
	ID            string `json:"id"`
	ReactionKey   string `json:"reactionKey"`
	ParticipantID string `json:"participantID"`
	Emoji         bool   `json:"emoji,omitempty"`
	ImgURL        string `json:"imgURL,omitempty"`
}

type APIMessagesResponse struct {
	Items   []APIMessage `json:"items"`
	HasMore      bool         `json:"hasMore"`
	OldestCursor string       `json:"oldestCursor,omitempty"`
	NewestCursor string       `json:"newestCursor,omitempty"`
}


type APILoginRequest struct {
	Email string `json:"email"`
}

type APILoginResponse struct {
	Status  string `json:"status"`
	Message string `json:"message"`
}

type APIVerifyRequest struct {
	Email string `json:"email"`
	Code  string `json:"code"`
}

type APIVerifyResponse struct {
	Status       string `json:"status"`
	UserID       string `json:"userID"`
	AccessToken  string `json:"accessToken"`
	HomeserverURL string `json:"homeserverURL"`
}

type APISendMessageRequest struct {
	Text             string                `json:"text,omitempty"`
	ReplyToMessageID string                `json:"replyToMessageID,omitempty"`
	Attachment       *APISendAttachment    `json:"attachment,omitempty"`
}

type APISendAttachment struct {
	UploadID string         `json:"uploadID"`
	MimeType string         `json:"mimeType,omitempty"`
	FileName string         `json:"fileName,omitempty"`
	Size     *APIAttachSize `json:"size,omitempty"`
	Duration float64        `json:"duration,omitempty"`
	Type     string         `json:"type,omitempty"`
}

type APISendMessageResponse struct {
	ChatID           string `json:"chatID"`
	PendingMessageID string `json:"pendingMessageID"`
}

type APIEditMessageRequest struct {
	Text string `json:"text"`
}

type APIEditMessageResponse struct {
	ChatID    string `json:"chatID"`
	MessageID string `json:"messageID"`
	Success   bool   `json:"success"`
}

type APIAddReactionRequest struct {
	ReactionKey   string `json:"reactionKey"`
	TransactionID string `json:"transactionID,omitempty"`
}

type APIAddReactionResponse struct {
	Success       bool   `json:"success"`
	ChatID        string `json:"chatID"`
	MessageID     string `json:"messageID"`
	ReactionKey   string `json:"reactionKey"`
	TransactionID string `json:"transactionID,omitempty"`
}

type APIRemoveReactionResponse struct {
	Success     bool   `json:"success"`
	ChatID      string `json:"chatID"`
	MessageID   string `json:"messageID"`
	ReactionKey string `json:"reactionKey"`
}

type APIArchiveRequest struct {
	Archived bool `json:"archived"`
}

type APICreateChatRequest struct {
	AccountID      string   `json:"accountID"`
	Mode           string   `json:"mode,omitempty"`
	Type           string   `json:"type,omitempty"`
	ParticipantIDs []string `json:"participantIDs,omitempty"`
	Title          string   `json:"title,omitempty"`
	MessageText    string   `json:"messageText,omitempty"`
}

type APICreateChatResponse struct {
	ChatID string `json:"chatID"`
	Status string `json:"status,omitempty"`
}

type APIUploadResponse struct {
	UploadID string  `json:"uploadID"`
	SrcURL   string  `json:"srcURL,omitempty"`
	FileName string  `json:"fileName,omitempty"`
	MimeType string  `json:"mimeType,omitempty"`
	FileSize int64   `json:"fileSize,omitempty"`
	Width    int     `json:"width,omitempty"`
	Height   int     `json:"height,omitempty"`
	Duration float64 `json:"duration,omitempty"`
}

type APIDownloadRequest struct {
	URL string `json:"url"`
}

type APIDownloadResponse struct {
	SrcURL string `json:"srcURL,omitempty"`
	Error  string `json:"error,omitempty"`
}

type APIErrorResponse struct {
	Message string `json:"message"`
	Code    string `json:"code"`
}


type WSEvent struct {
	Type string      `json:"type"`
	Data interface{} `json:"data"`
}

type WSSubscription struct {
	Type    string   `json:"type"`
	ChatIDs []string `json:"chatIDs"`
}

func RoomToAPIChat(room *Room, sidecarBaseURL string) APIChat {
	chat := APIChat{
		ID:            room.ID,
		LocalChatID:   room.LocalChatID,
		AccountID:     room.AccountID,
		Title:         room.Title,
		Type:          room.Type,
		UnreadCount:   room.UnreadCount,
		IsArchived:    room.IsArchived,
		IsMuted:       room.IsMuted,
		IsPinned:      room.IsPinned,
		IsLowPriority: room.IsLowPriority,
		Priority:      room.Priority,
		Tags:          room.Tags,
		SpaceID:       room.SpaceID,
	}

	if !room.LastActivity.IsZero() {
		chat.LastActivity = room.LastActivity.Format(time.RFC3339Nano)
	}

	avatarMxc := room.AvatarURL
	if avatarMxc == "" {
		selfID := room.SelfUserID
		for _, m := range room.GetMembersSnapshot() {
			if m.Membership == "join" && m.AvatarURL != "" &&
				m.UserID != selfID &&
				!strings.HasSuffix(m.UserID, "bot:beeper.com") &&
				!strings.HasSuffix(m.UserID, "bot:beeper.local") {
				avatarMxc = m.AvatarURL
				break
			}
		}
	}
	if avatarMxc != "" {
		chat.AvatarURL = fmt.Sprintf("%s/v1/assets/serve?uri=%s", sidecarBaseURL, url.QueryEscape(avatarMxc))
	}

	members := make([]APIUser, 0)
	for _, m := range room.GetMembersSnapshot() {
		if m.Membership == "join" {
			members = append(members, MemberToAPIUser(m, sidecarBaseURL))
		}
	}
	chat.Participants = APIPaginatedUsers{
		Items:   members,
		HasMore: false,
		Total:   len(members),
	}

	if room.Preview != nil {
		apiMsg := MessageToAPIMessage(room.Preview, sidecarBaseURL)
		chat.Preview = &apiMsg
	}

	return chat
}

func MessageToAPIMessage(msg *Message, sidecarBaseURL string) APIMessage {
	apiMsg := APIMessage{
		ID:              msg.ID,
		ChatID:          msg.ChatID,
		AccountID:       msg.AccountID,
		SenderID:        msg.SenderID,
		SenderName:      msg.SenderName,
		Timestamp:       msg.Timestamp.Format(time.RFC3339Nano),
		SortKey:         msg.SortKey,
		Type:            msg.Type,
		Text:            msg.Text,
		IsSender:        msg.IsSender,
		IsUnread:        msg.IsUnread,
		IsEdited:        msg.IsEdited,
		LinkedMessageID: msg.LinkedMessageID,
	}

	if len(msg.Attachments) > 0 {
		apiMsg.Attachments = make([]APIAttachment, len(msg.Attachments))
		for i, att := range msg.Attachments {
			apiAtt := APIAttachment{
				ID:         att.ID,
				Type:       att.Type,
				MimeType:   att.MimeType,
				FileName:   att.FileName,
				FileSize:   att.FileSize,
				IsGif:      att.IsGif,
				IsSticker:  att.IsSticker,
				IsVoiceNote: att.IsVoiceNote,
				Duration:   att.Duration,
			}
			if att.SrcURL != "" {
				apiAtt.SrcURL = fmt.Sprintf("%s/v1/assets/serve?uri=%s", sidecarBaseURL, att.SrcURL)
			}
			if att.Width > 0 && att.Height > 0 {
				apiAtt.Size = &APIAttachSize{Width: att.Width, Height: att.Height}
			}
			apiMsg.Attachments[i] = apiAtt
		}
	}

	if len(msg.Reactions) > 0 {
		apiMsg.Reactions = make([]APIReaction, len(msg.Reactions))
		for i, r := range msg.Reactions {
			apiMsg.Reactions[i] = APIReaction{
				ID:            r.ID,
				ReactionKey:   r.ReactionKey,
				ParticipantID: r.ParticipantID,
				Emoji:         r.IsEmoji,
				ImgURL:        r.ImgURL,
			}
		}
	}

	if len(msg.Mentions) > 0 {
		apiMsg.Mentions = make([]APIMention, len(msg.Mentions))
		for i, m := range msg.Mentions {
			apiMsg.Mentions[i] = APIMention{
				UserID:      m.UserID,
				DisplayName: m.DisplayName,
			}
		}
	}

	if !msg.EditedAt.IsZero() {
		apiMsg.EditedAt = msg.EditedAt.Format(time.RFC3339Nano)
	}

	if msg.LinkPreview != nil {
		lp := &APILinkPreview{
			URL:         msg.LinkPreview.URL,
			Title:       msg.LinkPreview.Title,
			Description: msg.LinkPreview.Description,
			ImageWidth:  msg.LinkPreview.ImageWidth,
			ImageHeight: msg.LinkPreview.ImageHeight,
		}
		if msg.LinkPreview.ImageMXC != "" {
			lp.ImageURL = fmt.Sprintf("%s/v1/assets/serve?uri=%s", sidecarBaseURL, url.QueryEscape(msg.LinkPreview.ImageMXC))
		}
		apiMsg.LinkPreview = lp
	}

	return apiMsg
}

func MemberToAPIUser(m *Member, sidecarBaseURL string) APIUser {
	user := APIUser{
		ID:       m.UserID,
		FullName: m.DisplayName,
	}
	if m.AvatarURL != "" {
		user.ImgURL = fmt.Sprintf("%s/v1/assets/serve?uri=%s", sidecarBaseURL, url.QueryEscape(m.AvatarURL))
	}
	return user
}

func AccountToAPIAccount(acct *Account) APIAccount {
	apiAcct := APIAccount{
		AccountID: acct.AccountID,
		Network:   acct.Network,
	}
	if acct.User != nil {
		apiAcct.User = APIUser{
			ID:          acct.User.ID,
			Username:    acct.User.Username,
			FullName:    acct.User.FullName,
			Email:       acct.User.Email,
			PhoneNumber: acct.User.PhoneNumber,
			ImgURL:      acct.User.ImgURL,
			DisplayText: acct.User.DisplayText,
			IsSelf:      acct.User.IsSelf,
		}
	}
	return apiAcct
}
