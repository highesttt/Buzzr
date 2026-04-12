package main

import (
	"strings"
)

type BridgeInfo struct {
	AccountID string
	Network   string
}

var bridgeBotPatterns = map[string]BridgeInfo{
	"@discordgobot:":    {AccountID: "discordgo", Network: "Discord"},
	"@facebookgobot:":   {AccountID: "facebookgo", Network: "Facebook"},
	"@instagramgobot:":  {AccountID: "instagramgo", Network: "Instagram"},
	"@gmessagesbot:":    {AccountID: "gmessages", Network: "Google Messages"},
	"@whatsappbot:":     {AccountID: "whatsapp", Network: "WhatsApp"},
	"@signalgobot:":     {AccountID: "signalgo", Network: "Signal"},
	"@telegramgobot:":   {AccountID: "telegramgo", Network: "Telegram"},
	"@linkedinbot:":     {AccountID: "linkedin", Network: "LinkedIn"},
	"@imessagegobot:":   {AccountID: "imessagego", Network: "iMessage"},
	"@imessagecloudbot:":{AccountID: "imessagego", Network: "iMessage"},
	"@slackgobot:":      {AccountID: "slackgo", Network: "Slack"},
	"@twittergobot:":    {AccountID: "twittergo", Network: "Twitter"},
	"@googlechatbot:":   {AccountID: "googlechat", Network: "Google Chat"},
	"@discordbot:":      {AccountID: "discordgo", Network: "Discord"},
	"@facebookbot:":     {AccountID: "facebookgo", Network: "Facebook"},
	"@instagrambot:":    {AccountID: "instagramgo", Network: "Instagram"},
	"@signalbot:":       {AccountID: "signalgo", Network: "Signal"},
	"@telegrambot:":     {AccountID: "telegramgo", Network: "Telegram"},
	"@imessagebot:":     {AccountID: "imessagego", Network: "iMessage"},
	"@slackbot:":        {AccountID: "slackgo", Network: "Slack"},
	"@twitterbot:":      {AccountID: "twittergo", Network: "Twitter"},
}

func DetectBridgeFromMembers(memberIDs []string) *BridgeInfo {
	for _, memberID := range memberIDs {
		for pattern, info := range bridgeBotPatterns {
			if strings.HasPrefix(memberID, pattern) {
				return &info
			}
		}
	}
	return nil
}

func DetectBridgeFromUserID(userID string) *BridgeInfo {
	if !strings.HasPrefix(userID, "@") {
		return nil
	}
	localpart := strings.SplitN(userID[1:], ":", 2)[0]

	prefixes := map[string]BridgeInfo{
		"discord_":       {AccountID: "discordgo", Network: "Discord"},
		"whatsapp_":      {AccountID: "whatsapp", Network: "WhatsApp"},
		"signal_":        {AccountID: "signalgo", Network: "Signal"},
		"telegram_":      {AccountID: "telegramgo", Network: "Telegram"},
		"facebook_":      {AccountID: "facebookgo", Network: "Facebook"},
		"instagram_":     {AccountID: "instagramgo", Network: "Instagram"},
		"linkedin_":      {AccountID: "linkedin", Network: "LinkedIn"},
		"gmessages_":     {AccountID: "gmessages", Network: "Google Messages"},
		"imessage_":      {AccountID: "imessagego", Network: "iMessage"},
		"imessagecloud_": {AccountID: "imessagego", Network: "iMessage"},
		"slack_":         {AccountID: "slackgo", Network: "Slack"},
		"twitter_":       {AccountID: "twittergo", Network: "Twitter"},
		"googlechat_":    {AccountID: "googlechat", Network: "Google Chat"},
	}

	for prefix, info := range prefixes {
		if strings.HasPrefix(localpart, prefix) {
			return &info
		}
	}
	return nil
}

func DefaultBridgeInfo() BridgeInfo {
	return BridgeInfo{
		AccountID: "hungryserv",
		Network:   "Beeper",
	}
}
